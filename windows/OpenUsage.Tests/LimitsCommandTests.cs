using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class LimitsCommandTests : IDisposable
{
    private static readonly DateTimeOffset FetchedAt = DateTimeOffset.Parse("2026-11-21T00:00:00Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openusage-limits-{Guid.NewGuid():N}");
    private string Cache => Path.Combine(_directory, "cache");
    private string Settings => Path.Combine(_directory, "settings.json");

    public LimitsCommandTests() => Directory.CreateDirectory(Cache);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task CommandReadsLastGoodSnapshotAndIgnoresOtherCacheEntries()
    {
        var runtime = new CodexRuntime();
        var settings = AppSettings.Load(Settings);
        settings.EnabledProviders.Add("codex");
        settings.Save();
        var store = new WidgetDataStore(new LayoutStore(settings, [runtime]), settings, Cache);
        await store.RefreshProviderAsync("codex");
        runtime.Failing = true;
        await store.RefreshProviderAsync("codex");
        File.WriteAllText(Path.Combine(Cache, "cursor.json"), "unused");

        var (code, output, error) = Run(FetchedAt.AddMinutes(10), "codex", "--force");
        Assert.Equal(0, code);
        Assert.Contains("--force is ignored", error);
        Assert.DoesNotContain("Could not read", error);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("openusage.limits.v2", doc.RootElement.GetProperty("schema").GetString());
        var providers = doc.RootElement.GetProperty("providers");
        var codex = Assert.Single(providers.EnumerateObject()).Value;
        Assert.Equal(FetchedAt, codex.GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.True(codex.GetProperty("stale").GetBoolean());
        var resources = codex.GetProperty("resources");
        var session = resources.GetProperty("session");
        Assert.Equal(42, session.GetProperty("used").GetDouble());
        Assert.Equal(58, session.GetProperty("remaining").GetDouble());
        Assert.Equal(18000, session.GetProperty("windowSeconds").GetDouble());
        var credits = resources.GetProperty("credits");
        Assert.Equal("balance", credits.GetProperty("kind").GetString());
        Assert.Equal(821, credits.GetProperty("available").GetDouble());
        Assert.False(credits.TryGetProperty("used", out _));
        Assert.False(resources.TryGetProperty("weekly", out _));
        Assert.DoesNotContain("a failed sign-in", output);

        settings.EnabledProviders.Clear();
        settings.Save();
        Assert.Equal(4, Run(FetchedAt).Code);
        Assert.Equal(2, Run(FetchedAt, "codex").Code);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(29, 600)]
    [InlineData(60, 120)]
    [InlineData(900, 1800)]
    public void CommandExpiresCachedDataAfterTwoConfiguredRefreshIntervals(int? interval, int freshness)
    {
        File.WriteAllText(Path.Combine(Cache, "codex.json"), """
            {"ProviderId":"codex","DisplayName":"Codex","RefreshedAt":"2026-11-21T00:00:00Z",
             "Limits":{"session":{"Kind":"consumption","Unit":"percent","Used":20}}}
            """);

        var settings = new Dictionary<string, object> { ["enabledProviders"] = new[] { "codex" } };
        if (interval is { } seconds) settings["refreshIntervalSeconds"] = seconds;
        File.WriteAllText(Settings, JsonSerializer.Serialize(settings));
        var expiry = FetchedAt.AddSeconds(freshness);

        var (freshCode, before, _) = Run(expiry.AddSeconds(-1), "codex");
        var (staleCode, atExpiry, _) = Run(expiry, "codex");
        Assert.Equal(0, freshCode);
        Assert.Equal(0, staleCode);
        using var fresh = JsonDocument.Parse(before);
        using var stale = JsonDocument.Parse(atExpiry);
        var freshEntry = fresh.RootElement.GetProperty("providers").GetProperty("codex");
        var staleEntry = stale.RootElement.GetProperty("providers").GetProperty("codex");
        Assert.Equal(expiry, freshEntry.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(freshEntry.GetProperty("stale").GetBoolean());
        Assert.True(staleEntry.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public void CommandRejectsUnknownArgumentsAndDoesNotCreateSettings()
    {
        Assert.Equal(2, Run(FetchedAt, "../keys").Code);
        Assert.Equal(2, Run(FetchedAt, "--refresh").Code);
        Assert.Equal(2, Run(FetchedAt, "codex", "claude").Code);
        var (code, output, _) = Run(FetchedAt);
        Assert.Equal(4, code);
        Assert.False(File.Exists(Settings));
        using var doc = JsonDocument.Parse(output);
        Assert.Empty(doc.RootElement.GetProperty("providers").EnumerateObject());
    }

    [Theory]
    [InlineData("--help", "Usage: openusage")]
    [InlineData("-h", "Usage: openusage")]
    [InlineData("--version", "openusage fixture-build")]
    [InlineData("-v", "openusage fixture-build")]
    public void HelpAndVersionReturnWithoutReadingSettings(string flag, string expected)
    {
        File.WriteAllText(Settings, "not JSON");

        var (code, output, error) = Run(FetchedAt, flag);
        Assert.Equal(0, code);
        Assert.StartsWith(expected, output);
        Assert.Empty(error);
        Assert.Equal(2, Run(FetchedAt, flag, "--unknown").Code);
    }

    [Fact]
    public void CommandReadsOnlyTheLimitsProjectionAndSkipsOldOrDamagedSnapshots()
    {
        var settings = AppSettings.Load(Settings);
        settings.EnabledProviders.UnionWith(["claude", "copilot", "codex"]);
        settings.Save();
        File.WriteAllText(Path.Combine(Cache, "claude.json"), """{"ProviderId":"claude","DisplayName":"Claude"}""");
        File.WriteAllText(Path.Combine(Cache, "copilot.json"), "{");
        File.WriteAllText(Path.Combine(Cache, "codex.json"), """
            {"ProviderId":"codex","DisplayName":"Codex","RefreshedAt":"2026-11-21T00:00:00Z",
             "Warning":"not for CLI output","Lines":[{"Type":"text","Value":"not for CLI output"}],
             "Limits":{"session":{"Kind":"consumption","Unit":"percent","Used":20}}}
            """);

        var (code, output, error) = Run(FetchedAt.AddMinutes(9));
        Assert.Equal(0, code);
        Assert.Contains("copilot.json", error);
        Assert.DoesNotContain("not for CLI output", output);
        using var doc = JsonDocument.Parse(output);
        var provider = Assert.Single(doc.RootElement.GetProperty("providers").EnumerateObject());
        Assert.Equal("codex", provider.Name);
        Assert.False(provider.Value.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task ProviderFilterIncludesItsAccountsAndAccountFilterIsExact()
    {
        var work = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Work");
        var personal = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Personal");
        var settings = AppSettings.Load(Settings);
        settings.EnabledProviders.UnionWith([work.CardId, personal.CardId]);
        settings.Save();
        var layout = new LayoutStore(settings, [new AccountRuntime(work, 11), new AccountRuntime(personal, 17)]);
        var store = new WidgetDataStore(layout, settings, Cache);
        await store.RefreshAllAsync(false);

        var (code, output, _) = Run(FetchedAt, "OpenRouter");
        Assert.Equal(0, code);
        using var all = JsonDocument.Parse(output);
        var providers = all.RootElement.GetProperty("providers");
        Assert.Equal(11, providers.GetProperty(work.CardId).GetProperty("resources").GetProperty("balance").GetProperty("available").GetDouble());
        Assert.Equal(17, providers.GetProperty(personal.CardId).GetProperty("resources").GetProperty("balance").GetProperty("available").GetDouble());
        var (singleCode, singleOutput, _) = Run(FetchedAt, work.CardId.ToUpperInvariant());
        Assert.Equal(0, singleCode);
        using var one = JsonDocument.Parse(singleOutput);
        Assert.Equal(work.CardId, Assert.Single(one.RootElement.GetProperty("providers").EnumerateObject()).Name);
        Assert.Equal(2, Run(FetchedAt, "openrouter@../account").Code);
    }

    private (int Code, string Output, string Error) Run(DateTimeOffset now, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = LimitsCommand.Run(args, _directory, output, error, now, "fixture-build");
        return (code, output.ToString(), error.ToString());
    }

    private sealed class CodexRuntime : IProviderRuntime
    {
        public bool Failing { get; set; }
        public Provider Provider { get; } = new("codex", "Codex", "codex");
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
        [
            WidgetDescriptor.Percent("codex.session", Provider, "Session"),
            WidgetDescriptor.Percent("codex.weekly", Provider, "Weekly"),
            WidgetDescriptor.Values("codex.credits", Provider, "Credits")
        ];

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(
            Failing ? ProviderSnapshot.Error(Provider, "a failed sign-in") :
            ProviderSnapshot.Make(Provider, "Pro", [
                MetricLine.Progress("Session", 42, 100, ProgressFormat.Percent, FetchedAt.AddHours(1), MetricPeriod.SessionMs),
                MetricLine.Values("Credits", [new MetricValue(5, MetricKind.Dollars), new MetricValue(821, MetricKind.Count, "credits")])
            ], FetchedAt));
    }

    private sealed class AccountRuntime(ApiKeyAccount account, int balance) : IProviderRuntime
    {
        public Provider Provider { get; } = new(account.CardId, $"OpenRouter · {account.Label}", "openrouter");
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
            [WidgetDescriptor.DollarBalance(Provider.Id + ".balance", Provider, "Balance", "left")];
        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(
            ProviderSnapshot.Make(Provider, null, [MetricLine.Values("Balance", [new MetricValue(balance, MetricKind.Dollars)])], FetchedAt));
    }
}
