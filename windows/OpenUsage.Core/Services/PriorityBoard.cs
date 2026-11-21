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
    public static PriorityBoard Make(LayoutStore layout, WidgetDataStore store, DateTimeOffset now)
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
        var runningOut = new List<PriorityMetric>();
        var usedUp = new List<PriorityMetric>();
        foreach (var metric in metrics)
        {
            var state = metric.Data.GetMeterState(now);
            if (state.Kind == MeterStateKind.Spent)
                usedUp.Add(metric);
            else if (metric.Descriptor.Pinnable && layout.IsPinned(metric.Descriptor.Id))
                pinned.Add(metric);
            else if (metric.Data.Limit is > 0 && (state.Severity is MeterSeverity.Warning or MeterSeverity.Critical))
                runningOut.Add(metric);
        }
        return new(pinned,
            runningOut.OrderBy(m => 1 - m.Data.Used / m.Data.Limit!.Value).ToList(),
            usedUp.OrderBy(m => m.Data.ResetsAt ?? DateTimeOffset.MaxValue).ToList(),
            waiting, metrics.Count > 0, hasRefreshErrors);
    }
}
