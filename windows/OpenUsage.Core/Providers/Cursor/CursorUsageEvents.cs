using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed record CursorUsageEvent(DateTimeOffset Date, string Model, TokenBreakdown Tokens);

public sealed record CursorUsageEventsPage(IReadOnlyList<CursorUsageEvent> Events, int TotalCount, int RejectedEventCount);

public sealed record CursorUsageRow(DateTimeOffset Date, string Model, TokenBreakdown Tokens, double? ImputedCostDollars);

/// <summary>One local day of Cursor usage events, summed per model.</summary>
public sealed class CursorUsageDay
{
    public Dictionary<string, TokenBreakdown> Models { get; init; } = new(StringComparer.Ordinal);

    /// <summary>False while the day is still running; such days are refetched on the next refresh.</summary>
    public bool IsComplete { get; init; }

    public void Add(TokenBreakdown tokens, string model)
    {
        var existing = Models.GetValueOrDefault(model);
        Models[model] = new TokenBreakdown(
            Sum(existing.Input, tokens.Input),
            Sum(existing.CacheWrite5m, tokens.CacheWrite5m),
            Sum(existing.CacheWrite1h, tokens.CacheWrite1h),
            Sum(existing.CacheRead, tokens.CacheRead),
            Sum(existing.Output, tokens.Output),
            existing.IsFast || tokens.IsFast);
    }

    private static int Sum(int a, int b) => (int)Math.Min((long)a + b, int.MaxValue);
}

/// <summary>
/// Parses one page of <c>cursor.com/api/dashboard/get-filtered-usage-events</c>. The page shape
/// (total count + event list) must be valid; individual malformed events are counted as rejected
/// so the fetcher can still tell whether every event of the day arrived.
/// </summary>
public static class CursorUsageEvents
{
    public static CursorUsageEventsPage ParsePage(byte[] body)
    {
        var root = JsonUtil.ParseObject(body)
            ?? throw new InvalidDataException("Cursor usage events response is not a JSON object.");
        if (Integer(root["totalUsageEventsCount"]) is not { } total || total < 0 || total > int.MaxValue)
            throw new InvalidDataException("Cursor usage events response has no total count.");
        if (root["usageEventsDisplay"] is not JsonArray rawEvents)
            throw new InvalidDataException("Cursor usage events response has no event list.");

        var events = new List<CursorUsageEvent>();
        var rejected = 0;
        foreach (var raw in rawEvents)
        {
            if (raw is not JsonObject item ||
                Timestamp(item["timestamp"]) is not { } date ||
                item["model"] is not JsonValue modelValue ||
                !modelValue.TryGetValue<string>(out var model) ||
                Tokens(item["tokenUsage"]) is not { } tokens)
            {
                rejected++;
                continue;
            }
            events.Add(new CursorUsageEvent(date, model.Trim(), tokens));
        }
        return new CursorUsageEventsPage(events, (int)total, rejected);
    }

    private static TokenBreakdown? Tokens(JsonNode? raw)
    {
        if (raw is null) return new TokenBreakdown();
        if (raw is not JsonObject usage) return null;
        if (TokenValue(usage, "inputTokens") is not { } input ||
            TokenValue(usage, "outputTokens") is not { } output ||
            TokenValue(usage, "cacheWriteTokens") is not { } cacheWrite ||
            TokenValue(usage, "cacheReadTokens") is not { } cacheRead)
        {
            return null;
        }
        return new TokenBreakdown(Input: input, CacheWrite5m: cacheWrite, CacheRead: cacheRead, Output: output);
    }

    private static int? TokenValue(JsonObject usage, string key)
    {
        if (!usage.TryGetPropertyValue(key, out var node) || node is null) return 0;
        return Integer(node) is { } value && value >= 0 && value <= int.MaxValue ? (int)value : null;
    }

    private static DateTimeOffset? Timestamp(JsonNode? node)
    {
        long? milliseconds = node is JsonValue value && value.TryGetValue<string>(out var text)
            ? long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : Integer(node);
        if (milliseconds is not { } ms || ms < 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Whole JSON numbers only: booleans, strings, fractions, and non-finite values are rejected.</summary>
    private static long? Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out _) || value.TryGetValue<string>(out _)) return null;
        if (value.TryGetValue<long>(out var whole)) return whole;
        if (value.TryGetValue<double>(out var number) &&
            double.IsFinite(number) &&
            Math.Truncate(number) == number &&
            number >= long.MinValue && number <= long.MaxValue)
        {
            return (long)number;
        }
        return null;
    }
}
