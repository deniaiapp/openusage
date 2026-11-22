using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class CliProxyHubAccountRuntime : IProviderRuntime, IDisposable
{
    private const string Token = CliProxyHubClient.TokenPlaceholder;
    private readonly AppHttp _http;
    private readonly Func<string?> _key;
    private readonly Func<CancellationToken, Task<ProviderSnapshot>> _read;

    internal CliProxyHubAccountRuntime(
        CliProxyHubClient client, CliProxyHubStore store, CliProxyHub hub, CliProxyMirroredAccount account)
        : this(client, hub, account, () => store.KeyFor(hub, account)) { }

    internal CliProxyHubAccountRuntime(
        CliProxyHubClient client, CliProxyHub hub, CliProxyMirroredAccount account, Func<string?> key)
    {
        if (account.Link.HubId != hub.Id)
            throw new ArgumentException("The account does not belong to this hub.", nameof(account));
        var address = CliProxyHubAddress.Parse(hub.Address.AbsoluteUri);
        if (address is null || address.BaseUrl.AbsoluteUri != hub.Address.AbsoluteUri)
            throw new ArgumentException("The saved CLIProxyAPI hub address is invalid.", nameof(hub));

        var displayName = account.ProviderId switch
        {
            "antigravity" => "Antigravity",
            "claude" => "Claude",
            "codex" => "Codex",
            "devin" => "Devin",
            "grok" => "Grok",
            _ => throw new ArgumentException("The account provider is not supported through a hub.", nameof(account))
        };
        var expired = CliProxyHubRefusal.Message(CliProxyHubRefusalKind.NoToken, hub.Address.Host);
        _key = key;
        _http = new AppHttp(new HttpClient(new CliProxyHubTunnel(
            client, hub.Id, address, key, account.Link.AuthIndex, displayName)));
        IProviderRuntime template = account.ProviderId switch
        {
            "antigravity" => new AntigravityProvider(_http),
            "claude" => new ClaudeProvider(_http),
            "codex" => new CodexProvider(_http),
            "devin" => new DevinProvider(_http, () => null, _ => throw new InvalidOperationException()),
            _ => new GrokProvider(_http)
        };
        var codexAuth = new CodexAuth { AccessToken = Token, AccountId = account.Link.ChatGptAccountId };
        _read = template switch
        {
            AntigravityProvider provider => ct => provider.RefreshWithHubTokenAsync(Token, expired, ct),
            ClaudeProvider provider => ct => provider.RefreshWithHubTokenAsync(Token, expired, ct),
            CodexProvider provider => ct => provider.FetchUsageAsync(codexAuth, ct, expired),
            DevinProvider provider => ct => provider.RefreshWithHubTokenAsync(Token, expired, ct),
            GrokProvider provider => ct => provider.RefreshWithHubTokenAsync(Token, expired, ct),
            _ => throw new InvalidOperationException()
        };
        var cardId = $"{account.ProviderId}@{account.AccountId:N}";
        Provider = template.Provider with { Id = cardId, DisplayName = $"{template.Provider.DisplayName} · {account.Label}" };
        WidgetDescriptors = template.WidgetDescriptors.Select(descriptor => descriptor with
        {
            Id = cardId + descriptor.Id[account.ProviderId.Length..],
            ProviderId = cardId
        }).ToArray();
    }

    public Provider Provider { get; }
    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors { get; }

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_key() is not null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _read(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot with { ProviderId = Provider.Id, DisplayName = Provider.DisplayName };
        }
        catch (ProviderRequestException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("cliproxy", $"{Provider.Id} usage check failed ({ex.GetType().Name}).");
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
