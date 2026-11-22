using System.Net;
using System.Text;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class GrokDeviceLoginTests
{
    private const string Challenge = """
        {"device_code":"fixture-device+code","user_code":"GROK-TEST","verification_uri":"https://auth.x.ai/activate",
         "verification_uri_complete":"https://example.test/activate","expires_in":900,"interval":5}
        """;
    private const string Tokens = """{"access_token":"fixture-first","refresh_token":"fixture-refresh","expires_in":3600}""";
    private const string Credits = """
        {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-11-21T02:00:00Z","end":"2026-11-28T02:00:00Z"},
        "creditUsagePercent":25}}
        """;

    [Fact]
    public async Task UsesTheTrustedVerificationPageAndSlowsDownAfterPending()
    {
        Queue<(int, string)> replies = new([
            (400, """{"error":"authorization_pending"}"""),
            (429, """{"error":"slow_down"}"""),
            (200, Tokens)
        ]);
        var delays = new List<double>();
        using var http = Http((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("auth.x.ai", request.RequestUri!.Host);
            Assert.Contains("client_id=" + GrokAuthStore.DefaultClientId, body);
            if (request.RequestUri.AbsolutePath == "/oauth2/device/code")
            {
                Assert.Contains("scope=openid+email+offline_access+grok-cli%3Aaccess", body);
                return (200, Challenge);
            }
            Assert.Equal("/oauth2/token", request.RequestUri.AbsolutePath);
            Assert.Contains("device_code=fixture-device%2Bcode", body);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", body);
            return replies.Dequeue();
        });
        var login = new GrokDeviceLogin(http, (delay, _) =>
        {
            delays.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });
        var auth = await login.AuthorizeAsync((code, page) =>
        {
            Assert.Equal("GROK-TEST", code);
            Assert.Equal("https://auth.x.ai/activate", page.AbsoluteUri);
        }, CancellationToken.None);
        Assert.Equal("fixture-first", auth.AccessToken);
        Assert.Equal("fixture-refresh", auth.RefreshToken);
        Assert.Equal(new double[] { 5, 5, 10 }, delays);
    }

    [Fact]
    public async Task DoesNotPromptWhenTheServerReturnsOnlyAnUntrustedPage()
    {
        using var http = Http((_, _) => (200, Challenge.Replace("https://auth.x.ai/activate", "http://auth.x.ai/activate")));
        var shown = false;
        await Assert.ThrowsAsync<ProviderRequestException>(() => new GrokDeviceLogin(http, NoWait)
            .AuthorizeAsync((_, _) => shown = true, CancellationToken.None));
        Assert.False(shown);
    }

    [Fact]
    public async Task CancelingThePromptPreventsPolling()
    {
        using var ct = new CancellationTokenSource();
        using var http = Http((request, _) =>
        {
            Assert.Equal("/oauth2/device/code", request.RequestUri!.AbsolutePath);
            return (200, Challenge);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GrokDeviceLogin(http, NoWait)
            .AuthorizeAsync((_, _) => ct.Cancel(), ct.Token));
    }

    [Theory]
    [InlineData("""{"error":"expired_token"}""", "expired")]
    [InlineData("""{"error":"access_denied"}""", "denied")]
    [InlineData("""{"access_token":"valid","refresh_token":"bad token"}""", "invalid sign-in tokens")]
    [InlineData("""{"access_token":"valid"}""", "invalid sign-in tokens")]
    public async Task RefusesTerminalErrorsOrUnusableCredentials(string payload, string message)
    {
        using var http = Http((request, _) => (200, request.RequestUri!.AbsolutePath == "/oauth2/device/code" ? Challenge : payload));
        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() => new GrokDeviceLogin(http, NoWait)
            .AuthorizeAsync((_, _) => { }, CancellationToken.None));
        Assert.Contains(message, ex.Message);
    }

    [WindowsFact]
    public async Task KeepsSavedSignInOnRejectedReplacementAndPersistsRotatedCredentials()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            var replacing = false;
            var expire = false;
            var renewed = 0;
            using var http = Http((request, form) =>
            {
                if (request.RequestUri == GrokProvider.RefreshUrl)
                {
                    if (form.Contains("grant_type=refresh_token"))
                    {
                        Assert.Contains("refresh_token=fixture-refresh", form);
                        renewed++;
                        return (200, """{"access_token":"fixture-rotated","refresh_token":"fixture-refresh-rotated","expires_in":3600}""");
                    }
                    return (200, replacing ? Tokens.Replace("fixture-first", "fixture-unverified") : Tokens);
                }
                if (request.RequestUri == GrokProvider.CreditsConfigUrl)
                {
                    var token = request.Headers.Authorization!.Parameter;
                    return token == "fixture-unverified" || expire && token == "fixture-first"
                        ? (401, "{}")
                        : (200, Credits);
                }
                if (request.RequestUri == GrokProvider.SettingsUrl)
                    return (200, """{"subscription_tier_display":"SuperGrok"}""");
                Assert.Equal("/oauth2/device/code", request.RequestUri!.AbsolutePath);
                return (200, Challenge);
            });
            var provider = new GrokProvider(http, new ApiKeyStore(path));
            var flow = new GrokDeviceLogin(http, NoWait);
            var snapshot = await provider.ConnectAsync(flow, (_, _) => { }, CancellationToken.None);
            Assert.Equal("SuperGrok", snapshot.Plan);
            Assert.NotNull(snapshot.Line("Weekly limit"));
            Assert.DoesNotContain("fixture-first", Encoding.UTF8.GetString(File.ReadAllBytes(path)));

            replacing = true;
            await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ConnectAsync(flow, (_, _) => { }, CancellationToken.None));
            Assert.False((await provider.RefreshAsync()).IsError);
            Assert.Equal(0, renewed);

            expire = true;
            var reopened = new GrokProvider(http, new ApiKeyStore(path));
            Assert.True(await reopened.HasLocalCredentialsAsync());
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(1, renewed);
            Assert.False((await new GrokProvider(http, new ApiKeyStore(path)).RefreshAsync()).IsError);
            Assert.Equal(1, renewed);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static Task NoWait(TimeSpan _, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static AppHttp Http(Func<HttpRequestMessage, string, (int Status, string Body)> handler) =>
        new(new HttpClient(new Handler(handler)));

    private sealed class Handler(Func<HttpRequestMessage, string, (int Status, string Body)> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, json) = handle(request, body);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
