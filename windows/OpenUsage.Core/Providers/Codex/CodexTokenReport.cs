using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

internal static class CodexTokenReport
{
    public static Dictionary<DateOnly, double>? Parse(JsonObject? body)
    {
        if (JsonUtil.Array(body?["data"]) is not { } records)
            return null;

        var counts = new Dictionary<DateOnly, double>();
        foreach (var record in records)
        {
            if (record is not JsonObject row ||
                JsonUtil.String(row["date"]) is not { Length: >= 10 } date ||
                !DateOnly.TryParseExact(date.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) ||
                JsonUtil.Object(row["totals"]) is not { } totals)
                return null;

            if (totals["text_total_tokens"] is not { } tokenCount)
                continue;
            if (JsonUtil.Number(tokenCount) is not (>= 0 and var count))
                return null;

            var sum = counts.GetValueOrDefault(day) + count;
            if (!double.IsFinite(sum))
                return null;
            counts[day] = sum;
        }
        return counts;
    }
}
