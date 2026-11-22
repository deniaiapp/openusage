using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CodexDeviceLoginTests
{
    private const string Start = "/api/accounts/deviceauth/usercode";
    private const string Poll = "/api/accounts/deviceauth/token";
    private const string Exchange = "/oauth/token";
    private const string Usage = "/backend-api/wham/usage";
    private const string StartResponse = """{"device_auth_id":"fixture-id","user_code":"ABCD-EFGH","interval":"3"}""";
    private const string PollResponse = """{"authorization_code":"fixture+code","code_verifier":"fixture-verifier"}""";
    private const string TokenResponse = """{"access_token":"fixture-access","refresh_token":"fixture-refresh"}""";
    private const string UsageResponse = """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":21}}}""";

    [Fact]
    public async Task ExchangesDeviceCodeAfterPendingAndSlowDown()
    {
        Queue<(int, string)> polls = new([(404, "{}"), (400, """{"error":"slow_down"}"""), (200, PollResponse)]);
        var delays = new List<double>();
        using var http = MakeHttp((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("auth.openai.com", request.RequestUri!.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case Start:
                    Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                    Assert.Equal(CodexProvider.ClientId, JsonUtil.String(JsonUtil.ParseObject(body)?["client_id"]));
                    return (200, StartResponse);
                case Poll:
                    Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                    Assert.Equal("fixture-id", JsonUtil.String(JsonUtil.ParseObject(body)?["device_auth_id"]));
                    Assert.Equal("ABCD-EFGH", JsonUtil.String(JsonUtil.ParseObject(body)?["user_code"]));
                    return polls.Dequeue();
                case Exchange:
                    Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                    Assert.Contains("grant_type=authorization_code", body);
                    Assert.Contains("code=fixture%2Bcode", body);
                    Assert.Contains("code_verifier=fixture-verifier", body);
                    Assert.Contains("redirect_uri=https%3A%2F%2Fauth.openai.com%2Fdeviceauth%2Fcallback", body);
                    return (200, TokenResponse);
                default: throw new InvalidOperationException();
            }
        });
        var login = new CodexDeviceLogin(http, (t, _) => { delays.Add(t.TotalSeconds); return Task.CompletedTask; });

        var auth = await login.AuthorizeAsync(code => Assert.Equal("ABCD-EFGH", code), default);

        Assert.Equal("fixture-access", auth.AccessToken);
        Assert.Equal("fixture-refresh", auth.RefreshToken);
        Assert.Equal([3, 3, 8], delays);
        Assert.Empty(polls);
    }

    [Fact]
    public async Task CancellationAfterPromptDoesNotPoll()
    {
        using var cts = new CancellationTokenSource();
        using var http = MakeHttp((request, _) =>
        {
            Assert.Equal(Start, request.RequestUri!.AbsolutePath);
            return (200, StartResponse);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CodexDeviceLogin(http).AuthorizeAsync(_ => cts.Cancel(), cts.Token));
    }

    [Theory]
    [InlineData(403, """{"error":"access_denied"}""", "denied")]
    [InlineData(404, """{"error":"expired_token"}""", "expired")]
    [InlineData(503, "{}", "HTTP 503")]
    [InlineData(200, "{}", "invalid")]
    public async Task TerminalPollFailureDoesNotExchange(int status, string body, string message)
    {
        using var http = MakeHttp((request, _) => request.RequestUri!.AbsolutePath switch
        {
            Start => (200, StartResponse),
            Poll => (status, body),
            _ => throw new InvalidOperationException()
        });
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => new CodexDeviceLogin(http, NoWait).AuthorizeAsync(_ => { }, default));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("""{"access_token":" fixture","refresh_token":"refresh"}""")]
    [InlineData("""{"access_token":"access"}""")]
    [InlineData("""{"access_token":"access","refresh_token":"re\tfresh"}""")]
    public void RejectsInvalidNewTokens(string body) =>
        Assert.Throws<ProviderRequestException>(() => CodexDeviceLogin.ParseTokens(JsonUtil.ParseObject(body), null));

    [WindowsFact]
    public async Task RejectsUnverifiedReplacementAndPersistsRotatedTokens()
    {
        var directory = Directory.CreateTempSubdirectory();
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory.FullName);
            var cliPath = Path.Combine(directory.FullName, "auth.json");
            const string cli = """{"tokens":{"access_token":"fixture-cli","refresh_token":"fixture-cli-refresh"}}""";
            File.WriteAllText(cliPath, cli);
            var path = Path.Combine(directory.FullName, "keys.bin");
            var replacing = false;
            var rotated = false;
            var oldAccess = MakeToken();
            var renewals = 0;
            using var http = MakeHttp((request, body) => request.RequestUri!.AbsolutePath switch
            {
                Start => (200, StartResponse),
                Poll => (200, PollResponse),
                Exchange when body.Contains("grant_type=refresh_token") => Renew(request, body),
                Exchange => (200, replacing ? TokenResponse : $$"""{"access_token":"{{oldAccess}}","refresh_token":"original-refresh"}"""),
                Usage => ReadUsage(request),
                "/backend-api/wham/rate-limit-reset-credits" => (404, "{}"),
                "/backend-api/wham/analytics/daily-workspace-usage-counts" => ReadUnavailableHistory(request),
                _ => throw new InvalidOperationException()
            });
            var provider = new CodexProvider(http, new ApiKeyStore(path));
            var login = new CodexDeviceLogin(http, NoWait);

            var connected = await provider.ConnectAsync(login, _ => { }, default);
            Assert.Equal(21, Assert.IsType<MetricLine.ProgressLine>(connected.Line("Session")).Used);
            replacing = true;
            await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ConnectAsync(login, _ => { }, default));
            var reopened = new CodexProvider(http, new ApiKeyStore(path));
            Assert.True(reopened.HasAppCredential);
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(1, renewals);
            Assert.False((await new CodexProvider(http, new ApiKeyStore(path)).RefreshAsync()).IsError);
            Assert.Equal(1, renewals);
            Assert.Equal(cli, File.ReadAllText(cliPath));

            (int, string) ReadUnavailableHistory(HttpRequestMessage req)
            {
                Assert.Equal(rotated ? "rotated-access" : oldAccess, req.Headers.Authorization?.Parameter);
                Assert.Equal("fixture-account", Assert.Single(req.Headers.GetValues("ChatGPT-Account-Id")));
                return (503, "{}");
            }

            (int, string) Renew(HttpRequestMessage req, string form)
            {
                Assert.Equal("auth.openai.com", req.RequestUri!.Host);
                Assert.Contains("refresh_token=original-refresh", form);
                Assert.Equal(1, ++renewals);
                rotated = true;
                return (200, """{"access_token":"rotated-access","refresh_token":"rotated-refresh"}""");
            }

            (int, string) ReadUsage(HttpRequestMessage req)
            {
                var header = req.Headers.Authorization?.Parameter;
                if (header == "fixture-access") return (401, "{}");
                Assert.Equal("fixture-account", Assert.Single(req.Headers.GetValues("ChatGPT-Account-Id")));
                if (rotated)
                {
                    Assert.Equal("rotated-access", header);
                    return (200, UsageResponse);
                }
                Assert.Equal(oldAccess, header);
                return replacing ? (401, "{}") : (200, UsageResponse);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CliCredentialsDoNotEnableTheProviderOrCauseRequests()
    {
        var directory = Directory.CreateTempSubdirectory();
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory.FullName);
            var cliPath = Path.Combine(directory.FullName, "auth.json");
            const string cli = """{"tokens":{"access_token":"fixture-cli","refresh_token":"fixture-cli-refresh","account_id":"fixture-account"}}""";
            File.WriteAllText(cliPath, cli);
            var calls = 0;
            using var http = MakeHttp((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("CLI credentials must not cause a request.");
            });
            var provider = new CodexProvider(http, new ApiKeyStore(Path.Combine(directory.FullName, "keys.bin")));
            Assert.False(provider.HasAppCredential);
            Assert.False(await provider.HasLocalCredentialsAsync());
            var snapshot = await provider.RefreshAsync();
            Assert.True(snapshot.IsError);
            Assert.Contains("Customize", Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
            Assert.Equal(0, calls);
            Assert.Equal(cli, File.ReadAllText(cliPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            directory.Delete(true);
        }
    }

    private static string MakeToken()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return "fixture." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$$"""{"exp":{{{expiry}}},"https://api.openai.com/auth":{"chatgpt_account_id":"fixture-account"}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
    }

    private static Task NoWait(TimeSpan _, CancellationToken ct) => Task.CompletedTask;

    private static AppHttp MakeHttp(Func<HttpRequestMessage, string, (int, string)> respond) =>
        new(new HttpClient(new Fixture(respond)));

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI is required.";
        }
    }

    private sealed class Fixture(Func<HttpRequestMessage, string, (int, string)> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var (status, response) = respond(request, body);
            return new((HttpStatusCode)status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
