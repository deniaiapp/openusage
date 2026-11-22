using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;

namespace OpenUsage.Core.Services;

public sealed record PriorityMetric(Provider Provider, WidgetDescriptor Descriptor, WidgetData Data);

public sealed record PriorityBoard(
    IReadOnlyList<PriorityMetric> Pinned,
    IReadOnlyList<PriorityMetric> RunningOut,
    IReadOnlyList<PriorityMetric> UsedUp,
    bool HasUsage,
    bool HasRefreshErrors)
{
    public IReadOnlyList<PriorityMetric> UseItOrLoseIt { get; init; } = [];

    public static PriorityBoard Make(LayoutStore layout, WidgetDataStore store, DateTimeOffset now,
        bool showUnusedQuota = true)
    {
        var providers = layout.VisibleProviders;
        var metrics = (from runtime in providers
                       from descriptor in layout.VisibleMetrics(runtime.Provider.Id, includeOnDemand: true)
                       let data = store.Resolve(descriptor)
                       where data.HasData
                       select new PriorityMetric(runtime.Provider, descriptor, data)).ToList();
        var pinned = new List<PriorityMetric>();
        var runningOut = new List<PriorityMetric>();
        var usedUp = new List<PriorityMetric>();
        var unused = new List<PriorityMetric>();
        foreach (var metric in metrics)
        {
            var state = metric.Data.GetMeterState(now);
            if (state.Kind == MeterStateKind.Spent)
                usedUp.Add(metric);
            else if (metric.Descriptor.Pinnable && layout.IsPinned(metric.Descriptor.Id))
                pinned.Add(metric);
            else if (metric.Data.Limit is > 0 && (state.Severity is MeterSeverity.Warning or MeterSeverity.Critical))
                runningOut.Add(metric);
            else if (showUnusedQuota && IsUnusedNearReset(metric.Data, now))
                unused.Add(metric);
        }
        return new(pinned,
            runningOut.OrderBy(m => 1 - m.Data.Used / m.Data.Limit!.Value).ToList(),
            usedUp.OrderBy(m => m.Data.ResetsAt ?? DateTimeOffset.MaxValue).ToList(),
            metrics.Count > 0,
            providers.Any(runtime => store.ProviderError(runtime.Provider.Id) is not null))
        {
            UseItOrLoseIt = unused.OrderBy(m => m.Data.ResetsAt).ToList()
        };
    }

    private static bool IsUnusedNearReset(WidgetData data, DateTimeOffset now) =>
        data.Kind != MetricKind.Dollars && data.Limit is > 0 and var limit
        && data.PeriodDurationMs > MetricPeriod.DayMs && data.ResetsAt is { } reset
        && reset > now && reset - now <= TimeSpan.FromDays(1)
        && Math.Round((1 - data.Used / limit) * 100, MidpointRounding.AwayFromZero) > 50;
}
