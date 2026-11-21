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
        DateTimeOffset? reset = start is { } instant && instant <= DateTimeOffset.MaxValue.AddMilliseconds(-MetricPeriod.MonthMs)
            ? instant.AddMilliseconds(MetricPeriod.MonthMs) : null;
        var format = ProgressFormat.Count("requests");
        return
        [
            MetricLine.Progress("Total usage", used, limit.Value, format, reset, MetricPeriod.MonthMs),
            MetricLine.Progress("Requests", used, limit.Value, format, reset, MetricPeriod.MonthMs)
        ];
    }
}
