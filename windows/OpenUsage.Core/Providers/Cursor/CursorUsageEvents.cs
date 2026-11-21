using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed record CursorUsageEvent(DateTimeOffset Date, TokenBreakdown Tokens, double ChargedCents);

public sealed record CursorUsageEventsPage(IReadOnlyList<CursorUsageEvent> Events, int TotalCount, int RejectedEventCount);

/// <summary>One local day of Cursor usage events.</summary>
public sealed class CursorUsageDay
{
    public long TotalTokens { get; set; }
    public double ChargedCents { get; set; }

    /// <summary>False while the day is still running; such days are refetched on the next refresh.</summary>
    public bool IsComplete { get; init; }

    public void Add(CursorUsageEvent item)
    {
        var tokens = item.Tokens;
        TotalTokens += (long)tokens.Input + tokens.CacheWrite5m + tokens.CacheRead + tokens.Output;
        ChargedCents += item.ChargedCents;
    }
}

/// <summary>
/// Parses one page of <c>cursor.com/api/dashboard/get-filtered-usage-events</c>. The page shape
/// (total count + event list) must be valid; individual malformed events are counted as rejected
/// so the fetcher can still tell whether every event of the day arrived.
/// </summary>
public static class CursorUsageEvents
{
    private const string ChargeableKind = "USAGE_EVENT_KIND_USAGE_BASED";

    public static CursorUsageEventsPage ParsePage(byte[] body)
    {
        var root = JsonUtil.ParseObject(body)
            ?? throw new InvalidDataException("Cursor usage events response is not a JSON object.");
        if (Integer(root["totalUsageEventsCount"]) is not { } total || total < 0 || total > int.MaxValue)
            throw new InvalidDataException("Cursor usage events response has no total count.");
        if (total == 0 && !root.ContainsKey("usageEventsDisplay"))
            return new CursorUsageEventsPage([], 0, 0);
        if (root["usageEventsDisplay"] is not JsonArray rawEvents)
            throw new InvalidDataException("Cursor usage events response has no event list.");

        var events = new List<CursorUsageEvent>();
        var rejected = 0;
        foreach (var raw in rawEvents)
        {
            if (raw is not JsonObject item ||
                Timestamp(item["timestamp"]) is not { } date ||
                Tokens(item["tokenUsage"]) is not { } tokens ||
                ChargedCents(item) is not { } cents)
            {
                rejected++;
                continue;
            }
            events.Add(new CursorUsageEvent(date, tokens, cents));
        }
        return new CursorUsageEventsPage(events, (int)total, rejected);
    }

    private static double? ChargedCents(JsonObject item)
    {
        if (JsonUtil.String(item["kind"]) != ChargeableKind ||
            item["isChargeable"] is JsonValue flag && flag.TryGetValue<bool>(out var chargeable) && !chargeable ||
            !item.TryGetPropertyValue("chargedCents", out var raw))
        {
            return 0;
        }

        return JsonUtil.Number(raw) is >= 0 and var cents ? cents : null;
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
