using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubAccountRuntimeTests
{
    private static readonly CliProxyHub Hub = new(Guid.NewGuid(), new Uri("https://hub.example/proxy/"));

    [Theory]
    [InlineData("claude", "Session", 10)]
    [InlineData("codex", "Session", 31)]
    [InlineData("antigravity", "Session", 25)]
    [InlineData("devin", "Weekly quota", 52)]
    [InlineData("grok", "Weekly limit", 68)]
    public async Task RefreshMapsProviderUsageThroughTheLinkedLogin(string providerId, string metric, double used)
    {
        using var handler = new FixtureHub();
        using var client = new CliProxyHubClient(new AppHttp(new HttpClient(handler)));
        var account = Account(providerId);
        using var runtime = new CliProxyHubAccountRuntime(client, Hub, account, () => "fixture-management-key");

        Assert.True(await runtime.HasLocalCredentialsAsync());
        var snapshot = await runtime.RefreshAsync();

        Assert.False(snapshot.IsError);
        Assert.Equal($"{providerId}@{account.AccountId:N}", snapshot.ProviderId);
        Assert.Equal(runtime.Provider.DisplayName, snapshot.DisplayName);
        Assert.EndsWith(" · Fixture Account", snapshot.DisplayName);
        Assert.All(runtime.WidgetDescriptors, descriptor =>
        {
            Assert.Equal(snapshot.ProviderId, descriptor.ProviderId);
            Assert.StartsWith(snapshot.ProviderId + ".", descriptor.Id);
        });
        Assert.Equal(used, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line(metric)).Used);
        Assert.NotEmpty(handler.Calls);
        Assert.All(handler.Calls, call =>
        {
            Assert.Equal(account.Link.AuthIndex, (string?)call["auth_index"]);
            Assert.DoesNotContain("fixture-management-key", call.ToJsonString());
            if (providerId == "devin")
            {
                Assert.Equal("POST", (string?)call["method"]);
                Assert.Equal("$TOKEN$", (string?)JsonNode.Parse((string)call["data"]!)?["metadata"]?["apiKey"]);
            }
            else
            {
                Assert.Equal("Bearer $TOKEN$", (string?)call["header"]?["Authorization"]);
                if (providerId == "codex")
                    Assert.Equal("fixture-workspace", (string?)call["header"]?["ChatGPT-Account-Id"]);
                if (providerId == "antigravity")
                    Assert.Equal("{}", (string?)call["data"]);
            }
        });
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("antigravity")]
    [InlineData("devin")]
    [InlineData("grok")]
    public async Task RefusedProviderTokenPromptsHubSignInWithoutRenewingItLocally(string providerId)
    {
        using var handler = new FixtureHub(401);
        using var client = new CliProxyHubClient(new AppHttp(new HttpClient(handler)));
        using var runtime = new CliProxyHubAccountRuntime(client, Hub, Account(providerId), () => "fixture-management-key");

        var snapshot = await runtime.RefreshAsync();

        Assert.True(snapshot.IsError);
        Assert.Contains("Sign it in again on the hub", Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
        Assert.Equal(providerId == "antigravity" ? 2 : 1, handler.Calls.Count);
    }

    [Fact]
    public async Task ReadsTheCurrentKeyAndStopsUsingADisconnectedLogin()
    {
        using var handler = new FixtureHub();
        using var client = new CliProxyHubClient(new AppHttp(new HttpClient(handler)));
        string? key = "fixture-management-key";
        using var runtime = new CliProxyHubAccountRuntime(client, Hub, Account("devin"), () => key);

        Assert.False((await runtime.RefreshAsync()).IsError);
        key = "fixture-replacement-key";
        Assert.False((await runtime.RefreshAsync()).IsError);
        key = null;
        Assert.False(await runtime.HasLocalCredentialsAsync());
        Assert.True((await runtime.RefreshAsync()).IsError);
        Assert.Equal(["fixture-management-key", "fixture-replacement-key"], handler.ManagementKeys);
    }

    [Fact]
    public async Task RejectingTheManagementKeyIsNotAProviderSignInError()
    {
        using var handler = new FixtureHub(hubStatus: HttpStatusCode.Unauthorized);
        using var client = new CliProxyHubClient(new AppHttp(new HttpClient(handler)));
        using var runtime = new CliProxyHubAccountRuntime(client, Hub, Account("antigravity"), () => "fixture-management-key");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await runtime.RefreshAsync();
            Assert.True(snapshot.IsError);
            Assert.Contains("Replace it in Customize", Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
        }
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task TwoLoginsOnTheSameHubUseTheirOwnIndexAndWorkspace()
    {
        using var handler = new FixtureHub();
        using var client = new CliProxyHubClient(new AppHttp(new HttpClient(handler)));
        var first = Account("codex");
        var second = Account("codex") with
        {
            Link = new(Hub.Id, "second-login", "second-index", "second-workspace")
        };
        using var firstRuntime = new CliProxyHubAccountRuntime(client, Hub, first, () => "fixture-management-key");
        using var secondRuntime = new CliProxyHubAccountRuntime(client, Hub, second, () => "fixture-management-key");

        Assert.False((await firstRuntime.RefreshAsync()).IsError);
        Assert.False((await secondRuntime.RefreshAsync()).IsError);
        Assert.NotEqual(firstRuntime.Provider.Id, secondRuntime.Provider.Id);
        var usageCalls = handler.Calls.Where(call => ((string)call["url"]!).EndsWith("/usage", StringComparison.Ordinal));
        Assert.Equal(
            [("fixture-index", "fixture-workspace"), ("second-index", "second-workspace")],
            usageCalls.Select(call => ((string?)call["auth_index"], (string?)call["header"]?["ChatGPT-Account-Id"])));
    }

    private static CliProxyMirroredAccount Account(string providerId) =>
        new(Guid.NewGuid(), providerId, "Fixture Account", new(Hub.Id, "fixture-login", "fixture-index", "fixture-workspace"));

    private sealed class FixtureHub(int status = 200, HttpStatusCode hubStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<JsonObject> Calls { get; } = [];
        public List<string?> ManagementKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://hub.example/proxy/v0/management/api-call", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var key = request.Headers.Authorization!.Parameter!;
            ManagementKeys.Add(key);
            var call = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Calls.Add(call);
            if (hubStatus != HttpStatusCode.OK) return new HttpResponseMessage(hubStatus);
            var upstream = new Uri((string)call["url"]!);
            var body = upstream.Host switch
            {
                "api.anthropic.com" when upstream.AbsolutePath.EndsWith("/usage") => """{"five_hour":{"utilization":10}}""",
                "api.anthropic.com" when upstream.AbsolutePath.EndsWith("/profile") => "{}",
                "chatgpt.com" when upstream.AbsolutePath.EndsWith("/usage") => """{"rate_limit":{"primary_window":{"used_percent":31}}}""",
                "chatgpt.com" when upstream.AbsolutePath.EndsWith("/rate-limit-reset-credits") ||
                    upstream.AbsolutePath.EndsWith("/daily-workspace-usage-counts") => "{}",
                "daily-cloudcode-pa.googleapis.com" or "cloudcode-pa.googleapis.com"
                    when upstream.AbsolutePath == "/v1internal:retrieveUserQuotaSummary" =>
                    """{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.75}]}]}""",
                "server.codeium.com" when upstream.AbsolutePath.EndsWith("/GetUserStatus") =>
                    """{"userStatus":{"planStatus":{"weeklyQuotaRemainingPercent":48,"planInfo":{"planName":"Fixture Plan"}}}}""",
                "cli-chat-proxy.grok.com" when upstream.AbsolutePath == "/v1/billing" =>
                    """{"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-01-01T00:00:00Z","end":"2026-01-08T00:00:00Z"},"creditUsagePercent":68}}""",
                "cli-chat-proxy.grok.com" when upstream.AbsolutePath == "/v1/settings" => "{}",
                _ => throw new InvalidOperationException($"Unexpected fixture route: {upstream.Host}{upstream.AbsolutePath}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status_code = status, body }),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
