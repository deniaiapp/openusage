using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class CliProxyHubSync(CliProxyHubClient client, CliProxyHubStore store, TimeProvider? clock = null)
{
    private static readonly TimeSpan ScheduledInterval = TimeSpan.FromHours(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1);
    private readonly Dictionary<Guid, DateTimeOffset> _lastSuccess = [];

    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) return;
        try
        {
            var hubs = store.List();
            var ids = hubs.Select(hub => hub.Id).ToHashSet();
            foreach (var id in _lastSuccess.Keys.Where(id => !ids.Contains(id)).ToArray())
                _lastSuccess.Remove(id);

            var now = _clock.GetUtcNow();
            var results = await Task.WhenAll(hubs
                .Where(hub => force || !_lastSuccess.TryGetValue(hub.Id, out var last) || now - last >= ScheduledInterval)
                .Select(hub => ReadAsync(hub, ct)));
            foreach (var (id, readAt) in results)
            {
                if (readAt is { } time) _lastSuccess[id] = time;
                else _lastSuccess.Remove(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("cliproxy", $"Could not read saved hubs ({ex.GetType().Name}).");
        }
        finally { _refresh.Release(); }
    }

    private async Task<(Guid Id, DateTimeOffset? ReadAt)> ReadAsync(CliProxyHub hub, CancellationToken ct)
    {
        try
        {
            var saved = store.ReadKey(hub);
            if (saved is not { } credential) return (hub.Id, null);
            var address = CliProxyHubAddress.Parse(hub.Address.AbsoluteUri);
            if (address is null || address.BaseUrl.AbsoluteUri != hub.Address.AbsoluteUri)
                throw new InvalidDataException("Could not read the saved CLIProxyAPI hub address.");
            var logins = await client.ListAsync(address, credential.Key, ct, hub.Id);
            ct.ThrowIfCancellationRequested();
            if (store.UpdateAccounts(hub, credential.Revision, logins))
                return (hub.Id, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("cliproxy", $"{hub.Address.Host} login list could not be refreshed ({ex.GetType().Name}).");
        }
        return (hub.Id, null);
    }
}
