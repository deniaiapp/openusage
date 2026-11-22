using OpenUsage.Core.Support;

namespace OpenUsage.Core.Models;

internal static class UnifiedMetrics
{
    internal static IReadOnlyList<string> MergeOrder(IEnumerable<IReadOnlyList<string>> orders)
    {
        var merged = new List<string>();
        foreach (var order in orders)
        {
            for (var index = 0; index < order.Count; index++)
            {
                var metric = order[index];
                if (merged.Contains(metric)) continue;
                var next = order.Skip(index + 1).Select(key => merged.IndexOf(key)).FirstOrDefault(i => i >= 0, -1);
                merged.Insert(next < 0 ? merged.Count : next, metric);
            }
        }
        return merged;
    }

    internal static WidgetData? Pool(IEnumerable<WidgetData> readings)
    {
        var members = readings.Where(reading => reading.HasData && !reading.IsChart).ToArray();
        if (members.Length == 0) return null;

        var first = members[0];
        if (members.All(reading => reading.Limit is > 0 && reading.Kind == first.Kind &&
            CurrencyKey(reading.Kind, reading.CurrencyCode) == CurrencyKey(first.Kind, first.CurrencyCode) &&
            reading.CountSuffix == first.CountSuffix &&
            reading.ValueTextOverride is null))
        {
            var limit = members.Sum(reading => reading.Limit!.Value);
            var used = members.Sum(reading => Math.Min(reading.Used, reading.Limit!.Value));
            var scale = first.Kind == MetricKind.Percent ? 100 / limit : 1;
            var pooled = WithoutPace(first) with
            {
                Used = used * scale,
                Limit = limit * scale,
                ResetsAt = members.Min(reading => reading.ResetsAt),
                SubtitleOverride = null
            };
            if (pooled.IsSpent && members.Any(reading => !reading.IsSpent))
                pooled = pooled with { Used = Math.Max(0, pooled.Limit!.Value - DisplayUnit(pooled)) };
            return pooled;
        }

        var template = members.FirstOrDefault(reading => !reading.IsBounded) ?? first;
        var labels = members.Select(reading => reading.ValueTextOverride).OfType<string>().Distinct().ToArray();
        var values = members.SelectMany(ValuesFor)
            .GroupBy(value => (value.Label, value.Kind, CurrencyKey(value.Kind, value.CurrencyCode)))
            .Select(group => group.First() with { Number = group.Sum(value => value.Number) })
            .ToArray();
        return WithoutPace(template) with
        {
            Limit = null,
            ResetsAt = null,
            Used = 0,
            Values = labels.Length == 0 ? values : [],
            ValueTextOverride = labels.Length == 0 ? null : string.Join(" · ", labels),
            ExpiriesAt = members.SelectMany(reading => reading.ExpiriesAt).Order().ToArray(),
            HasData = values.Length != 0 || labels.Length != 0
        };
    }

    private static WidgetData WithoutPace(WidgetData reading) => reading with
    {
        PeriodDurationMs = null, SessionStartSignal = null
    };

    private static IEnumerable<MetricValue> ValuesFor(WidgetData reading)
    {
        if (!reading.IsBounded) return reading.Values;
        return [new(reading.Used, reading.Kind, reading.CountSuffix, reading.CurrencyCode)];
    }

    private static string? CurrencyKey(MetricKind kind, string? currency) =>
        kind == MetricKind.Dollars ? currency ?? "USD" : null;

    private static double DisplayUnit(WidgetData reading) => reading.Kind switch
    {
        MetricKind.Percent => 1,
        MetricKind.Count => 0.1,
        MetricKind.Dollars when reading.CurrencyCode is not (null or "USD") =>
            Math.Pow(10, -MetricFormatter.NonUsdFractionDigits),
        _ => 0.01
    };
}
