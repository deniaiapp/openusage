using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

/// <summary>Regression tests for Codex fixes ported from the macOS app.</summary>
public class CodexUpstreamTests
{
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ModelPricing TestPricing() => ModelPricing.FromJson("""
        {
          "pricing": {
            "gpt-6-astra": { "input_per_million": 10, "output_per_million": 50, "cache_read_per_million": 1 },
            "gpt-5.5": { "input_per_million": 5, "output_per_million": 30, "cache_read_per_million": 0.5 },
            "gpt-5.6-luna": { "input_per_million": 0.2, "output_per_million": 0.9, "cache_read_per_million": 0.02 },
            "gpt-5.4": { "input_per_million": 2.5, "output_per_million": 15, "cache_read_per_million": 0.25 }
          }
        }
        """, Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));

    private static CodexLogUsageScanner.Event Ev(
        string model, string at, long input = 100_000, bool fast = false, bool ultrafast = false, string? pricingModel = null) =>
        new(DateTimeOffset.Parse(at), model, pricingModel, input, 0, 0, 0, input, fast, ultrafast);

    private static double TotalCost(LogUsageScan scan) => scan.Series.Daily.Sum(d => d.CostUsd ?? 0);

    private static double Cost(params CodexLogUsageScanner.Event[] events) =>
        TotalCost(CodexLogUsageScanner.Aggregate(events, LongAgo, TestPricing()));

    // #1332
    [Theory]
    [InlineData("prolite", "Pro 100")]
    [InlineData("pro", "Pro 200")]
    [InlineData("promax", "Pro 500")]
    [InlineData("  PROLITE\n", "Pro 100")]
    [InlineData(" ProMax ", "Pro 500")]
    [InlineData("self_serve_business_prolite", "Business Premium")]
    [InlineData("promax_future", "Promax Future")]
    [InlineData("plus", "Plus")]
    public void PlanNames_UseCurrentProNames(string raw, string expected)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["plan_type"] = raw }.ToJsonString());
        var mapped = CodexUsageMapper.Map(new AppHttpResponse(200, body, new Dictionary<string, string>()), null, DateTimeOffset.Now);
        Assert.Equal(expected, mapped.Plan);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n")]
    public void PlanNames_MissingPlanInventsNothing(string? raw) => Assert.Null(CodexUsageMapper.FormatPlan(raw));

    // #1364
    [Fact]
    public void DashboardLink_PointsAtSettingsUsage()
    {
        using var http = new AppHttp();
        var link = Assert.Single(new CodexProvider(http).Provider.Links!, l => l.Label == "Dashboard");
        Assert.Equal("https://chatgpt.com/settings/usage", link.Url);
    }

    // #1354
    [Fact]
    public void AutoReview_UsesDatedEstimatesBeforeOctober6AndIsFreeAfter()
    {
        Assert.Equal("gpt-5.6-luna", CodexUsagePricing.PricingModel("codex-auto-review", DateTimeOffset.Parse("2026-08-01T00:00:00Z")));
        Assert.Equal("gpt-5.4", CodexUsagePricing.PricingModel("codex-auto-review", DateTimeOffset.Parse("2026-03-05T00:00:00Z")));
        Assert.Equal("gpt-5", CodexUsagePricing.PricingModel("codex-auto-review", DateTimeOffset.Parse("2025-01-01T00:00:00Z")));

        Assert.Equal(0.02, Cost(Ev("codex-auto-review", "2026-10-05T23:59:59Z")), 6);
        Assert.Equal(0, Cost(Ev("codex-auto-review", "2026-10-06T00:00:00Z")));
    }

    [Fact]
    public void AutoReview_FreeRequestsKeepTokensWithoutUnknownWarning()
    {
        var empty = ModelPricing.FromJson("{}", Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));
        var scan = CodexLogUsageScanner.Aggregate([Ev("codex-auto-review", "2026-10-07T12:00:00Z")], LongAgo, empty);
        var day = Assert.Single(scan.Series.Daily);
        Assert.Equal(100_000, day.TotalTokens);
        Assert.Equal(0, day.CostUsd);
        Assert.Empty(scan.UnknownModelsByDay);
    }

    // #1247
    [Fact]
    public void Reserve_PricesAtLunaRates()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {"type":"turn_context","payload":{"model":"gpt-reserve"}}
            {"type":"event_msg","timestamp":"2026-09-13T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100000,"output_tokens":0,"total_tokens":100000}}}}
            """);
        try
        {
            var ev = Assert.Single(CodexLogUsageScanner.ParseFile(path));
            Assert.Equal("gpt-reserve", ev.Model);
            Assert.Equal(0.02, Cost(ev), 6);
        }
        finally { File.Delete(path); }
    }

    // #1327, #1331
    [Fact]
    public void Ultrafast_IsSixTimesForAstraAndFastMultiplierElsewhere()
    {
        Assert.Equal(1, Cost(Ev("gpt-6-astra", "2026-09-30T12:00:00Z")), 6);
        Assert.Equal(2, Cost(Ev("gpt-6-astra", "2026-09-30T12:00:00Z", fast: true)), 6);
        Assert.Equal(6, Cost(Ev("gpt-6-astra", "2026-09-30T12:00:00Z", ultrafast: true)), 6);
        Assert.Equal("gpt-6-astra", CodexUsagePricing.DatedBaseModel("gpt-6-astra-2026-09-01"));
        Assert.Equal("gpt-6-astra", CodexUsagePricing.DatedBaseModel("gpt-6-astra-20260901"));
        Assert.Equal(1.25, Cost(Ev("gpt-5.5", "2026-09-30T12:00:00Z", ultrafast: true)), 6);
        // A -fast slug normalizes to its base rate, then takes one multiplier.
        Assert.Equal(2, Cost(Ev("gpt-6-astra-fast", "2026-09-30T12:00:00Z")), 6);
        Assert.Equal(6, Cost(Ev("gpt-6-astra-fast", "2026-09-30T12:00:00Z", ultrafast: true)), 6);
    }

    [Fact]
    public void LongContext_UsesCodexRatesAbove272k()
    {
        Assert.Equal(1.0, Cost(Ev("gpt-5.5", "2026-09-30T12:00:00Z", input: 200_000)), 6);
        Assert.Equal(3.0, Cost(Ev("gpt-5.5", "2026-09-30T12:00:00Z", input: 300_000)), 6);
    }

    [Fact]
    public void TierlessSettingsRecord_KeepsTheCurrentTier()
    {
        var parser = new CodexLogParser();
        parser.Parse("""{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"ultrafast"}}}""");
        parser.Parse("""{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-6-astra"}}}""");
        var ev = parser.Parse(TokenLine("2026-09-30T12:00:00Z", 100));
        Assert.True(ev!.Value.IsUltrafast);
        Assert.False(ev.Value.IsFast);

        parser.Parse("""{"type":"event_msg","payload":{"type":"thread_settings_applied","service_tier":"standard"}}""");
        Assert.False(parser.Parse(TokenLine("2026-09-30T12:01:00Z", 50))!.Value.IsUltrafast);
    }

    private static string TokenLine(string at, int input) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + at + "\",\"payload\":{\"type\":\"token_count\",\"info\":" +
        "{\"last_token_usage\":{\"input_tokens\":" + input + ",\"output_tokens\":1,\"total_tokens\":" + (input + 1) + "}}}}";

    // #1383
    [Fact]
    public void GrowingRollout_ReadsOnlyNewLinesWithCarriedState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-tail-{Guid.NewGuid():N}.jsonl");
        var cache = new CodexScanCache();
        try
        {
            File.WriteAllText(path,
                """{"type":"turn_context","payload":{"model":"gpt-5.5"}}""" + "\n" +
                """{"type":"event_msg","payload":{"type":"thread_settings_applied","service_tier":"fast"}}""" + "\n" +
                TokenLine("2026-10-01T10:00:00Z", 100) + "\n");
            var first = cache.Events([path]);
            Assert.Single(first);
            Assert.Equal(CodexScanCache.ReadKind.Full, cache.LastRead(path));

            File.AppendAllText(path, TokenLine("2026-10-01T10:05:00Z", 200) + "\n");
            var second = cache.Events([path]);
            Assert.Equal(CodexScanCache.ReadKind.Appended, cache.LastRead(path));
            Assert.Equal(2, second.Count);
            Assert.Equal("gpt-5.5", second[1].Model);
            Assert.True(second[1].IsFast);

            Assert.Equal(2, cache.Events([path]).Count);
            Assert.Equal(CodexScanCache.ReadKind.Reused, cache.LastRead(path));

            // A half-written record is not resumed from; the next read starts over and counts it once.
            var line = TokenLine("2026-10-01T10:10:00Z", 300);
            File.AppendAllText(path, line[..20]);
            Assert.Equal(2, cache.Events([path]).Count);
            File.AppendAllText(path, line[20..] + "\n");
            var third = cache.Events([path]);
            Assert.Equal(3, third.Count);
            Assert.Equal(300, third[2].Input);

            // A rewritten prefix is never trusted for a tail read.
            File.WriteAllText(path,
                """{"type":"turn_context","payload":{"model":"gpt-5.4"}}""" + "\n" +
                TokenLine("2026-10-02T10:00:00Z", 10) + "\n" +
                TokenLine("2026-10-02T10:01:00Z", 20) + "\n" +
                TokenLine("2026-10-02T10:02:00Z", 30) + "\n" +
                TokenLine("2026-10-02T10:03:00Z", 40) + "\n");
            var rewritten = cache.Events([path]);
            Assert.Equal(CodexScanCache.ReadKind.Full, cache.LastRead(path));
            Assert.Equal(4, rewritten.Count);
            Assert.All(rewritten, e => Assert.Equal("gpt-5.4", e.Model));
        }
        finally { File.Delete(path); }
    }

    // #1338
    [Fact]
    public async Task SlowHistory_DoesNotBlockQuotaAndIsCollectedLater()
    {
        var refresh = new CodexHistoryRefresh();
        using var gate = new ManualResetEventSlim();
        var scan = new LogUsageScan();
        var starts = 0;
        LogUsageScan? Slow() { Interlocked.Increment(ref starts); gate.Wait(); return scan; }

        var pending = await refresh.ValueAsync(TimeSpan.FromMilliseconds(30), Slow, default);
        Assert.True(pending.Pending);
        Assert.False(pending.HasHistory);

        var stillPending = await refresh.ValueAsync(TimeSpan.FromMilliseconds(30), Slow, default);
        Assert.True(stillPending.Pending);
        Assert.Equal(1, starts);

        gate.Set();
        var done = await refresh.ValueAsync(TimeSpan.FromSeconds(10), Slow, default);
        Assert.False(done.Pending);
        Assert.Same(scan, done.Scan);

        using var secondGate = new ManualResetEventSlim();
        var retained = await refresh.ValueAsync(TimeSpan.FromMilliseconds(30), () => { secondGate.Wait(); return null; }, default);
        Assert.True(retained.Pending);
        Assert.Same(scan, retained.Scan);
        secondGate.Set();
    }

    [Fact]
    public async Task SlowHistory_PendingCardShowsWarningWithoutNoDataBadge()
    {
        using var http = new AppHttp();
        using var gate = new ManualResetEventSlim();
        var provider = new CodexProvider(http, TimeSpan.FromMilliseconds(20), () => { gate.Wait(); return null; });
        var lines = new List<MetricLine>();
        var warning = await provider.AppendHistoryAsync(lines, default);
        Assert.Equal(CodexHistoryRefresh.PendingWarning, warning);
        Assert.Empty(lines);
        gate.Set();
    }

    // #1322
    [Fact]
    public void Save_KeepsOtherFieldsAndRefusesToOverwriteANewerLogin()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"auth_mode":"chatgpt","tokens":{"access_token":"a1","refresh_token":"r1","account_id":"acc"}}""");
            var store = new CodexAuthStore();
            var auth = CodexAuthStore.ParseFile(path)!;
            auth.AccessToken = "a2";
            auth.RefreshToken = "r2";
            Assert.True(store.Save(auth));
            var saved = JsonUtil.ParseObject(File.ReadAllText(path))!;
            Assert.Equal("chatgpt", JsonUtil.String(saved["auth_mode"]));
            Assert.Equal("a2", JsonUtil.String(saved["tokens"]!["access_token"]));
            Assert.Equal("acc", JsonUtil.String(saved["tokens"]!["account_id"]));

            File.WriteAllText(path, """{"tokens":{"access_token":"a3","refresh_token":"r3"}}""");
            auth.AccessToken = "a4";
            auth.RefreshToken = "r4";
            Assert.False(store.Save(auth));
            Assert.Equal("a3", CodexAuthStore.ParseFile(path)!.AccessToken);
        }
        finally { File.Delete(path); }
    }

    // #1317
    [Fact]
    public void KeyringAccount_MatchesCodexStoreKey()
    {
        Assert.Equal("cli|ba7816bf8f01cfea", CodexAuthStore.KeyringAccount("abc"));
        Assert.All(CodexAuthStore.KeyringAccounts("~/.codex"), a => Assert.Matches("^cli\\|[0-9a-f]{16}$", a));
    }
}
