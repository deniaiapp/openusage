using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

internal static class CursorRequestUsageMapper
{
    public static List<MetricLine>? Map(JsonObject? usage)
    {
        var requests = JsonUtil.Object(usage?["gpt-4"]);
        var limit = JsonUtil.Number(requests?["maxRequestUsage"]);
        if (limit is not > 0) return null;
        var used = Math.Max(0, JsonUtil.Number(requests?["numRequests"])
            ?? JsonUtil.Number(requests?["numRequestsTotal"]) ?? 0);
        var start = Iso8601.Parse(JsonUtil.String(usage?["startOfMonth"]))?.ToUniversalTime();
        var reset = start is { } instant && (instant.Year < 9999 || instant.Month < 12)
            ? instant.AddMonths(1) : (DateTimeOffset?)null;
        var period = reset is { } end && start is { } beginning
            ? (long)(end - beginning).TotalMilliseconds : MetricPeriod.MonthMs;
        var format = ProgressFormat.Count("requests");
        return
        [
            MetricLine.Progress("Total usage", used, limit.Value, format, reset, period),
            MetricLine.Progress("Requests", used, limit.Value, format, reset, period)
        ];
    }
}
