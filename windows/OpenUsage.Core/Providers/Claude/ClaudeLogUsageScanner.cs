using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public static class ClaudeLogUsageScanner
{
    /// Shared across refreshes so growing session logs are read incrementally (see `ClaudeLogScanCache`).
    private static readonly ClaudeLogScanCache SharedCache = new();

    public static LogUsageScan? Scan(
        int daysBack = 30,
        DateTimeOffset? now = null,
        ModelPricing? pricing = null,
        IReadOnlyList<string>? roots = null,
        ClaudeLogScanCache? cache = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var since = DailyUsageAccumulator.Since(daysBack, current);
        var price = pricing ?? ModelPricing.Shared;
        var files = (roots ?? Roots()).SelectMany(root => JsonlScan.FilesUnder(Path.Combine(root, "projects"), since: since))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var entries = (cache ?? SharedCache).Entries(files);
        if (files.Count == 0) return null;
        return Aggregate(Dedup(entries), since, price);
    }

    public static IEnumerable<Entry> ParseLine(string line)
    {
        var obj = JsonUtil.ParseObject(line);
        if (obj is null) yield break;
        var timestamp = Iso8601.Parse(JsonUtil.String(obj["timestamp"]));
        if (timestamp is null) yield break;
        var message = JsonUtil.Object(obj["message"]);
        var usage = JsonUtil.Object(message?["usage"]);
        if (message is null || usage is null) yield break;
        if (Tokens(usage) is not { } tokens) yield break;
        if (!IsValid(obj, message)) yield break;
        var model = JsonUtil.String(message["model"]);
        if (model == "<synthetic>") model = null;
        yield return new Entry(
            timestamp.Value,
            tokens,
            JsonUtil.String(message["id"]),
            JsonUtil.String(obj["requestId"]),
            JsonUtil.Bool(obj["isSidechain"]) == true,
            usage["speed"] is not null,
            JsonUtil.Number(obj["costUSD"]),
            model);

        if (JsonUtil.Array(usage["iterations"]) is not { } iterations) yield break;
        var advisorIndex = 0;
        foreach (var iteration in iterations.OfType<JsonObject>())
        {
            if (JsonUtil.String(iteration["type"]) != "advisor_message") continue;
            var advisorModel = JsonUtil.String(iteration["model"]);
            if (string.IsNullOrEmpty(advisorModel) || Tokens(iteration) is not { } advisorTokens) continue;
            yield return new Entry(
                timestamp.Value,
                advisorTokens,
                JsonUtil.String(message["id"]) is { } id ? $"{id}:advisor:{advisorIndex}" : null,
                JsonUtil.String(obj["requestId"]),
                JsonUtil.Bool(obj["isSidechain"]) == true,
                iteration["speed"] is not null,
                null,
                advisorModel);
            advisorIndex++;
        }
    }

    public readonly record struct Entry(
        DateTimeOffset Timestamp,
        TokenBreakdown Tokens,
        string? MessageId,
        string? RequestId,
        bool IsSidechain,
        bool HasSpeed,
        double? CostUsd,
        string? Model);

    private static IEnumerable<string> Roots()
    {
        var env = AppPaths.Env("CLAUDE_CONFIG_DIR");
        if (env is not null)
        {
            foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return AppPaths.Expand(part);
            yield break;
        }
        var xdg = AppPaths.Env("XDG_CONFIG_HOME");
        if (xdg is not null) yield return Path.Combine(AppPaths.Expand(xdg), "claude");
        yield return AppPaths.Expand("~/.claude");
    }

    private static TokenBreakdown? Tokens(JsonObject usage)
    {
        if (JsonUtil.Number(usage["input_tokens"]) is not { } input) return null;
        if (JsonUtil.Number(usage["output_tokens"]) is not { } output) return null;
        var speed = JsonUtil.String(usage["speed"]);
        if (speed is not null && speed is not "fast" and not "standard") return null;
        var cacheWrite5m = 0;
        var cacheWrite1h = 0;
        if (JsonUtil.Object(usage["cache_creation"]) is { } cacheCreation)
        {
            cacheWrite5m = (int)(JsonUtil.Number(cacheCreation["ephemeral_5m_input_tokens"]) ?? 0);
            cacheWrite1h = (int)(JsonUtil.Number(cacheCreation["ephemeral_1h_input_tokens"]) ?? 0);
        }
        else
        {
            cacheWrite5m = (int)(JsonUtil.Number(usage["cache_creation_input_tokens"]) ?? 0);
        }
        return new TokenBreakdown(
            (int)input,
            cacheWrite5m,
            cacheWrite1h,
            (int)(JsonUtil.Number(usage["cache_read_input_tokens"]) ?? 0),
            (int)output,
            speed == "fast");
    }

    private static bool IsValid(JsonObject obj, JsonObject message)
    {
        var version = JsonUtil.String(obj["version"]);
        if (version is not null && !Regex.IsMatch(version, @"^\d+\.\d+\.\d")) return false;
        foreach (var node in new[] { obj["sessionId"], obj["requestId"], message["id"], message["model"] })
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0)
                return false;
        }
        return true;
    }

    private static List<Entry> Dedup(List<Entry> entries)
    {
        var result = new List<Entry>();
        var exact = new Dictionary<(string, string?), int>();
        foreach (var entry in entries)
        {
            if (entry.MessageId is null)
            {
                result.Add(entry);
                continue;
            }
            var key = (entry.MessageId, entry.RequestId);
            if (exact.TryGetValue(key, out var index))
            {
                if (ShouldReplace(entry, result[index])) result[index] = entry;
                continue;
            }
            exact[key] = result.Count;
            result.Add(entry);
        }
        return result;
    }

    private static bool ShouldReplace(Entry candidate, Entry existing)
    {
        if (existing.IsSidechain != candidate.IsSidechain) return existing.IsSidechain && !candidate.IsSidechain;
        if (candidate.Tokens.TotalTokens != existing.Tokens.TotalTokens)
            return candidate.Tokens.TotalTokens > existing.Tokens.TotalTokens;
        return candidate.HasSpeed && !existing.HasSpeed;
    }

    private static LogUsageScan Aggregate(List<Entry> entries, DateTimeOffset since, ModelPricing pricing)
    {
        var acc = new DailyUsageAccumulator();
        foreach (var entry in entries.Where(e => e.Timestamp >= since))
        {
            var day = DailyUsageAccumulator.DayKey(entry.Timestamp);
            var tokens = entry.Tokens.TotalTokens;
            double? cost = entry.CostUsd;
            if (cost is null && entry.Model is { } model)
            {
                cost = pricing.EstimatedCost(model, entry.Tokens);
                if (cost is null && tokens > 0) acc.AddUnknown(day, model);
            }
            if (cost is null) continue;
            acc.Add(day, tokens, cost.Value);
        }
        return acc.Build();
    }
}
