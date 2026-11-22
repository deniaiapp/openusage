using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed partial class CodexProvider : IProviderRuntime
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly Uri RefreshUrl = new("https://auth.openai.com/oauth/token");
    public static readonly Uri UsageUrl = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri ResetCreditsUrl = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");

    private readonly CodexAuthStore _auth = new();
    private readonly AppHttp _http;
    private readonly CodexTokenHistory _history = new();

    public CodexProvider(AppHttp http, ApiKeyStore? keys = null)
    {
        _http = http;
        _keys = keys;
    }

    public Provider Provider { get; } = new(
        "codex", "Codex", "codex",
        [
            new ProviderLink("Status", "https://status.openai.com/"),
            new ProviderLink("Dashboard", "https://chatgpt.com/settings/usage")
        ]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("codex.session", Provider, "Session"),
        WidgetDescriptor.Percent("codex.weekly", Provider, "Weekly"),
        WidgetDescriptor.Percent("codex.spark", Provider, "Spark"),
        WidgetDescriptor.Percent("codex.sparkWeekly", Provider, "Spark Weekly"),
        WidgetDescriptor.Values("codex.credits", Provider, "Credits") with { HideWhenMissing = true },
        WidgetDescriptor.BoundedCount("codex.creditLimit", Provider, "Credit Limit", 0, "credits")
            with { HideWhenMissing = true },
        WidgetDescriptor.Values("codex.rateLimitResets", Provider, "Rate Limit Resets",
            valueWord: "available", traySuffix: "resets", showsResetExpiries: true),
        TokenHistoryDescriptor("codex.tokensToday", "Today"),
        TokenHistoryDescriptor("codex.tokensYesterday", "Yesterday"),
        TokenHistoryDescriptor("codex.tokensLast30", "Last 30 Days")
    ];

    private WidgetDescriptor TokenHistoryDescriptor(string id, string title)
    {
        var descriptor = WidgetDescriptor.Values(id, Provider, title, isUsagePeriod: true, traySuffix: "tokens");
        return descriptor with
        {
            HideWhenMissing = true,
            Sample = descriptor.Sample with { Kind = MetricKind.Count, CountSuffix = "tokens" }
        };
    }

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HasAppCredential || _auth.HasUsableCredentials());

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (HasAppCredential)
            return await RefreshAppCredentialAsync(cancellationToken);

        var candidates = _auth.LoadCandidates().Where(c => !string.IsNullOrEmpty(c.AccessToken)).ToList();
        if (candidates.Count == 0)
            return ProviderSnapshot.Error(Provider, "Not logged in. Open Customize → Codex to sign in.");

        Exception? last = null;
        foreach (var auth in candidates)
        {
            try { return await ProbeAsync(auth, cancellationToken); }
            catch (Exception ex)
            {
                last = ex;
                AppLog.Error("codex", "candidate failed", ex);
            }
        }
        return ProviderSnapshot.Error(Provider, last ?? new InvalidOperationException("Codex refresh failed."));
    }

    internal async Task<ProviderSnapshot> ProbeAsync(CodexAuth auth, CancellationToken ct)
    {
        AdoptLiveAuth(auth);
        if (NeedsRefresh(auth) && !string.IsNullOrEmpty(auth.RefreshToken))
        {
            try { await RefreshTokenAsync(auth, ct); }
            catch (Exception ex)
            {
                // The CLI may have already rotated this refresh token. Reload and try the live
                // access token rather than failing the whole card (#516).
                AppLog.Warn("codex", $"proactive refresh failed: {ex.Message}");
                AdoptLiveAuth(auth);
            }
        }

        var usage = await AuthRetry.FetchAsync(
            auth.AccessToken!,
            token =>
            {
                auth.AccessToken = token;
                return SendAuthGet(UsageUrl, auth, ct);
            },
            async () =>
            {
                AdoptLiveAuth(auth);
                try { await RefreshTokenAsync(auth, ct); }
                catch (Exception ex)
                {
                    AppLog.Warn("codex", $"token refresh failed: {ex.Message}");
                    AdoptLiveAuth(auth);
                    if (string.IsNullOrEmpty(auth.AccessToken)) throw;
                }
                return auth.AccessToken!;
            },
            new InvalidOperationException("Token expired. Run `codex` to log in again."));

        return await MapUsageAsync(usage, auth, ct);
    }

    private async Task<ProviderSnapshot> MapUsageAsync(AppHttpResponse usage, CodexAuth auth, CancellationToken ct)
    {
        AppHttpResponse? resets = null;
        try { resets = await SendAuthGet(ResetCreditsUrl, auth, ct, extra: true); }
        catch (Exception ex) { AppLog.Warn("codex", $"reset credits fetch failed: {ex.Message}"); }

        var mapped = CodexUsageMapper.Map(usage, resets, DateTimeOffset.Now);
        var lines = mapped.Lines.ToList();
        var history = await _history.RefreshAsync(auth.AccessToken!, auth.AccountId, DateTimeOffset.UtcNow,
            (url, token) => SendAuthGet(url, auth, token, timeoutSeconds: 5), ct);
        AddTokenCount("Today", history.Today);
        AddTokenCount("Yesterday", history.Yesterday);
        AddTokenCount("Last 30 Days", history.Last30);
        ct.ThrowIfCancellationRequested();
        MetricLine.AppendNoDataIfNeeded(lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, lines, DateTimeOffset.Now);

        void AddTokenCount(string label, double? value)
        {
            if (value is { } count)
                lines.Add(MetricLine.Values(label, [new MetricValue(count, MetricKind.Count, "tokens")]));
        }
    }

    /// <summary>
    /// Adopts the auth file's tokens only when another tool changed them since this refresh last saw
    /// the file. A rotated token whose save failed keeps serving the retry instead of being replaced
    /// by the refresh token it already consumed (#1322).
    /// </summary>
    private static void AdoptLiveAuth(CodexAuth auth)
    {
        if (!auth.IsFile) return;
        var live = CodexAuthStore.ParseFile(auth.Path);
        if (live is null || string.IsNullOrEmpty(live.AccessToken)) return;
        if ((live.AccessToken, live.RefreshToken) == auth.Baseline) return;
        auth.AccessToken = live.AccessToken;
        auth.RefreshToken = live.RefreshToken ?? auth.RefreshToken;
        auth.IdToken = live.IdToken ?? auth.IdToken;
        auth.AccountId = live.AccountId ?? auth.AccountId;
        auth.Baseline = live.Baseline;
    }

    private static bool NeedsRefresh(CodexAuth auth)
    {
        if (Jwt.Expiration(auth.AccessToken) is { } exp)
            return exp - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);
        return false;
    }

    private async Task RefreshTokenAsync(CodexAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.RefreshToken))
            throw new InvalidOperationException("Token expired. Run `codex` to log in again.");
        // OpenUsage can't write Credential Manager items back; rotating their refresh token would
        // sign the Codex CLI out, so let the CLI refresh its own login.
        if (!auth.IsFile)
            throw new InvalidOperationException("Token expired. Run `codex` to refresh your login.");
        var body = $"grant_type=refresh_token&client_id={WebUtility.UrlEncode(ClientId)}&refresh_token={WebUtility.UrlEncode(auth.RefreshToken)}";
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST", RefreshUrl,
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            Encoding.UTF8.GetBytes(body), 15), ct);
        if (response.StatusCode is 400 or 401)
        {
            var code = JsonUtil.String(JsonUtil.Object(response.Json?["error"])?["code"])
                ?? JsonUtil.String(response.Json?["error"])
                ?? JsonUtil.String(response.Json?["code"]);
            throw new InvalidOperationException(code switch
            {
                "refresh_token_reused" => "Token conflict. Run `codex` to log in again.",
                "refresh_token_expired" => "Session expired. Run `codex` to log in again.",
                "refresh_token_invalidated" => "Token revoked. Run `codex` to log in again.",
                _ => "Session expired. Run `codex` to log in again."
            });
        }
        if (!response.IsSuccess)
            throw new InvalidOperationException(ProviderErrors.RequestFailed(response.StatusCode));
        var json = response.Json ?? throw new InvalidOperationException("Token expired. Run `codex` to log in again.");
        var access = JsonUtil.String(json["access_token"]);
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Token expired. Run `codex` to log in again.");
        auth.AccessToken = access;
        auth.RefreshToken = JsonUtil.String(json["refresh_token"]) ?? auth.RefreshToken;
        auth.IdToken = JsonUtil.String(json["id_token"]) ?? auth.IdToken;
        // An unsaved rotation still serves this refresh from memory.
        try { _auth.Save(auth); }
        catch (Exception ex) { AppLog.Error("codex", "could not persist rotated credentials", ex); }
    }

    private Task<AppHttpResponse> SendAuthGet(
        Uri url, CodexAuth auth, CancellationToken ct, bool extra = false, int timeoutSeconds = 10)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {auth.AccessToken}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "OpenUsage"
        };
        if (!string.IsNullOrEmpty(auth.AccountId))
            headers["ChatGPT-Account-Id"] = auth.AccountId;
        if (extra)
        {
            headers["OpenAI-Beta"] = "codex-1";
            headers["originator"] = "Codex Desktop";
        }
        return _http.SendAsync(new AppHttpRequest("GET", url, headers, TimeoutSeconds: timeoutSeconds), ct);
    }
}
