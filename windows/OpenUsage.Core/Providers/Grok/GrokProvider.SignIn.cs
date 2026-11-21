using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Grok;

public sealed partial class GrokProvider
{
    private const string CredentialKey = "grok.oauth";
    internal const string SignInAgain = "Grok sign-in expired. Sign in again in Customize → Grok.";
    private readonly ApiKeyStore? _keys;
    private readonly SemaphoreSlim _credentialGate = new(1);

    public bool HasAppCredential => _keys?.Get(CredentialKey) is not null;

    internal Task<ProviderSnapshot> ConnectAsync(Action<string, Uri> showCode, CancellationToken ct) =>
        ConnectAsync(new GrokDeviceLogin(_http), showCode, ct);

    internal async Task<ProviderSnapshot> ConnectAsync(GrokDeviceLogin login, Action<string, Uri> showCode, CancellationToken ct)
    {
        var keys = _keys ?? throw new InvalidOperationException("OpenUsage could not open its credentials store.");
        var auth = await login.AuthorizeAsync(showCode, ct);
        ProviderSnapshot snapshot;
        try
        {
            var credits = await ReadCreditsAsync(auth.AccessToken, ct);
            snapshot = await MapUsageAsync(credits, auth.AccessToken, "Grok could not verify this sign-in. Try again.", ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        await _credentialGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            keys.Set(CredentialKey, JsonSerializer.Serialize(auth));
        }
        finally { _credentialGate.Release(); }
        return snapshot;
    }

    private async Task<ProviderSnapshot> RefreshConnectedAsync(CancellationToken ct)
    {
        await _credentialGate.WaitAsync(ct);
        try
        {
            var auth = ReadAppCredential();
            var login = new GrokDeviceLogin(_http);
            var renewed = false;
            ProviderRequestException? earlyRenewalError = null;
            var now = DateTimeOffset.UtcNow;
            if (auth.ExpiresAt is { } expiry && expiry <= now.AddMinutes(5))
            {
                try
                {
                    auth = await RenewAsync(login, auth, ct);
                    renewed = true;
                }
                catch (ProviderRequestException ex) when (!ct.IsCancellationRequested && expiry > DateTimeOffset.UtcNow)
                {
                    earlyRenewalError = ex;
                    AppLog.Warn("grok", $"early token renewal failed; keeping the current token: {ex.Message}");
                }
            }

            var credits = await ReadCreditsAsync(auth.AccessToken, ct);
            if (!renewed && AuthRetry.IsAuthFailure(credits))
            {
                if (earlyRenewalError is not null) throw earlyRenewalError;
                auth = await RenewAsync(login, auth, ct);
                credits = await ReadCreditsAsync(auth.AccessToken, ct);
            }
            return await MapUsageAsync(credits, auth.AccessToken, SignInAgain, ct);
        }
        finally { _credentialGate.Release(); }
    }

    private GrokOAuth ReadAppCredential()
    {
        try
        {
            var raw = _keys?.Get(CredentialKey);
            var auth = raw is null ? null : JsonSerializer.Deserialize<GrokOAuth>(raw);
            if (auth is not null && GrokOAuth.IsToken(auth.AccessToken) && GrokOAuth.IsToken(auth.RefreshToken))
                return auth;
        }
        catch (JsonException) { }
        throw new ProviderRequestException(SignInAgain);
    }

    private async Task<GrokOAuth> RenewAsync(GrokDeviceLogin login, GrokOAuth auth, CancellationToken ct)
    {
        var replacement = await login.RenewAsync(auth, ct);
        _keys!.Set(CredentialKey, JsonSerializer.Serialize(replacement));
        return replacement;
    }
}
