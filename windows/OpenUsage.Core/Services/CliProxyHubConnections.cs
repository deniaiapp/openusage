using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class CliProxyHubConnections(CliProxyHubClient client, CliProxyHubStore store)
{
    public IReadOnlyList<CliProxyHub> List() => store.List();

    public async Task<(CliProxyHub Hub, IReadOnlyList<CliProxyHubLogin> Logins)> ConnectAsync(
        string input, string key, CancellationToken ct)
    {
        var address = CliProxyHubAddress.Parse(input)
            ?? throw new ProviderRequestException("Enter a valid CLIProxyAPI hub address.");
        if (store.List().Any(hub => hub.Address.AbsoluteUri == address.BaseUrl.AbsoluteUri))
            throw new ProviderRequestException("This CLIProxyAPI hub is already connected.");

        var logins = await client.ListAsync(address, key, ct);
        ct.ThrowIfCancellationRequested();
        return (store.Add(address.BaseUrl, key), logins);
    }

    public async Task<IReadOnlyList<CliProxyHubLogin>> ReplaceKeyAsync(Guid id, string key, CancellationToken ct)
    {
        var hub = store.List().FirstOrDefault(hub => hub.Id == id)
            ?? throw HubNotConnected();
        var address = CliProxyHubAddress.Parse(hub.Address.AbsoluteUri);
        if (address is null || address.BaseUrl.AbsoluteUri != hub.Address.AbsoluteUri)
            throw new ProviderRequestException("Remove this CLIProxyAPI hub and connect it again.");

        var logins = await client.ListAsync(address, key, ct);
        ct.ThrowIfCancellationRequested();
        if (!store.ReplaceKey(hub, key)) throw HubNotConnected();
        return logins;
    }

    public bool Remove(Guid id) => store.Remove(id);

    private static ProviderRequestException HubNotConnected() => new("This CLIProxyAPI hub is no longer connected.");
}
