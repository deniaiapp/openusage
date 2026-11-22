using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class UnifiedAccountViewTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("openusage-unified-");
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CombinesOnlyEnabledAccountsAndKeepsTheirOrderAndEarliestReset()
    {
        var first = new Runtime("codex@first", "Plus", [("session", 20), ("weekly", 90)], Now.AddHours(3));
        var second = new Runtime("codex@second", "Pro", [("session", 60), ("extra", 50), ("weekly", 30)], Now.AddHours(1));
        var other = new Runtime("grok@first", "Pro", [("session", 100)], Now.AddHours(2));
        var secondOther = new Runtime("grok@second", "Pro", [("session", 100)], Now.AddHours(2));
        var (layout, store, settings) = Make(first, second, other, secondOther);
        Assert.Empty(store.UnifiedAccounts());
        layout.SetUnifiesAccounts("codex", true);
        layout.SetUnifiesAccounts("grok", true);
        layout.SetMetricEnabled("codex@first.weekly", false);
        layout.SetOnDemand("codex@second.extra", true);
        await store.RefreshAllAsync(false);

        Assert.Equal(["codex"], store.UnifiedAccounts().Keys);
        var account = store.UnifiedAccounts()["codex"];
        Assert.Equal("codex.unified", account.Provider.Id);
        Assert.Equal("Multiple", account.Plan);
        Assert.Equal([first.Provider, second.Provider], account.Accounts);
        Assert.Equal(["session", "extra", "weekly"], account.Metrics.Select(metric => metric.Key));
        Assert.Equal(40d, account.Metrics[0].Data.Used);
        Assert.Equal(Now.AddHours(1), account.Metrics[0].Data.ResetsAt);
        Assert.True(account.Metrics[1].OnDemand);
        Assert.Equal(30d, account.Metrics[2].Data.Used);
        Assert.Equal(["codex@first.weekly", "codex@second.weekly"], account.Metrics[2].CopyIds);

        layout.SetProviderEnabled(second.Provider.Id, false);
        Assert.Empty(store.UnifiedAccounts());
        Assert.Contains("codex", AppSettings.Load(settings.FilePath).UnifiedProviders);
    }

    [Fact]
    public async Task UnifiedPinsCountOnceAndHideAffectsEveryCopy()
    {
        var first = new Runtime("claude@first", "Pro", [("session", 100), ("weekly", 30), ("extra", 30)], Now.AddHours(3));
        var second = new Runtime("claude@second", "Pro", [("session", 50), ("weekly", 30), ("extra", 30)], Now.AddHours(2));
        var (layout, store, _) = Make(first, second);
        layout.SetUnifiesAccounts("claude", true);
        await store.RefreshAllAsync(false);

        var metric = store.UnifiedAccounts()["claude"].Metrics[0];
        Assert.True(layout.TogglePin("claude@second.weekly"));
        Assert.True(layout.TogglePin("claude@second.extra"));
        Assert.False(layout.ToggleUnifiedMetricPin(metric));
        Assert.DoesNotContain(metric.CopyIds, layout.IsPinned);
        Assert.True(layout.TogglePin("claude@second.weekly"));
        Assert.True(layout.TogglePin("claude@second.extra"));

        Assert.True(layout.ToggleUnifiedMetricPin(metric));
        Assert.All(metric.CopyIds, id => Assert.True(layout.IsPinned(id)));
        Assert.Equal(75d, Assert.Single(store.PinnedTiles()).Data.Used);
        var board = PriorityBoard.Make(layout, store, Now);
        Assert.Equal(metric.Descriptor.Id, Assert.Single(board.Pinned).Descriptor.Id);
        Assert.Empty(board.UsedUp);

        layout.HideUnifiedMetric(metric);
        Assert.DoesNotContain(metric.CopyIds, layout.IsMetricEnabled);
        Assert.Empty(store.PinnedTiles());
        Assert.Empty(PriorityBoard.Make(layout, store, Now).Pinned);
        layout.SetUnifiesAccounts("claude", false);
        Assert.Empty(store.UnifiedAccounts());
    }

    [Fact]
    public async Task AWaitingAccountDoesNotDiluteQuotaOrLoseItsCopy()
    {
        var first = new Runtime("codex@first", "Pro", [("session", 75)], Now.AddHours(1));
        var waiting = new Runtime("codex@waiting", "Pro", [("session", 5)], Now.AddHours(2));
        var (layout, store, _) = Make(first, waiting);
        layout.SetUnifiesAccounts("codex", true);
        Assert.Empty(store.UnifiedAccounts()["codex"].Metrics);

        await store.RefreshProviderAsync(first.Provider.Id);
        var metric = Assert.Single(store.UnifiedAccounts()["codex"].Metrics);
        Assert.Equal(75d, metric.Data.Used);
        Assert.Equal(["codex@first.session", "codex@waiting.session"], metric.CopyIds);
        Assert.Equal(waiting.Provider, Assert.Single(PriorityBoard.Make(layout, store, Now).WaitingForFirstRead));
    }

    private (LayoutStore Layout, WidgetDataStore Store, AppSettings Settings) Make(params Runtime[] runtimes)
    {
        var settings = AppSettings.Load(Path.Combine(_directory.FullName, "settings.json"));
        settings.EnabledProviders = runtimes.Select(runtime => runtime.Provider.Id).ToHashSet(StringComparer.Ordinal);
        settings.EnabledMetrics = runtimes.SelectMany(runtime => runtime.WidgetDescriptors).Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        settings.PinnedMetrics.Clear();
        var layout = new LayoutStore(settings, runtimes);
        return (layout, new WidgetDataStore(layout, settings, _directory.FullName), settings);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class Runtime(string id, string plan, (string Key, double Used)[] readings, DateTimeOffset resetsAt)
        : IProviderRuntime
    {
        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
            readings.Select(reading => WidgetDescriptor.Percent(id + "." + reading.Key, Provider, reading.Key)).ToArray();

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderSnapshot.Make(Provider, plan,
                readings.Select(reading => MetricLine.Progress(reading.Key, reading.Used, 100, ProgressFormat.Percent, resetsAt))
                    .ToArray(),
                Now));
    }
}
