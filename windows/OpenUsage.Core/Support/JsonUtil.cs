using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenUsage.Core.Support;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonObject? ParseObject(byte[] body) => ParseObject(Encoding.UTF8.GetString(body));

    public static JsonObject? Object(JsonNode? node) => node as JsonObject;

    public static JsonArray? Array(JsonNode? node) => node as JsonArray;

    public static string? String(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            var trimmed = s.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        return null;
    }

    public static bool? Bool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    public static double? Number(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<decimal>(out var m)) return (double)m;
            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                double.IsFinite(parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    public static double ClampPercent(double value) => Math.Clamp(value, 0, 100);

    public static double CentsToDollars(double cents) => cents / 100.0;

    public static string TitleCase(string raw, bool underscores = true)
    {
        var parts = raw.Split(underscores ? ['_', '-', ' '] : [' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }
}
