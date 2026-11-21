using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Stores;
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
