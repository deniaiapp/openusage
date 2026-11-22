using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed partial class CodexProvider
{
    public const string VerificationUrl = CodexDeviceLogin.VerificationUrl;
    private const string CredentialKey = "codex.oauth";
    private readonly ApiKeyStore? _keys;
    private readonly SemaphoreSlim _credentialGate = new(1);

    public bool HasAppCredential => _keys?.Get(CredentialKey) is not null;

    public Task<ProviderSnapshot> ConnectAsync(Action<string> showCode, CancellationToken ct) =>
        ConnectAsync(new CodexDeviceLogin(_http), showCode, ct);

    internal async Task<ProviderSnapshot> ConnectAsync(CodexDeviceLogin login, Action<string> showCode, CancellationToken ct)
    {
        var auth = await login.AuthorizeAsync(showCode, ct);
        await _credentialGate.WaitAsync(ct);
        try
        {
            var snapshot = await FetchUsageAsync(auth, ct);
            if (snapshot.Plan is null && snapshot.Lines.All(line => line is MetricLine.BadgeLine))
                throw new ProviderRequestException("ChatGPT did not return usage for this account.");
            ct.ThrowIfCancellationRequested();
            SaveAppCredential(auth);
            return snapshot;
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        finally { _credentialGate.Release(); }
    }

    private async Task<ProviderSnapshot> RefreshAppCredentialAsync(CancellationToken ct)
    {
        await _credentialGate.WaitAsync(ct);
        try
        {
            var saved = JsonUtil.ParseObject(_keys!.Get(CredentialKey));
            var auth = CodexDeviceLogin.ParseTokens(saved, null);
            auth.AccountId ??= JsonUtil.String(saved?["account_id"]);
            var renewed = NeedsRefresh(auth);
            if (renewed)
                auth = await RenewAppCredentialAsync(auth, ct);
            var usage = await SendAuthGet(UsageUrl, auth, ct);
            if (!renewed && AuthRetry.IsAuthFailure(usage))
            {
                auth = await RenewAppCredentialAsync(auth, ct);
                usage = await SendAuthGet(UsageUrl, auth, ct);
            }
            return await ReadAppUsageAsync(usage, auth, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Error("codex", "app sign-in refresh failed", ex);
            return ProviderSnapshot.Error(Provider, ex);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        finally { _credentialGate.Release(); }
    }

    private Task<ProviderSnapshot> ReadAppUsageAsync(AppHttpResponse usage, CodexAuth auth, CancellationToken ct)
    {
        AuthRetry.RequireSuccess(usage, ExpiredSignIn(), status => new ProviderRequestException(ProviderErrors.RequestFailed(status)));
        return MapUsageAsync(usage, auth, ct);
    }

    private async Task<CodexAuth> RenewAppCredentialAsync(CodexAuth auth, CancellationToken ct)
    {
        var response = await CodexDeviceLogin.PostTokenAsync(_http, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = auth.RefreshToken!
        }, ct);
        if (response.StatusCode is 400 or 401 or 403)
            throw ExpiredSignIn();
        if (!response.IsSuccess)
            throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
        var renewed = CodexDeviceLogin.ParseTokens(response.Json, auth);
        SaveAppCredential(renewed);
        return renewed;
    }

    private void SaveAppCredential(CodexAuth auth)
    {
        if (_keys is null) throw new InvalidOperationException("ChatGPT sign-in storage is unavailable.");
        _keys.Set(CredentialKey, new JsonObject
        {
            ["access_token"] = auth.AccessToken,
            ["refresh_token"] = auth.RefreshToken,
            ["account_id"] = auth.AccountId
        }.ToJsonString());
    }

    private static ProviderRequestException ExpiredSignIn() =>
        new("ChatGPT sign-in expired. Open Customize → Codex and sign in again.");
}
