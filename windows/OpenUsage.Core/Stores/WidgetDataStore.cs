using System.Collections.Concurrent;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed class WidgetDataStore
{
    private readonly LayoutStore _layout;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, ProviderSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _providerRefreshes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string? _cacheDir;
    private DateTimeOffset _lastRefresh;
    private DateTimeOffset _nextRefresh;
    private bool _refreshing;

    /// <param name="cacheDir">Snapshot cache folder; defaults to the app's cache. Tests point it elsewhere.</param>
    public WidgetDataStore(LayoutStore layout, AppSettings settings, string? cacheDir = null)
    {
        _layout = layout;
        _settings = settings;
        _cacheDir = cacheDir;
        _nextRefresh = DateTimeOffset.Now;
        LoadCache();
    }

    public event Action? Changed;

    public DateTimeOffset LastRefresh => _lastRefresh;
    public DateTimeOffset NextRefresh => _nextRefresh;
    public bool IsRefreshing => _refreshing;

    public ProviderSnapshot? Snapshot(string providerId) =>
        _snapshots.TryGetValue(providerId, out var snapshot) ? snapshot : null;

    public string? ProviderError(string providerId) =>
        _errors.TryGetValue(providerId, out var message) ? message : null;

    public WidgetData Resolve(WidgetDescriptor descriptor)
    {
        var line = Snapshot(descriptor.ProviderId)?.Line(descriptor.MetricLabel);
        return WidgetData.Resolve(descriptor, line, _settings.MeterStyle, _settings.ResetDisplay, _settings.AlwaysShowPacing);
    }

    public IReadOnlyList<(WidgetDescriptor Descriptor, WidgetData Data)> PinnedTiles()
    {
        var tiles = new List<(WidgetDescriptor, WidgetData)>();
        foreach (var descriptor in _layout.PinnedDescriptors())
        {
            var data = Resolve(descriptor);
            if (!data.HasData) continue;
            tiles.Add((descriptor, data));
        }
        return tiles;
    }

    public async Task RefreshAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_refreshing) return;
            _refreshing = true;
        }
        Raise();
        try
        {
            await Task.WhenAll(_layout.VisibleProviders.Select(provider => RefreshAsync(provider, cancellationToken)));
            _lastRefresh = DateTimeOffset.Now;
            _nextRefresh = _lastRefresh.AddSeconds(Math.Max(30, _settings.RefreshIntervalSeconds));
        }
        finally
        {
            lock (_gate) _refreshing = false;
            Raise();
        }
    }

    public async Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = _layout.Providers.FirstOrDefault(p => p.Provider.Id == providerId);
        if (provider is null) return;
        await RefreshAsync(provider, cancellationToken);
        Raise();
    }

    private Task RefreshAsync(IProviderRuntime provider, CancellationToken cancellationToken) =>
        _providerRefreshes.GetOrAdd(provider.Provider.Id, id =>
            new Lazy<Task>(() => RefreshCoreAsync(id, provider, cancellationToken))).Value;

    private async Task RefreshCoreAsync(string providerId, IProviderRuntime provider, CancellationToken cancellationToken)
    {
        try
        {
            ApplySnapshot(providerId, await provider.RefreshAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            AppLog.Error(providerId, "refresh failed", ex);
            RememberFailure(providerId, ex.Message);
        }
        finally
        {
            _providerRefreshes.TryRemove(providerId, out _);
        }
    }

    private void ApplySnapshot(string providerId, ProviderSnapshot snapshot)
    {
        if (snapshot.IsError)
        {
            var message = snapshot.Lines.OfType<MetricLine.BadgeLine>().FirstOrDefault()?.Message
                ?? "Refresh failed.";
            RememberFailure(providerId, message);
            return;
        }
        _errors.TryRemove(providerId, out _);
        _snapshots[providerId] = snapshot;
        SaveCache(snapshot);
    }

    private void RememberFailure(string providerId, string message)
    {
        _errors[providerId] = message;
        AppLog.Warn(providerId, $"refresh failed; keeping last-good snapshot: {message}");
    }

    private void Raise() => Changed?.Invoke();

    private void LoadCache()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.json"))
            {
                using var source = AtomicFile.OpenRead(file);
                var json = JsonSerializer.Deserialize<CachedSnapshot>(source);
                if (json is null) continue;
                var lines = json.Lines.Select(CachedLine.ToMetric).OfType<MetricLine>().ToList();
                _snapshots[json.ProviderId] = new ProviderSnapshot(
                    json.ProviderId, json.DisplayName, lines, json.RefreshedAt, json.Plan, json.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("cache", $"could not load snapshot cache: {ex.Message}");
        }
    }

    private string CacheDir => _cacheDir ?? AppPaths.CacheDir;

    private void SaveCache(ProviderSnapshot snapshot)
    {
        if (snapshot.IsError) return;
        try
        {
            var cached = new CachedSnapshot
            {
                ProviderId = snapshot.ProviderId,
                DisplayName = snapshot.DisplayName,
                Plan = snapshot.Plan,
                Warning = snapshot.Warning,
                RefreshedAt = snapshot.RefreshedAt,
                Lines = snapshot.Lines.Select(CachedLine.FromMetric).ToList()
            };
            var path = Path.Combine(CacheDir, $"{snapshot.ProviderId}.json");
            var descriptors = _layout.Providers.FirstOrDefault(p => p.Provider.Id == snapshot.ProviderId)?.WidgetDescriptors;
            cached.Limits = LimitsProjection.Project(snapshot, descriptors ?? []);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("cache", $"could not save snapshot: {ex.Message}");
        }
    }

    private sealed class CachedSnapshot
    {
        public string ProviderId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Plan { get; set; }
        public string? Warning { get; set; }
        public DateTimeOffset RefreshedAt { get; set; }
        public List<CachedLine> Lines { get; set; } = [];
        public Dictionary<string, LimitResource>? Limits { get; set; }
    }

    private sealed class CachedLine
    {
        public string Type { get; set; } = "";
        public string Label { get; set; } = "";
        public string? Value { get; set; }
        public string? Text { get; set; }
        public double Used { get; set; }
        public double Limit { get; set; }
        public string? Format { get; set; }
        public string? Suffix { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public long? PeriodDurationMs { get; set; }
        public string? ColorHex { get; set; }
        public string? Subtitle { get; set; }
        public List<CachedValue> Values { get; set; } = [];
        public List<DateTimeOffset> ExpiriesAt { get; set; } = [];

        public static CachedLine FromMetric(MetricLine line) => line switch
        {
            MetricLine.ProgressLine p => new CachedLine
            {
                Type = "progress",
                Label = p.Label,
                Used = p.Used,
                Limit = p.Limit,
                Format = p.Format.FormatKind.ToString().ToLowerInvariant(),
                Suffix = p.Format.Suffix,
                ResetsAt = p.ResetsAt,
                PeriodDurationMs = p.PeriodDurationMs,
                ColorHex = p.ColorHex
            },
            MetricLine.ValuesLine v => new CachedLine
            {
                Type = "values",
                Label = v.Label,
                Values = v.Items.Select(x => new CachedValue { Number = x.Number, Kind = x.Kind.ToString(), Label = x.Label }).ToList(),
                ColorHex = v.ColorHex,
                ExpiriesAt = v.ExpiriesAt?.ToList() ?? []
            },
            MetricLine.BadgeLine b => new CachedLine
            {
                Type = "badge",
                Label = b.Label,
                Text = b.Message,
                ColorHex = b.ColorHex,
                Subtitle = b.Subtitle
            },
            MetricLine.TextLine t => new CachedLine
            {
                Type = "text",
                Label = t.Label,
                Value = t.Value,
                ColorHex = t.ColorHex,
                Subtitle = t.Subtitle
            },
            _ => new CachedLine { Type = "text", Label = line.Label, Value = "" }
        };

        public static MetricLine? ToMetric(CachedLine line) => line.Type switch
        {
            "progress" => MetricLine.Progress(
                line.Label, line.Used, line.Limit,
                line.Format == "dollars" ? ProgressFormat.Dollars
                    : line.Format == "count" ? ProgressFormat.Count(line.Suffix ?? "")
                    : ProgressFormat.Percent,
                line.ResetsAt, line.PeriodDurationMs, line.ColorHex),
            "values" => MetricLine.Values(
                line.Label,
                line.Values.Select(v => new MetricValue(v.Number, Enum.Parse<MetricKind>(v.Kind, true), v.Label)).ToList(),
                line.ColorHex,
                line.ExpiriesAt),
            "badge" => MetricLine.Badge(line.Label, line.Text ?? "", line.ColorHex, line.Subtitle),
            "text" => MetricLine.Text(line.Label, line.Value ?? "", line.ColorHex, line.Subtitle),
            _ => null
        };
    }

    private sealed class CachedValue
    {
        public double Number { get; set; }
        public string Kind { get; set; } = "Dollars";
        public string? Label { get; set; }
    }
}
