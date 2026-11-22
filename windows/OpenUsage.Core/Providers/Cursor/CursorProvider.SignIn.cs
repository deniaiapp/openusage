using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed partial class CursorProvider
{
    internal const string BrowserCredentialSource = "openusage";
    private const string BrowserCredentialKey = "cursor.browser-sign-in";
    private static readonly TimeSpan BrowserRenewalLead = TimeSpan.FromDays(7);
    private readonly object _browserCredentialGate = new();
    private readonly ApiKeyStore? _keys;

    public bool HasAppCredential => _keys?.Get(BrowserCredentialKey) is not null;

    internal async Task<ProviderSnapshot> ConnectAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        var state = await new CursorBrowserLogin(_http).AuthorizeAsync(openBrowser, ct);
        return await ConnectAccountAsync(state, ct);
    }

    internal async Task<ProviderSnapshot> ConnectAccountAsync(CursorAuthState state, CancellationToken ct)
    {
        var keys = _keys ?? throw new ProviderRequestException("OpenUsage couldn't open its credentials store.");
        ct.ThrowIfCancellationRequested();
        var snapshot = await ProbeAsync(state, ct, includeHistory: false);
        lock (_browserCredentialGate)
        {
            ct.ThrowIfCancellationRequested();
            keys.Set(BrowserCredentialKey, SerializeCredential(state));
        }
        return snapshot;
    }

    private Task<ProviderSnapshot> RefreshBrowserAccountAsync(string saved, CancellationToken ct)
    {
        var json = JsonUtil.ParseObject(saved);
        if (CursorBrowserLogin.Token(json?["AccessToken"]) is not { } access ||
            CursorBrowserLogin.Token(json?["RefreshToken"]) is not { } refresh)
            throw new ProviderRequestException("OpenUsage couldn't read your Cursor sign-in. Sign in again in Customize.");
        var state = new CursorAuthState { AccessToken = access, RefreshToken = refresh, Source = BrowserCredentialSource };
        return ProbeAsync(state, ct, renewed =>
        {
            lock (_browserCredentialGate)
            {
                ct.ThrowIfCancellationRequested();
                if (_keys!.Get(BrowserCredentialKey) != saved) return;
                var next = SerializeCredential(renewed);
                _keys.Set(BrowserCredentialKey, next);
                saved = next;
            }
        });
    }

    private static string SerializeCredential(CursorAuthState state) =>
        JsonSerializer.Serialize(new { state.AccessToken, state.RefreshToken });

    private static ProviderRequestException SignInExpired(CursorAuthState state) =>
        new(state.Source == BrowserCredentialSource
            ? "Cursor sign-in expired. Sign in again in Customize."
            : "Cursor sign-in expired. Sign in in Customize or in the Cursor app.");
}
