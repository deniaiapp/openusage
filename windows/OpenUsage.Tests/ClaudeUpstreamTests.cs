using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class ClaudeUpstreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // MARK: - Rate Limit Resets (cedar_ember)

    [Fact]
    public void ResetGrants_CountsRemainingResetsWithOneExpiryEach()
    {
        var block = JsonNode.Parse("""
            { "eligible": true, "grants": [
                { "resets_left": 2, "ends_at": "2026-10-20T00:00:00Z" },
                { "resets_left": 1, "ends_at": "2026-10-01T00:00:00Z" },
                { "resets_left": 0, "ends_at": "2026-10-30T00:00:00Z" },
                { "resets_left": 1 }
            ] }
            """);
        var line = Assert.IsType<MetricLine.ValuesLine>(ClaudeUsageMapper.ResetGrantsLine(block, Now));
        Assert.Equal("Rate Limit Resets", line.Label);
        Assert.Equal(3, line.Items[0].Number);
        Assert.Equal("available", line.Items[0].Label);
        Assert.Equal(2, line.ExpiriesAt!.Count);
        Assert.All(line.ExpiriesAt, e => Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), e));
    }

    [Fact]
    public void ResetGrants_IneligibleReadsZeroAndMissingBlockHasNoRow()
    {
        var ineligible = ClaudeUsageMapper.ResetGrantsLine(JsonNode.Parse("""{ "eligible": false, "grants": [] }"""), Now);
        Assert.Equal(0, Assert.IsType<MetricLine.ValuesLine>(ineligible).Items[0].Number);
        Assert.Null(ClaudeUsageMapper.ResetGrantsLine(null, Now));
    }

    [Theory]
    [InlineData("""[{"resets_left":1001,"ends_at":"2026-10-20T00:00:00Z"}]""")]
    [InlineData("""[{"resets_left":600},{"resets_left":401}]""")]
    [InlineData("""[{"resets_left":1e20}]""")]
    [InlineData("""[{"resets_left":"unavailable"}]""")]
    public void ResetGrants_RejectsInvalidCountsBeforeAllocatingDeadlines(string grants)
    {
        var block = new JsonObject { ["eligible"] = true, ["grants"] = JsonNode.Parse(grants) };
        var failure = Assert.Throws<InvalidOperationException>(() => ClaudeUsageMapper.ResetGrantsLine(block, Now));
        Assert.Equal(ProviderErrors.InvalidResponse, failure.Message);
    }

    [Fact]
    public void HeldResetGrants_DropResetsPastTheirDeadline()
    {
        var held = MetricLine.Values("Rate Limit Resets",
            [new MetricValue(3, MetricKind.Count, "available")],
            expiriesAt: [Now.AddHours(-1), Now.AddDays(2)]);
        var line = Assert.IsType<MetricLine.ValuesLine>(ClaudeUsageMapper.DroppingElapsedResetGrants(held, Now));
        Assert.Equal(2, line.Items[0].Number);
        Assert.Equal([Now.AddDays(2)], line.ExpiriesAt);
    }

    [Fact]
    public void ResetGrantsUrl_AddsClaudeCodeQuery()
    {
        Assert.Equal("https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1",
            ClaudeProvider.ResetGrantsUrl("https://api.anthropic.com/api/oauth/usage").ToString());
        Assert.Equal("https://api.anthropic.com/api/oauth/profile",
            ClaudeProvider.ProfileUrl("https://api.anthropic.com/api/oauth/usage").ToString());
    }

    // MARK: - Live plan badge

    [Theory]
    [InlineData("max", null, "Max")]
    [InlineData("max", "default_claude_max_5x", "Max")]
    [InlineData("max", "default_claude_max_20x", "Ultra")]
    [InlineData("max", "default_claude_ultra", "Ultra")]
    [InlineData("ultra", "default_claude_max_20x", "Ultra")]
    [InlineData("team", "default_claude_team_5x", "Team 5x")]
    public void PlanBadgeUsesCurrentMaxAndUltraNames(string plan, string? tier, string expected) =>
        Assert.Equal(expected, ClaudeUsageMapper.FormatPlan(plan, tier));

    [Fact]
    public void LivePlan_ReadsOrganizationTypeAndTier()
    {
        var stored = new ClaudeOAuth { SubscriptionType = "max", RateLimitTier = "default_claude_max_5x" };
        var profile = JsonUtil.ParseObject("""
            { "account": { "uuid": "a" },
              "organization": { "uuid": "o", "organization_type": "claude_max", "rate_limit_tier": "default_claude_max_20x" } }
            """)!;
        Assert.Equal("Ultra", ClaudeUsageMapper.FormatLivePlan(profile, stored));

        var partial = JsonUtil.ParseObject("""{ "organization": { "uuid": "o", "organization_type": "claude_pro" } }""")!;
        Assert.Equal("Pro 5x", ClaudeUsageMapper.FormatLivePlan(partial, stored));
        Assert.Null(ClaudeUsageMapper.FormatLivePlan(JsonUtil.ParseObject("""{ "account": { "uuid": "a" } }""")!, stored));
    }

    [Theory]
    [InlineData("claude_enterprise", null, true, "Spend")]
    [InlineData("claude_enterprise", "default_claude_enterprise_5x", false, "Spend")]
    [InlineData("claude_max", "default_claude_max_5x", true, "Extra Usage")]
    [InlineData(null, null, true, "Extra Usage")]
    public void SpendTitleUsesTheCachedPlanWithoutChangingThePin(
        string? organizationType, string? tier, bool bounded, string title)
    {
        using var dir = new TempDir();
        using var http = new AppHttp(new HttpClient());
        var provider = new ClaudeProvider(http, new ClaudeAuthStore(dir.Path, readCredentialManager: false));
        var settings = AppSettings.Load(Path.Combine(dir.Path, "settings.json"));
        settings.EnabledProviders.Add("claude");
        settings.EnabledMetrics.Add("claude.extra");
        settings.PinnedMetrics.Add("claude.extra");
        var layout = new LayoutStore(settings, [provider]);
        var descriptor = Assert.IsType<WidgetDescriptor>(layout.Descriptor("claude.extra"));
        var data = new WidgetDataStore(layout, settings, dir.Path);
        Assert.Equal("Extra Usage", data.Resolve(descriptor).Title);
        var oauth = new ClaudeOAuth();
        var profile = new JsonObject
        {
            ["organization"] = new JsonObject { ["organization_type"] = organizationType, ["rate_limit_tier"] = tier }
        };
        var usage = new JsonObject
        {
            ["extra_usage"] = new JsonObject { ["is_enabled"] = true, ["used_credits"] = 1234 }
        };
        if (bounded) usage["extra_usage"]!["monthly_limit"] = 5000;
        var lines = ClaudeUsageMapper.Map(usage, oauth).Lines;
        data.ReplaceAccountSnapshot(ProviderSnapshot.Make(provider.Provider,
            ClaudeUsageMapper.FormatLivePlan(profile, oauth), lines, Now));

        var restored = new WidgetDataStore(layout, settings, dir.Path);
        var pin = Assert.Single(restored.PinnedTiles());
        Assert.Equal("claude.extra", pin.Descriptor.Id);
        Assert.Equal("Extra usage spent", pin.Descriptor.MetricLabel);
        Assert.Equal(title, data.Resolve(descriptor).Title);
        Assert.Equal(title, pin.Data.Title);
        Assert.Equal(bounded, pin.Data.IsBounded);
    }

    // MARK: - Credential document preservation

    [Fact]
    public void SaveRotated_KeepsMcpLoginsAndUnknownFields()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(), """
            { "claudeAiOauth": { "accessToken": "old-access", "refreshToken": "old-refresh", "expiresAt": 1,
                                 "subscriptionType": "max", "futureField": { "keep": true } },
              "mcpOAuth": { "server": { "accessToken": "mcp-token" } } }
            """);
        var oauth = Assert.Single(store.LoadCandidates());
        oauth.AccessToken = "new-access";
        oauth.RefreshToken = "new-refresh";
        oauth.ExpiresAt = 1_800_000_000_000;

        Assert.True(store.SaveRotated(oauth, "old-refresh"));
        var saved = JsonUtil.ParseObject(File.ReadAllText(store.CredentialsPath()))!;
        var stored = saved["claudeAiOauth"]!.AsObject();
        Assert.Equal("new-access", JsonUtil.String(stored["accessToken"]));
        Assert.Equal("new-refresh", JsonUtil.String(stored["refreshToken"]));
        Assert.Equal(1_800_000_000_000, JsonUtil.Number(stored["expiresAt"]));
        Assert.Equal("max", JsonUtil.String(stored["subscriptionType"]));
        Assert.True(JsonUtil.Bool(stored["futureField"]!["keep"]));
        Assert.Equal("mcp-token", JsonUtil.String(saved["mcpOAuth"]!["server"]!["accessToken"]));
    }

    [Fact]
    public void SaveRotated_LeavesANewerLoginAlone()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        var original = """{ "claudeAiOauth": { "accessToken": "theirs", "refreshToken": "their-refresh" } }""";
        File.WriteAllText(store.CredentialsPath(), original);
        var rotated = new ClaudeOAuth { AccessToken = "ours", RefreshToken = "our-refresh" };

        Assert.False(store.SaveRotated(rotated, "spent-refresh"));
        Assert.Equal(original, File.ReadAllText(store.CredentialsPath()));
    }

    // MARK: - Provider cadence

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task Provider_RetriesFirstResetGrantsReadIfItCouldNotConnect(bool lostConnection, int expectedCalls)
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"fixture-token"}}""");
        var handler = new FakeAnthropic
        {
            DisconnectResetGrants = lostConnection,
            FailResetGrants = !lostConnection
        };
        var clock = Now;
        using var http = new AppHttp(new HttpClient(handler));
        var provider = new ClaudeProvider(http, store, () => clock);

        var first = await provider.RefreshAsync();
        Assert.NotNull(first.Line("Session"));
        Assert.Null(first.Line("Rate Limit Resets"));
        Assert.Equal(1, handler.ResetGrantCalls);

        handler.DisconnectResetGrants = false;
        handler.FailResetGrants = false;
        clock = Now.AddMinutes(5);
        var next = await provider.RefreshAsync();
        Assert.Equal(lostConnection, next.Line("Rate Limit Resets") is not null);
        Assert.Equal(expectedCalls, handler.ResetGrantCalls);

        clock = Now.AddMinutes(10);
        await provider.RefreshAsync();
        Assert.Equal(expectedCalls, handler.ResetGrantCalls);
    }

    [Fact]
    public async Task Provider_ChecksResetGrantsHourlyAndThePlanOncePerToken()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(),
            """{ "claudeAiOauth": { "accessToken": "token", "refreshToken": "refresh", "subscriptionType": "max", "rateLimitTier": "default_claude_max_5x" } }""");
        var handler = new FakeAnthropic();
        var clock = Now;
        var provider = new ClaudeProvider(new AppHttp(new HttpClient(handler)), store, () => clock);

        var first = await provider.RefreshAsync();
        Assert.Equal("Ultra", first.Plan);
        Assert.Equal(1, Assert.IsType<MetricLine.ValuesLine>(first.Line("Rate Limit Resets")).Items[0].Number);
        Assert.Equal(1, handler.ResetGrantCalls);
        Assert.Equal(1, handler.ProfileCalls);
        Assert.All(handler.UsageAgents, ua => Assert.Equal(ClaudeProvider.UserAgent, ua));

        // Within the hour: plain usage only, held resets row and plan.
        clock = Now.AddMinutes(30);
        handler.FailResetGrants = true;
        var second = await provider.RefreshAsync();
        Assert.NotNull(second.Line("Rate Limit Resets"));
        Assert.Equal("Ultra", second.Plan);
        Assert.Equal(1, handler.ResetGrantCalls);
        Assert.Equal(1, handler.ProfileCalls);
        Assert.Equal(2, handler.PlainUsageCalls);

        // After the hour a failed check keeps the previous row and waits another hour.
        clock = Now.AddMinutes(61);
        var third = await provider.RefreshAsync();
        Assert.NotNull(third.Line("Rate Limit Resets"));
        Assert.Equal(2, handler.ResetGrantCalls);
        clock = Now.AddMinutes(90);
        await provider.RefreshAsync();
        Assert.Equal(2, handler.ResetGrantCalls);
    }

    [Fact]
    public async Task Provider_KeepsLastKnownResetsWhenTheNextCountIsInvalid()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"token"}}""");
        var handler = new FakeAnthropic();
        var clock = Now;
        var provider = new ClaudeProvider(new AppHttp(new HttpClient(handler)), store, () => clock);

        var first = await provider.RefreshAsync();
        Assert.Equal(1, Assert.IsType<MetricLine.ValuesLine>(first.Line(ClaudeUsageMapper.RateLimitResetsLabel)).Items[0].Number);

        clock = Now.AddMinutes(61);
        handler.ResetCount = "1e20";
        var second = await provider.RefreshAsync();
        Assert.Equal(2, handler.ResetGrantCalls);
        var held = Assert.IsType<MetricLine.ValuesLine>(second.Line(ClaudeUsageMapper.RateLimitResetsLabel));
        Assert.Equal(1, held.Items[0].Number);
        Assert.Single(held.ExpiriesAt!);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("openusage-claude-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class FakeAnthropic : HttpMessageHandler
    {
        public int PlainUsageCalls;
        public int ResetGrantCalls;
        public int ProfileCalls;
        public bool FailResetGrants;
        public bool DisconnectResetGrants;
        public string ResetCount = "1";
        public List<string> UsageAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string body;
            var status = HttpStatusCode.OK;
            if (uri.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal))
            {
                ProfileCalls++;
                body = """{ "account": { "uuid": "a" }, "organization": { "uuid": "o", "organization_type": "claude_max", "rate_limit_tier": "default_claude_max_20x" } }""";
            }
            else if (uri.Query.Contains("cedar_ember=1", StringComparison.Ordinal))
            {
                ResetGrantCalls++;
                UsageAgents.Add(string.Join(" ", request.Headers.GetValues("User-Agent")));
                if (DisconnectResetGrants)
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Fixture connection failure");
                status = FailResetGrants ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK;
                body = $$"""{ "cedar_ember": { "eligible": true, "grants": [ { "resets_left": {{ResetCount}}, "ends_at": "2026-12-01T00:00:00Z" } ] } }""";
            }
            else
            {
                PlainUsageCalls++;
                UsageAgents.Add(string.Join(" ", request.Headers.GetValues("User-Agent")));
                body = """{ "five_hour": { "utilization": 10 }, "seven_day": { "utilization": 20 }, "cedar_ember": null }""";
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
