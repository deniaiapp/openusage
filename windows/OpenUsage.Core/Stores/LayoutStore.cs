using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;

namespace OpenUsage.Core.Stores;

public sealed class LayoutStore
{
    public const int MaxPinsPerProvider = 2;

    private readonly AppSettings _settings;
    private volatile Registry _registry;
    private Dictionary<string, WidgetDescriptor> _descriptors => _registry.Descriptors;

    public LayoutStore(AppSettings settings, IEnumerable<IProviderRuntime> providers)
    {
        _settings = settings;
        _registry = Registry.From(providers.ToArray());
        if (_settings.EnabledMetrics.Count == 0)
            _settings.EnabledMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal);
        if (_settings.PinnedMetrics.Count == 0)
            _settings.PinnedMetrics = new HashSet<string>(DefaultLayout.PinnedMetricIds, StringComparer.Ordinal);
        if (_settings.ExpandedMetrics.Count == 0)
            _settings.ExpandedMetrics = new HashSet<string>(DefaultLayout.ExpandedMetricIds, StringComparer.Ordinal);
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
        var providers = Providers.ToList();
        var index = providers.FindIndex(p => p.Provider.Id == id);
        if (index >= 0) providers[index] = runtime;
        else providers.Add(runtime);

        if (!_settings.ProviderOrder.Contains(id))
        {
            var order = _settings.ProviderOrder.ToList();
            foreach (var other in providers.Where(p => p.Provider.Id != id))
                if (!order.Contains(other.Provider.Id)) order.Add(other.Provider.Id);
            order.Add(id);
            _settings.ProviderOrder = order;
            SeedAccountLayout(runtime, providerId);
            SetProviderEnabled(id, true);
        }
        _registry = Registry.From(providers);
    }

    private void SeedAccountLayout(IProviderRuntime runtime, string providerId)
    {
        foreach (var descriptor in runtime.WidgetDescriptors)
        {
            var templateId = providerId + descriptor.Id[runtime.Provider.Id.Length..];
            if (!DefaultLayout.MetricIds.Contains(templateId)) continue;
            _settings.EnabledMetrics.Add(descriptor.Id);
            if (DefaultLayout.ExpandedMetricIds.Contains(templateId))
                _settings.ExpandedMetrics.Add(descriptor.Id);
        }
    }

    internal void RemoveAccount(string id)
    {
        var descriptors = Providers.FirstOrDefault(p => p.Provider.Id == id)?.WidgetDescriptors ?? [];
        _registry = Registry.From(Providers.Where(p => p.Provider.Id != id).ToArray());
        _settings.ProviderOrder = _settings.ProviderOrder.Where(provider => provider != id).ToList();
        _settings.OpenCards.Remove(id);
        foreach (var descriptor in descriptors)
        {
            _settings.EnabledMetrics.Remove(descriptor.Id);
            _settings.ExpandedMetrics.Remove(descriptor.Id);
            _settings.PinnedMetrics.Remove(descriptor.Id);
        }
        SetProviderEnabled(id, false);
    }

    public IReadOnlyList<IProviderRuntime> VisibleProviders
    {
        get
        {
            var enabled = Providers.Where(p => IsProviderEnabled(p.Provider.Id)).ToList();
            var order = _settings.ProviderOrder;
            if (order.Count == 0) return enabled;
            return enabled
                .OrderBy(p =>
                {
                    var index = order.IndexOf(p.Provider.Id);
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();
        }
    }

    public bool IsProviderEnabled(string providerId) => _settings.EnabledProviders.Contains(providerId);

    public void SetProviderEnabled(string providerId, bool enabled)
    {
        var providers = new HashSet<string>(_settings.EnabledProviders, StringComparer.Ordinal);
        if (enabled) providers.Add(providerId);
        else providers.Remove(providerId);
        _settings.EnabledProviders = providers;
        _settings.Save();
    }

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
        var accountLayouts = Providers.Where(p => p.Provider.Id.Contains('@')).ToArray();
        _settings.EnabledProviders = new HashSet<string>(DefaultLayout.StarterProviders, StringComparer.Ordinal);
        _settings.EnabledMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal);
        _settings.ExpandedMetrics = new HashSet<string>(DefaultLayout.ExpandedMetricIds, StringComparer.Ordinal);
        _settings.PinnedMetrics = new HashSet<string>(DefaultLayout.PinnedMetricIds, StringComparer.Ordinal);
        _settings.SeededDefaultMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal);
        _settings.ProviderOrder = accountLayouts.Length == 0 ? [] : Providers.Select(p => p.Provider.Id).ToList();
        foreach (var runtime in accountLayouts)
        {
            _settings.EnabledProviders.Add(runtime.Provider.Id);
            SeedAccountLayout(runtime, runtime.Provider.Id.Split('@')[0]);
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
