using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubRuntimesTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("openusage-hub-runtimes-");
    private readonly FixtureHub _handler = new();
    private readonly RefreshClock _clock = new();
    private readonly CliProxyHubClient _client;
    private readonly LayoutStore _layout;
    private readonly WidgetDataStore _data;
    private readonly CliProxyHubRuntimes _runtimes;

    public CliProxyHubRuntimesTests()
    {
        var settings = AppSettings.Load(Path.Combine(_directory.FullName, "settings.json"));
        _layout = new LayoutStore(settings, []);
        _data = new WidgetDataStore(_layout, settings, _directory.FullName, _clock);
        _client = new CliProxyHubClient(new AppHttp(new HttpClient(_handler)));
        _runtimes = new(_layout, _data,
            item => new CliProxyHubAccountRuntime(_client, item.Hub, item.Account, () => "fixture-key"));
    }

    [Fact]
    public async Task ARefreshReadsNewHubAccountsAfterSyncInSavedOrderWithoutPinningOrCachingThem()
    {
        var first = Account("first", "first");
        var second = first with
        {
            Account = new(Guid.NewGuid(), "codex", "second", first.Account.Link with
            {
                LoginId = "second", AuthIndex = "second"
            })
        };
        using var startSync = new TaskCompletionSourceSignal();
        var refresh = _data.RefreshAllAsync(false, async _ =>
        {
            await startSync.Task;
            _runtimes.Reconcile([first, second]);
        }, default);
        Assert.Empty(_handler.Calls);
        startSync.Release();
        await refresh;

        Assert.Equal(["Codex · first", "Codex · second"], _layout.VisibleProviders.Select(p => p.Provider.DisplayName));
        Assert.Equal(10, Used(CardId(first)));
        Assert.Equal(20, Used(CardId(second)));
        Assert.Empty(_layout.PinnedDescriptors());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, CardId(first) + ".json")));
        Assert.Empty(_runtimes.Reconcile([first, second]));
    }

    [Fact]
    public async Task SameHubCardsStartTheirTimeoutOnlyWhenTheirTurnBegins()
    {
        var first = Account("first", "first");
        var second = first with { Account = new(Guid.NewGuid(), "codex", "second",
            first.Account.Link with { LoginId = "second", AuthIndex = "second" }) };
        var otherHub = Account("other", "other");
        _runtimes.Reconcile([first, second]);
        using var release = new TaskCompletionSourceSignal();
        _handler.BeforeResponse = index => index == "first" ? release.Task : Task.CompletedTask;

        var refresh = _data.RefreshAllAsync(false, _ => Task.CompletedTask, default);
        Assert.Equal(1, _clock.TimersStarted);
        Assert.DoesNotContain(_handler.Calls, call => call.Index == "second");

        _runtimes.Reconcile([first, second, otherHub]);
        await _data.RefreshProviderAsync(CardId(otherHub)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(20, Used(CardId(otherHub)));

        release.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, Used(CardId(first)));
        Assert.Equal(20, Used(CardId(second)));
        Assert.Null(_data.ProviderError(CardId(second)));
    }

    [Fact]
    public async Task RelinkingKeepsPreferencesWhileDisconnectingRemovesOnlyThatHubsCards()
    {
        var first = Account("first", "first");
        var second = Account("second", "second");
        Assert.Equal([CardId(first), CardId(second)], _runtimes.Reconcile([first, second]));
        await _data.RefreshAllAsync(false);

        var firstRuntime = _layout.Providers.Single(p => p.Provider.Id == CardId(first));
        var metric = firstRuntime.WidgetDescriptors.First();
        Assert.True(_layout.TogglePin(metric.Id));
        _layout.SetOnDemand(metric.Id, true);
        var relinked = first with { Account = first.Account with
        {
            Link = first.Account.Link with { AuthIndex = "second", ChatGptAccountId = "new-workspace" }
        } };
        Assert.Equal([CardId(first)], _runtimes.Reconcile([relinked, second]));
        Assert.NotSame(firstRuntime, _layout.Providers.First());
        Assert.True(_layout.IsOnDemand(metric.Id));
        Assert.True(_layout.IsPinned(metric.Id));
        Assert.Null(_data.Snapshot(CardId(first)));
        Assert.Equal(20, Used(CardId(second)));
        await _data.RefreshProviderAsync(CardId(first));
        Assert.Equal(20, Used(CardId(first)));
        Assert.Contains((first.Hub.Address.Host, "second", "new-workspace"), _handler.Calls);

        _runtimes.Reconcile([second]);
        Assert.Equal(CardId(second), Assert.Single(_layout.VisibleProviders).Provider.Id);
        Assert.Null(_layout.Descriptor(metric.Id));
        Assert.False(_layout.IsPinned(metric.Id));
        Assert.Null(_data.Snapshot(CardId(first)));
        Assert.Equal(20, Used(CardId(second)));
    }

    [Fact]
    public async Task SameLinkKeyReplacementDiscardsAnInFlightReadBeforeRefreshingWithTheNewRuntime()
    {
        using var release = new TaskCompletionSourceSignal();
        using var requestStarted = new TaskCompletionSourceSignal();
        var account = Account("first", "first");
        _runtimes.Reconcile([account]);
        _handler.BeforeResponse = async _ =>
        {
            requestStarted.Release();
            await release.Task;
        };
        var old = _data.RefreshProviderAsync(CardId(account));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([CardId(account)], _runtimes.Reconcile([account with { KeyRevision = Guid.NewGuid() }]));
        _handler.BeforeResponse = null;
        release.Release();
        await old.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(_data.Snapshot(CardId(account)));
        Assert.Null(_data.ProviderError(CardId(account)));
        await _data.RefreshProviderAsync(CardId(account));
        Assert.Equal(10, Used(CardId(account)));
    }

    [WindowsFact]
    public async Task ScheduledUsageListsActiveHubsBeforeUsingAnySavedLink()
    {
        var store = new CliProxyHubStore(Path.Combine(_directory.FullName, "hubs.bin"));
        var hub = store.Add(new Uri("https://active.example/"), "fixture-key",
            [new("stable-login", "first", "codex", "Fixture", true, false, "fixture-workspace")]);
        store.Add(new Uri("https://idle.example/"), "fixture-key");
        var sync = new CliProxyHubSync(_client, store, new FixedClock());
        _handler.Index = "first";
        _runtimes.Reconcile(store.ListRuntimeAccounts());
        var account = Assert.Single(store.ListRuntimeAccounts());
        var cardId = CardId(account);

        Task Refresh() => _data.RefreshAllAsync(false, async ct =>
        {
            var active = _layout.VisibleProviders.OfType<CliProxyHubAccountRuntime>().Select(p => p.HubId).ToHashSet();
            await sync.RefreshAsync(false, ct, active);
            _runtimes.Reconcile(store.ListRuntimeAccounts());
        }, default);

        await Refresh();
        Assert.Contains("idle.example", _handler.Listings);
        var metric = _layout.Providers.Single().WidgetDescriptors.First();
        Assert.True(_layout.TogglePin(metric.Id));
        Assert.Equal(10, Used(cardId));

        _handler.Calls.Clear();
        _handler.Listings.Clear();
        _handler.Index = "second";
        await Refresh();

        Assert.Equal([hub.Address.Host], _handler.Listings);
        Assert.Equal((hub.Address.Host, "second", "fixture-workspace"), Assert.Single(_handler.Calls));
        Assert.Equal(account.Account.AccountId, Assert.Single(store.ListRuntimeAccounts()).Account.AccountId);
        Assert.True(_layout.IsPinned(metric.Id));
        Assert.Null(_data.ProviderError(cardId));
        Assert.Equal(20, Used(cardId));
    }

    private double Used(string id) => Assert.IsType<MetricLine.ProgressLine>(_data.Snapshot(id)?.Line("Session")).Used;

    private static CliProxyHubRuntimeAccount Account(string label, string index)
    {
        var hub = new CliProxyHub(Guid.NewGuid(), new Uri($"https://{label}.example/"));
        return new(hub, new(Guid.NewGuid(), "codex", label, new(hub.Id, "fixture-login", index, "fixture-workspace")),
            Guid.NewGuid());
    }

    private static string CardId(CliProxyHubRuntimeAccount account) => $"codex@{account.Account.AccountId:N}";

    public void Dispose()
    {
        _runtimes.Dispose();
        _client.Dispose();
        _directory.Delete(recursive: true);
    }

    private sealed class TaskCompletionSourceSignal : IDisposable
    {
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => _signal.Task;
        public void Release() => _signal.TrySetResult();
        public void Dispose() => Release();
    }

    private sealed class FixtureHub : HttpMessageHandler
    {
        public ConcurrentQueue<(string Hub, string Index, string? Workspace)> Calls { get; } = [];
        public ConcurrentQueue<string> Listings { get; } = [];
        public string? Index { get; set; }
        public Func<string, Task>? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/v0/management/auth-files")
            {
                var host = request.RequestUri.Host;
                Listings.Enqueue(host);
                var files = host == "active.example"
                    ? $$"""[{"id":"stable-login","provider":"codex","auth_index":"{{Index}}","id_token":{"chatgpt_account_id":"fixture-workspace"},"label":"Fixture"}]"""
                    : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"files":{{files}}}""", Encoding.UTF8, "application/json")
                };
            }
            Assert.Equal("/v0/management/api-call", request.RequestUri!.AbsolutePath);
            var call = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var index = call["auth_index"]!.GetValue<string>();
            var usage = call["url"]!.GetValue<string>().EndsWith("/usage", StringComparison.Ordinal);
            if (usage)
            {
                Calls.Enqueue((request.RequestUri.Host, index, (string?)call["header"]?["ChatGPT-Account-Id"]));
                if (BeforeResponse is { } before) await before(index);
            }
            if (Index is not null && index != Index)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"auth credential not found"}""", Encoding.UTF8, "application/json")
                };
            var used = index == "first" ? 10 : 20;
            var body = usage
                ? JsonSerializer.Serialize(new { rate_limit = new { primary_window = new { used_percent = used } } })
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status_code = 200, body }),
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class RefreshClock : TimeProvider
    {
        private int _timersStarted;
        public int TimersStarted => Volatile.Read(ref _timersStarted);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersStarted);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
        }
    }
}
