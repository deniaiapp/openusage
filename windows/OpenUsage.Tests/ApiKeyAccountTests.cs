using System.Net;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Ollama;
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
    [InlineData("ollama", 401, "{}", "rejected")]
    [InlineData("ollama", 200, """{"included":{}}""", "unavailable")]
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
            Assert.Equal(provider switch
            {
                "ollama" => new Uri(OllamaProvider.Host + OllamaProvider.BalancePath),
                "openrouter" => OpenRouterProvider.KeyUrl,
                _ => ZaiProvider.QuotaUrl
            }, request.RequestUri);
            return (status, body);
        });
        var service = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            service.ConnectAsync(provider, "Sample", " fixture-key "));

        Assert.Contains(message, error.Message);
        Assert.False(File.Exists(AccountFile));
    }

    [Fact]
    public async Task OllamaApiKeyRefreshDoesNotUseLocalSignIn()
    {
        var requests = new List<(HttpMethod, string)>();
        using var http = MakeHttp(request =>
        {
            Assert.Equal("https://ollama.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            requests.Add((request.Method, path));
            return path == OllamaProvider.BalancePath
                ? (200, """{"included":{},"purchased":{"balance_usd":5}}""")
                : (200, """{"Plan":"pro"}""");
        });
        var provider = new OllamaProvider(http, () => throw new InvalidOperationException("Local key was consulted"));

        var snapshot = await provider.RefreshWithApiKeyAsync("fixture-key", default);

        Assert.False(snapshot.IsError);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(5, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("Purchased Credits")).Items).Number);
        Assert.Equal(new[] { (HttpMethod.Get, OllamaProvider.BalancePath), (HttpMethod.Post, OllamaProvider.AccountPath) }, requests);
    }

    [WindowsFact]
    public async Task OllamaAccountsReconnectFromTheEncryptedStore()
    {
        using var http = MakeHttp(request =>
        {
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            return request.RequestUri!.AbsolutePath == OllamaProvider.BalancePath
                ? (200, """{"included":{"allowance_usd":20,"balance_usd":17}}""")
                : (200, """{"Plan":"pro"}""");
        });
        var service = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile));
        var connected = await service.ConnectAsync("ollama", "Work", "fixture-key");

        var loaded = new ApiKeyAccountStore(AccountFile);
        var restored = Assert.Single(loaded.List());
        Assert.Equal(connected, restored);
        var runtime = new ApiKeyAccounts(http, loaded).MakeRuntime(restored, new OllamaProvider(http));
        var snapshot = await runtime.RefreshAsync();
        Assert.Equal(restored.CardId, snapshot.ProviderId);
        Assert.Equal("Ollama · Work", snapshot.DisplayName);
        Assert.Equal(3, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Monthly")).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenRouterManagementAccountTotalsAllListedKeys(bool requireKey)
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
        var snapshot = await provider.RefreshWithKeyAsync("fixture-management-key", default, requireKey);
        Assert.False(snapshot.IsError);
        Assert.Null(snapshot.Line("Balance"));
        Assert.Equal(new[] { "?include_disabled=true&include_expired=true&offset=0", "?include_disabled=true&include_expired=true&offset=100" }, queries);
        Assert.Equal(104, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("Today")).Items).Number);
        Assert.Equal(205, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("This Week")).Items).Number);
        Assert.Equal(306, Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("This Month")).Items).Number);
    }

    [Fact]
    public async Task OpenRouterPrimaryManagementAccountDoesNotShowKeySpendIfTheListFails()
    {
        using var http = MakeHttp(request => request.RequestUri == OpenRouterProvider.KeyUrl
            ? (200, """{"data":{"is_management_key":true,"usage_daily":0}}""")
            : (503, "{}"));
        var provider = new OpenRouterProvider(http, new ApiKeyStore());
        var snapshot = await provider.RefreshWithKeyAsync("fixture-management-key", default);

        Assert.True(snapshot.IsError);
        Assert.Null(snapshot.Line("Today"));
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
    public void MovingProviderGroupsPersistsAndUndoDoesNotRevertVisibility()
    {
        using var http = new AppHttp();
        var keys = new ApiKeyStore();
        var openRouter = new OpenRouterProvider(http, keys);
        var zai = new ZaiProvider(http, keys);
        var work = new ApiKeyAccount(Guid.NewGuid(), "openrouter", "Work");
        var account = new ApiKeyAccounts(http, new ApiKeyAccountStore(AccountFile)).MakeRuntime(work, openRouter);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.EnabledProviders.Add("zai");
        var layout = new LayoutStore(settings, [openRouter, zai]);
        layout.RegisterAccount(account, work.ProviderId);

        Assert.Equal(["zai", "openrouter"], layout.ProviderGroups().Select(group => group.Key));
        Assert.False(layout.MoveProviderGroup("zai", -1));
        Assert.False(layout.CanUndoProviderOrder);
        Assert.True(layout.MoveProviderGroup("openrouter", -1));
        Assert.Equal([work.CardId, "zai"], layout.VisibleProviders.Select(p => p.Provider.Id));

        var restored = new LayoutStore(AppSettings.Load(settingsPath), [openRouter, zai]);
        restored.RegisterAccount(account, work.ProviderId);
        Assert.Equal([work.CardId, "zai"], restored.VisibleProviders.Select(p => p.Provider.Id));
        Assert.False(restored.CanUndoProviderOrder);

        layout.SetProviderEnabled(work.CardId, false);
        Assert.True(layout.UndoProviderOrder());
        Assert.Equal(["zai", "openrouter"], layout.ProviderGroups().Select(group => group.Key));
        Assert.False(layout.IsProviderEnabled(work.CardId));
        Assert.False(layout.UndoProviderOrder());
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

    [WindowsFact]
    public async Task ReplacementKeepsAccountIdentityAndOnlySavesValidUniqueKeys()
    {
        using var http = MakeHttp(request =>
        {
            if (request.RequestUri != OpenRouterProvider.KeyUrl) return (403, "{}");
            return request.Headers.Authorization?.Parameter switch
            {
                "old-key" => (200, """{"data":{"limit":20,"limit_remaining":11}}"""),
                "new-key" => (200, """{"data":{"limit":20,"limit_remaining":15}}"""),
                "other-key" => (200, """{"data":{"limit":20,"limit_remaining":12}}"""),
                "rejected-key" => (401, "{}"),
                _ => throw new InvalidOperationException("Unexpected key sent.")
            };
        });
        var store = new ApiKeyAccountStore(AccountFile);
        var service = new ApiKeyAccounts(http, store);
        var original = await service.ConnectAsync("openrouter", "Work", "old-key");
        var other = await service.ConnectAsync("openrouter", "Other", "other-key");
        Assert.True(store.Rename(original.Id, "Renamed Work"));

        await Assert.ThrowsAsync<ProviderRequestException>(() => service.ReplaceKeyAsync(original, "rejected-key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceKeyAsync(original, "other-key"));
        Assert.Equal("old-key", new ApiKeyAccountStore(AccountFile).Key(original.Id));

        await service.ReplaceKeyAsync(original, " new-key ");
        var reloaded = new ApiKeyAccountStore(AccountFile);
        Assert.Equal(new[] { original with { Label = "Renamed Work" }, other }, reloaded.List());
        Assert.Equal("new-key", reloaded.Key(original.Id));
        Assert.Equal("other-key", reloaded.Key(other.Id));

        Assert.True(store.Remove(original.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceKeyAsync(original, "new-key"));
        Assert.Equal(other, Assert.Single(reloaded.List()));
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
