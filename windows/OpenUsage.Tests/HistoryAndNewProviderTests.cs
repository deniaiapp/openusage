using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class HistoryAndNewProviderTests
{
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
    public void ClaudeScanner_ParsesUsageLine()
    {
        var line = """
            {"timestamp":"2026-09-13T12:00:00.000Z","sessionId":"s1","requestId":"r1","version":"1.0.24","message":{"id":"m1","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":10,"speed":"standard"}},"costUSD":0.01}
            """;
        var entries = ClaudeLogUsageScanner.ParseLine(line).ToList();
        Assert.Single(entries);
        Assert.Equal("claude-sonnet-4-20250514", entries[0].Model);
        Assert.Equal(100, entries[0].Tokens.Input);
        Assert.Equal(20, entries[0].Tokens.Output);
        Assert.Equal(0.01, entries[0].CostUsd);
    }

    [Fact]
    public void CodexScanner_ParsesTokenCountAfterTurnContext()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {"type":"turn_context","payload":{"model":"gpt-5"}}
            {"type":"event_msg","timestamp":"2026-09-13T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":50,"output_tokens":10,"cached_input_tokens":5,"total_tokens":65}}}}
            """);
        try
        {
            var events = CodexLogUsageScanner.ParseFile(path).ToList();
            Assert.Single(events);
            Assert.Equal("gpt-5", events[0].Model);
            Assert.Equal(50, events[0].Input);
            Assert.Equal(5, events[0].Cached);
            Assert.Equal(10, events[0].Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GrokScanner_ParsesCompletedTurn()
    {
        var line = """
            {"params":{"update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4":{"inputTokens":80,"outputTokens":20,"cachedReadTokens":10}}}},"_meta":{"eventId":"e1","agentTimestampMs":1757764800000}}}
            """;
        var entries = GrokLogUsageScanner.ParseCompletedTurn(line).ToList();
        Assert.Single(entries);
        Assert.Equal("grok-4", entries[0].Model);
        Assert.Equal(70, entries[0].Tokens.Input);
        Assert.Equal(10, entries[0].Tokens.CacheRead);
        Assert.Equal(20, entries[0].Tokens.Output);
        Assert.Equal("e1", entries[0].EventId);
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
    public void AntigravityProto_ReadsGenerationEvent()
    {
        static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                var b = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
            return [.. bytes];
        }
        static byte[] Field(uint number, byte wire, byte[] payload)
        {
            var tag = Varint((number << 3) | wire);
            if (wire == 2) return [.. tag, .. Varint((ulong)payload.Length), .. payload];
            return [.. tag, .. payload];
        }

        var usage = Field(1, 0, Varint(10))
            .Concat(Field(2, 0, Varint(20)))
            .Concat(Field(3, 0, Varint(5)))
            .Concat(Field(5, 0, Varint(3)))
            .ToArray();
        var timestamp = Field(1, 0, Varint(1_757_764_800));
        var timing = Field(4, 2, timestamp);
        var inner = Field(19, 2, Encoding.UTF8.GetBytes("gemini-3.1-pro"))
            .Concat(Field(21, 2, Encoding.UTF8.GetBytes("Gemini 3.1 Pro")))
            .Concat(Field(4, 2, usage))
            .Concat(Field(9, 2, timing))
            .ToArray();
        var blob = Field(1, 2, inner);
        var ev = AntigravityProtoDecoder.GenerationEventFrom(blob);
        Assert.NotNull(ev);
        Assert.Equal("gemini-3.1-pro", ev!.ModelId);
        Assert.Equal(30, ev.InputTokens);
        Assert.Equal(5, ev.OutputTokens);
        Assert.Equal(3, ev.CacheReadTokens);
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
