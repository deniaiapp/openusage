using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

public static class AntigravityProtoDecoder
{
    public sealed record GenerationEvent(string? ModelId, string? Label, int InputTokens, int OutputTokens, int CacheReadTokens, long TimestampSeconds);

    public static GenerationEvent? GenerationEventFrom(byte[] blob, byte[]? stepMetadata = null)
    {
        if (BytesField(1, blob) is not { } wrapped) return null;
        var modelId = TrimmedString(19, wrapped);
        var label = TrimmedString(21, wrapped);
        if (BytesField(4, wrapped) is not { } usage) return null;
        var systemPrompt = (int)(VarintField(1, usage) ?? 0);
        var input = (int)(VarintField(2, usage) ?? 0);
        var output = (int)(VarintField(3, usage) ?? 0);
        var cacheRead = (int)(VarintField(5, usage) ?? 0);
        var billableInput = systemPrompt + input;
        var generated = input != 0 || output != 0 || cacheRead != 0;
        if ((modelId is null && label is null && !generated) || (!generated && billableInput == 0)) return null;
        long? timestamp = null;
        if (BytesField(9, wrapped) is { } timing)
            timestamp = TimestampSeconds(BytesField(4, timing));
        timestamp ??= TimestampFromStep(stepMetadata);
        if (timestamp is null or <= 0) return null;
        return new GenerationEvent(modelId, label, billableInput, output, cacheRead, timestamp.Value);
    }

    private static long? TimestampFromStep(byte[]? stepMetadata)
    {
        if (stepMetadata is null) return null;
        return TimestampSeconds(BytesField(1, stepMetadata));
    }

    private static long? TimestampSeconds(byte[]? message)
    {
        if (message is null) return null;
        var seconds = VarintField(1, message);
        return seconds is { } value and > 0 ? (long)value : null;
    }

    private static string? TrimmedString(uint number, byte[] message)
    {
        var bytes = BytesField(number, message);
        if (bytes is null) return null;
        var text = Encoding.UTF8.GetString(bytes).Trim();
        return text.Length == 0 ? null : text;
    }

    private static byte[]? BytesField(uint number, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!TryVarint(bytes, offset, out var tag, out var next)) return null;
            var field = (uint)(tag >> 3);
            var wire = tag & 0x7;
            offset = next;
            switch (wire)
            {
                case 0:
                    if (!TryVarint(bytes, offset, out var value, out next)) return null;
                    if (field == number) return null;
                    offset = next;
                    break;
                case 2:
                    if (!TryVarint(bytes, offset, out var length, out next)) return null;
                    offset = next;
                    if (length > (ulong)(bytes.Length - offset)) return null;
                    var count = (int)length;
                    if (field == number) return bytes[offset..(offset + count)];
                    offset += count;
                    break;
                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static ulong? VarintField(uint number, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!TryVarint(bytes, offset, out var tag, out var next)) return null;
            var field = (uint)(tag >> 3);
            var wire = tag & 0x7;
            offset = next;
            switch (wire)
            {
                case 0:
                    if (!TryVarint(bytes, offset, out var value, out next)) return null;
                    if (field == number) return value;
                    offset = next;
                    break;
                case 2:
                    if (!TryVarint(bytes, offset, out var length, out next)) return null;
                    offset = next + (int)length;
                    break;
                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool TryVarint(byte[] bytes, int offset, out ulong value, out int next)
    {
        value = 0;
        next = offset;
        for (var i = 0; i < 10; i++)
        {
            if (offset + i >= bytes.Length) return false;
            var b = bytes[offset + i];
            value |= (ulong)(b & 0x7f) << (7 * i);
            if ((b & 0x80) == 0)
            {
                next = offset + i + 1;
                return true;
            }
        }
        return false;
    }
}

public static class AntigravityDbUsageScanner
{
    private const int MaxBlobBytes = 1_048_576;
    private const int BatchSize = 8;

    public static LogUsageScan? Scan(int daysBack = 30, DateTimeOffset? now = null, ModelPricing? pricing = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var since = DailyUsageAccumulator.Since(daysBack, current);
        var price = pricing ?? ModelPricing.Shared;
        var gemini = AppPaths.Expand("~/.gemini");
        if (!Directory.Exists(gemini)) return null;
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(gemini, "antigravity*")
                .Select(d => Path.Combine(d, "conversations"))
                .Where(Directory.Exists)
                .ToArray();
        }
        catch { return null; }

        var dbs = dirs.SelectMany(d =>
        {
            try { return Directory.GetFiles(d, "*.db", SearchOption.AllDirectories); }
            catch { return []; }
        }).ToList();
        if (dbs.Count == 0) return null;

        var acc = new DailyUsageAccumulator();
        var any = false;
        foreach (var db in dbs)
        {
            var includeSteps = SqliteReader.QueryValue(db, "SELECT 1 FROM pragma_table_info('steps') WHERE name = 'metadata'") is not null;
            var after = 0;
            while (true)
            {
                var json = SqliteReader.QueryValue(db, DataSql(after, includeSteps));
                if (string.IsNullOrWhiteSpace(json) || json == "[]") break;
                var array = JsonNode.Parse(json) as JsonArray;
                if (array is null || array.Count == 0) break;
                any = true;
                foreach (var node in array.OfType<JsonObject>())
                {
                    after = Math.Max(after, (int)(JsonUtil.Number(node["index"]) ?? 0));
                    var hex = JsonUtil.String(node["hex"]);
                    if (hex is null) continue;
                    byte[] blob;
                    try { blob = Convert.FromHexString(hex); }
                    catch { continue; }
                    byte[]? step = null;
                    if (JsonUtil.String(node["step_hex"]) is { } stepHex)
                    {
                        try { step = Convert.FromHexString(stepHex); }
                        catch { /* ignore */ }
                    }
                    var ev = AntigravityProtoDecoder.GenerationEventFrom(blob, step);
                    if (ev is null) continue;
                    var timestamp = DateTimeOffset.FromUnixTimeSeconds(ev.TimestampSeconds);
                    if (timestamp < since) continue;
                    var model = ev.Label ?? ev.ModelId;
                    if (string.IsNullOrWhiteSpace(model)) continue;
                    var tokens = new TokenBreakdown(
                        Math.Max(0, ev.InputTokens - ev.CacheReadTokens),
                        0, 0, ev.CacheReadTokens, ev.OutputTokens);
                    var cost = price.EstimatedCost(model, tokens) ?? price.EstimatedCost(ev.ModelId ?? model, tokens);
                    var day = DailyUsageAccumulator.DayKey(timestamp);
                    if (cost is null)
                    {
                        acc.AddUnknown(day, model);
                        continue;
                    }
                    acc.Add(day, tokens.TotalTokens, cost.Value);
                }
                if (array.Count < BatchSize) break;
            }
        }
        return any ? acc.Build() : null;
    }

    private static string DataSql(int after, bool includeSteps)
    {
        var stepColumn = includeSteps
            ? ", 'step_hex', (SELECT CASE WHEN length(metadata) <= " + MaxBlobBytes + " THEN hex(metadata) ELSE NULL END FROM steps WHERE idx = g.idx)"
            : "";
        return $"""
            SELECT json_group_array(json_object('index', g.idx, 'hex',
                CASE WHEN length(g.data) <= {MaxBlobBytes} THEN hex(g.data) ELSE NULL END{stepColumn}))
            FROM (
                SELECT idx, data FROM gen_metadata
                WHERE idx > {after} AND data IS NOT NULL
                ORDER BY idx
                LIMIT {BatchSize}
            ) g
            """;
    }
}
