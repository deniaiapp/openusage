using System.Net;
using System.Text;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CopilotDeviceLoginTests
{
    private const string DeviceResponse = """{"device_code":"fixture-device+code","user_code":"ABCD-EFGH","expires_in":900,"interval":5}""";
    private const string TokenResponse = """{"access_token":"fixture-copilot-token","token_type":"bearer"}""";

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
            var snapshot = await copilot.ConnectAsync(_ => { }, CancellationToken.None);
            Assert.Equal("Pro", snapshot.Plan);
            Assert.True(copilot.HasAppCredential);
            Assert.DoesNotContain("fixture-copilot-token", Encoding.UTF8.GetString(File.ReadAllBytes(path)));

            otherToken = true;
            var error = await Assert.ThrowsAsync<ProviderRequestException>(() => copilot.ConnectAsync(_ => { }, CancellationToken.None));
            Assert.Contains("Copilot seat", error.Message);
            var reopened = new CopilotProvider(http, new ApiKeyStore(path));
            Assert.True(reopened.HasAppCredential);
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(new[] { "fixture-copilot-token", "fixture-rejected-token", "fixture-copilot-token" }, usages);
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

    private static AppHttp MakeHttp(Func<HttpRequestMessage, string, (int Status, string Body)> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private sealed class StubHandler(Func<HttpRequestMessage, string, (int Status, string Body)> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, body) = respond(request, form);
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
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
