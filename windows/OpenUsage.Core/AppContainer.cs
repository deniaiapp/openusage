using System.Reflection;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core;

public sealed class AppContainer : IDisposable
{
    public static string Version { get; } = typeof(AppContainer).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+', 2)[0];

    private readonly AppHttp _http;
    private readonly CliProxyHubClient _hubClient = new();
    private readonly CliProxyHubConnections _hubConnections;
    private readonly CancellationTokenSource _cts = new();
    private Task? _refreshLoop;

    public AppContainer()
    {
        _hubConnections = new CliProxyHubConnections(_hubClient, new CliProxyHubStore());
        Settings = AppSettings.Load();
        Keys = new ApiKeyStore();
        _http = new AppHttp();
        Providers = ProviderCatalog.Make(_http, Keys);
        ApiKeyAccountStore = new ApiKeyAccountStore();
        _apiKeyAccounts = new ApiKeyAccounts(_http, ApiKeyAccountStore);
        Layout = new LayoutStore(Settings, Providers);
        WelcomePending = FirstLaunchSetup.MarkPendingIfNeeded(Settings);
        try
        {
            foreach (var account in ApiKeyAccountStore.List())
                if (ApiKeyAccounts.Supports(account.ProviderId)) RegisterAccount(account);
        }
        catch (Exception ex)
        {
            AppLog.Error("accounts", "could not restore API key accounts", ex);
        }
        Data = new WidgetDataStore(Layout, Settings);
        foreach (var muse in Providers.OfType<OpenUsage.Core.Providers.Muse.MuseProvider>())
            muse.AttachSnapshots(() => Data.Snapshot(muse.Provider.Id));
    }

    public AppSettings Settings { get; }
    public ApiKeyStore Keys { get; }
    public ApiKeyAccountStore ApiKeyAccountStore { get; }
    private readonly ApiKeyAccounts _apiKeyAccounts;
    public IReadOnlyList<IProviderRuntime> Providers { get; }
    public LayoutStore Layout { get; }
    public WidgetDataStore Data { get; }

    /// <summary>True when this launch opens the welcome screen instead of the dashboard.</summary>
    public bool WelcomePending { get; private set; }

    /// <summary>Starts scheduled refreshes unless the welcome screen is still pending.</summary>
    public void Start()
    {
        AppLog.Info("lifecycle", $"OpenUsage {Version} starting");
        if (!WelcomePending) StartRefreshing();
    }

    public FirstLaunchSetup MakeFirstLaunchSetup() => new(Layout, Data, Settings);

    /// <summary>Closes out the welcome screen and starts the scheduled refreshes it was holding back.</summary>
    public void FinishWelcome(FirstLaunchSetup setup)
    {
        setup.Finish();
        WelcomePending = false;
        StartRefreshing();
    }

    public void StartRefreshing()
    {
        if (_refreshLoop is not null) return;
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_cts.Token));
    }

    public Task RefreshNowAsync() => Data.RefreshAllAsync(true, _cts.Token);

    public async Task ConnectClaudeAsync(Action<Uri> openBrowser, Task<string> pastedCode, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var claude = Providers.OfType<ClaudeProvider>().Single();
        var snapshot = await claude.ConnectAsync(openBrowser, pastedCode, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectAntigravityAsync(AntigravityProvider provider, Action<Uri> openBrowser, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var snapshot = await provider.ConnectAsync(openBrowser, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectDevinAsync(DevinProvider provider, Action<Uri> openBrowser, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var snapshot = await provider.ConnectAsync(openBrowser, linked.Token);
        if (!Layout.IsProviderEnabled(provider.Provider.Id))
            Layout.SetProviderEnabled(provider.Provider.Id, true);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectCodexAsync(Action<string> showCode, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var codex = Providers.OfType<CodexProvider>().Single();
        var snapshot = await codex.ConnectAsync(showCode, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectCopilotAsync(string address, Action<string, Uri> showCode, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var copilot = Providers.OfType<CopilotProvider>().Single();
        var snapshot = await copilot.ConnectAsync(address, showCode, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectAccountAsync(string providerId, string label, string key)
    {
        var account = await _apiKeyAccounts.ConnectAsync(providerId, label, key, _cts.Token);
        RegisterAccount(account);
        _ = Data.RefreshProviderAsync(account.CardId, _cts.Token);
    }

    public async Task ConnectGrokAsync(Action<string, Uri> showCode, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var grok = Providers.OfType<GrokProvider>().Single();
        var snapshot = await grok.ConnectAsync(showCode, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public async Task ConnectCursorAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var cursor = Providers.OfType<CursorProvider>().Single();
        var snapshot = await cursor.ConnectAsync(openBrowser, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public void RenameAccount(ApiKeyAccount account, string label)
    {
        if (!ApiKeyAccountStore.Rename(account.Id, label)) return;
        var renamed = account with { Label = label.Trim() };
        RegisterAccount(renamed);
        Data.RenameAccount(renamed.CardId, Layout.Providers.First(p => p.Provider.Id == renamed.CardId).Provider.DisplayName);
    }

    public async Task ReplaceAccountKeyAsync(ApiKeyAccount account, string key)
    {
        await _apiKeyAccounts.ReplaceKeyAsync(account, key, _cts.Token);
        Data.ForgetAccount(account.CardId);
        _ = Data.RefreshProviderAsync(account.CardId, _cts.Token);
    }

    public void RemoveAccount(ApiKeyAccount account)
    {
        if (!ApiKeyAccountStore.Remove(account.Id)) return;
        Layout.RemoveAccount(account.CardId);
        Data.ForgetAccount(account.CardId);
    }

    private void RegisterAccount(ApiKeyAccount account)
    {
        var template = Providers.First(p => p.Provider.Id == account.ProviderId);
        Layout.RegisterAccount(_apiKeyAccounts.MakeRuntime(account, template), account.ProviderId);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        await Data.RefreshAllAsync(false, ct);
        while (!ct.IsCancellationRequested)
        {
            var delay = Data.NextRefresh - DateTimeOffset.Now;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
            await Data.RefreshAllAsync(false, ct);
        }
    }

    public async Task ConnectDeviceProviderAsync(IDeviceSignInProvider provider, Action<DevicePrompt> prompt, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var snapshot = await provider.ConnectAsync(prompt, linked.Token);
        Data.ReplaceAccountSnapshot(snapshot);
    }

    public IReadOnlyList<CliProxyHubSummary> CliProxyHubs() =>
        _hubConnections.List().Select(hub => new CliProxyHubSummary(hub.Id, hub.Address)).ToArray();

    public async Task ConnectCliProxyHubAsync(string address, string key, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await _hubConnections.ConnectAsync(address, key, linked.Token);
    }

    public async Task ReplaceCliProxyHubKeyAsync(Guid id, string key, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await _hubConnections.ReplaceKeyAsync(id, key, linked.Token);
    }

    public bool RemoveCliProxyHub(Guid id) => _hubConnections.Remove(id);

    public sealed record CliProxyHubSummary(Guid Id, Uri Address);

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _hubClient.Dispose();
    }
}
