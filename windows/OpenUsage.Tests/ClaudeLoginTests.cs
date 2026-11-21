using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class ClaudeLoginTests
{
    private const string Usage = """{"five_hour":{"utilization":12,"resets_at":"2026-11-21T12:00:00Z"}}""";
    private const string Profile = """{"account":{"uuid":"fixture-account"},"organization":{"organization_type":"claude_pro"}}""";
    private const string Token = """{"access_token":"fixture-access","refresh_token":"fixture-refresh","expires_in":3600}""";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T10:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PastedCodeUsesASeparateStateAndPkceVerifier(bool callbackUrl)
    {
        var input = Completion<string>();
        Uri? authorization = null;
        using var http = MakeHttp((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(ClaudeAuthStore.ProdRefreshUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var query = HttpUtility.ParseQueryString(authorization!.Query);
            var exchange = JsonNode.Parse(body)!;
            var verifier = exchange["code_verifier"]!.GetValue<string>();
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(challenge, query["code_challenge"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(ClaudeLogin.Scope, query["scope"]);
            Assert.Equal("true", query["code"]);
            Assert.Equal("authorization_code", exchange["grant_type"]!.GetValue<string>());
            Assert.Equal("fixture-code+value", exchange["code"]!.GetValue<string>());
            Assert.Equal(query["state"], exchange["state"]!.GetValue<string>());
            Assert.NotEqual(query["state"], verifier);
            Assert.Equal(query["redirect_uri"], exchange["redirect_uri"]!.GetValue<string>());
            return (200, Token);
        });
        var token = await new ClaudeLogin(http, () => Now).AuthorizeAsync(uri =>
        {
            authorization = uri;
            Assert.Equal(ClaudeLogin.AuthorizeUrl.GetLeftPart(UriPartial.Path), uri.GetLeftPart(UriPartial.Path));
            var query = HttpUtility.ParseQueryString(uri.Query);
            var redirect = new Uri(query["redirect_uri"]!);
            Assert.Equal("localhost", redirect.Host);
            Assert.Equal("/callback", redirect.AbsolutePath);
            input.SetResult(callbackUrl ? $"{redirect}?code=fixture-code%2Bvalue&state={query["state"]}"
                : $"fixture-code+value#{query["state"]}");
        }, input.Task, CancellationToken.None);
        Assert.Equal("fixture-access", token.AccessToken);
        Assert.Equal("fixture-refresh", token.RefreshToken);
        Assert.Equal((double)Now.AddHours(1).ToUnixTimeMilliseconds(), token.ExpiresAt);
    }

    [Fact]
    public async Task LoopbackCallbackCompletesTheSignIn()
    {
        var authorization = Completion<Uri>();
        using var http = MakeHttp((_, _) => (200, Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var login = new ClaudeLogin(http, () => Now).AuthorizeAsync(authorization.SetResult, Completion<string>().Task, deadline.Token);
        var query = HttpUtility.ParseQueryString((await authorization.Task.WaitAsync(deadline.Token)).Query);
        var redirect = new Uri(query["redirect_uri"]!);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirect.Port, deadline.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /callback?code=fixture-code&state={query["state"]} HTTP/1.1\r\nHost: {redirect.Authority}\r\nConnection: close\r\n\r\n"),
            deadline.Token);
        using var reader = new StreamReader(stream);
        Assert.StartsWith("HTTP/1.1 200", await reader.ReadLineAsync(deadline.Token));
        Assert.Equal("fixture-access", (await login).AccessToken);
    }

    [Theory]
    [InlineData("fixture-code#wrong-state")]
    [InlineData("https://example.invalid/callback?code=fixture-code&state={0}")]
    [InlineData("https://example.invalid/callback#{0}")]
    public async Task RejectsAnUnmatchedPasteWithoutExchangingIt(string input)
    {
        using var http = MakeHttp((_, _) => throw new InvalidOperationException("Unexpected token exchange"));
        var pasted = Completion<string>();
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => new ClaudeLogin(http, () => Now).AuthorizeAsync(
            url => pasted.SetResult(string.Format(input, HttpUtility.ParseQueryString(url.Query)["state"])),
            pasted.Task, CancellationToken.None));
        Assert.Contains("did not match", error.Message);
    }

    [Fact]
    public async Task CancellationClosesTheCallbackWithoutExchanging()
    {
        using var http = MakeHttp((_, _) => throw new InvalidOperationException("Unexpected token exchange"));
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeLogin(http, () => Now).AuthorizeAsync(
            _ => stop.Cancel(), Completion<string>().Task, stop.Token));
    }

    [Fact]
    public async Task WaitingForACodeHasADeadline()
    {
        using var http = MakeHttp((_, _) => throw new InvalidOperationException("Unexpected token exchange"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => new ClaudeLogin(http, () => Now,
            TimeSpan.FromMilliseconds(30)).AuthorizeAsync(_ => { }, Completion<string>().Task, CancellationToken.None));
        Assert.Contains("timed out", error.Message);
    }

    [WindowsFact]
    public async Task ARejectedReplacementLeavesTheSavedAndLiveConnectionIntact()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            var candidate = "fixture-access";
            var rejectUsage = false;
            var calls = new List<(string? Token, string Path)>();
            using var http = MakeHttp((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                    return (200, Token.Replace("fixture-access", candidate));
                var endpoint = request.RequestUri!.AbsolutePath;
                var access = request.Headers.Authorization?.Parameter;
                calls.Add((access, endpoint));
                if (access == "fixture-replacement" && (rejectUsage || endpoint.EndsWith("/profile")))
                    return (403, "{}");
                return endpoint.EndsWith("/profile") ? (200, Profile) : (200, Usage);
            });
            var provider = new ClaudeProvider(http, keys: new ApiKeyStore(path), now: () => Now);
            Assert.False((await Connect(provider)).IsError);
            Assert.True(provider.IsSignedIn);
            Assert.DoesNotContain("fixture-access", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            var saved = File.ReadAllBytes(path);

            candidate = "fixture-replacement";
            rejectUsage = true;
            await Assert.ThrowsAsync<ProviderRequestException>(() => Connect(provider));
            rejectUsage = false;
            var error = await Assert.ThrowsAsync<ProviderRequestException>(() => Connect(provider));
            Assert.Contains("profile", error.Message);
            Assert.Equal(saved, File.ReadAllBytes(path));
            Assert.False((await provider.RefreshAsync()).IsError);
            Assert.False((await new ClaudeProvider(http, keys: new ApiKeyStore(path), now: () => Now).RefreshAsync()).IsError);
            Assert.Equal(2, calls.Count(x => x is ("fixture-replacement", "/api/oauth/usage")));
            Assert.Single(calls, x => x is ("fixture-replacement", "/api/oauth/profile"));
            Assert.Equal("fixture-access", calls.Last().Token);
        }
        finally { dir.Delete(recursive: true); }
    }

    [WindowsFact]
    public async Task AppOwnedRenewalPersistsRotatedTokensWithoutEditingTheCliFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            var auth = new ClaudeAuthStore(dir.FullName, readCredentialManager: false);
            const string cli = """{"claudeAiOauth":{"accessToken":"fixture-cli"}}""";
            File.WriteAllText(auth.CredentialsPath(), cli);
            var now = Now;
            var renewalCount = 0;
            using var http = MakeHttp((request, body) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var grant = JsonNode.Parse(body)!;
                    if (grant["grant_type"]!.GetValue<string>() == "authorization_code") return (200, Token);
                    renewalCount++;
                    Assert.Equal("fixture-refresh", grant["refresh_token"]!.GetValue<string>());
                    Assert.Equal(ClaudeLogin.Scope, grant["scope"]!.GetValue<string>());
                    return (200, Token.Replace("fixture-access", "fixture-rotated").Replace("fixture-refresh", "fixture-new-refresh"));
                }
                Assert.Equal(now == Now ? "fixture-access" : "fixture-rotated", request.Headers.Authorization?.Parameter);
                return request.RequestUri!.AbsolutePath.EndsWith("/profile") ? (200, Profile) : (200, Usage);
            });
            var provider = new ClaudeProvider(http, auth, () => now, new ApiKeyStore(path));
            await Connect(provider);
            now = now.AddHours(2);
            Assert.False((await provider.RefreshAsync()).IsError);
            var reopened = new ClaudeProvider(http, auth, () => now, new ApiKeyStore(path));
            Assert.True(reopened.IsSignedIn);
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(1, renewalCount);
            Assert.Equal(cli, File.ReadAllText(auth.CredentialsPath()));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExistingCliCredentialsWorkUntilConnectingInCustomize()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var auth = new ClaudeAuthStore(dir.FullName, readCredentialManager: false);
            File.WriteAllText(auth.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"fixture-cli"}}""");
            using var http = MakeHttp((request, _) =>
            {
                Assert.Equal("fixture-cli", request.Headers.Authorization?.Parameter);
                return request.RequestUri!.AbsolutePath.EndsWith("/profile") ? (200, Profile) : (200, Usage);
            });
            var provider = new ClaudeProvider(http, auth, () => Now, new ApiKeyStore(Path.Combine(dir.FullName, "keys.bin")));
            Assert.False(provider.IsSignedIn);
            Assert.True(await provider.HasLocalCredentialsAsync());
            Assert.False((await provider.RefreshAsync()).IsError);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static Task<ProviderSnapshot> Connect(ClaudeProvider provider)
    {
        var code = Completion<string>();
        return provider.ConnectAsync(url => code.SetResult($"fixture-code#{HttpUtility.ParseQueryString(url.Query)["state"]}"),
            code.Task, CancellationToken.None);
    }

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AppHttp MakeHttp(Func<HttpRequestMessage, string, (int Status, string Body)> respond) =>
        new(new HttpClient(new FixtureHandler(respond)));

    private sealed class FixtureHandler(Func<HttpRequestMessage, string, (int Status, string Body)> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = respond(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI is required.";
        }
    }
}
