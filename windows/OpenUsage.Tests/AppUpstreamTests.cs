using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

/// <summary>Ports of upstream app-shell behavior: the first-launch welcome flow (#1380, #1384).</summary>
public sealed class AppUpstreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openusage-tests-" + Guid.NewGuid().ToString("N"));

    public AppUpstreamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private (AppSettings Settings, LayoutStore Layout, WidgetDataStore Data) Make(params FakeProvider[] providers)
    {
        var settings = AppSettings.Load(SettingsPath);
        var layout = new LayoutStore(settings, providers);
        var data = new WidgetDataStore(layout, settings, _dir);
        return (settings, layout, data);
    }

    [Fact]
    public void FreshInstall_StartsWithNothingOnAndOwesTheWelcome()
    {
        var (settings, _, _) = Make(new FakeProvider("claude", detected: true));

        Assert.True(FirstLaunchSetup.MarkPendingIfNeeded(settings));
        Assert.Empty(settings.EnabledProviders);
        Assert.True(settings.Seeded);

        var reloaded = AppSettings.Load(SettingsPath);
        Assert.True(reloaded.WelcomePending);
        Assert.Empty(reloaded.EnabledProviders);
    }

    [Fact]
    public void ExistingInstall_SkipsTheWelcomeAndKeepsItsProviders()
    {
        var settings = AppSettings.Load(SettingsPath);
        settings.Seeded = true;
        settings.EnabledProviders = new HashSet<string>(["codex"], StringComparer.Ordinal);

        Assert.False(FirstLaunchSetup.MarkPendingIfNeeded(settings));
        Assert.Equal(["codex"], settings.EnabledProviders);
    }

    [Fact]
    public void WelcomeLeftOpen_ReturnsOnNextLaunchWithoutReseeding()
    {
        var settings = AppSettings.Load(SettingsPath);
        FirstLaunchSetup.MarkPendingIfNeeded(settings);
        settings.EnabledProviders.Add("claude");
        settings.Save();

        var next = AppSettings.Load(SettingsPath);
        Assert.True(FirstLaunchSetup.MarkPendingIfNeeded(next));
        Assert.Contains("claude", next.EnabledProviders);
    }

    [Fact]
    public async Task Detect_PreselectsSignedInProvidersWithoutBlockingTheCaller()
    {
        var claude = new FakeProvider("claude", detected: true);
        var codex = new FakeProvider("codex", detected: false);
        var broken = new FakeProvider("cursor", detected: true, probeThrows: true);
        using var gate = new ManualResetEventSlim(false);
        claude.Gate = gate;
        var (settings, layout, data) = Make(claude, codex, broken);
        FirstLaunchSetup.MarkPendingIfNeeded(settings);
        var setup = new FirstLaunchSetup(layout, data, settings);

        // A blocking credential read must not hold up the caller (the UI thread at launch).
        var detection = setup.DetectAsync();
        Assert.False(detection.IsCompleted);
        Assert.True(setup.IsDetecting);
        gate.Set();
        await detection;

        Assert.False(setup.IsDetecting);
        Assert.Equal(["claude"], setup.SelectedIds);
        Assert.Equal(["claude"], setup.VisibleChoices.Select(c => c.Id));
        Assert.Empty(settings.EnabledProviders);

        setup.ToggleShowAll();
        Assert.Equal(3, setup.VisibleChoices.Count);
    }

    [Fact]
    public async Task Connect_TurnsOnWhatWorksAndKeepsFailuresOffWithTheReason()
    {
        var claude = new FakeProvider("claude", detected: true);
        var codex = new FakeProvider("codex", detected: true, refreshError: "Sign in to Codex again.");
        var (settings, layout, data) = Make(claude, codex);
        FirstLaunchSetup.MarkPendingIfNeeded(settings);
        var setup = new FirstLaunchSetup(layout, data, settings);
        await setup.DetectAsync();

        await setup.ConnectSelectedAsync();

        Assert.True(setup.HasAttemptedConnection);
        Assert.True(setup.HasFailures);
        Assert.Equal(ConnectionState.Connected, setup.Choices.Single(c => c.Id == "claude").Connection);
        var failed = setup.Choices.Single(c => c.Id == "codex");
        Assert.Equal(ConnectionState.Failed, failed.Connection);
        Assert.Equal("Sign in to Codex again.", failed.FailureMessage);
        Assert.Equal(["claude"], settings.EnabledProviders);

        // Retry only fetches what hasn't connected yet.
        codex.RefreshError = null;
        await setup.ConnectSelectedAsync();
        Assert.Equal(1, claude.RefreshCount);
        Assert.Equal(ConnectionState.Connected, failed.Connection);
        Assert.Equal(new HashSet<string>(["claude", "codex"]), settings.EnabledProviders);
    }

    [Fact]
    public async Task Skip_FinishesWithNothingOnAndDismissesTheCustomizeHint()
    {
        var (settings, layout, data) = Make(new FakeProvider("claude", detected: true));
        FirstLaunchSetup.MarkPendingIfNeeded(settings);
        var setup = new FirstLaunchSetup(layout, data, settings);
        await setup.DetectAsync();

        setup.Finish();

        var reloaded = AppSettings.Load(SettingsPath);
        Assert.False(reloaded.WelcomePending);
        Assert.Empty(reloaded.EnabledProviders);
        Assert.True(reloaded.CustomizeHintDismissed);
    }

    [Fact]
    public async Task Finish_AfterConnectingKeepsTheCustomizeHint()
    {
        var (settings, layout, data) = Make(new FakeProvider("claude", detected: true));
        FirstLaunchSetup.MarkPendingIfNeeded(settings);
        var setup = new FirstLaunchSetup(layout, data, settings);
        await setup.DetectAsync();
        await setup.ConnectSelectedAsync();

        setup.Finish();

        Assert.False(settings.WelcomePending);
        Assert.False(settings.CustomizeHintDismissed);
        Assert.Equal(["claude"], settings.EnabledProviders);
    }

    private sealed class FakeProvider(string id, bool detected, bool probeThrows = false, string? refreshError = null)
        : IProviderRuntime
    {
        public Provider Provider { get; } = new(id, id, id);
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => [];
        public string? RefreshError { get; set; } = refreshError;
        public int RefreshCount { get; private set; }
        public ManualResetEventSlim? Gate { get; set; }

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult(RefreshError is { } message
                ? ProviderSnapshot.Error(Provider, message)
                : ProviderSnapshot.Make(Provider, null, [MetricLine.Text("Status", "OK")], DateTimeOffset.Now));
        }

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Gate?.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            if (probeThrows) throw new InvalidOperationException("probe failed");
            return Task.FromResult(detected);
        }
    }
}
