using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;

namespace OpenUsage.Core.Services;

public sealed record PriorityMetric(Provider Provider, WidgetDescriptor Descriptor, WidgetData Data);

public sealed record PriorityBoard(
    IReadOnlyList<PriorityMetric> Pinned,
    IReadOnlyList<PriorityMetric> RunningOut,
    IReadOnlyList<PriorityMetric> UsedUp,
    IReadOnlyList<Provider> WaitingForFirstRead,
    bool HasUsage,
    bool HasRefreshErrors)
{
    public IReadOnlyList<PriorityMetric> UseItOrLoseIt { get; init; } = [];

    public static PriorityBoard Make(LayoutStore layout, WidgetDataStore store, DateTimeOffset now,
        bool showUnusedQuota = true)
    {
        var providers = layout.VisibleProviders;
        var waiting = new List<Provider>();
        var hasRefreshErrors = false;
        foreach (var runtime in providers)
        {
            if (store.ProviderError(runtime.Provider.Id) is not null)
                hasRefreshErrors = true;
            else if (store.Snapshot(runtime.Provider.Id) is null)
                waiting.Add(runtime.Provider);
        }
        var metrics = (from runtime in providers
                       from descriptor in layout.VisibleMetrics(runtime.Provider.Id, includeOnDemand: true)
                       let data = store.Resolve(descriptor)
                       where data.HasData
                       select new PriorityMetric(runtime.Provider, descriptor, data)).ToList();
        var pinned = new List<PriorityMetric>();
        var runningOut = new List<(PriorityMetric Metric, (int Group, double Value) Rank)>();
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
                runningOut.Add((metric, RunningOutRank(metric.Data, state)));
            else if (showUnusedQuota && IsUnusedNearReset(metric.Data, now))
                unused.Add(metric);
        }
        return new(pinned,
            runningOut.OrderBy(entry => entry.Rank).Select(entry => entry.Metric).ToList(),
            usedUp.OrderBy(m => m.Data.ResetsAt ?? DateTimeOffset.MaxValue).ToList(),
            waiting, metrics.Count > 0, hasRefreshErrors)
        {
            UseItOrLoseIt = unused.OrderBy(m => m.Data.ResetsAt).ToList()
        };
    }

    private static (int Group, double Value) RunningOutRank(WidgetData data, MeterState state)
    {
        var remaining = 1 - data.Used / data.Limit!.Value;
        if (state.ProjectedFraction > 0 && double.IsFinite(state.ProjectedFraction)
            && data.PeriodDurationMs is > 0 and var duration)
            return (0, remaining * duration / state.ProjectedFraction);
        return (1, remaining);
    }

    private static bool IsUnusedNearReset(WidgetData data, DateTimeOffset now) =>
        data.Kind != MetricKind.Dollars && data.Limit is > 0 and var limit
        && data.PeriodDurationMs > MetricPeriod.DayMs && data.ResetsAt is { } reset
        && reset > now && reset - now <= TimeSpan.FromDays(1)
        && Math.Round((1 - data.Used / limit) * 100, MidpointRounding.AwayFromZero) > 50;
}
