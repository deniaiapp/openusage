using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public sealed partial class ClaudeProvider
{
    private const string CredentialKey = "claude.oauth";
    private const string UsageUrl = ClaudeLogin.BaseApi + "/api/oauth/usage";
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly ApiKeyStore? _keys;
    private volatile ClaudeOAuth? _connected;

    public bool IsSignedIn => _connected is not null;

    internal async Task<ProviderSnapshot> ConnectAsync(Action<Uri> openBrowser, Task<string> pastedCode, CancellationToken ct)
    {
        var keys = _keys ?? throw new InvalidOperationException("Claude sign-in storage is unavailable.");
        var oauth = await new ClaudeLogin(_http, _now).AuthorizeAsync(openBrowser, pastedCode, ct);
        await _authGate.WaitAsync(ct);
        var previous = (_lastGood, _rateLimit, _livePlan, _resetGrantsCheck);
        try
        {
            var snapshot = await ProbeAsync(oauth, ct, validateReplacement: true);
            if (snapshot.IsError) throw new ProviderRequestException("Couldn’t verify Claude usage. Try again later.");
            ct.ThrowIfCancellationRequested();
            keys.Set(CredentialKey, JsonSerializer.Serialize(oauth));
            _connected = oauth;
            return snapshot;
        }
        catch
        {
            (_lastGood, _rateLimit, _livePlan, _resetGrantsCheck) = previous;
            ct.ThrowIfCancellationRequested();
            throw;
        }
        finally { _authGate.Release(); }
    }

    private async Task<ClaudeOAuth> RenewAppCredentialAsync(ClaudeOAuth previous, CancellationToken ct)
    {
        var oauth = await new ClaudeLogin(_http, _now).RenewAsync(previous, ct);
        CarryUsageAcrossRenewal(CredentialFingerprint(previous), oauth);
        _connected = oauth;
        try { _keys!.Set(CredentialKey, JsonSerializer.Serialize(oauth)); }
        catch (Exception ex) { AppLog.Warn("claude", $"could not save renewed sign-in ({ex.GetType().Name})"); }
        return oauth;
    }

    private void CarryUsageAcrossRenewal(string previousFingerprint, ClaudeOAuth oauth)
    {
        var fingerprint = CredentialFingerprint(oauth);
        if (_resetGrantsCheck is { } check && check.Fingerprint == previousFingerprint)
            _resetGrantsCheck = check with { Fingerprint = fingerprint };
        if (_lastGood is { } good && good.Fingerprint == previousFingerprint)
            _lastGood = good with { Fingerprint = fingerprint };
    }

    private static ClaudeOAuth? LoadAppCredential(ApiKeyStore? keys)
    {
        if (keys?.Get(CredentialKey) is not { } serialized) return null;
        try
        {
            var oauth = JsonSerializer.Deserialize<ClaudeOAuth>(serialized);
            if (ClaudeLogin.IsToken(oauth?.AccessToken) && ClaudeLogin.IsToken(oauth?.RefreshToken)) return oauth;
        }
        catch (JsonException) { }
        AppLog.Warn("claude", "saved sign-in could not be read");
        return null;
    }
}
