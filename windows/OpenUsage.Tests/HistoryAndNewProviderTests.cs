using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class HistoryAndNewProviderTests
{
    [Fact]
    public void LocalLogMetricsAreNotOfferedButProviderHistoryRemains()
    {
        using var http = new AppHttp();
        IProviderRuntime[] providers = [new ClaudeProvider(http), new CodexProvider(http), new GrokProvider(http)];
        foreach (var runtime in providers)
        {
            var id = runtime.Provider.Id;
            Assert.Contains(runtime.WidgetDescriptors, descriptor => descriptor.Id == $"{id}.weekly");
            foreach (var metric in new[] { "trend", "today", "yesterday", "last30" })
            {
                var metricId = $"{id}.{metric}";
                Assert.DoesNotContain(runtime.WidgetDescriptors, descriptor => descriptor.Id == metricId);
                Assert.DoesNotContain(metricId, DefaultLayout.MetricIds);
                Assert.DoesNotContain(metricId, DefaultLayout.ExpandedMetricIds);
            }
        }

        Assert.Contains(new CursorProvider(http).WidgetDescriptors, descriptor => descriptor.Id == "cursor.trend");
    }

    [Fact]
    public void SpendTiles_EmitTodayYesterdayAndTrend()
    {
        var today = DailyUsageAccumulator.DayKey(DateTimeOffset.Now);
        var yesterday = DailyUsageAccumulator.DayKey(DateTimeOffset.Now.AddDays(-1));
        var acc = new DailyUsageAccumulator();
        acc.Add(today, 2_000_000_000, 4.08);
        acc.Add(yesterday, 2_000_000_000, 2.1);
        var scan = acc.Build();
        var lines = new List<MetricLine>();
        SpendTileMapper.AppendAll(scan, lines, DateTimeOffset.Now, "From test logs");
        Assert.Contains(lines, l => l is MetricLine.ValuesLine v && v.Label == "Today");
        Assert.Contains(lines, l => l is MetricLine.ValuesLine v && v.Label == "Yesterday");
        Assert.Contains(lines, l => l is MetricLine.ValuesLine v && v.Label == "Last 30 Days");
        Assert.Contains(lines, l => l is MetricLine.ChartLine c && c.Label == "Usage Trend" && c.Points.Count == 31);
    }

    [Fact]
    public void AntigravityMapper_ReadsQuotaSummaryBuckets()
    {
        var json = JsonUtil.ParseObject("""
            {"groups":[{"buckets":[
              {"bucketId":"gemini-5h","remainingFraction":0.75,"resetTime":"2026-09-13T17:00:00Z"},
              {"bucketId":"gemini-weekly","remainingFraction":0.4,"resetTime":"2026-09-20T17:00:00Z"},
              {"bucketId":"3p-5h","remainingFraction":1.0}
            ]}]}
            """)!;
        var lines = AntigravityUsageMapper.ParseQuotaSummary(json);
        Assert.NotNull(lines);
        Assert.Contains(lines!, l => l is MetricLine.ProgressLine p && p.Label == "Session" && p.Used == 25);
        Assert.Contains(lines!, l => l is MetricLine.ProgressLine p && p.Label == "Weekly" && p.Used == 60);
        Assert.Contains(lines!, l => l is MetricLine.ProgressLine p && p.Label == "Claude" && p.Used == 0);
    }

    [Fact]
    public void OllamaMapper_FlipsLegacyRemainingToUsed()
    {
        var balance = JsonUtil.ParseObject("""
            {"included":{"session":{"remaining_percent":65.1},"weekly":{"remaining_percent":80}},"purchased":{"balance_usd":"1.25"}}
            """)!;
        var mapped = OllamaUsageMapper.Map(balance, """{"plan":"pro"}""");
        Assert.Equal("Pro", mapped.Plan.Name);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Session" && Math.Abs(p.Used - 34.9) < 1e-9);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ValuesLine v && v.Label == "Purchased Credits");
    }

    [Fact]
    public void ModelPricing_UsesSupplementThenCatalog()
    {
        var supplement = """
            {"pricing":{"composer-2.5":{"input_per_million":0.5,"output_per_million":2.5,"cache_write_per_million":0.5,"cache_read_per_million":0.2}},
             "alias_rules":[{"pattern":"^composer$","canonical":"composer-2.5"}]}
            """;
        var litellm = Encoding.UTF8.GetBytes("""{"models":{"gpt-5":{"i":1.25,"o":10,"cw":1.25,"cr":0.125}}}""");
        var modelsDev = Encoding.UTF8.GetBytes("""{"models":{}}""");
        var pricing = ModelPricing.FromJson(supplement, litellm, modelsDev);
        Assert.Equal(0.5, pricing.Resolve("composer")!.InputPerMillion);
        Assert.Equal(1.25, pricing.Resolve("gpt-5")!.InputPerMillion);
        var cost = pricing.EstimatedCost("gpt-5", new TokenBreakdown(Input: 1_000_000, Output: 1_000_000));
        Assert.Equal(11.25, cost);
    }
}
