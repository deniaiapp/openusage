using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Support;

namespace OpenUsage.Tests;

/// <summary>Ports of upstream Swift pricing, Cursor, and Grok regression tests.</summary>
public class CursorGrokPricingUpstreamTests
{
    private static readonly ModelPricing Pricing = ModelPricing.Load();

    private static double[] Rates(ModelRates? rates)
    {
        Assert.NotNull(rates);
        return [rates!.InputPerMillion, rates.CacheWritePerMillion, rates.CacheReadPerMillion, rates.OutputPerMillion];
    }

    // ---- Pricing supplement ----

    [Theory]
    [InlineData("gpt-5.6-sol-ultra", 4)]
    [InlineData("gpt-5.6-sol-ultra-fast", 8)]
    [InlineData("cursor-grok-4.7-high", 2)]
    [InlineData("grok-4-7-slow", 2)]
    [InlineData("grok-4-7-slow-xhigh", 2)]
    [InlineData("grok-4.7-xhigh-fast", 4)]
    [InlineData("cursor-grok-4.7-high-fast", 4)]
    [InlineData("grok-4-7-xhigh", 2)]
    [InlineData("grok-4.7-500k", 4)]
    [InlineData("grok-4.7-500k-fast", 6)]
    [InlineData("grok-4.7[500k]-high-fast", 6)]
    [InlineData("grok-bot-default", 4)]
    [InlineData("grok-bot-automation", 2)]
    [InlineData("grok-bot-cua", 2)]
    public void KnownCursorSlugsPriceCorrectly(string model, double expectedInput)
    {
        Assert.Equal(expectedInput, Pricing.Resolve(model)?.InputPerMillion);
    }

    [Fact]
    public void ClaudeSonnet55PricingAndAliases()
    {
        var sonnet55 = Pricing.Resolve("claude-sonnet-5-5");
        Assert.Equal([2.0, 2.5, 0.2, 10.0], Rates(sonnet55));
        foreach (var slug in new[] { "claude-sonnet-5.5", "claude-sonnet-5-5-low", "claude-sonnet-5-5-high", "claude-sonnet-5-5-thinking-max", "claude-sonnet-5-5[1m]" })
            Assert.Equal(Rates(sonnet55), Rates(Pricing.Resolve(slug)));
        Assert.NotEqual(Rates(sonnet55), Rates(Pricing.Resolve("claude-sonnet-5")));
    }

    [Fact]
    public void ClaudeOpus55PricingAliasesAndFastMode()
    {
        var standard = Pricing.Resolve("claude-opus-5-5");
        Assert.Equal([4.0, 5.0, 0.2, 20.0], Rates(standard));
        Assert.Equal(2.0, standard!.FastMultiplier);
        foreach (var slug in new[] { "claude-opus-5.5", "claude-opus-5-5[1m]", "claude-opus-5-5-thinking-xhigh", "claude-5.5-opus-high-thinking" })
            Assert.Equal(Rates(standard), Rates(Pricing.Resolve(slug)));

        var fast = Pricing.Resolve("claude-opus-5-5-fast");
        Assert.Equal([8.0, 10.0, 0.4, 40.0], Rates(fast));
        Assert.Equal(Rates(fast), Rates(Pricing.Resolve("claude-opus-5.5-thinking-high-fast")));
        Assert.Equal(Rates(fast), Rates(Pricing.Resolve("claude-5.5-opus-high-thinking-fast")));

        var tokens = new TokenBreakdown(Input: 1_000_000, CacheWrite5m: 1_000_000, CacheRead: 1_000_000, Output: 1_000_000);
        Assert.Equal(standard.CostDollars(tokens) * 2, standard.CostDollars(tokens with { IsFast = true }), 6);
    }

    [Theory]
    [InlineData("gpt-6.1-sol", 2, 2.5, 0.1, 10)]
    [InlineData("gpt-6-sol", 2, 2.5, 0.2, 10)]
    [InlineData("gpt-6-luna", 0.1, 0.125, 0.01, 0.5)]
    public void Gpt6SolModelsPricingAndAliases(string model, double input, double cacheWrite, double cacheRead, double output)
    {
        var baseRates = Pricing.Resolve(model);
        Assert.Equal([input, cacheWrite, cacheRead, output], Rates(baseRates));
        Assert.Equal(Rates(baseRates), Rates(Pricing.Resolve($"{model}-high")));
        Assert.Equal(model, Pricing.CanonicalName($"{model}-high"));
        Assert.Equal($"{model}-fast", Pricing.CanonicalName($"{model}-high-fast"));
        var fast = Pricing.Resolve($"{model}-high-fast");
        Assert.Equal(input * 2, fast!.InputPerMillion, 9);
        Assert.Equal(output * 2, fast.OutputPerMillion, 9);
    }

    [Fact]
    public void Gpt56SolUsesPromotionalRates()
    {
        Assert.Equal([4.0, 5.0, 0.4, 20.0], Rates(Pricing.Resolve("gpt-5.6-sol")));
    }

    [Fact]
    public void Grok47TiersAndAliases()
    {
        Assert.Equal([2.0, 2.0, 0.5, 6.0], Rates(Pricing.Resolve("grok-4.7-high")));
        Assert.Equal([4.0, 4.0, 1.0, 12.0], Rates(Pricing.Resolve("grok-4.7-fast")));
        var longContext = Pricing.Resolve("grok-4.7-500k");
        var longContextFast = Pricing.Resolve("grok-4.7-500k-fast");
        Assert.Equal([4.0, 4.0, 1.0, 12.0], Rates(longContext));
        Assert.Equal([6.0, 6.0, 1.5, 18.0], Rates(longContextFast));
        foreach (var alias in new[] { "grok-4.7-500k-high", "grok-4-7-500k", "grok-4.7[500k]", "cursor-grok-4.7-500k-xhigh" })
            Assert.Equal("grok-4.7-500k", Pricing.CanonicalName(alias));
        foreach (var alias in new[] { "grok-4.7-500k-fast-high", "grok-4.7-500k-high-fast", "grok-4.7[500k]-fast", "cursor-grok-4.7-500k-fast-xhigh" })
            Assert.Equal("grok-4.7-500k-fast", Pricing.CanonicalName(alias));
    }

    [Fact]
    public void GrokBotModesUseDistinctPricing()
    {
        foreach (var (bot, canonical) in new[]
                 {
                     ("grok-bot-default", "grok-4.6-fast"),
                     ("grok-bot-automation", "grok-4.6"),
                     ("grok-bot-cua", "grok-4.7")
                 })
        {
            Assert.Equal(canonical, Pricing.CanonicalName(bot));
            Assert.Equal(Rates(Pricing.Resolve(canonical)), Rates(Pricing.Resolve(bot)));
        }
        Assert.Equal("grok-bot-unknown", Pricing.CanonicalName("grok-bot-unknown"));
        Assert.Null(Pricing.Resolve("grok-bot-unknown"));
    }

    [Fact]
    public void GeminiNoneEffortAcceptsNShorthand()
    {
        Assert.Equal("gemini-3.8-flash", Pricing.CanonicalName("gemini-3.8-flash-n"));
        Assert.Equal("gemini-2.5-flash", Pricing.CanonicalName("gemini-2.5-flash-n"));
        Assert.Equal("gemini-3.1-pro-preview", Pricing.CanonicalName("gemini-3.1-pro-n"));
        Assert.Equal(Rates(Pricing.Resolve("gemini-3.8-flash")), Rates(Pricing.Resolve("gemini-3.8-flash-n")));
    }

    [Theory]
    [InlineData("Opus 5.5 (Auto Balanced)", "claude-opus-5-5")]
    [InlineData("Claude Opus 5.5 Fast (Auto)", "claude-opus-5-5-fast")]
    [InlineData("Sonnet 5.5 (Auto Balanced)", "claude-sonnet-5-5")]
    [InlineData("Grok 4.7 (Auto Intelligence)", "grok-4.7")]
    [InlineData("Cursor Grok 4.7 Fast (Auto)", "grok-4.7-fast")]
    [InlineData("Grok 4.7 500k (Auto Intelligence)", "grok-4.7-500k")]
    [InlineData("Cursor Grok 4.7 500k Fast (Auto)", "grok-4.7-500k-fast")]
    [InlineData("GPT-6 Sol (Auto)", "gpt-6-sol")]
    [InlineData("GPT-6.1 Sol (Auto)", "gpt-6.1-sol")]
    [InlineData("GPT-6 Luna (Auto)", "gpt-6-luna")]
    public void CursorRouterLabelsMapToCanonicalModels(string label, string canonical)
    {
        Assert.Equal(canonical, Pricing.CanonicalName(label));
    }

    // ---- Cursor spend from usage history ----

    [Theory]
    [InlineData(1)]
    [InlineData(1_000)]
    public void CursorGrokBotModelsUseTheirMappedRates(int scale)
    {
        var tokens = new TokenBreakdown(Input: 1_000 * scale, CacheWrite5m: 2_000 * scale, CacheRead: 3_000 * scale, Output: 4_000 * scale);
        foreach (var (model, cost) in new[] { ("grok-bot-default", 0.063), ("grok-bot-automation", 0.0315), ("grok-bot-cua", 0.0315) })
            Assert.Equal(cost * scale, Pricing.EstimatedCost(model, tokens, applyLongContext: false)!.Value, 9);
    }

    [Fact]
    public void CursorProviderExposesUsageTrendAndSpendTiles()
    {
        var ids = new CursorProvider(new AppHttp()).WidgetDescriptors.Select(d => d.Id).ToList();
        Assert.Equal(["cursor.trend", "cursor.today", "cursor.yesterday", "cursor.last30"], ids.TakeLast(4));
    }

    // ---- Cursor team usage pools ----

    private static (string? Plan, List<MetricLine> Lines) MapTeam(string planUsageJson)
    {
        var usage = JsonUtil.ParseObject($$"""
            {
              "enabled": true, "planUsage": {{planUsageJson}},
              "billingCycleStart": 1770000000000, "billingCycleEnd": 1772592000000,
              "spendLimitUsage": { "limitType": "team", "pooledUsed": 0 }
            }
            """)!;
        return CursorUsageMapper.Map(usage, "Team", null, 0);
    }

    private static void AssertPercent(List<MetricLine> lines, string label, double expected)
    {
        var line = Assert.IsType<MetricLine.ProgressLine>(lines.Single(l => l.Label == label));
        Assert.Equal(expected, line.Used, 6);
        Assert.Equal(100, line.Limit);
        Assert.Equal(ProgressFormat.Percent, line.Format);
    }

    [Fact]
    public void CursorTeamSeatUsesStructuredTotalInsteadOfLegacyDollars()
    {
        var mapped = MapTeam("""
            { "totalSpend": 1022, "includedSpend": 1022, "remaining": 978, "limit": 2000,
              "autoPercentUsed": 2.6476190476190475, "apiPercentUsed": 11.65, "totalPercentUsed": 4.088 }
            """);
        AssertPercent(mapped.Lines, "Total usage", 4.088);
        AssertPercent(mapped.Lines, "Cursor Models", 2.6476190476190475);
        AssertPercent(mapped.Lines, "Other Models", 11.65);
    }

    [Fact]
    public void CursorTeamSeatWithoutTotalDoesNotInventOne()
    {
        var mapped = MapTeam("""{ "limit": 2000, "totalSpend": 666, "autoPercentUsed": 0.3728571428571429, "apiPercentUsed": 2.025 }""");
        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Total usage");
        AssertPercent(mapped.Lines, "Cursor Models", 0.3728571428571429);
        AssertPercent(mapped.Lines, "Other Models", 2.025);
    }

    [Fact]
    public void CursorPoolsWithoutLimitStillMap()
    {
        var usage = JsonUtil.ParseObject("""
            { "enabled": true, "planUsage": { "autoPercentUsed": 0, "apiPercentUsed": 0 },
              "spendLimitUsage": { "limitType": "team" } }
            """)!;
        var mapped = CursorUsageMapper.Map(usage, null, null, 0);
        AssertPercent(mapped.Lines, "Cursor Models", 0);
        AssertPercent(mapped.Lines, "Other Models", 0);
    }

    [Fact]
    public void CursorLegacyPoolWithZeroPlaceholdersKeepsDollarMeter()
    {
        var mapped = MapTeam("""{ "limit": 500000, "totalSpend": 234500, "autoPercentUsed": 0, "apiPercentUsed": 0, "totalPercentUsed": 0 }""");
        var total = Assert.IsType<MetricLine.ProgressLine>(mapped.Lines.Single(l => l.Label == "Total usage"));
        Assert.Equal(2345, total.Used);
        Assert.Equal(5000, total.Limit);
        Assert.Equal(ProgressFormat.Dollars, total.Format);
    }

    [Theory]
    [InlineData("""{ "autoPercentUsed": 1 }""")]
    [InlineData("""{ "autoPercentUsed": true, "apiPercentUsed": 2 }""")]
    [InlineData("""{ "autoPercentUsed": -1, "apiPercentUsed": 2 }""")]
    public void CursorIncompleteOrMalformedPoolsKeepLegacyDollars(string pools)
    {
        var plan = JsonUtil.ParseObject(pools)!;
        plan["limit"] = 500000;
        plan["totalSpend"] = 234500;
        var mapped = MapTeam(plan.ToJsonString());
        var total = Assert.IsType<MetricLine.ProgressLine>(mapped.Lines.Single(l => l.Label == "Total usage"));
        Assert.Equal(ProgressFormat.Dollars, total.Format);
    }

    [Fact]
    public void CursorTeamNameAloneRecognizesPoolsAndKeepsExtraUsageAndCredits()
    {
        var usage = JsonUtil.ParseObject("""
            { "enabled": true, "planUsage": { "limit": 2000, "autoPercentUsed": 0, "apiPercentUsed": 12 },
              "spendLimitUsage": { "individualLimit": 5000, "individualRemaining": 4000 } }
            """)!;
        var mapped = CursorUsageMapper.Map(usage, " Team ", null, 1000);
        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Total usage");
        Assert.Contains(mapped.Lines, l => l.Label == "On-demand");
        Assert.Contains(mapped.Lines, l => l.Label == "Credits");
    }

    // ---- Cursor usage events parsing ----

    [Fact]
    public void CursorUsageEventsParseTokenBucketsTimestampsAndRejectedEvents()
    {
        var page = CursorUsageEvents.ParsePage(Encoding.UTF8.GetBytes("""
            {
              "totalUsageEventsCount": 5,
              "usageEventsDisplay": [
                { "timestamp": "1800000000123", "model": "  composer-1 \n",
                  "tokenUsage": { "inputTokens": 11, "outputTokens": 17, "cacheWriteTokens": 13, "cacheReadTokens": 19 } },
                { "timestamp": 1800000001234, "model": " \n" },
                { "timestamp": "not-a-date", "model": "composer-3" },
                { "timestamp": "1800000000123", "model": "composer-4", "tokenUsage": { "inputTokens": -1 } },
                { "timestamp": "1800000000123", "model": "composer-5", "tokenUsage": { "outputTokens": 1.5 } }
              ]
            }
            """));
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.RejectedEventCount);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(new TokenBreakdown(Input: 11, CacheWrite5m: 13, CacheRead: 19, Output: 17), page.Events[0].Tokens);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123), page.Events[0].Date);
        Assert.Equal(new TokenBreakdown(), page.Events[1].Tokens);
    }

    [Theory]
    [InlineData("""{ "usageEventsDisplay": [] }""")]
    [InlineData("""{ "totalUsageEventsCount": 1 }""")]
    [InlineData("""{ "totalUsageEventsCount": 0, "usageEventsDisplay": null }""")]
    [InlineData("[]")]
    [InlineData("""{ "totalUsageEventsCount": true, "usageEventsDisplay": [] }""")]
    public void CursorUsageEventsRequireValidPageShape(string json)
    {
        Assert.ThrowsAny<Exception>(() => CursorUsageEvents.ParsePage(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void CursorAcceptsOmittedEventListForAnEmptyDay()
    {
        var page = CursorUsageEvents.ParsePage("""{ "totalUsageEventsCount": 0 }"""u8.ToArray());
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Events);
    }

    // ---- Cursor spend history fetcher ----

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly CursorSession Session = new("cursor-history-user", "cursor-history-user%3A%3Atoken");

    private sealed class FakeHandler(Func<JsonObject, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public ConcurrentBag<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(CursorSpendHistoryFetcher.UsageEventsUrl, request.RequestUri);
            Assert.Equal("https://cursor.com", request.Headers.GetValues("Origin").Single());
            var body = JsonUtil.ParseObject(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(body);
            return await respond(body);
        }

        public HashSet<string> RequestedDayKeys() =>
            Requests.Select(StartKey).ToHashSet();
    }

    private static string StartKey(JsonObject body) =>
        DailyUsageAccumulator.DayKey(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(JsonUtil.String(body["startDate"])!)));

    private static long StartMs(JsonObject body) => long.Parse(JsonUtil.String(body["startDate"])!);

    private static Task<HttpResponseMessage> Page(int total, IEnumerable<string>? events = null, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent($$"""{ "totalUsageEventsCount": {{total}}, "usageEventsDisplay": [{{string.Join(",", events ?? [])}}] }""")
        });

    private static string Event(long startMs, string model, int input, string fields = "") =>
        $$"""{ "timestamp": "{{startMs + 1_000}}", "model": "{{model}}", "tokenUsage": { "inputTokens": {{input}} } {{fields}} }""";

    private static List<string> WindowKeys() =>
        Enumerable.Range(0, 30)
            .Select(offset => DailyUsageAccumulator.DayKey(new DateTimeOffset(Now.ToLocalTime().Date.AddDays(-offset), TimeZoneInfo.Local.GetUtcOffset(Now.ToLocalTime().Date.AddDays(-offset)))))
            .ToList();

    private static (CursorUsageHistoryStore Store, string Directory) MakeStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cursor-usage-history-tests-{Guid.NewGuid():N}");
        return (new CursorUsageHistoryStore(dir), dir);
    }

    private static Dictionary<string, CursorUsageDay> CachedHistory() =>
        WindowKeys().Select((key, index) => (key, index)).ToDictionary(
            x => x.key,
            x => new CursorUsageDay
            {
                TotalTokens = 2_000 + x.index,
                ChargedCents = 25,
                IsComplete = x.index > 0
            });

    private static CursorSpendHistoryFetcher Fetcher(FakeHandler handler, CursorUsageHistoryStore store, double budgetSeconds = 20) =>
        new(new AppHttp(new HttpClient(handler)), store, TimeSpan.FromSeconds(budgetSeconds));

    [Fact]
    public async Task CursorBackfillPublishesThirtyDaysAndWritesTheCache()
    {
        var (store, dir) = MakeStore();
        try
        {
            var handler = new FakeHandler(body => Page(5,
            [
                Event(StartMs(body), "unknown", 10_000, """, "kind": "USAGE_EVENT_KIND_USAGE_BASED", "chargedCents": "25.25" """),
                Event(StartMs(body), "composer-1", 20_000, """, "kind": "USAGE_EVENT_KIND_INCLUDED", "chargedCents": 50"""),
                Event(StartMs(body), "composer-1", 30_000, """, "kind": "USAGE_EVENT_KIND_USAGE_BASED", "isChargeable": false, "chargedCents": 100"""),
                Event(StartMs(body), "composer-1", 1, """, "kind": "USAGE_EVENT_KIND_USAGE_BASED" """),
                $$"""{ "timestamp": {{StartMs(body) + 1_000}}, "kind": "USAGE_EVENT_KIND_USAGE_BASED", "chargedCents": 0.25 }"""
            ]));
            var days = await Fetcher(handler, store).RefreshAsync(Session, Now);

            Assert.NotNull(days);
            Assert.Equal(30, days!.Count);
            Assert.Equal(WindowKeys().ToHashSet(), handler.RequestedDayKeys());
            var lines = new List<MetricLine>();
            CursorSpendMapper.AppendSpendLines(days, Now, lines);
            foreach (var (label, dayCount) in new[] { ("Today", 1), ("Yesterday", 1), ("Last 30 Days", 30) })
            {
                var line = Assert.IsType<MetricLine.ValuesLine>(lines.Single(l => l.Label == label));
                Assert.Equal(0.255 * dayCount, line.Items[0].Number, 6);
                Assert.Equal(MetricKind.Dollars, line.Items[0].Kind);
                Assert.Equal(60_001 * dayCount, line.Items[1].Number);
            }
            Assert.Contains(lines, l => l is MetricLine.ChartLine c && c.Note == CursorSpendMapper.HistoryNote);

            var saved = store.Load(Session.UserId, TimeZoneInfo.Local.Id);
            Assert.Equal(30, saved.Count);
            Assert.False(saved[WindowKeys()[0]].IsComplete);
            Assert.True(saved[WindowKeys()[1]].IsComplete);
            Assert.Equal(25.5, saved[WindowKeys()[1]].ChargedCents);
            Assert.Equal(60_001, saved[WindowKeys()[1]].TotalTokens);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorIncrementalRefreshOnlyRequestsTodayAndYesterday()
    {
        var (store, dir) = MakeStore();
        try
        {
            store.Save(CachedHistory(), Session.UserId, TimeZoneInfo.Local.Id);
            var handler = new FakeHandler(_ => Page(0));
            var days = await Fetcher(handler, store).RefreshAsync(Session, Now);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(WindowKeys().Take(2).ToHashSet(), handler.RequestedDayKeys());
            Assert.Equal(30, days!.Count);
            Assert.Equal(2_002, days[2].Day.TotalTokens);
            Assert.Equal(25, days[2].Day.ChargedCents);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorRefetchesOlderDayCachedBeforeItEnded()
    {
        var (store, dir) = MakeStore();
        try
        {
            var keys = WindowKeys();
            var cached = CachedHistory();
            cached[keys[5]] = new CursorUsageDay { TotalTokens = 100, IsComplete = false };
            store.Save(cached, Session.UserId, TimeZoneInfo.Local.Id);
            var handler = new FakeHandler(body => StartKey(body) == keys[5]
                ? Page(1, JsonUtil.Number(body["page"]) == 1 ? [Event(StartMs(body), "composer-1", 900)] : [])
                : Page(0));

            var days = await Fetcher(handler, store).RefreshAsync(Session, Now);

            Assert.Equal(new HashSet<string> { keys[0], keys[1], keys[5] }, handler.RequestedDayKeys());
            Assert.Equal(900, days![5].Day.TotalTokens);
            Assert.True(days[5].Day.IsComplete);
            var saved = store.Load(Session.UserId, TimeZoneInfo.Local.Id)[keys[5]];
            Assert.Equal(900, saved.TotalTokens);
            Assert.True(saved.IsComplete);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorInvalidChargeKeepsThePreviouslyCachedDay()
    {
        var (store, dir) = MakeStore();
        try
        {
            store.Save(CachedHistory(), Session.UserId, TimeZoneInfo.Local.Id);
            var today = WindowKeys()[0];
            var handler = new FakeHandler(body => StartKey(body) == today ? Page(2,
            [
                Event(StartMs(body), "composer-1", 10, """, "kind": "USAGE_EVENT_KIND_USAGE_BASED", "chargedCents": 50"""),
                Event(StartMs(body), "composer-1", 20, """, "kind": "USAGE_EVENT_KIND_USAGE_BASED", "chargedCents": -10""")
            ]) : Page(0));

            var days = await Fetcher(handler, store).RefreshAsync(Session, Now);
            Assert.Equal(25, days![0].Day.ChargedCents);
            Assert.Equal(2_000, store.Load(Session.UserId, TimeZoneInfo.Local.Id)[today].TotalTokens);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorRebuildsTheTokenOnlyCacheToGetActualCharges()
    {
        var (store, dir) = MakeStore();
        try
        {
            store.Save(CachedHistory(), Session.UserId, TimeZoneInfo.Local.Id);
            var file = Directory.GetFiles(dir).Single();
            var old = JsonUtil.ParseObject(File.ReadAllBytes(file))!;
            old["schemaVersion"] = 2;
            old["days"] = new JsonObject
            {
                [WindowKeys()[2]] = JsonUtil.ParseObject("""{"isComplete":true,"models":{"composer-1":{"input":25}}}""")
            };
            File.WriteAllText(file, old.ToJsonString());
            Assert.Empty(store.Load(Session.UserId, TimeZoneInfo.Local.Id));

            var handler = new FakeHandler(_ => Page(0));
            var days = await Fetcher(handler, store).RefreshAsync(Session, Now);
            Assert.Equal(30, handler.Requests.Count);
            Assert.All(days!, day => Assert.Equal(0, day.Day.ChargedCents));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorHttpFailureLeavesMissingDayUnstoredAndReturnsNoHistory()
    {
        var (store, dir) = MakeStore();
        try
        {
            var today = WindowKeys()[0];
            var handler = new FakeHandler(body => StartKey(body) == today ? Page(0, status: HttpStatusCode.InternalServerError) : Page(0));

            Assert.Null(await Fetcher(handler, store).RefreshAsync(Session, Now));
            var partial = store.Load(Session.UserId, TimeZoneInfo.Local.Id);
            Assert.False(partial.ContainsKey(today));
            Assert.Equal(29, partial.Count);

            // A refresh where every refetch fails keeps the cached days and the file untouched.
            store.Save(CachedHistory(), Session.UserId, TimeZoneInfo.Local.Id);
            var file = Directory.GetFiles(dir).Single();
            var original = File.ReadAllBytes(file);
            var failing = new FakeHandler(_ => Page(0, status: HttpStatusCode.InternalServerError));
            var retained = await Fetcher(failing, store).RefreshAsync(Session, Now);
            Assert.Equal(30, retained!.Count);
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorHistoryCacheIsPerAccount()
    {
        var (store, dir) = MakeStore();
        try
        {
            store.Save(CachedHistory(), "account-a", TimeZoneInfo.Local.Id);
            var handler = new FakeHandler(_ => Page(0));
            var days = await Fetcher(handler, store).RefreshAsync(new CursorSession("account-b", "account-b%3A%3Atoken"), Now);

            Assert.Equal(30, handler.Requests.Count);
            Assert.Equal(30, days!.Count);
            Assert.Equal(30, store.Load("account-a", TimeZoneInfo.Local.Id).Count);
            Assert.Equal(30, store.Load("account-b", TimeZoneInfo.Local.Id).Count);
            Assert.Empty(store.Load("account-a", "Other/Zone"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CursorDeadlineKeepsFastDaysAndNextRefreshFinishesBackfill()
    {
        var (store, dir) = MakeStore();
        try
        {
            var keys = WindowKeys();
            var slow = new FakeHandler(async body =>
            {
                if (keys.IndexOf(StartKey(body)) > 10) await Task.Delay(TimeSpan.FromSeconds(5));
                return await Page(0);
            });
            var started = DateTimeOffset.UtcNow;
            var first = await Fetcher(slow, store, budgetSeconds: 0.3).RefreshAsync(Session, Now);
            Assert.Null(first);
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
            var persisted = store.Load(Session.UserId, TimeZoneInfo.Local.Id);
            Assert.InRange(persisted.Count, 1, 29);

            var fast = new FakeHandler(_ => Page(0));
            var second = await Fetcher(fast, store, budgetSeconds: 5).RefreshAsync(Session, Now);
            var expected = keys.Where((key, index) => index < 2 || !persisted.ContainsKey(key)).ToHashSet();
            Assert.Equal(expected, fast.RequestedDayKeys());
            Assert.Equal(30, second!.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Grok team billing ----

    private static AppHttpResponse Response(int status, string body) =>
        new(status, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>());

    [Fact]
    public void GrokTeamBillingUnavailableMatchesOnlyTheNoPersonalTeam412()
    {
        Assert.True(GrokUsageMapper.IsTeamBillingUnavailable(Response(412, """{ "error": "No personal team." }""")));
        Assert.False(GrokUsageMapper.IsTeamBillingUnavailable(Response(412, """{ "error": "Precondition failed" }""")));
        Assert.False(GrokUsageMapper.IsTeamBillingUnavailable(Response(412, "No personal team")));
        Assert.False(GrokUsageMapper.IsTeamBillingUnavailable(Response(500, """{ "error": "No personal team." }""")));
    }
}
