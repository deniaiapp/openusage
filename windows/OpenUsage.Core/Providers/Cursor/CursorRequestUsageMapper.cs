using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

internal static class CursorRequestUsageMapper
{
    public static List<MetricLine>? Map(JsonObject? usage, JsonObject? currentPeriod = null)
    {
        var requests = JsonUtil.Object(usage?["gpt-4"]);
        var limit = JsonUtil.Number(requests?["maxRequestUsage"]);
        if (limit is not > 0) return null;
        var used = Math.Max(0, JsonUtil.Number(requests?["numRequests"])
            ?? JsonUtil.Number(requests?["numRequestsTotal"]) ?? 0);
        var (reset, period) = currentPeriod is null
            ? (null, MetricPeriod.MonthMs)
            : CursorUsageMapper.BillingCycle(currentPeriod);
        if (reset is null && Iso8601.Parse(JsonUtil.String(usage?["startOfMonth"]))?.ToUniversalTime() is { } start
            && (start.Year < 9999 || start.Month < 12))
        {
            var nextMonth = start.AddMonths(1);
            reset = nextMonth;
            period = (long)(nextMonth - start).TotalMilliseconds;
        }
        var format = ProgressFormat.Count("requests");
        return
        [
            MetricLine.Progress("Total usage", used, limit.Value, format, reset, period),
            MetricLine.Progress("Requests", used, limit.Value, format, reset, period)
        ];
    }
}
