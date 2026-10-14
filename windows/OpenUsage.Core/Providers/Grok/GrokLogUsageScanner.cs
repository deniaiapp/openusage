using System.Text.Json.Nodes;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Grok;

public static class GrokLogUsageScanner
{
    public static LogUsageScan? Scan(int daysBack = 30, DateTimeOffset? now = null, ModelPricing? pricing = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var since = DailyUsageAccumulator.Since(daysBack, current);
        var price = pricing ?? ModelPricing.Shared;
        var home = AppPaths.Env("GROK_HOME") ?? "~/.grok";
        var sessions = Path.Combine(AppPaths.Expand(home), "sessions");
        var files = JsonlScan.FilesUnder(sessions, "updates.jsonl", since).OrderBy(p => p).ToList();
        if (files.Count == 0) return null;

        var entries = new List<Entry>();
        foreach (var file in files)
        {
            foreach (var line in JsonlScan.ReadLines(file))
            {
                if (!line.Contains("turn_completed", StringComparison.Ordinal)) continue;
                entries.AddRange(ParseCompletedTurn(line));
            }
        }
        return Aggregate(Dedup(entries), since, price);
    }

    public readonly record struct Entry(string? EventId, DateTimeOffset Timestamp, string Model, TokenBreakdown Tokens, double? CarriedCost);

    public static IEnumerable<Entry> ParseCompletedTurn(string line)
    {
        var obj = JsonUtil.ParseObject(line);
        if (obj is null) yield break;
        var parameters = JsonUtil.Object(obj["params"]);
        var update = JsonUtil.Object(parameters?["update"]) ?? JsonUtil.Object(obj["update"]);
        if (update is null || JsonUtil.String(update["sessionUpdate"]) != "turn_completed") yield break;
        var usage = JsonUtil.Object(update["usage"]);
        var modelUsage = JsonUtil.Object(usage?["modelUsage"]);
        var timestamp = Timestamp(obj, parameters);
        if (usage is null || modelUsage is null || timestamp is null) yield break;
        var metadata = JsonUtil.Object(parameters?["_meta"]) ?? JsonUtil.Object(obj["_meta"]);
        var eventId = JsonUtil.String(metadata?["eventId"]);
        var topTicks = JsonUtil.Number(usage["costUsdTicks"]);
        var models = modelUsage.ToList();
        foreach (var property in models)
        {
            var model = property.Key.Trim();
            if (string.IsNullOrEmpty(model) || JsonUtil.Object(property.Value) is not { } values) continue;
            if (JsonUtil.Number(values["inputTokens"]) is not { } inputValue || inputValue < 0) continue;
            var input = Bound(inputValue);
            var cacheRead = Math.Min(Bound(JsonUtil.Number(values["cachedReadTokens"]) ?? 0), input);
            var cacheWrite = Math.Min(Bound(JsonUtil.Number(values["cacheCreationTokens"]) ?? 0), Math.Max(0, input - cacheRead));
            var output = Bound(JsonUtil.Number(values["outputTokens"]) ?? 0);
            var ticks = JsonUtil.Number(values["costUsdTicks"]) ?? (models.Count == 1 ? topTicks : null);
            yield return new Entry(
                string.IsNullOrEmpty(eventId) ? null : eventId,
                timestamp.Value,
                model,
                new TokenBreakdown(Math.Max(0, input - cacheRead - cacheWrite), cacheWrite, 0, cacheRead, output),
                ticks is { } t && t >= 0 ? t / 10_000_000_000d : null);
        }
    }

    private static DateTimeOffset? Timestamp(JsonObject obj, JsonObject? parameters)
    {
        foreach (var meta in new[] { JsonUtil.Object(parameters?["_meta"]), JsonUtil.Object(obj["_meta"]) })
        {
            if (JsonUtil.Number(meta?["agentTimestampMs"]) is { } ms && ms > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }
        if (JsonUtil.Number(obj["timestamp"]) is { } seconds && seconds > 0)
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        return Iso8601.Parse(JsonUtil.String(obj["timestamp"]));
    }

    private static int Bound(double value)
    {
        if (value <= 0) return 0;
        if (value > 1_000_000_000_000) return 1_000_000_000;
        return (int)value;
    }

    private static List<Entry> Dedup(List<Entry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return entries.Where(e => e.EventId is null || seen.Add(e.EventId + "\0" + e.Model)).ToList();
    }

    private static LogUsageScan Aggregate(List<Entry> entries, DateTimeOffset since, ModelPricing pricing)
    {
        var acc = new DailyUsageAccumulator();
        foreach (var entry in entries.Where(e => e.Timestamp >= since))
        {
            var day = DailyUsageAccumulator.DayKey(entry.Timestamp);
            var cost = entry.CarriedCost ?? pricing.EstimatedCost(entry.Model, entry.Tokens);
            if (cost is null)
            {
                if (entry.Tokens.TotalTokens > 0) acc.AddUnknown(day, entry.Model);
                continue;
            }
            acc.Add(day, entry.Tokens.TotalTokens, cost.Value);
        }
        return acc.Build();
    }
}
