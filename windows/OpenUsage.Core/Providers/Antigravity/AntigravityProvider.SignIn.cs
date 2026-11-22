using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

public sealed partial class AntigravityProvider
{
    private const string CredentialKey = "antigravity.oauth";
    private readonly ApiKeyStore? _keys;
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private volatile AntigravitySignIn? _connected;

    public bool IsSignedIn => _connected is not null;

    internal async Task<ProviderSnapshot> ConnectAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        var tokens = await new AntigravityLogin(_http).AuthorizeAsync(openBrowser, ct);
        return await ValidateAndSaveAsync(tokens, ct);
    }

    internal async Task<ProviderSnapshot> ValidateAndSaveAsync(AntigravitySignIn tokens, CancellationToken ct)
    {
        var keys = _keys ?? throw new InvalidOperationException("Antigravity sign-in storage is unavailable.");
        await _signInGate.WaitAsync(ct);
        try
        {
            var snapshot = await ReadConnectedUsageAsync(tokens, ct);
            ct.ThrowIfCancellationRequested();
            keys.Set(CredentialKey, JsonSerializer.Serialize(tokens));
            _connected = tokens;
            return snapshot;
        }
        finally { _signInGate.Release(); }
    }

    private async Task<ProviderSnapshot> RefreshConnectedAsync(CancellationToken ct)
    {
        await _signInGate.WaitAsync(ct);
        try
        {
            var tokens = _connected!;
            if (tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                tokens = await new AntigravityLogin(_http).RenewAsync(tokens.Refresh, ct);
                _connected = tokens;
                try { _keys!.Set(CredentialKey, JsonSerializer.Serialize(tokens)); }
                catch (Exception ex) { AppLog.Warn("antigravity", $"Couldn't save renewed sign-in ({ex.GetType().Name})."); }
            }
            return await ReadConnectedUsageAsync(tokens, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
        finally { _signInGate.Release(); }
    }

    private async Task<ProviderSnapshot> ReadConnectedUsageAsync(AntigravitySignIn tokens, CancellationToken ct)
    {
        try
        {
            var (lines, plan) = await _usage.QuotaAsync(tokens.Access, ct);
            if (lines.Count == 0)
                throw new ProviderRequestException("Couldn't read Antigravity usage for this Google account. Try again.");
            return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now);
        }
        catch (AntigravityUsageClient.TokenRefused) { throw ExpiredSignIn(); }
    }

    private static AntigravitySignIn? LoadConnected(ApiKeyStore? keys)
    {
        if (keys?.Get(CredentialKey) is not { } json) return null;
        try
        {
            var tokens = JsonSerializer.Deserialize<AntigravitySignIn>(json);
            if (tokens is not null && AntigravityLogin.IsToken(tokens.Access) && AntigravityLogin.IsToken(tokens.Refresh))
                return tokens;
        }
        catch (JsonException) { }
        AppLog.Warn("antigravity", "Couldn't read the saved sign-in.");
        return null;
    }
}
