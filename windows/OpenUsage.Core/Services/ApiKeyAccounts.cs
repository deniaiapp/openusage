using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Providers.OpenCode;
using OpenUsage.Core.Providers.OpenRouter;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public sealed class ApiKeyAccounts(AppHttp http, ApiKeyAccountStore store)
{
    public static bool Supports(string providerId) => providerId is "ollama" or "opencode" or "openrouter" or "zai";

    public IProviderRuntime MakeRuntime(ApiKeyAccount account, IProviderRuntime provider) =>
        (provider, account.ProviderId) switch
        {
            (OllamaProvider ollama, "ollama") => new AccountRuntime(account, provider, store, ollama.RefreshWithApiKeyAsync),
            (OpenCodeProvider openCode, OpenCodeProvider.Id) =>
                new AccountRuntime(account, provider, store, openCode.RefreshWithApiKeyAsync),
            (OpenRouterProvider openRouter, "openrouter") =>
                new AccountRuntime(account, provider, store, (key, ct) => openRouter.RefreshWithKeyAsync(key, ct, requireKey: true)),
            (ZaiProvider zai, "zai") => new AccountRuntime(account, provider, store, zai.AccountRefresh()),
            _ => throw new ArgumentException("The account does not match the provider.")
        };

    public async Task<ApiKeyAccount> ConnectAsync(
        string providerId, string label, string key, CancellationToken ct = default)
    {
        label = ApiKeyAccountStore.NormalizeLabel(label);
        key = await ValidateKeyAsync(providerId, key, ct);
        ct.ThrowIfCancellationRequested();
        return store.Add(providerId, label, key);
    }

    public async Task ReplaceKeyAsync(ApiKeyAccount account, string key, CancellationToken ct = default)
    {
        key = await ValidateKeyAsync(account.ProviderId, key, ct);
        ct.ThrowIfCancellationRequested();
        store.ReplaceKey(account, key);
    }

    private async Task<string> ValidateKeyAsync(string providerId, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        key = key.Trim();
        if (key.Length == 0 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a valid API key.");
        var request = providerId switch
        {
            "ollama" => OllamaProvider.ApiKeyRequest("GET", OllamaProvider.BalancePath, key),
            "opencode" => OpenCodeProvider.UsageRequest(key),
            "openrouter" => BearerGet(OpenRouterProvider.KeyUrl, key),
            "zai" => BearerGet(ZaiProvider.QuotaUrl, key),
            _ => throw new ArgumentException("This provider does not support API key accounts.")
        };
        var response = await http.SendAsync(request, ct);
        ct.ThrowIfCancellationRequested();

        if (providerId == "opencode" && OpenCodeProvider.IsNoGoSubscription(response))
            throw new ProviderRequestException(OpenCodeErrors.NoGoSubscription);
        if (AuthRetry.IsAuthFailure(response))
            throw new ProviderRequestException("The provider rejected this API key.");
        if (!response.IsSuccess)
            throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
        if (response.Json is not { } body)
            throw new ProviderRequestException(ProviderErrors.InvalidResponse);

        if (providerId == "ollama")
        {
            try { _ = OllamaUsageMapper.BalanceLines(body); }
            catch (InvalidOperationException ex)
            {
                throw new ProviderRequestException(ProviderErrors.InvalidResponse, ex);
            }
        }
        else if (providerId == "opencode")
        {
            try { _ = OpenCodeUsageMapper.MeterLines(response, DateTimeOffset.Now); }
            catch (InvalidOperationException ex)
            {
                throw new ProviderRequestException(ProviderErrors.InvalidResponse, ex);
            }
        }
        else if (providerId == "zai")
        {
            if (ZaiUsageMapper.IsRejectedKey(body))
                throw new ProviderRequestException("Z.ai rejected this API key.");
            if (ZaiUsageMapper.IsNoCodingPlan(body))
                throw new ProviderRequestException("This API key has no active GLM Coding Plan.");
            if (JsonUtil.Bool(body["success"]) == false ||
                JsonUtil.Array((JsonUtil.Object(body["data"]) ?? body)["limits"]) is null)
                throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        }
        else if (JsonUtil.Object(body["data"]) is null)
        {
            throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        }

        return key;
    }

    private static AppHttpRequest BearerGet(Uri url, string key) =>
        new("GET", url, new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {key}",
            ["Accept"] = "application/json"
        });

    private sealed class AccountRuntime(
        ApiKeyAccount account, IProviderRuntime template, ApiKeyAccountStore accounts,
        Func<string, CancellationToken, Task<ProviderSnapshot>> refresh) : IProviderRuntime
    {
        public Provider Provider => template.Provider with
        {
            Id = account.CardId,
            DisplayName = $"{template.Provider.DisplayName} · {account.Label}"
        };

        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors => template.WidgetDescriptors
            .Select(d => d with
            {
                Id = account.CardId + d.Id[template.Provider.Id.Length..],
                ProviderId = account.CardId
            }).ToArray();

        public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(accounts.Key(account.Id) is not null);

        public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            var key = accounts.Key(account.Id);
            if (key is null) return ProviderSnapshot.Error(Provider, "This account is no longer connected.");
            var snapshot = await refresh(key, cancellationToken);
            return snapshot with { ProviderId = Provider.Id, DisplayName = Provider.DisplayName };
        }
    }
}
