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
            Assert.Equal("test", Assert.Single(before.WaitingForFirstRead).Id);

            await store.RefreshAllAsync(false);
            await store.RefreshProviderAsync("hidden");
            var board = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.True(board.HasUsage);
            Assert.False(board.HasRefreshErrors);
            Assert.Empty(board.WaitingForFirstRead);
            Assert.Equal(new[] { "pin" }, board.Pinned.Select(m => m.Descriptor.Id));
            Assert.Equal(new[] { "critical", "warn" }, board.RunningOut.Select(m => m.Descriptor.Id));
            Assert.Equal(new[] { "spent-pin" }, board.UsedUp.Select(m => m.Descriptor.Id));

            active.Fail = true;
            await store.RefreshProviderAsync("test");
            var stale = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.True(stale.HasUsage);
            Assert.True(stale.HasRefreshErrors);
            Assert.Empty(stale.WaitingForFirstRead);
            Assert.Equal(2, stale.RunningOut.Count);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task RunningOutSortsByTimeToLimitBeforeFallingBackToRemainingQuota()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-priority-");
        try
        {
            var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
            var active = new Runtime("test", ("expired", 95), ("low", 99), ("slower", 75), ("faster", 50))
            {
                Windows = new Dictionary<string, (DateTimeOffset, long)>
                {
                    ["expired"] = (now.AddMinutes(-1), MetricPeriod.SessionMs),
                    ["slower"] = (now.AddMinutes(30), MetricPeriod.SessionMs),
                    ["faster"] = (now.AddHours(5).AddMinutes(-20), MetricPeriod.SessionMs)
                }
            };
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.Add("test");
            settings.EnabledMetrics.UnionWith(["expired", "low", "slower", "faster"]);
            var layout = new LayoutStore(settings, [active]);
            var store = new WidgetDataStore(layout, settings, directory.FullName);

            await store.RefreshAllAsync(false);

            Assert.Equal(new[] { "faster", "slower", "low", "expired" },
                PriorityBoard.Make(layout, store, now).RunningOut.Select(m => m.Descriptor.Id));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task SeparatesFirstReadFailureFromAnEmptySuccessfulReadAndRestoresCachedReads()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-priority-");
        try
        {
            var failed = new Runtime("failed") { Fail = true };
            var empty = new Runtime("empty");
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.UnionWith(["failed", "empty"]);
            var layout = new LayoutStore(settings, [failed, empty]);
            var store = new WidgetDataStore(layout, settings, directory.FullName);

            await store.RefreshProviderAsync("empty");
            var mixed = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.Equal("failed", Assert.Single(mixed.WaitingForFirstRead).Id);
            Assert.False(mixed.HasUsage);
            Assert.False(mixed.HasRefreshErrors);

            await store.RefreshProviderAsync("failed");
            var afterFailure = PriorityBoard.Make(layout, store, DateTimeOffset.UtcNow);
            Assert.Empty(afterFailure.WaitingForFirstRead);
            Assert.True(afterFailure.HasRefreshErrors);

            failed.Fail = false;
            await store.RefreshProviderAsync("failed");
            var reopened = new WidgetDataStore(layout, settings, directory.FullName);
            var afterRestart = PriorityBoard.Make(layout, reopened, DateTimeOffset.UtcNow);
            Assert.Empty(afterRestart.WaitingForFirstRead);
            Assert.False(afterRestart.HasUsage);
            Assert.False(afterRestart.HasRefreshErrors);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class Runtime(string id, params (string Id, double Used)[] metrics) : IProviderRuntime
    {
        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
            metrics.Select(metric => WidgetDescriptor.Percent(metric.Id, Provider, metric.Id)).ToList();

        public bool Fail { get; set; }
        public IReadOnlyDictionary<string, (DateTimeOffset Reset, long PeriodMs)> Windows { get; init; } =
            new Dictionary<string, (DateTimeOffset, long)>();
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Fail
                ? ProviderSnapshot.Error(Provider, "Fixture retry.")
                : ProviderSnapshot.Make(Provider, null,
                    metrics.Select(Line).ToList(),
                    DateTimeOffset.UtcNow));

        private MetricLine Line((string Id, double Used) metric) =>
            Windows.TryGetValue(metric.Id, out var window)
                ? MetricLine.Progress(metric.Id, metric.Used, 100, ProgressFormat.Percent, window.Reset, window.PeriodMs)
                : MetricLine.Progress(metric.Id, metric.Used, 100, ProgressFormat.Percent);
    }
}
