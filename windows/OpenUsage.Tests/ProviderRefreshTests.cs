using System.Net.Sockets;
using System.Threading.Channels;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class ProviderRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndScheduledRefreshJoinTheSameInFlightRead(bool manualStartsFirst)
    {
        var directory = Directory.CreateTempSubdirectory("openusage-refresh-");
        try
        {
            var runtime = new Runtime("codex");
            var other = new Runtime("claude");
            other.Release();
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.UnionWith([runtime.Provider.Id, other.Provider.Id]);
            var data = new WidgetDataStore(new LayoutStore(settings, [runtime, other]), settings, directory.FullName);

            var first = manualStartsFirst ? data.RefreshProviderAsync(runtime.Provider.Id) : data.RefreshAllAsync(false);
            await runtime.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var second = manualStartsFirst ? data.RefreshAllAsync(false) : data.RefreshProviderAsync(runtime.Provider.Id);
            await other.Started.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, runtime.Calls);
            Assert.False(second.IsCompleted);

            runtime.Release();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, runtime.Calls);
            Assert.NotNull(data.Snapshot(runtime.Provider.Id));

            await data.RefreshProviderAsync(runtime.Provider.Id);
            Assert.Equal(2, runtime.Calls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ScheduledBackoffIsPerProviderAndResetsOnSuccess()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-refresh-backoff-");
        try
        {
            var clock = new ManualClock();
            var codex = new BackoffRuntime("codex");
            var claude = new BackoffRuntime("claude");
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.UnionWith(["codex", "claude"]);
            var store = new WidgetDataStore(new LayoutStore(settings, [codex, claude]), settings, directory.FullName, clock);

            await store.RefreshAllAsync(false);
            var lastGood = store.Snapshot("codex");
            Assert.NotNull(lastGood);
            codex.Fail = true;
            await store.RefreshAllAsync(false);
            clock.Advance(TimeSpan.FromMinutes(4));
            await store.RefreshAllAsync(false);
            Assert.Equal(2, codex.Calls);
            Assert.Equal(3, claude.Calls);
            Assert.Same(lastGood, store.Snapshot("codex"));

            clock.Advance(TimeSpan.FromMinutes(1));
            await store.RefreshAllAsync(false);
            Assert.Equal(3, codex.Calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await store.RefreshAllAsync(false);
            Assert.Equal(3, codex.Calls);

            codex.Fail = false;
            await store.RefreshAllAsync(true);
            Assert.Equal(4, codex.Calls);
            Assert.Null(store.ProviderError("codex"));
            await store.RefreshAllAsync(false);
            Assert.Equal(5, codex.Calls);

            codex.Fail = true;
            await store.RefreshProviderAsync("codex");
            clock.Advance(TimeSpan.FromMinutes(5));
            await store.RefreshAllAsync(false);
            Assert.Equal(7, codex.Calls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task OfflineRetriesJoinTheInFlightRefreshAndDoNotFlagTheCardWhileWaiting()
    {
        using var directory = new TempDir();
        var clock = new RetryClock();
        var runtime = new BackoffRuntime("codex") { Failure = ConnectionError(SocketError.NetworkUnreachable) };
        var settings = AppSettings.Load(Path.Combine(directory.Path, "settings.json"));
        settings.EnabledProviders.Add("codex");
        var data = new WidgetDataStore(new LayoutStore(settings, [runtime]), settings, directory.Path, clock);

        var refresh = data.RefreshAllAsync(false);
        var timer = await clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Due);
        Assert.Null(data.ProviderError("codex"));
        var joined = data.RefreshProviderAsync("codex");
        Assert.Equal(1, runtime.Calls);

        runtime.Failure = null;
        timer.Fire();
        await Task.WhenAll(refresh, joined).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, runtime.Calls);
        Assert.NotNull(data.Snapshot("codex"));
        Assert.Null(data.ProviderError("codex"));
    }

    [Fact]
    public async Task ExhaustedOfflineRetriesKeepLastDataAndUseBackoff()
    {
        using var directory = new TempDir();
        var clock = new RetryClock();
        var runtime = new BackoffRuntime("codex");
        var settings = AppSettings.Load(Path.Combine(directory.Path, "settings.json"));
        settings.EnabledProviders.Add("codex");
        var data = new WidgetDataStore(new LayoutStore(settings, [runtime]), settings, directory.Path, clock);
        await data.RefreshAllAsync(false);
        var lastGood = data.Snapshot("codex");
        Assert.NotNull(lastGood);
        runtime.Failure = ConnectionError(SocketError.NetworkDown);

        var refresh = data.RefreshAllAsync(false);
        foreach (var seconds in new[] { 2, 4, 8, 16, 32 })
        {
            var timer = await clock.NextTimer();
            Assert.Equal(TimeSpan.FromSeconds(seconds), timer.Due);
            Assert.Null(data.ProviderError("codex"));
            timer.Fire();
        }
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProviderErrors.ConnectionFailed, data.ProviderError("codex"));
        Assert.Same(lastGood, data.Snapshot("codex"));
        Assert.Equal(7, runtime.Calls);

        await data.RefreshAllAsync(false);
        Assert.Equal(7, runtime.Calls);
    }

    [Fact]
    public async Task AnInvalidatedAccountDoesNotRetryAfterItsOfflineDelay()
    {
        using var directory = new TempDir();
        var clock = new RetryClock();
        var runtime = new BackoffRuntime($"openrouter@{Guid.NewGuid():N}")
            { Failure = ConnectionError(SocketError.NetworkDown) };
        var settings = AppSettings.Load(Path.Combine(directory.Path, "settings.json"));
        var layout = new LayoutStore(settings, []);
        layout.RegisterAccount(runtime, "openrouter");
        var data = new WidgetDataStore(layout, settings, directory.Path, clock);

        var refresh = data.RefreshProviderAsync(runtime.Provider.Id);
        var timer = await clock.NextTimer();
        data.ForgetAccount(runtime.Provider.Id);
        timer.Fire();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runtime.Calls);
        Assert.Null(data.ProviderError(runtime.Provider.Id));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    public void AnEndpointFailureDoesNotCountAsOffline(SocketError error) =>
        Assert.False(ProviderErrors.IsOffline(ConnectionError(error)));

    private static Exception ConnectionError(SocketError error) =>
        new ProviderRequestException(ProviderErrors.ConnectionFailed,
            new HttpRequestException("fixture connection error", new SocketException((int)error)));

    [Fact]
    public async Task InvalidatedAccountRefreshCannotPublishOrEvictItsReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-refresh-");
        try
        {
            var previous = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = new AccountRuntime(previous.Task, replacement.Task);
            var cardId = runtime.Provider.Id;
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            var layout = new LayoutStore(settings, []);
            layout.RegisterAccount(runtime, "openrouter");
            var data = new WidgetDataStore(layout, settings, directory.FullName);

            var oldRefresh = data.RefreshProviderAsync(cardId);
            data.ForgetAccount(cardId);
            var newRefresh = data.RefreshProviderAsync(cardId);
            Assert.Equal(2, runtime.Calls);

            previous.SetResult(ProviderSnapshot.Make(runtime.Provider, "Old Key", [], DateTimeOffset.UtcNow));
            await oldRefresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(data.Snapshot(cardId));
            Assert.False(File.Exists(Path.Combine(directory.FullName, $"{cardId}.json")));
            var joined = data.RefreshProviderAsync(cardId);
            Assert.Equal(2, runtime.Calls);

            replacement.SetResult(ProviderSnapshot.Make(runtime.Provider, "New Key", [], DateTimeOffset.UtcNow));
            await Task.WhenAll(newRefresh, joined).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("New Key", data.Snapshot(cardId)?.Plan);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class BackoffRuntime(string id) : IProviderRuntime
    {
        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => [];
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Exception? Failure { get; set; }
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is { } error) return Task.FromResult(ProviderSnapshot.Error(Provider, error));
            return Task.FromResult(Fail
                ? ProviderSnapshot.Error(Provider, "Fixture refresh failed.")
                : ProviderSnapshot.Make(Provider, null, [], DateTimeOffset.UtcNow));
        }
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly Channel<RetryTimer> _timers = Channel.CreateUnbounded<RetryTimer>();

        public async Task<RetryTimer> NextTimer()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await _timers.Reader.ReadAsync(timeout.Token);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RetryTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("openusage-refresh-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class RetryTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due => due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AccountRuntime(Task<ProviderSnapshot> previous, Task<ProviderSnapshot> replacement) : IProviderRuntime
    {
        public Provider Provider { get; } = new($"openrouter@{Guid.NewGuid():N}", "OpenRouter", "OpenRouter");
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => [];
        public int Calls { get; private set; }
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) => ++Calls == 1 ? previous : replacement;
    }

    private sealed class Runtime(string id) : IProviderRuntime
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => [];
        public int Calls => Volatile.Read(ref _calls);
        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return ProviderSnapshot.Make(Provider, null, [], DateTimeOffset.UtcNow);
        }
    }
}
