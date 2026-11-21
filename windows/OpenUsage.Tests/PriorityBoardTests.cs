using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class PriorityBoardTests
{
    [Fact]
    public async Task SortsLowLimitsAndDoesNotDuplicatePinsOrRevealHiddenMetrics()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-priority-");
        try
        {
            var active = new Runtime("test", ("healthy", 5), ("spent-pin", 100), ("pin", 55), ("warn", 82), ("critical", 97), ("hidden", 100));
            var hidden = new Runtime("hidden", ("hidden-provider", 100));
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.Add("test");
            settings.EnabledMetrics.UnionWith(["healthy", "spent-pin", "pin", "warn", "critical", "hidden-provider"]);
            settings.PinnedMetrics.UnionWith(["pin", "spent-pin"]);
            settings.ExpandedMetrics.Add("critical");
            var layout = new LayoutStore(settings, [active, hidden]);
            var store = new WidgetDataStore(layout, settings, directory.FullName);

            var before = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.False(before.HasUsage);
            Assert.Empty(before.Pinned);

            await store.RefreshAllAsync(false);
            await store.RefreshProviderAsync("hidden");
            var board = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.True(board.HasUsage);
            Assert.False(board.HasRefreshErrors);
            Assert.Equal(new[] { "pin" }, board.Pinned.Select(m => m.Descriptor.Id));
            Assert.Equal(new[] { "critical", "warn" }, board.RunningOut.Select(m => m.Descriptor.Id));
            Assert.Equal(new[] { "spent-pin" }, board.UsedUp.Select(m => m.Descriptor.Id));

            active.Fail = true;
            await store.RefreshProviderAsync("test");
            var stale = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.True(stale.HasUsage);
            Assert.True(stale.HasRefreshErrors);
            Assert.Equal(2, stale.RunningOut.Count);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class Runtime(string id, params (string Id, double Used)[] metrics) : IProviderRuntime
    {
        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
            metrics.Select(metric => WidgetDescriptor.Percent(metric.Id, Provider, metric.Id)).ToList();

        public bool Fail { get; set; }
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Fail
                ? ProviderSnapshot.Error(Provider, "Fixture retry.")
                : ProviderSnapshot.Make(Provider, null,
                    metrics.Select(metric => MetricLine.Progress(metric.Id, metric.Used, 100, ProgressFormat.Percent)).ToList(),
                    DateTimeOffset.UtcNow));
    }
}
