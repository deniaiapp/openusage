using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class ClaudeUpstreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("""{"is_enabled":true,"used_credits":1234,"monthly_limit":5000,"currency":"eur","decimal_places":2}""",
        "eur", 12.34, 50.0, "EUR 12.34 used")]
    [InlineData("""{"is_enabled":true,"used_credits":1234,"currency":"JPY","decimal_places":0}""",
        "jpy", 1234, null, "JPY 1.2K spent")]
    [InlineData("""{"is_enabled":true,"used_credits":1234,"monthly_limit":5000}""",
        "usd", 12.34, 50.0, "$12.34 used")]
    public async Task ExtraUsageKeepsItsBillingCurrencyInTheDashboardCacheAndCli(
        string extraUsage, string unit, double used, double? limit, string label)
    {
        using var dir = new TempDir();
        var body = new JsonObject { ["extra_usage"] = JsonNode.Parse(extraUsage) };
        var (_, lines, _) = ClaudeUsageMapper.Map(body, new ClaudeOAuth());
        using var http = new AppHttp();
        var provider = new ClaudeProvider(http);
        var runtime = new CachedClaudeFixture(provider.Provider, provider.WidgetDescriptors, lines);
        var settings = AppSettings.Load(Path.Combine(dir.Path, "settings.json"));
        settings.EnabledProviders.Add("claude");
        settings.Save();
        var cache = Directory.CreateDirectory(Path.Combine(dir.Path, "cache")).FullName;
        var layout = new LayoutStore(settings, [runtime]);
        var store = new WidgetDataStore(layout, settings, cache);

        await store.RefreshProviderAsync("claude");
        var restored = new WidgetDataStore(layout, settings, cache);
        var data = restored.Resolve(runtime.WidgetDescriptors.Single(d => d.Id == "claude.extra")) with
            { DisplayMode = WidgetDisplayMode.Used };
        Assert.Equal(label, data.IsBounded ? data.Headline : data.UnboundedDetail);
        if (unit == "eur") Assert.Equal("EUR 50 limit", data.BoundedTrailingText(Now));

        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, LimitsCommand.Run(["claude"], dir.Path, output, errors, Now));
        var resource = JsonNode.Parse(output.ToString())!["providers"]!["claude"]!["resources"]!["extraUsage"]!;
        Assert.Equal(unit, resource["unit"]!.GetValue<string>());
        Assert.Equal(used, resource["used"]!.GetValue<double>());
        Assert.Equal(limit, resource["limit"]?.GetValue<double>());
    }

    [Theory]
    [InlineData(999, "KWD 0.001 left", MeterStateKind.Level)]
    [InlineData(1000, "KWD 0 left", MeterStateKind.Spent)]
    public void ExtraUsageUsesDisplayedCurrencyPrecisionToDecideWhenTheLimitIsSpent(
        int usedCredits, string headline, MeterStateKind state)
    {
        var body = new JsonObject
        {
            ["extra_usage"] = new JsonObject
            {
                ["is_enabled"] = true, ["used_credits"] = usedCredits, ["monthly_limit"] = 1000,
                ["currency"] = "KWD", ["decimal_places"] = 3
            }
        };
        var (_, lines, _) = ClaudeUsageMapper.Map(body, new ClaudeOAuth());
        using var http = new AppHttp();
        var descriptor = new ClaudeProvider(http).WidgetDescriptors.Single(d => d.Id == "claude.extra");
        var data = WidgetData.Resolve(descriptor, Assert.Single(lines), WidgetDisplayMode.Remaining,
            ResetDisplayMode.Relative, alwaysShowPacing: false);

        Assert.Equal(headline, data.Headline);
        Assert.Equal(state, data.GetMeterState(Now).Kind);
    }

    private sealed record CachedClaudeFixture(Provider Provider, IReadOnlyList<WidgetDescriptor> WidgetDescriptors,
        IReadOnlyList<MetricLine> Lines) : IProviderRuntime
    {
        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSnapshot.Make(Provider, null, Lines, Now));

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Theory]
    [InlineData(" 600 ", 600)]
    [InlineData("Wed, 14 Oct 2026 12:15:00 GMT", 900)]
    [InlineData("999999999999999999999999", 3600)]
    [InlineData("Wed, 14 Oct 2026 13:15:00 GMT", 3600)]
    [InlineData("Wed, 14 Oct 2026 11:59:00 GMT", null)]
    [InlineData("not a date", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    public void RetryAfter_ParsesPositiveDelaysAndCapsThemAtAnHour(string header, int? seconds)
    {
        var response = new AppHttpResponse(429, [], new Dictionary<string, string> { ["retry-after"] = header });
        Assert.Equal(seconds is { } delay ? TimeSpan.FromSeconds(delay) : (TimeSpan?)null, response.RetryAfter(Now));
    }

    [Fact]
    public async Task Provider_HoldsLastReadingThroughRetryAfterAndResumesAtTheDeadline()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"fixture-access"}}""");
        var clock = Now;
        var handler = new FakeAnthropic();
        using var http = new AppHttp(new HttpClient(handler));
        var provider = new ClaudeProvider(http, store, () => clock);
        var initial = await provider.RefreshAsync();
        Assert.NotNull(initial.Line("Session"));

        handler.RetryAfter = "1200";
        var held = await provider.RefreshAsync();
        Assert.Equal(ClaudeUsageMapper.RateLimitedWarning, held.Warning);
        Assert.Equal(initial.Line("Session"), held.Line("Session"));
        Assert.Equal(2, handler.PlainUsageCalls);

        handler.RetryAfter = null;
        clock = Now.AddMinutes(19);
        Assert.Equal(ClaudeUsageMapper.RateLimitedWarning, (await provider.RefreshAsync()).Warning);
        Assert.Equal(2, handler.PlainUsageCalls);
        clock = Now.AddMinutes(20);
        Assert.Null((await provider.RefreshAsync()).Warning);
        Assert.Equal(3, handler.PlainUsageCalls);
    }

    [Fact]
    public async Task Provider_HonorsTheFirstThrottleWithoutBlockingADifferentCliLogin()
    {
        using var dir = new TempDir();
        var store = new ClaudeAuthStore(dir.Path, readCredentialManager: false);
        File.WriteAllText(store.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"fixture-first"}}""");
        var handler = new FakeAnthropic { RetryAfter = "1800" };
        using var http = new AppHttp(new HttpClient(handler));
        var provider = new ClaudeProvider(http, store, () => Now);
        Assert.True((await provider.RefreshAsync()).IsError);
        Assert.True((await provider.RefreshAsync()).IsError);
        Assert.Equal(1, handler.PlainUsageCalls);

        File.WriteAllText(store.CredentialsPath(), """{"claudeAiOauth":{"accessToken":"fixture-second"}}""");
        handler.RetryAfter = null;
        Assert.False((await provider.RefreshAsync()).IsError);
        Assert.Equal(2, handler.PlainUsageCalls);
    }

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
        public string? RetryAfter;
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
                if (RetryAfter is not null)
                {
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                    throttled.Headers.TryAddWithoutValidation("Retry-After", RetryAfter);
                    return Task.FromResult(throttled);
                }
                body = """{ "five_hour": { "utilization": 10 }, "seven_day": { "utilization": 20 }, "cedar_ember": null }""";
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
