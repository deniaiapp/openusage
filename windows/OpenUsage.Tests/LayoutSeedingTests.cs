using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

/// <summary>New default metrics reach layouts saved before they shipped, once.</summary>
public sealed class LayoutSeedingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openusage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyList<IProviderRuntime> _providers = ProviderCatalog.Make(new AppHttp(), new ApiKeyStore());

    public LayoutSeedingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// A layout saved by the first Windows build: baseline defaults, no seeding record.
    private AppSettings LegacySettings()
    {
        var settings = AppSettings.Load(SettingsPath);
        settings.EnabledMetrics = new HashSet<string>(DefaultLayout.MigrationBaselineMetricIds, StringComparer.Ordinal);
        settings.EnabledMetrics.Remove("claude.session");
        settings.ExpandedMetrics = new HashSet<string>(["claude.sonnet"], StringComparer.Ordinal);
        settings.PinnedMetrics = new HashSet<string>(["ollama.session", "ollama.weekly"], StringComparer.Ordinal);
        settings.SeededDefaultMetrics = null;
        return settings;
    }

    [Fact]
    public void LegacyLayout_GetsNewDefaultsWithTheirPlacement()
    {
        var settings = LegacySettings();
        var layout = new LayoutStore(settings, _providers);

        Assert.True(layout.IsMetricEnabled("claude.rateLimitResets"));
        Assert.True(layout.IsOnDemand("claude.rateLimitResets"));
        Assert.True(layout.IsMetricEnabled("ollama.monthly"));
        Assert.True(layout.IsMetricEnabled("ollama.purchasedCredits"));
        Assert.True(layout.IsOnDemand("ollama.purchasedCredits"));
        // Ollama already has two pins, so the new default pin doesn't push past the cap.
        Assert.False(layout.IsPinned("ollama.monthly"));
        // A baseline metric the user turned off stays off.
        Assert.False(layout.IsMetricEnabled("claude.session"));
        Assert.True(AppSettings.Load(SettingsPath).SeededDefaultMetrics!.SetEquals(DefaultLayout.MetricIds));
    }

    [Fact]
    public void NewDefaultTurnedOff_StaysOffOnNextLaunch()
    {
        var layout = new LayoutStore(LegacySettings(), _providers);
        layout.SetMetricEnabled("ollama.monthly", false);

        var next = new LayoutStore(AppSettings.Load(SettingsPath), _providers);
        Assert.False(next.IsMetricEnabled("ollama.monthly"));
    }

    [Fact]
    public void EmptyMetricPreferencesStayEmptyAfterRestart()
    {
        var settings = AppSettings.Load(SettingsPath);
        settings.EnabledMetrics.Clear();
        settings.ExpandedMetrics.Clear();
        settings.PinnedMetrics.Clear();
        settings.Save();

        var restored = AppSettings.Load(SettingsPath);
        _ = new LayoutStore(restored, _providers);

        Assert.Empty(restored.EnabledMetrics);
        Assert.Empty(restored.ExpandedMetrics);
        Assert.Empty(restored.PinnedMetrics);
    }

    [Fact]
    public void MissingMetricPreferencesUseDefaults()
    {
        File.WriteAllText(SettingsPath, """{"enabledProviders":["claude"],"pinnedMetrics":null}""");
        var settings = AppSettings.Load(SettingsPath);
        _ = new LayoutStore(settings, _providers);

        Assert.True(settings.EnabledMetrics.SetEquals(DefaultLayout.MetricIds));
        Assert.True(settings.ExpandedMetrics.SetEquals(DefaultLayout.ExpandedMetricIds));
        Assert.True(settings.PinnedMetrics.SetEquals(DefaultLayout.PinnedMetricIds));
    }

    [Fact]
    public void FreshInstall_RecordsEveryDefaultAsSeeded()
    {
        var settings = AppSettings.Load(SettingsPath);
        var layout = new LayoutStore(settings, _providers);

        Assert.True(settings.SeededDefaultMetrics!.SetEquals(DefaultLayout.MetricIds));
        Assert.True(layout.IsMetricEnabled("claude.rateLimitResets"));
        Assert.False(File.Exists(SettingsPath));
    }
}
