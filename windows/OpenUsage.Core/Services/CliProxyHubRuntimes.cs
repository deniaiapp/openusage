using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed record CliProxyHubRuntimeAccount(CliProxyHub Hub, CliProxyMirroredAccount Account, Guid? KeyRevision);

internal sealed class CliProxyHubRuntimes(
    LayoutStore layout, WidgetDataStore data,
    Func<CliProxyHubRuntimeAccount, CliProxyHubAccountRuntime> makeRuntime) : IDisposable
{
    private readonly Dictionary<Guid, (CliProxyHubRuntimeAccount Saved, CliProxyHubAccountRuntime Runtime)> _active = [];

    public IReadOnlyList<string> Reconcile(IReadOnlyList<CliProxyHubRuntimeAccount> saved)
    {
        var current = saved.Select(item => item.Account.AccountId).ToHashSet();
        var removed = _active.Keys.Where(id => !current.Contains(id)).ToArray();
        var updates = saved.Where(item =>
            !_active.TryGetValue(item.Account.AccountId, out var entry) || entry.Saved != item).ToArray();
        if (removed.Length == 0 && updates.Length == 0) return [];

        var retired = new List<CliProxyHubAccountRuntime>();
        var refresh = new List<string>();
        data.UpdateAccountRuntimes(() =>
        {
            foreach (var id in removed)
            {
                if (!_active.Remove(id, out var previous)) continue;
                layout.RemoveAccount(previous.Runtime.Provider.Id);
                retired.Add(previous.Runtime);
            }
            foreach (var item in updates)
            {
                CliProxyHubAccountRuntime runtime;
                try { runtime = makeRuntime(item); }
                catch (ArgumentException ex)
                {
                    AppLog.Warn("cliproxy", $"Invalid saved hub account ({ex.GetType().Name}).");
                    continue;
                }

                var id = item.Account.AccountId;
                if (_active.TryGetValue(id, out var previous)) retired.Add(previous.Runtime);
                layout.RegisterAccount(runtime, item.Account.ProviderId);
                _active[id] = (item, runtime);
                refresh.Add(runtime.Provider.Id);
            }
            return retired.Select(runtime => runtime.Provider.Id);
        });
        foreach (var runtime in retired) runtime.Dispose();
        return refresh;
    }

    public void Dispose()
    {
        foreach (var (_, runtime) in _active.Values) runtime.Dispose();
        _active.Clear();
    }
}
