using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class PriorityUnusedQuotaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(30);

    [Fact]
    public async Task PriorityShowsOnlyUnpinnedLongWindowsInResetOrder()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-unused-quota-");
        try
        {
            var runtime = new Runtime([
                Limit("tomorrow", hoursUntilReset: 24), Limit("soon", hoursUntilReset: 3),
                Limit("pinned"), Limit("hidden"), Limit("later", hoursUntilReset: 25),
                Limit("past", hoursUntilReset: 0), Limit("session", period: MetricPeriod.DayMs),
                Limit("spending", format: ProgressFormat.Dollars), Limit("half", used: 50),
                Limit("low", used: 90), Limit("spent", used: 100)
            ]);
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.Add(runtime.Provider.Id);
            settings.EnabledMetrics.UnionWith(runtime.WidgetDescriptors.Select(d => d.Id).Where(id => id != "hidden"));
            settings.ExpandedMetrics.Add("soon");
            settings.PinnedMetrics.Add("pinned");
            var layout = new LayoutStore(settings, [runtime]);
            var store = new WidgetDataStore(layout, settings, directory.FullName);
            await store.RefreshAllAsync(false);

            var board = PriorityBoard.Make(layout, store, Now);
            Assert.Equal(new[] { "soon", "tomorrow" }, board.UseItOrLoseIt.Select(item => item.Descriptor.Id));
            Assert.Equal("pinned", Assert.Single(board.Pinned).Descriptor.Id);
            Assert.Equal("low", Assert.Single(board.RunningOut).Descriptor.Id);
            Assert.Equal("spent", Assert.Single(board.UsedUp).Descriptor.Id);

            settings.ShowUnusedQuotaInPriority = false;
            settings.Save();
            var reloaded = AppSettings.Load(settings.FilePath);
            Assert.False(reloaded.ShowUnusedQuotaInPriority);
            Assert.Empty(PriorityBoard.Make(layout, store, Now, reloaded.ShowUnusedQuotaInPriority).UseItOrLoseIt);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void SettingDefaultsOnForAnExistingLayoutWithoutThePreference()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-unused-quota-setting-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{}");
            Assert.True(AppSettings.Load(path).ShowUnusedQuotaInPriority);
            Assert.True(AppSettings.Fresh().ShowUnusedQuotaInPriority);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static MetricLine.ProgressLine Limit(string label, int hoursUntilReset = 2, double used = 20,
        long period = MetricPeriod.WeekMs, ProgressFormat? format = null) =>
        new(label, used, 100, format ?? ProgressFormat.Percent, Now.AddHours(hoursUntilReset), period);

    private sealed class Runtime(MetricLine.ProgressLine[] limits) : IProviderRuntime
    {
        public Provider Provider { get; } = new("fixture-quota", "Fixture Quota", "fixture");

        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => limits.Select(line =>
            line.Format == ProgressFormat.Dollars
                ? WidgetDescriptor.BoundedDollars(line.Label, Provider, line.Label, 100)
                : WidgetDescriptor.Percent(line.Label, Provider, line.Label)).ToArray();

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderSnapshot.Make(Provider, null, limits, Now));
    }
}
