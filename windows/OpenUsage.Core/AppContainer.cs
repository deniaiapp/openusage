using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core;

public sealed class AppContainer : IDisposable
{
    public const string Version = "0.7.15-beta.1";

    private readonly AppHttp _http;
    private readonly CancellationTokenSource _cts = new();
    private LocalApiServer? _api;
    private Task? _refreshLoop;

    public AppContainer()
    {
        Settings = AppSettings.Load();
        Keys = new ApiKeyStore();
        _http = new AppHttp();
        Providers = ProviderCatalog.Make(_http, Keys);
        Layout = new LayoutStore(Settings, Providers);
        Data = new WidgetDataStore(Layout, Settings);
        // Fresh installs start with nothing on; the welcome screen turns on the user's picks.
        WelcomePending = FirstLaunchSetup.MarkPendingIfNeeded(Settings);
    }

    public AppSettings Settings { get; }
    public ApiKeyStore Keys { get; }
    public IReadOnlyList<IProviderRuntime> Providers { get; }
    public LayoutStore Layout { get; }
    public WidgetDataStore Data { get; }

    /// <summary>True when this launch opens the welcome screen instead of the dashboard.</summary>
    public bool WelcomePending { get; private set; }

    /// <summary>Starts the local API, and scheduled refreshes unless the welcome screen is still pending.</summary>
    public void Start()
    {
        AppLog.Info("lifecycle", $"OpenUsage {Version} starting");
        _api = new LocalApiServer(Data, Layout);
        _api.Start();
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
        _api?.Dispose();
        _http.Dispose();
    }
}
