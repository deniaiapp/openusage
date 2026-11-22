using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class CliProxyHubSync(CliProxyHubClient client, CliProxyHubStore store, TimeProvider? clock = null)
{
    private static readonly TimeSpan ScheduledInterval = TimeSpan.FromHours(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1);
    private readonly Dictionary<Guid, DateTimeOffset> _lastSuccess = [];

    public async Task RefreshAsync(bool force, CancellationToken ct, IReadOnlySet<Guid>? beforeUsage = null)
    {
        if (beforeUsage is not null) await _refresh.WaitAsync(ct);
        else if (!await _refresh.WaitAsync(0, ct)) return;
        try
        {
            var hubs = store.List();
            var ids = hubs.Select(hub => hub.Id).ToHashSet();
            foreach (var id in _lastSuccess.Keys.Where(id => !ids.Contains(id)).ToArray())
                _lastSuccess.Remove(id);

            var now = _clock.GetUtcNow();
            var results = await Task.WhenAll(hubs
                .Where(hub => force || beforeUsage?.Contains(hub.Id) == true ||
                    !_lastSuccess.TryGetValue(hub.Id, out var last) || now - last >= ScheduledInterval)
                .Select(hub => ReadAsync(hub, ct)));
            foreach (var result in results) Track(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("cliproxy", $"Could not read saved hubs ({ex.GetType().Name}).");
        }
        finally { _refresh.Release(); }
    }

    public async Task RefreshHubAsync(Guid id, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            if (store.List().FirstOrDefault(hub => hub.Id == id) is { } hub)
                Track(await ReadAsync(hub, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("cliproxy", $"Could not read saved hub ({ex.GetType().Name}).");
        }
        finally { _refresh.Release(); }
    }

    private void Track((Guid Id, DateTimeOffset? ReadAt) result)
    {
        if (result.ReadAt is { } time) _lastSuccess[result.Id] = time;
        else _lastSuccess.Remove(result.Id);
    }

    private async Task<(Guid Id, DateTimeOffset? ReadAt)> ReadAsync(CliProxyHub hub, CancellationToken ct)
    {
        var saved = store.ReadKey(hub);
        if (saved is not { } credential) return (hub.Id, null);
        try
        {
            var address = CliProxyHubAddress.Parse(hub.Address.AbsoluteUri);
            if (address is null || address.BaseUrl.AbsoluteUri != hub.Address.AbsoluteUri)
                throw new ProviderRequestException("The saved hub address is invalid. Disconnect the hub and connect it again.");
            var logins = await client.ListAsync(address, credential.Key, ct, hub.Id);
            ct.ThrowIfCancellationRequested();
            if (store.UpdateAccounts(hub, credential.Revision, logins))
                return (hub.Id, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("cliproxy", $"{hub.Address.Host} login list could not be refreshed ({ex.GetType().Name}).");
            store.SetSyncError(hub, credential.Revision, ex is ProviderRequestException
                ? ex.Message
                : "Couldn't update this hub's login list. Try again.");
        }
        return (hub.Id, null);
    }
}
