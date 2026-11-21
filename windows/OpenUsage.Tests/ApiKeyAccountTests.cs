using System.Net;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.OpenRouter;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class ApiKeyAccountTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openusage-accounts-").FullName;
    private string AccountFile => Path.Combine(_directory, "accounts.bin");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("openrouter", 401, "{}", "rejected")]
    [InlineData("openrouter", 200, "{}", "unavailable")]
    [InlineData("zai", 200, """{"success":false,"code":401}""", "rejected")]
    [InlineData("zai", 200, """{"success":false,"msg":"No coding plan"}""", "GLM Coding Plan")]
    [InlineData("zai", 200, """{"success":true,"data":{}}""", "unavailable")]
    public async Task FailedProofLeavesAccountStoreUntouched(string provider, int status, string body, string message)
    {
        using var http = MakeHttp(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            Assert.Equal(provider == "openrouter" ? OpenRouterProvider.KeyUrl : ZaiProvider.QuotaUrl,
                request.RequestUri);
            return (status, body);
        });
        var service = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            service.ConnectAsync(provider, "Sample", " fixture-key "));

        Assert.Contains(message, error.Message);
        Assert.False(File.Exists(AccountFile));
    }

    [Fact]
    public async Task OpenRouterManagementAccountTotalsAllListedKeys()
    {
        var firstPage = Enumerable.Repeat(new { usage_daily = 1, usage_weekly = 2, usage_monthly = 3 }, 100);
        var secondPage = new[] { new { usage_daily = 4, usage_weekly = 5, usage_monthly = 6 } };
        var queries = new List<string>();
        using var http = MakeHttp(request =>
        {
            Assert.Equal("fixture-management-key", request.Headers.Authorization?.Parameter);
            if (request.RequestUri == OpenRouterProvider.CreditsUrl) return (403, "{}");
            if (request.RequestUri == OpenRouterProvider.KeyUrl)
                return (200, """{"data":{"is_management_key":true,"usage_daily":0,"usage_weekly":0,"usage_monthly":0}}""");

            Assert.Equal("/api/v1/keys", request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            queries.Add(query);
            var keys = query.EndsWith("=0", StringComparison.Ordinal) ? firstPage : secondPage;
            return (200, JsonSerializer.Serialize(new { data = keys }));
        });
        var provider = new OpenRouterProvider(http, new ApiKeyStore());
        var snapshot = await provider.RefreshWithKeyAsync("fixture-management-key", default, requireKey: true);
        Assert.False(snapshot.IsError);
        Assert.Equal(new[] { "?include_disabled=true&include_expired=true&offset=0", "?include_disabled=true&include_expired=true&offset=100" }, queries);
        Assert.Equal(104, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("Today")).Items).Number);
        Assert.Equal(205, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("This Week")).Items).Number);
        Assert.Equal(306, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("This Month")).Items).Number);
    }

    [Fact]
    public void AddingAccountsKeepsEachProviderGroupTogetherAcrossRestart()
    {
        using var http = new AppHttp();
        var keys = new ApiKeyStore();
        var openRouter = new OpenRouterProvider(http, keys);
        var zai = new ZaiProvider(http, keys);
        var factory = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        var work = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Work");
        var coding = new ApiKeyAccount(Guid.NewGuid(), "zai", "Coding");
        var personal = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Personal");
        var settingsPath = Path.Combine(_directory, "settings.json");

        void Register(LayoutStore layout)
        {
            layout.RegisterAccount(factory.MakeRuntime(work, openRouter), work.ProviderId);
            layout.RegisterAccount(factory.MakeRuntime(coding, zai), coding.ProviderId);
            layout.RegisterAccount(factory.MakeRuntime(personal, openRouter), personal.ProviderId);
        }

        var settings = AppSettings.Load(settingsPath);
        settings.EnabledProviders.UnionWith(["openrouter", "zai"]);
        var initial = new LayoutStore(settings, [openRouter, zai]);
        Register(initial);
        var restored = new LayoutStore(AppSettings.Load(settingsPath), [openRouter, zai]);
        Register(restored);
        string[] expected = ["openrouter", work.CardId, personal.CardId, "zai", coding.CardId];
        Assert.Equal(expected, restored.VisibleProviders.Select(p => p.Provider.Id));
        Assert.Equal(expected, restored.Providers.Select(p => p.Provider.Id));
    }

    [Fact]
    public void FirstAccountForHiddenProviderAppendsAndLaterAccountsJoinIt()
    {
        using var http = new AppHttp();
        var keys = new ApiKeyStore();
        var openRouter = new OpenRouterProvider(http, keys);
        var zai = new ZaiProvider(http, keys);
        var factory = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        var work = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Work");
        var personal = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Personal");
        var settingsPath = Path.Combine(_directory, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.EnabledProviders = new HashSet<string>(["zai"], StringComparer.Ordinal);
        var layout = new LayoutStore(settings, [openRouter, zai]);

        layout.RegisterAccount(factory.MakeRuntime(work, openRouter), work.ProviderId);
        Assert.Equal(new[] { "zai", work.CardId }, layout.VisibleProviders.Select(p => p.Provider.Id));

        layout.RegisterAccount(factory.MakeRuntime(personal, openRouter), personal.ProviderId);
        var restored = new LayoutStore(AppSettings.Load(settingsPath), [openRouter, zai]);
        restored.RegisterAccount(factory.MakeRuntime(work, openRouter), work.ProviderId);
        restored.RegisterAccount(factory.MakeRuntime(personal, openRouter), personal.ProviderId);
        Assert.Equal(new[] { "zai", work.CardId, personal.CardId }, restored.VisibleProviders.Select(p => p.Provider.Id));
    }

    [Fact]
    public void HiddenAdditionalAccountKeepsItsPlaceWhenAnotherAccountIsAdded()
    {
        using var http = new AppHttp();
        var openRouter = new OpenRouterProvider(http, new ApiKeyStore());
        var factory = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        var work = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Work");
        var personal = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Personal");
        var settings = AppSettings.Load(Path.Combine(_directory, "settings.json"));
        var layout = new LayoutStore(settings, [openRouter]);
        layout.SetProviderEnabled("openrouter", true);

        layout.RegisterAccount(factory.MakeRuntime(work, openRouter), work.ProviderId);
        layout.SetProviderEnabled(work.CardId, false);
        layout.RegisterAccount(factory.MakeRuntime(personal, openRouter), personal.ProviderId);
        Assert.Equal(new[] { "openrouter", personal.CardId }, layout.VisibleProviders.Select(p => p.Provider.Id));
        layout.SetProviderEnabled(work.CardId, true);
        Assert.Equal(new[] { "openrouter", work.CardId, personal.CardId }, layout.VisibleProviders.Select(p => p.Provider.Id));
    }

    [Fact]
    public async Task UnsupportedProvidersAndInvalidInputNeverMakeARequest()
    {
        using var http = MakeHttp(_ => throw new InvalidOperationException("Unexpected request"));
        var accounts = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.ConnectAsync("claude", "Work", "fixture-key"));
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.ConnectAsync("openrouter", "Work", "first\r\nsecond"));
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.ConnectAsync("openrouter", " ", "fixture-key"));
    }

    [WindowsFact]
    public async Task ConnectedAccountsUseTheirOwnKeyAndKeepTheirIdsAcrossRestarts()
    {
        using var http = MakeHttp(request =>
        {
            var remaining = request.Headers.Authorization?.Parameter switch
            {
                "fixture-key-work" => 12,
                "fixture-key-personal" => 16,
                _ => throw new InvalidOperationException("An unexpected key was used")
            };
            return request.RequestUri == OpenRouterProvider.KeyUrl
                ? (200, JsonSerializer.Serialize(new { data = new { limit = 20, limit_remaining = remaining } }))
                : (403, "{}");
        });
        var service = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        var work = await service.ConnectAsync("openrouter", " Work ", "fixture-key-work");
        var personal = await service.ConnectAsync("openrouter", "Personal", "fixture-key-personal");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectAsync("openrouter", "Duplicate", "fixture-key-work"));
        var stored = File.ReadAllBytes(AccountFile);
        Assert.DoesNotContain("fixture-key-work", System.Text.Encoding.UTF8.GetString(stored));
        Assert.NotEqual(work.CardId, personal.CardId);

        var loaded = new ApiKeyAccountStore(AccountFile);
        Assert.Equal(new[] { work, personal }, loaded.List());
        Assert.True(loaded.Rename(work.Id, "Client Work"));
        var runtimeFactory = new ApiKeyAccounts(http, loaded);
        var provider = new OpenRouterProvider(http, new ApiKeyStore());
        var workRuntime = runtimeFactory.MakeRuntime(loaded.List()[0], provider);
        var workSnapshot = await workRuntime.RefreshAsync();
        Assert.Equal(work.CardId, workSnapshot.ProviderId);
        Assert.Equal("OpenRouter · Client Work", workSnapshot.DisplayName);
        Assert.Equal(8, Assert.IsType<MetricLine.ProgressLine>(workSnapshot.Line("Key Limit")).Used);
        Assert.All(workRuntime.WidgetDescriptors, descriptor => Assert.StartsWith(work.CardId + ".", descriptor.Id));

        Assert.True(loaded.Remove(work.Id));
        Assert.True((await workRuntime.RefreshAsync()).IsError);
        Assert.Equal(personal, Assert.Single(new ApiKeyAccountStore(AccountFile).List()));
        var personalSnapshot = await runtimeFactory.MakeRuntime(personal, provider).RefreshAsync();
        Assert.Equal(4, Assert.IsType<MetricLine.ProgressLine>(personalSnapshot.Line("Key Limit")).Used);
    }

    private static AppHttp MakeHttp(Func<HttpRequestMessage, (int Status, string Body)> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private sealed class StubHandler(Func<HttpRequestMessage, (int Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
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
