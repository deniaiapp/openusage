using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core;

public sealed class AppContainer : IDisposable
{
    public const string Version = "0.7.15-beta.1";

    private readonly AppHttp _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _refreshLoop;

    public AppContainer()
    {
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

    public async Task ConnectCopilotAsync(Action<string> showCode, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var copilot = Providers.OfType<CopilotProvider>().Single();
        var snapshot = await copilot.ConnectAsync(showCode, linked.Token);
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

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
