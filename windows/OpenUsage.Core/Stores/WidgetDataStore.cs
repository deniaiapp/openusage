using System.Collections.Concurrent;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed class WidgetDataStore
{
    private static readonly int[] OfflineRetrySeconds = [2, 4, 8, 16, 32];

    private readonly LayoutStore _layout;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, ProviderSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _providerRefreshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshBackoff> _backoffs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string? _cacheDir;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshTimeout;
    private DateTimeOffset _lastRefresh;
    private DateTimeOffset _nextRefresh;
    private bool _refreshing;

    /// <param name="cacheDir">Snapshot cache folder; defaults to the app's cache. Tests point it elsewhere.</param>
    public WidgetDataStore(LayoutStore layout, AppSettings settings, string? cacheDir = null, TimeProvider? clock = null,
        TimeSpan? refreshTimeout = null)
    {
        _layout = layout;
        _settings = settings;
        _cacheDir = cacheDir;
        _clock = clock ?? TimeProvider.System;
        _refreshTimeout = refreshTimeout ?? TimeSpan.FromMinutes(2);
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

    internal void ReplaceAccountSnapshot(ProviderSnapshot snapshot)
    {
        lock (_gate)
        {
            _providerRefreshes.TryRemove(snapshot.ProviderId, out _);
            ApplySnapshot(snapshot.ProviderId, snapshot);
        }
        Raise();
    }

    internal void ForgetAccount(string cardId)
    {
        lock (_gate) ForgetAccountLocked(cardId);
        Raise();
    }

    internal void UpdateAccountRuntimes(Func<IEnumerable<string>> update)
    {
        lock (_gate)
            foreach (var id in update()) ForgetAccountLocked(id);
        Raise();
    }

    private void ForgetAccountLocked(string cardId)
    {
        _providerRefreshes.TryRemove(cardId, out _);
        _snapshots.TryRemove(cardId, out _);
        _errors.TryRemove(cardId, out _);
        _backoffs.Remove(cardId);
        try { File.Delete(Path.Combine(CacheDir, $"{cardId}.json")); }
        catch (IOException ex) { AppLog.Warn("cache", $"could not remove account snapshot: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { AppLog.Warn("cache", $"could not remove account snapshot: {ex.Message}"); }
    }

    internal void RenameAccount(string cardId, string displayName)
    {
        lock (_gate)
        {
            if (_snapshots.TryGetValue(cardId, out var snapshot))
            {
                snapshot = snapshot with { DisplayName = displayName };
                _snapshots[cardId] = snapshot;
                SaveCache(snapshot);
            }
        }
        Raise();
    }

    public WidgetData Resolve(WidgetDescriptor descriptor)
    {
        var snapshot = Snapshot(descriptor.ProviderId);
        var data = WidgetData.Resolve(descriptor, snapshot?.Line(descriptor.MetricLabel), _settings.MeterStyle, _settings.ResetDisplay,
            _settings.AlwaysShowPacing, _settings.PriorityLimits);
        return descriptor.PlanTitle?.Invoke(snapshot?.Plan) is { } title ? data with { Title = title } : data;
    }

    public IReadOnlyList<(WidgetDescriptor Descriptor, WidgetData Data)> ResolveCardRows(
        IEnumerable<WidgetDescriptor> descriptors) =>
        descriptors.Select(descriptor => (Descriptor: descriptor, Data: Resolve(descriptor)))
            .Where(row => !row.Descriptor.HideWhenMissing || row.Data.HasData)
            .ToArray();

    public void NotifyPresentationChanged() => Raise();

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

    public Task RefreshAllAsync(bool force, CancellationToken cancellationToken = default) =>
        RefreshAllAsync(force, null, cancellationToken);

    internal async Task RefreshAllAsync(bool force, Func<CancellationToken, Task>? syncHubs, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_refreshing) return;
            _refreshing = true;
        }
        Raise();
        try
        {
            var providers = _layout.VisibleProviders;
            if (syncHubs is null)
                await Task.WhenAll(providers.Select(provider => RefreshAsync(provider, force, cancellationToken)));
            else
            {
                var localUsage = Task.WhenAll(providers.Where(provider => provider is not CliProxyHubAccountRuntime)
                    .Select(provider => RefreshAsync(provider, force, cancellationToken)));
                await Task.WhenAll(localUsage, RefreshHubUsageAsync());
            }
            _lastRefresh = DateTimeOffset.Now;
            _nextRefresh = _lastRefresh.AddSeconds(Math.Max(30, _settings.RefreshIntervalSeconds));
        }
        finally
        {
            lock (_gate) _refreshing = false;
            Raise();
        }

        async Task RefreshHubUsageAsync()
        {
            await syncHubs!(cancellationToken);
            await Task.WhenAll(_layout.VisibleProviders.OfType<CliProxyHubAccountRuntime>()
                .Select(provider => RefreshAsync(provider, force, cancellationToken)));
        }
    }

    public async Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = _layout.Providers.FirstOrDefault(p => p.Provider.Id == providerId);
        if (provider is null) return;
        await RefreshAsync(provider, force: true, cancellationToken);
        Raise();
    }

    private Task RefreshAsync(IProviderRuntime provider, bool force, CancellationToken cancellationToken)
    {
        var id = provider.Provider.Id;
        Lazy<Task> refresh;
        lock (_gate)
        {
            if (provider is CliProxyHubAccountRuntime && !_layout.Providers.Contains(provider))
                return Task.CompletedTask;
            if (_providerRefreshes.TryGetValue(id, out var active))
                refresh = active;
            else
            {
                if (!force && _backoffs.TryGetValue(id, out var backoff) && _clock.GetUtcNow() < backoff.RetryAt)
                    return Task.CompletedTask;
                refresh = _providerRefreshes.GetOrAdd(id, _ => CreateRefresh(provider, cancellationToken));
            }
        }
        return refresh.Value;
    }

    private Lazy<Task> CreateRefresh(IProviderRuntime provider, CancellationToken cancellationToken)
    {
        Lazy<Task> refresh = null!;
        refresh = new(() => RefreshCoreAsync(provider, refresh, cancellationToken));
        return refresh;
    }

    private async Task RefreshCoreAsync(IProviderRuntime provider, Lazy<Task> refresh, CancellationToken cancellationToken)
    {
        var providerId = provider.Provider.Id;
        bool IsCurrent() => _providerRefreshes.TryGetValue(providerId, out var active) && active == refresh;
        // Waiting for another account on the hub is not part of this provider's timeout.
        var refreshTurn = (provider as CliProxyHubAccountRuntime)?.RefreshTurn;
        if (refreshTurn is not null)
        {
            try { await refreshTurn.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _providerRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(providerId, refresh));
                return;
            }
            if (!IsCurrent() || cancellationToken.IsCancellationRequested)
            {
                _providerRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(providerId, refresh));
                refreshTurn.Release();
                return;
            }
        }
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Task.Run(() => ReadWithOfflineRetriesAsync(provider, IsCurrent, attempt.Token), attempt.Token);
        try
        {
            var snapshot = await work.WaitAsync(_refreshTimeout, _clock, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (snapshot is not null && IsCurrent() && !cancellationToken.IsCancellationRequested)
                    ApplySnapshot(providerId, snapshot);
            }
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (IsCurrent()) RememberFailure(providerId, "Provider refresh timed out. Try again later.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Error(providerId, "refresh failed", ex);
            lock (_gate)
            {
                if (IsCurrent() && !cancellationToken.IsCancellationRequested) RememberFailure(providerId, ex.Message);
            }
        }
        finally
        {
            _ = RetireRefreshAsync(providerId, refresh, work, attempt, refreshTurn);
        }
    }

    private async Task<ProviderSnapshot?> ReadWithOfflineRetriesAsync(
        IProviderRuntime provider, Func<bool> isCurrent, CancellationToken ct)
    {
        for (var retry = 0; isCurrent(); retry++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(provider, ct);
            if (!snapshot.IsError || snapshot.ErrorCategory != ProviderSnapshot.OfflineCategory
                || retry == OfflineRetrySeconds.Length)
                return snapshot;
            if (!isCurrent()) return null;
            var delay = OfflineRetrySeconds[retry];
            AppLog.Warn(provider.Provider.Id, $"No network path. Retrying the refresh in {delay}s.");
            await Task.Delay(TimeSpan.FromSeconds(delay), _clock, ct);
        }
        return null;
    }

    private async Task RetireRefreshAsync(string providerId, Lazy<Task> refresh, Task work,
        CancellationTokenSource attempt, SemaphoreSlim? refreshTurn)
    {
        using (attempt)
        {
            if (!work.IsCompleted)
            {
                try { await attempt.CancelAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error(providerId, "refresh cancellation failed", ex); }
            }

            // Don't start a second read while the first can still rotate credentials.
            await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _providerRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(providerId, refresh));
            refreshTurn?.Release();
        }
    }

    private static async Task<ProviderSnapshot> ReadSnapshotAsync(IProviderRuntime provider, CancellationToken ct)
    {
        try { return await provider.RefreshAsync(ct); }
        catch (Exception ex) when (ProviderErrors.IsOffline(ex)) { return ProviderSnapshot.Error(provider.Provider, ex); }
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
        if (providerId.Contains('@'))
        {
            var account = _layout.Providers.FirstOrDefault(p => p.Provider.Id == providerId);
            if (account is null) return;
            snapshot = snapshot with { DisplayName = account.Provider.DisplayName };
        }
        _errors.TryRemove(providerId, out _);
        _backoffs.Remove(providerId);
        _snapshots[providerId] = snapshot;
        SaveCache(snapshot);
    }

    private void RememberFailure(string providerId, string message)
    {
        if (providerId.Contains('@') && !_layout.Providers.Any(p => p.Provider.Id == providerId)) return;
        _errors[providerId] = message;
        _backoffs[providerId] = _backoffs.GetValueOrDefault(providerId).AfterFailure(_clock.GetUtcNow());
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
                if (json.ProviderId.Contains('@') && !_layout.Providers.Any(p => p.Provider.Id == json.ProviderId))
                    continue;
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
        var runtime = _layout.Providers.FirstOrDefault(p => p.Provider.Id == snapshot.ProviderId);
        if (runtime is CliProxyHubAccountRuntime) return;
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
            cached.Limits = LimitsProjection.Project(snapshot, runtime?.WidgetDescriptors ?? []);
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

    private readonly record struct RefreshBackoff(int Failures, DateTimeOffset RetryAt)
    {
        public RefreshBackoff AfterFailure(DateTimeOffset now)
        {
            var failures = Math.Min(Failures + 1, 5);
            var minutes = Math.Min(5 * (1 << (failures - 1)), 60);
            return new(failures, now.AddMinutes(minutes));
        }
    }

    private sealed class CachedLine
    {
        public string Type { get; set; } = "";
        public string Label { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }
        public string? Value { get; set; }
        public string? Text { get; set; }
        public double Used { get; set; }
        public double Limit { get; set; }
        public string? Format { get; set; }
        public string? Suffix { get; set; }
        public string? CurrencyCode { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public long? PeriodDurationMs { get; set; }
        public string? ColorHex { get; set; }
        public string? Subtitle { get; set; }
        public List<CachedValue> Values { get; set; } = [];
        public List<DateTimeOffset> ExpiriesAt { get; set; } = [];

        public static CachedLine FromMetric(MetricLine line)
        {
            var cached = Encode(line);
            cached.Title = line.Title;
            return cached;
        }

        private static CachedLine Encode(MetricLine line) => line switch
        {
            MetricLine.ProgressLine p => new CachedLine
            {
                Type = "progress",
                Label = p.Label,
                Used = p.Used,
                Limit = p.Limit,
                Format = p.Format.FormatKind.ToString().ToLowerInvariant(),
                Suffix = p.Format.Suffix,
                CurrencyCode = p.Format.CurrencyCode,
                ResetsAt = p.ResetsAt,
                PeriodDurationMs = p.PeriodDurationMs,
                ColorHex = p.ColorHex
            },
            MetricLine.ValuesLine v => new CachedLine
            {
                Type = "values",
                Label = v.Label,
                Values = v.Items.Select(x => new CachedValue
                    { Number = x.Number, Kind = x.Kind.ToString(), Label = x.Label, CurrencyCode = x.CurrencyCode }).ToList(),
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

        public static MetricLine? ToMetric(CachedLine line) => Decode(line) is { } metric
            ? metric with { Title = line.Title }
            : null;

        private static MetricLine? Decode(CachedLine line) => line.Type switch
        {
            "progress" => MetricLine.Progress(
                line.Label, line.Used, line.Limit,
                line.Format == "dollars" ? ProgressFormat.Currency(line.CurrencyCode)
                    : line.Format == "count" ? ProgressFormat.Count(line.Suffix ?? "")
                    : ProgressFormat.Percent,
                line.ResetsAt, line.PeriodDurationMs, line.ColorHex),
            "values" => MetricLine.Values(
                line.Label,
                line.Values.Select(v => new MetricValue(v.Number, Enum.Parse<MetricKind>(v.Kind, true), v.Label, v.CurrencyCode)).ToList(),
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
        public string? CurrencyCode { get; set; }
    }
}
