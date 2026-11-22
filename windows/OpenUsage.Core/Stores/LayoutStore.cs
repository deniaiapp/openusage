using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;

namespace OpenUsage.Core.Stores;

public sealed class LayoutStore
{
    public const int MaxPinsPerProvider = 2;
    private const int MaxOrderUndoSteps = 40;

    private readonly AppSettings _settings;
    private readonly List<List<string>> _orderUndo = [];
    private volatile Registry _registry;
    private Dictionary<string, WidgetDescriptor> _descriptors => _registry.Descriptors;

    public LayoutStore(AppSettings settings, IEnumerable<IProviderRuntime> providers)
    {
        _settings = settings;
        _registry = Registry.From(providers.ToArray());
        SeedNewDefaultMetrics();
    }

    /// <summary>
    /// Turns on default metrics added since this layout was saved, once, with their default
    /// On Demand and pin placement. A metric the user later turns off stays off.
    /// </summary>
    private void SeedNewDefaultMetrics()
    {
        var seeded = _settings.SeededDefaultMetrics
            ?? new HashSet<string>(DefaultLayout.MigrationBaselineMetricIds, StringComparer.Ordinal);
        var toAdd = DefaultLayout.MetricIds
            .Where(id => _descriptors.ContainsKey(id) && !seeded.Contains(id) && !_settings.EnabledMetrics.Contains(id))
            .ToList();
        foreach (var id in toAdd)
        {
            _settings.EnabledMetrics.Add(id);
            if (DefaultLayout.ExpandedMetricIds.Contains(id)) _settings.ExpandedMetrics.Add(id);
            if (DefaultLayout.PinnedMetricIds.Contains(id)) TryAddPin(id);
        }
        var allSeeded = DefaultLayout.MetricIds.All(seeded.Contains);
        if (toAdd.Count == 0 && allSeeded && _settings.SeededDefaultMetrics is not null) return;
        seeded.UnionWith(DefaultLayout.MetricIds);
        _settings.SeededDefaultMetrics = seeded;
        _settings.Save();
    }

    private bool TryAddPin(string metricId)
    {
        if (!_descriptors.TryGetValue(metricId, out var descriptor) || !descriptor.Pinnable) return false;
        var providerPins = _settings.PinnedMetrics.Count(id =>
            _descriptors.TryGetValue(id, out var d) && d.ProviderId == descriptor.ProviderId);
        if (providerPins >= MaxPinsPerProvider) return false;
        return _settings.PinnedMetrics.Add(metricId);
    }

    public IReadOnlyList<IProviderRuntime> Providers => _registry.Providers;

    internal void RegisterAccount(IProviderRuntime runtime, string providerId)
    {
        var id = runtime.Provider.Id;
        bool IsProviderGroup(string candidate) =>
            candidate == providerId || candidate.StartsWith(providerId + "@", StringComparison.Ordinal);

        var providers = Providers.ToList();
        var index = providers.FindIndex(p => p.Provider.Id == id);
        if (index >= 0) providers[index] = runtime;
        else
        {
            _orderUndo.Clear();
            var last = providers.FindLastIndex(p => IsProviderGroup(p.Provider.Id));
            providers.Insert(last < 0 ? providers.Count : last + 1, runtime);
        }

        if (!_settings.ProviderOrder.Contains(id))
        {
            var order = _settings.ProviderOrder.ToList();
            foreach (var other in providers.Where(p => p.Provider.Id != id))
                if (!order.Contains(other.Provider.Id)) order.Add(other.Provider.Id);
            var last = order.FindLastIndex(candidate => candidate == providerId
                ? IsProviderEnabled(candidate)
                : IsProviderGroup(candidate));
            order.Insert(last < 0 ? order.Count : last + 1, id);
            _settings.ProviderOrder = order;
            var firstTemplateAccount = IsAccountTemplate(providerId) &&
                !Providers.Any(p => p.Provider.Id != providerId && IsProviderGroup(p.Provider.Id));
            SeedAccountLayout(runtime, providerId, firstTemplateAccount);
            SetProviderEnabled(id, true);
        }
        else if (SeedNewAccountMetrics(runtime, providerId))
            _settings.Save();
        _registry = Registry.From(providers);
    }

    private void SeedAccountLayout(IProviderRuntime runtime, string providerId, bool inheritPins = false)
    {
        var pins = 0;
        foreach (var descriptor in runtime.WidgetDescriptors)
        {
            var templateId = providerId + descriptor.Id[runtime.Provider.Id.Length..];
            if (!DefaultLayout.MetricIds.Contains(templateId)) continue;
            _settings.EnabledMetrics.Add(descriptor.Id);
            if (DefaultLayout.ExpandedMetricIds.Contains(templateId))
                _settings.ExpandedMetrics.Add(descriptor.Id);
            if (inheritPins && descriptor.Pinnable && DefaultLayout.PinnedMetricIds.Contains(templateId) &&
                pins < MaxPinsPerProvider && _settings.PinnedMetrics.Add(descriptor.Id))
                pins++;
        }
        SeedNewAccountMetrics(runtime, providerId);
    }

    private bool SeedNewAccountMetrics(IProviderRuntime runtime, string providerId)
    {
        var seeded = _settings.SeededDefaultMetrics!;
        var changed = false;
        foreach (var descriptor in runtime.WidgetDescriptors)
        {
            var templateId = providerId + descriptor.Id[runtime.Provider.Id.Length..];
            if (!DefaultLayout.NewAccountMetricIds.Contains(templateId) || !seeded.Add(descriptor.Id))
                continue;
            if (_settings.EnabledMetrics.Add(descriptor.Id) && DefaultLayout.ExpandedMetricIds.Contains(templateId))
                _settings.ExpandedMetrics.Add(descriptor.Id);
            changed = true;
        }
        return changed;
    }

    internal void RemoveAccount(string id)
    {
        _orderUndo.Clear();
        var descriptors = Providers.FirstOrDefault(p => p.Provider.Id == id)?.WidgetDescriptors ?? [];
        _registry = Registry.From(Providers.Where(p => p.Provider.Id != id).ToArray());
        _settings.ProviderOrder = _settings.ProviderOrder.Where(provider => provider != id).ToList();
        _settings.OpenCards.Remove(id);
        foreach (var descriptor in descriptors)
        {
            _settings.EnabledMetrics.Remove(descriptor.Id);
            _settings.ExpandedMetrics.Remove(descriptor.Id);
            _settings.PinnedMetrics.Remove(descriptor.Id);
            _settings.SeededDefaultMetrics?.Remove(descriptor.Id);
        }
        SetProviderEnabled(id, false);
    }

    private IEnumerable<IProviderRuntime> OrderedProviders()
    {
        var order = _settings.ProviderOrder;
        return Providers.OrderBy(p =>
        {
            var index = order.IndexOf(p.Provider.Id);
            return index < 0 ? int.MaxValue : index;
        });
    }

    public IReadOnlyList<IProviderRuntime> VisibleProviders =>
        OrderedProviders().Where(p => IsProviderEnabled(p.Provider.Id)).ToList();

    public bool HasVisibleMetrics =>
        Providers.Any(p => IsProviderEnabled(p.Provider.Id) &&
            p.WidgetDescriptors.Any(d => IsMetricEnabled(d.Id)));

    public IGrouping<string, IProviderRuntime>[] ProviderGroups()
    {
        var order = OrderedProviders().ToList();
        return order.GroupBy(p => p.Provider.Id.Split('@', 2)[0], StringComparer.Ordinal)
            .OrderBy(group => order.IndexOf(
                group.FirstOrDefault(p => p.Provider.Id == group.Key && IsProviderEnabled(p.Provider.Id))
                ?? group.FirstOrDefault(p => p.Provider.Id != group.Key)
                ?? group.First()))
            .ToArray();
    }

    public bool MoveProviderGroup(string providerId, int offset)
    {
        var groups = ProviderGroups();
        var from = Array.FindIndex(groups, group => group.Key == providerId);
        if (from < 0 || offset is not (-1 or 1)) return false;
        var to = from + offset;
        if (to < 0 || to >= groups.Length) return false;

        (groups[from], groups[to]) = (groups[to], groups[from]);
        _orderUndo.Add(_settings.ProviderOrder.ToList());
        if (_orderUndo.Count > MaxOrderUndoSteps) _orderUndo.RemoveAt(0);
        _settings.ProviderOrder = groups.SelectMany(group => group).Select(p => p.Provider.Id).ToList();
        _settings.Save();
        return true;
    }

    public bool CanUndoProviderOrder => _orderUndo.Count > 0;

    public bool UndoProviderOrder()
    {
        if (!CanUndoProviderOrder) return false;
        _settings.ProviderOrder = _orderUndo[^1];
        _orderUndo.RemoveAt(_orderUndo.Count - 1);
        _settings.Save();
        return true;
    }

    public bool IsProviderEnabled(string providerId) =>
        _settings.EnabledProviders.Contains(providerId) && !IsAccountTemplate(providerId);

    public void SetProviderEnabled(string providerId, bool enabled)
    {
        var providers = new HashSet<string>(_settings.EnabledProviders, StringComparer.Ordinal);
        if (enabled && !IsAccountTemplate(providerId)) providers.Add(providerId);
        else providers.Remove(providerId);
        _settings.EnabledProviders = providers;
        _settings.Save();
    }

    private bool IsAccountTemplate(string providerId) =>
        Providers.Any(p => p.Provider.Id == providerId && p.IsAccountTemplate);

    public bool IsMetricEnabled(string metricId) => _settings.EnabledMetrics.Contains(metricId);

    public void SetMetricEnabled(string metricId, bool enabled)
    {
        if (enabled) _settings.EnabledMetrics.Add(metricId);
        else _settings.EnabledMetrics.Remove(metricId);
        _settings.Save();
    }

    public bool IsOnDemand(string metricId) => _settings.ExpandedMetrics.Contains(metricId);

    public void SetOnDemand(string metricId, bool onDemand)
    {
        if (onDemand) _settings.ExpandedMetrics.Add(metricId);
        else _settings.ExpandedMetrics.Remove(metricId);
        _settings.Save();
    }

    public bool IsPinned(string metricId) => _settings.PinnedMetrics.Contains(metricId);

    public bool TogglePin(string metricId)
    {
        if (!_descriptors.TryGetValue(metricId, out var descriptor) || !descriptor.Pinnable)
            return false;
        if (_settings.PinnedMetrics.Contains(metricId))
        {
            _settings.PinnedMetrics.Remove(metricId);
            _settings.Save();
            return true;
        }
        if (!TryAddPin(metricId)) return false;
        _settings.Save();
        return true;
    }

    public bool IsCardOpen(string providerId) => _settings.OpenCards.Contains(providerId);

    public void ToggleCard(string providerId)
    {
        if (!_settings.OpenCards.Add(providerId))
            _settings.OpenCards.Remove(providerId);
        _settings.Save();
    }

    public WidgetDescriptor? Descriptor(string id) =>
        _descriptors.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyList<WidgetDescriptor> VisibleMetrics(string providerId, bool includeOnDemand)
    {
        if (Providers.FirstOrDefault(p => p.Provider.Id == providerId) is not { } provider)
            return [];
        return provider.WidgetDescriptors
            .Where(d => IsMetricEnabled(d.Id))
            .Where(d => includeOnDemand || !IsOnDemand(d.Id))
            .ToList();
    }

    public IReadOnlyList<WidgetDescriptor> OnDemandMetrics(string providerId) =>
        VisibleMetrics(providerId, true).Where(d => IsOnDemand(d.Id)).ToList();

    public IReadOnlyList<WidgetDescriptor> PinnedDescriptors() =>
        _settings.PinnedMetrics
            .Select(Descriptor)
            .OfType<WidgetDescriptor>()
            .Where(d => IsProviderEnabled(d.ProviderId) && IsMetricEnabled(d.Id) && d.Pinnable)
            .ToList();

    public void ResetToDefault()
    {
        _orderUndo.Clear();
        var accountLayouts = Providers.Where(p => p.Provider.Id.Contains('@')).ToArray();
        _settings.EnabledProviders = new HashSet<string>(DefaultLayout.StarterProviders, StringComparer.Ordinal);
        _settings.EnabledMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal);
        _settings.ExpandedMetrics = new HashSet<string>(DefaultLayout.ExpandedMetricIds, StringComparer.Ordinal);
        _settings.PinnedMetrics = new HashSet<string>(DefaultLayout.PinnedMetricIds, StringComparer.Ordinal);
        _settings.SeededDefaultMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal);
        _settings.ProviderOrder = accountLayouts.Length == 0 ? [] : Providers.Select(p => p.Provider.Id).ToList();
        var templatesWithPins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runtime in accountLayouts)
        {
            _settings.EnabledProviders.Add(runtime.Provider.Id);
            var providerId = runtime.Provider.Id.Split('@')[0];
            SeedAccountLayout(runtime, providerId, IsAccountTemplate(providerId) && templatesWithPins.Add(providerId));
            SeedNewAccountMetrics(runtime, providerId);
        }
        _settings.OpenCards.Clear();
        _settings.Save();
    }

    private sealed record Registry(IReadOnlyList<IProviderRuntime> Providers, Dictionary<string, WidgetDescriptor> Descriptors)
    {
        public static Registry From(IReadOnlyList<IProviderRuntime> providers) =>
            new(providers, providers.SelectMany(p => p.WidgetDescriptors).ToDictionary(d => d.Id, StringComparer.Ordinal));
    }
}
