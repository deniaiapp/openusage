using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class AntigravityLoginTests
{
    [Fact]
    public async Task ExchangesOnlyMatchingCallbackWithIndependentStateAndPkce()
    {
        var browser = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exchange = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new AppHttp(new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            exchange.SetResult(await request.Content!.ReadAsStringAsync(ct));
            return Response(HttpStatusCode.OK, """{"access_token":"fixture-access","refresh_token":"fixture-refresh","expires_in":3600}""");
        })));
        var login = new AntigravityLogin(http, "fixture-client", "fixture-secret")
            .AuthorizeAsync(uri => browser.SetResult(uri), wait.Token, port: 0);
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var uri = await browser.Task.WaitAsync(wait.Token);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("fixture-client", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Null(query["client_secret"]);
        var state = Assert.IsType<string>(query["state"]);
        var redirect = new Uri(Assert.IsType<string>(query["redirect_uri"]));
        Assert.Equal("localhost", redirect.Host);
        Assert.Equal("/oauth-callback", redirect.AbsolutePath);
        using (var unrelated = await local.GetAsync(new Uri(redirect, "/?code=ignored&state=" + state), wait.Token))
            Assert.Equal(HttpStatusCode.BadRequest, unrelated.StatusCode);
        using (var wrongState = await local.GetAsync(redirect + "?code=ignored&state=other", wait.Token))
            Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
        Assert.False(exchange.Task.IsCompleted);
        using (var accepted = await local.GetAsync(redirect + "?code=fixture-code&state=" + state, wait.Token))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var tokens = await login;
        var fields = HttpUtility.ParseQueryString(await exchange.Task);
        Assert.Equal("authorization_code", fields["grant_type"]);
        Assert.Equal("fixture-code", fields["code"]);
        Assert.Equal("fixture-secret", fields["client_secret"]);
        Assert.Equal(redirect.AbsoluteUri, fields["redirect_uri"]);
        var verifier = Assert.IsType<string>(fields["code_verifier"]);
        Assert.NotEqual(state, verifier);
        Assert.Equal(query["code_challenge"], Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.Equal("fixture-access", tokens.Access);
        Assert.Equal("fixture-refresh", tokens.Refresh);
    }

    [Fact]
    public async Task CancelStopsTheBrowserWaitWithoutCallingGoogle()
    {
        var browser = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new AppHttp(new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No provider request expected."))));
        var login = new AntigravityLogin(http, "fixture-client", "fixture-secret")
            .AuthorizeAsync(uri => browser.SetResult(uri), canceled.Token, port: 0);
        await browser.Task.WaitAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
    }

    [Fact]
    public async Task RenewalRetainsTheRefreshTokenWhenGoogleDoesNotReplaceIt()
    {
        using var http = new AppHttp(new HttpClient(new Handler(async (request, ct) =>
        {
            var fields = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("refresh_token", fields["grant_type"]);
            Assert.Equal("fixture-refresh", fields["refresh_token"]);
            return Response(HttpStatusCode.OK, """{"access_token":"fixture-rotated"}""");
        })));
        var tokens = await new AntigravityLogin(http, "fixture-client", "fixture-secret").RenewAsync("fixture-refresh", default);
        Assert.Equal("fixture-rotated", tokens.Access);
        Assert.Equal("fixture-refresh", tokens.Refresh);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again")]
    [InlineData(HttpStatusCode.Forbidden, "Couldn't read Antigravity usage")]
    public async Task RefusedSignInReportsAnActionableErrorWithoutSaving(HttpStatusCode status, string message)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            using var http = new AppHttp(new HttpClient(new Handler((request, _) =>
            {
                Assert.Contains(":retrieveUserQuotaSummary", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Response(status, """{"error":"fixture-denied"}"""));
            })));
            var provider = new AntigravityProvider(http, new ApiKeyStore(path));
            var oauth = new AntigravitySignIn("fixture-rejected", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1));

            var error = await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ValidateAndSaveAsync(oauth, default));

            Assert.Contains(message, error.Message);
            Assert.False(provider.IsSignedIn);
            Assert.False(File.Exists(path));
        }
        finally { dir.Delete(recursive: true); }
    }

    [WindowsFact]
    public async Task RejectedReplacementKeepsEncryptedSignInAndRestartsWithTheSameAccount()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            using var http = new AppHttp(new HttpClient(new Handler((request, _) =>
            {
                Assert.Contains(":retrieveUserQuotaSummary", request.RequestUri!.AbsolutePath);
                var token = request.Headers.Authorization?.Parameter;
                return Task.FromResult(token == "fixture-access"
                    ? Response(HttpStatusCode.OK, """{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.8}]}]}""")
                    : Response(HttpStatusCode.Unauthorized, """{"error":"fixture-denied"}"""));
            })));
            var oauth = new AntigravitySignIn("fixture-access", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1));
            var provider = new AntigravityProvider(http, new ApiKeyStore(path));
            await Assert.ThrowsAsync<ProviderRequestException>(() =>
                provider.ValidateAndSaveAsync(oauth with { Access = "fixture-rejected" }, default));
            Assert.False(provider.IsSignedIn);
            Assert.False(File.Exists(path));
            var snapshot = await provider.ValidateAndSaveAsync(oauth, default);
            Assert.Equal(20, Assert.IsType<MetricLine.ProgressLine>(Assert.Single(snapshot.Lines)).Used, 6);
            Assert.DoesNotContain("fixture-refresh", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            await Assert.ThrowsAsync<ProviderRequestException>(() =>
                provider.ValidateAndSaveAsync(oauth with { Access = "fixture-rejected" }, default));
            var restored = new AntigravityProvider(http, new ApiKeyStore(path));
            Assert.True(await restored.HasLocalCredentialsAsync());
            Assert.True(restored.IsSignedIn);
            Assert.False((await restored.RefreshAsync()).IsError);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI required.";
        }
    }
}
