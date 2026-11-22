using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CopilotDeviceLoginTests
{
    private const string DeviceResponse = """{"device_code":"fixture-device+code","user_code":"ABCD-EFGH","expires_in":900,"interval":5}""";
    private const string TokenResponse = """{"access_token":"fixture-copilot-token","token_type":"bearer"}""";

    [Theory]
    [InlineData("", "github.com")]
    [InlineData("https://github.com/login/device", "github.com")]
    [InlineData("OctoCorp", "octocorp.ghe.com")]
    [InlineData(" https://OctoCorp.ghe.com/login/device?ref=test ", "octocorp.ghe.com")]
    [InlineData("http://octocorp.ghe.com", null)]
    [InlineData("octocorp.ghe.com.other.example", null)]
    [InlineData("https://octocorp.ghe.com@other.example/", null)]
    [InlineData("octocorp.ghe.com:8443", null)]
    [InlineData("-octocorp", null)]
    public void AcceptsOnlyPublicGitHubOrAnEnterpriseCloudAddress(string input, string? host) =>
        Assert.Equal(host, CopilotDomain.Parse(input)?.Host);

    [Fact]
    public async Task EnterpriseDeviceAndTokenRequestsGoToTheChosenTenant()
    {
        var requests = new List<string>();
        using var http = MakeHttp((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/login/device/code" ? (200, DeviceResponse) : (200, TokenResponse);
        });
        var login = new CopilotDeviceLogin(http, NoWait);
        var token = await login.AuthorizeAsync(_ => { }, CancellationToken.None, CopilotDomain.Parse("octocorp")!);
        Assert.Equal("fixture-copilot-token", token);
        Assert.Equal(new[] { "https://octocorp.ghe.com/login/device/code", "https://octocorp.ghe.com/login/oauth/access_token" }, requests);
    }

    [Fact]
    public async Task PollsAfterTheAdvertisedIntervalAndSlowsDownWhenAsked()
    {
        Queue<string> replies = new([
            """{"error":"authorization_pending"}""", """{"error":"slow_down"}""", TokenResponse
        ]);
        var intervals = new List<double>();
        var events = new List<string>();
        using var http = MakeHttp((request, form) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("github.com", request.RequestUri!.Host);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            if (request.RequestUri.AbsolutePath == "/login/device/code")
            {
                Assert.Contains("scope=read%3Auser", form);
                events.Add("start");
                return (200, DeviceResponse);
            }
            Assert.Equal("/login/oauth/access_token", request.RequestUri.AbsolutePath);
            Assert.Contains("device_code=fixture-device%2Bcode", form);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", form);
            events.Add("poll");
            return (200, replies.Dequeue());
        });
        var login = new CopilotDeviceLogin(http, (delay, _) =>
        {
            events.Add("wait");
            intervals.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });

        var token = await login.AuthorizeAsync(code =>
        {
            Assert.Equal("ABCD-EFGH", code);
            events.Add("show");
        }, CancellationToken.None);

        Assert.Equal("fixture-copilot-token", token);
        Assert.Equal(new double[] { 5, 5, 10 }, intervals);
        Assert.Equal(new[] { "start", "show", "wait", "poll", "wait", "poll", "wait", "poll" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriesTheSameCodeAfterALostConnectionOrTimeout(bool timeout)
    {
        var polls = 0;
        var waits = new List<TimeSpan>();
        using var http = MakeHttp((request, form) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code") return (200, DeviceResponse);
            Assert.Contains("device_code=fixture-device%2Bcode", form);
            if (++polls == 1)
                throw timeout ? new TaskCanceledException() : new HttpRequestException(HttpRequestError.ConnectionError);
            return (200, TokenResponse);
        });
        var login = new CopilotDeviceLogin(http, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });

        var prompts = 0;
        Assert.Equal("fixture-copilot-token", await login.AuthorizeAsync(_ => prompts++, CancellationToken.None));
        Assert.Equal(1, prompts);
        Assert.Equal(2, polls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, waits);
    }

    [Fact]
    public async Task AnHttpErrorIsNotRetriedAsAConnectionFailure()
    {
        var polls = 0;
        using var http = MakeHttp((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code") return (200, DeviceResponse);
            Assert.Equal(1, ++polls);
            return (503, "{}");
        });

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new CopilotDeviceLogin(http, NoWait).AuthorizeAsync(_ => { }, CancellationToken.None));

        Assert.Contains("HTTP 503", error.Message);
        Assert.Equal(1, polls);
    }

    [Fact]
    public async Task CancelingBetweenPollsAfterALostConnectionStopsSignIn()
    {
        using var cancellation = new CancellationTokenSource();
        var polls = 0;
        using var http = MakeHttp((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code") return (200, DeviceResponse);
            Assert.Equal(1, ++polls);
            throw new HttpRequestException(HttpRequestError.ConnectionError);
        });
        var login = new CopilotDeviceLogin(http, (_, ct) =>
        {
            if (polls > 0) cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            login.AuthorizeAsync(_ => { }, cancellation.Token));
        Assert.Equal(1, polls);
    }

    [Theory]
    [InlineData("access_denied", "denied")]
    [InlineData("expired_token", "expired")]
    [InlineData("invalid_client", "could not finish")]
    public async Task StopsAfterOAuthFailures(string error, string message)
    {
        using var http = MakeHttp((request, _) => request.RequestUri!.AbsolutePath == "/login/device/code"
            ? (200, DeviceResponse)
            : (200, $$"""{"error":"{{error}}"}"""));
        var result = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new CopilotDeviceLogin(http, NoWait).AuthorizeAsync(_ => { }, CancellationToken.None));
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public async Task CancelingAfterThePromptDoesNotPoll()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = MakeHttp((request, _) =>
        {
            Assert.Equal("/login/device/code", request.RequestUri!.AbsolutePath);
            return (200, DeviceResponse);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CopilotDeviceLogin(http).AuthorizeAsync(_ => cancellation.Cancel(), cancellation.Token));
    }

    [Fact]
    public async Task MalformedChallengeNeverOpensThePrompt()
    {
        using var http = MakeHttp((_, _) => (200, """{"device_code":"test","user_code":"<script>","expires_in":900}"""));
        var shown = false;
        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new CopilotDeviceLogin(http, NoWait).AuthorizeAsync(_ => shown = true, CancellationToken.None));
        Assert.False(shown);
    }

    [Fact]
    public async Task WithoutAnAppSignInCopilotDoesNotRequestUsage()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            using var http = MakeHttp((_, _) => throw new InvalidOperationException("No usage request expected."));
            var copilot = new CopilotProvider(http, new ApiKeyStore(Path.Combine(directory.FullName, "keys.bin")));

            Assert.False(await copilot.HasLocalCredentialsAsync());
            var snapshot = await copilot.RefreshAsync();
            Assert.True(snapshot.IsError);
            Assert.Equal("Sign in to GitHub in Customize → Copilot.",
                Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public async Task RejectedReplacementDoesNotOverrideTheSavedGitHubSignIn()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "keys.bin");
            var otherToken = false;
            var usages = new List<string?>();
            using var http = MakeHttp((request, _) =>
            {
                if (request.RequestUri == CopilotProvider.UsageUrl)
                {
                    var token = request.Headers.Authorization?.Parameter;
                    usages.Add(token);
                    return token == "fixture-copilot-token"
                        ? (200, """{"copilot_plan":"pro","quota_snapshots":{"premium_interactions":{"entitlement":100,"percent_remaining":75}}}""")
                        : (403, "{}");
                }
                return request.RequestUri!.AbsolutePath == "/login/device/code"
                    ? (200, DeviceResponse)
                    : (200, otherToken ? """{"access_token":"fixture-rejected-token"}""" : TokenResponse);
            });
            var copilot = new CopilotProvider(http, new ApiKeyStore(path), new CopilotDeviceLogin(http, NoWait));
            var snapshot = await copilot.ConnectAsync("github.com", (_, _) => { }, CancellationToken.None);
            Assert.Equal("Pro", snapshot.Plan);
            Assert.True(copilot.HasAppCredential);
            Assert.DoesNotContain("fixture-copilot-token", Encoding.UTF8.GetString(File.ReadAllBytes(path)));

            otherToken = true;
            var error = await Assert.ThrowsAsync<ProviderRequestException>(() => copilot.ConnectAsync("github.com", (_, _) => { }, CancellationToken.None));
            Assert.Contains("Copilot seat", error.Message);
            var reopened = new CopilotProvider(http, new ApiKeyStore(path));
            Assert.True(reopened.HasAppCredential);
            Assert.True(await reopened.HasLocalCredentialsAsync());
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(new[] { "fixture-copilot-token", "fixture-rejected-token", "fixture-copilot-token" }, usages);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public async Task EnterpriseSeatVerificationAndRefreshUseOnlyTheSavedTenant()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "keys.bin");
            var requests = new List<string>();
            using var http = MakeHttp((request, _) =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                if (request.RequestUri.AbsolutePath == "/copilot_internal/user")
                    Assert.Equal("fixture-copilot-token", request.Headers.Authorization?.Parameter);
                return request.RequestUri.AbsolutePath switch
                {
                    "/login/device/code" => (200, DeviceResponse),
                    "/login/oauth/access_token" => (200, TokenResponse),
                    _ when request.RequestUri.Host == "api.noseat.ghe.com" => (404, "{}"),
                    _ => (200, """{"copilot_plan":"business","quota_snapshots":{"premium_interactions":{"entitlement":100,"percent_remaining":75}}}""")
                };
            });
            var provider = new CopilotProvider(http, new ApiKeyStore(path), new CopilotDeviceLogin(http, NoWait));
            Assert.Equal("https://github.com/settings/copilot", Assert.Single(provider.Provider.VisibleLinks).Url);
            var snapshot = await provider.ConnectAsync("octocorp", (code, url) =>
            {
                Assert.Equal("ABCD-EFGH", code);
                Assert.Equal("https://octocorp.ghe.com/login/device", url.AbsoluteUri);
            }, CancellationToken.None);
            Assert.Equal("Business", snapshot.Plan);
            Assert.Equal("https://octocorp.ghe.com/settings/copilot", Assert.Single(provider.Provider.VisibleLinks).Url);

            var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
                provider.ConnectAsync("noseat.ghe.com", (_, _) => { }, CancellationToken.None));
            Assert.Contains("Copilot seat on noseat.ghe.com", error.Message);

            var reopened = new CopilotProvider(http, new ApiKeyStore(path));
            Assert.Equal("octocorp.ghe.com", reopened.ConnectedHost);
            Assert.Equal("https://octocorp.ghe.com/settings/copilot", Assert.Single(reopened.Provider.VisibleLinks).Url);
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(new[]
            {
                "https://octocorp.ghe.com/login/device/code",
                "https://octocorp.ghe.com/login/oauth/access_token",
                "https://api.octocorp.ghe.com/copilot_internal/user",
                "https://noseat.ghe.com/login/device/code",
                "https://noseat.ghe.com/login/oauth/access_token",
                "https://api.noseat.ghe.com/copilot_internal/user",
                "https://api.octocorp.ghe.com/copilot_internal/user"
            }, requests);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Task NoWait(TimeSpan _, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(403, "X-RateLimit-Remaining", "0", "rate-limiting")]
    [InlineData(403, "Retry-After", "30", "rate-limiting")]
    [InlineData(429, null, null, "rate-limiting")]
    [InlineData(403, "X-RateLimit-Remaining", "15", "Copilot seat")]
    [InlineData(401, "Retry-After", "30", "Copilot seat")]
    [InlineData(403, "Retry-After", "30", "rate-limiting", "octocorp.ghe.com")]
    [InlineData(403, "X-RateLimit-Remaining", "15", "Copilot seat on octocorp.ghe.com", "octocorp.ghe.com")]
    public async Task GitHubUsageDistinguishesRateLimitsFromRejectedSignIn(int status, string? header, string? value,
        string expected, string host = "github.com")
    {
        using var http = MakeHttp((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/user") return (status, "{}");
            return request.RequestUri!.AbsolutePath == "/login/device/code"
                ? (200, DeviceResponse) : (200, TokenResponse);
        }, header, value);
        var path = Path.Combine(Path.GetTempPath(), $"openusage-rate-limit-{Guid.NewGuid():N}.bin");
        var copilot = new CopilotProvider(http, new ApiKeyStore(path), new CopilotDeviceLogin(http, NoWait));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            copilot.ConnectAsync(host, (_, _) => { }, CancellationToken.None));
        Assert.Contains(expected, error.Message);
        Assert.False(File.Exists(path));
    }

    private static AppHttp MakeHttp(Func<HttpRequestMessage, string, (int Status, string Body)> respond,
        string? header = null, string? value = null) =>
        new(new HttpClient(new StubHandler(respond, header, value)));

    private sealed class StubHandler(Func<HttpRequestMessage, string, (int Status, string Body)> respond,
        string? header, string? value) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, body) = respond(request, form);
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (header is not null && request.RequestUri?.AbsolutePath == "/copilot_internal/user")
                response.Headers.TryAddWithoutValidation(header, value);
            return response;
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
