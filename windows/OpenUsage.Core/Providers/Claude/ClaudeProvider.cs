using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public sealed class ClaudeProvider : IProviderRuntime
{
    /// Matches Claude Code's own `claude-cli/<version> (external, cli)` format. Anthropic decides
    /// reset-grant eligibility by client surface; anything else reads `ineligible_reason: "surface"`.
    public const string UserAgent = "claude-cli/2.1.294 (external, cli)";

    /// Anthropic throttles the reset-grants request when it's polled, so it runs at most once an hour
    /// per login and the last answer is held in between.
    public static readonly TimeSpan ResetGrantsCheckInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    private readonly ClaudeAuthStore _auth;
    private readonly AppHttp _http;
    private readonly Func<DateTimeOffset> _now;
    private (string Fingerprint, string? Plan, List<MetricLine> Lines)? _lastGood;
    private (string Fingerprint, DateTimeOffset Until)? _rateLimit;
    private (string AccessFingerprint, string? Plan)? _livePlan;
    private (string Fingerprint, DateTimeOffset CheckedAt, MetricLine? Line)? _resetGrantsCheck;

    /// Rotated logins that could not be written back (they came from Credential Manager), keyed by the
    /// refresh token they replaced, so the next refresh doesn't reuse the spent one.
    private readonly Dictionary<string, ClaudeOAuth> _rotatedInMemory = new(StringComparer.Ordinal);

    public ClaudeProvider(
        AppHttp http,
        ClaudeAuthStore? auth = null,
        Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _auth = auth ?? new ClaudeAuthStore();
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public Provider Provider { get; } = new(
        "claude",
        "Claude",
        "claude",
        [
            new ProviderLink("Status", "https://status.anthropic.com/"),
            new ProviderLink("Dashboard", "https://claude.ai/settings/usage")
        ]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("claude.session", Provider, "Session", sessionStartSignal: SessionStartSignal.MissingResetDate),
        WidgetDescriptor.Percent("claude.weekly", Provider, "Weekly"),
        WidgetDescriptor.Percent("claude.fable", Provider, "Fable"),
        WidgetDescriptor.Percent("claude.sonnet", Provider, "Sonnet"),
        WidgetDescriptor.BoundedDollars("claude.extra", Provider, "Extra Usage", 100, "Extra usage spent", valueWord: "spent"),
        WidgetDescriptor.Values("claude.rateLimitResets", Provider, ClaudeUsageMapper.RateLimitResetsLabel,
            valueWord: "available", traySuffix: "resets", showsResetExpiries: true)
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.HasUsableCredentials());

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var candidates = _auth.LoadCandidates()
            .Select(c => c.RefreshToken is { } r && _rotatedInMemory.TryGetValue(r, out var rotated) ? rotated : c)
            .ToList();
        if (candidates.Count == 0)
            return ProviderSnapshot.Error(Provider, "Not logged in. Run `claude` to authenticate.");

        Exception? last = null;
        foreach (var oauth in candidates)
        {
            try
            {
                return await ProbeAsync(oauth, cancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
                AppLog.Warn("claude", $"candidate failed: {ex.Message}");
            }
        }
        return ProviderSnapshot.Error(Provider, last ?? new InvalidOperationException("Claude refresh failed."));
    }

    private async Task<ProviderSnapshot> ProbeAsync(ClaudeOAuth oauth, CancellationToken ct)
    {
        var fingerprint = CredentialFingerprint(oauth);
        if (_rateLimit is { } limit && limit.Fingerprint == fingerprint && _now() < limit.Until)
            return HeldUsage(fingerprint);

        var config = _auth.OAuthConfig();
        // Only file logins are refreshed: OpenUsage can't write Credential Manager items back, and
        // rotating their refresh token would sign the Claude CLI out.
        if (oauth.Source == ClaudeCredentialSource.File && _auth.NeedsRefresh(oauth) && !string.IsNullOrEmpty(oauth.RefreshToken))
        {
            await RefreshTokenAsync(oauth, config, ct);
            fingerprint = CredentialFingerprint(oauth);
        }

        var response = await _http.SendAsync(UsageRequest(new Uri(config.UsageUrl), oauth), ct);

        if (response.StatusCode == 429)
        {
            var now = _now();
            var delay = response.RetryAfter(now) ?? RateLimitCooldown;
            _rateLimit = (fingerprint, now.Add(delay > RateLimitCooldown ? delay : RateLimitCooldown));
            return HeldUsage(fingerprint);
        }

        if (response.StatusCode is 401 or 403)
            throw new InvalidOperationException("Session expired. Run `claude` to log in again.");
        if (!response.IsSuccess)
            throw new InvalidOperationException(ProviderErrors.RequestFailed(response.StatusCode));
        if (response.Json is not { } body)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);

        var mapped = ClaudeUsageMapper.Map(body, oauth);
        var lines = mapped.Lines.ToList();
        // Both run only after the usage call succeeded: the token is known-good, so a failure here costs
        // the resets row or the live plan label, never the bars.
        if (await ResetGrantsLineAsync(oauth, config.UsageUrl, ct) is { } resets) lines.Add(resets);
        var plan = await ResolveLivePlanAsync(oauth, config.UsageUrl, ct) ?? mapped.Plan;
        _lastGood = (fingerprint, plan, lines);
        _rateLimit = null;
        return MakeSnapshot(plan, lines.ToList(), mapped.Warning);
    }

    private ProviderSnapshot HeldUsage(string fingerprint) =>
        _lastGood is { } held && held.Fingerprint == fingerprint
            ? RateLimitedSnapshot(held.Plan, held.Lines)
            : ProviderSnapshot.Error(Provider, "Rate limited, try again later.");

    private ProviderSnapshot RateLimitedSnapshot(string? plan, List<MetricLine> lines)
    {
        var now = _now();
        var held = lines.Select(l => ClaudeUsageMapper.DroppingElapsedResetGrants(l, now)).ToList();
        held.Add(ClaudeUsageMapper.RateLimitedNote);
        return MakeSnapshot(plan, held, ClaudeUsageMapper.RateLimitedWarning);
    }

    private ProviderSnapshot MakeSnapshot(string? plan, List<MetricLine> lines, string? warning)
    {
        MetricLine.AppendNoDataIfNeeded(lines);
        return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now, warning);
    }

    /// The Rate Limit Resets row, asking Anthropic at most once an hour per login. A failed attempt keeps
    /// the previous line and still counts as the hour's check, so an outage or 429 is retried an hour
    /// later rather than on every refresh.
    private async Task<MetricLine?> ResetGrantsLineAsync(ClaudeOAuth oauth, string usageUrl, CancellationToken ct)
    {
        var fingerprint = CredentialFingerprint(oauth);
        var now = _now();
        var previous = _resetGrantsCheck is { } check && check.Fingerprint == fingerprint ? check : default;
        var held = previous.Line is { } line ? ClaudeUsageMapper.DroppingElapsedResetGrants(line, now) : null;
        if (previous.Fingerprint is not null && now - previous.CheckedAt < ResetGrantsCheckInterval) return held;
        try
        {
            var response = await _http.SendAsync(UsageRequest(ResetGrantsUrl(usageUrl), oauth), ct);
            var fresh = ClaudeUsageMapper.MapResetGrantsResponse(response, now);
            _resetGrantsCheck = (fingerprint, now, fresh);
            return fresh;
        }
        catch (Exception ex)
        {
            // A cancelled refresh is not a verdict on the endpoint; let the next refresh try again.
            if (ct.IsCancellationRequested) return held;
            AppLog.Warn("claude", $"reset grants lookup failed; retrying in an hour: {ex.Message}");
            _resetGrantsCheck = (fingerprint, now, previous.Line);
            return held;
        }
    }

    /// The plan Anthropic's profile reports for this access token, looked up at most once per token
    /// (a failed lookup included, which keeps the stored plan until the token rotates).
    private async Task<string?> ResolveLivePlanAsync(ClaudeOAuth oauth, string usageUrl, CancellationToken ct)
    {
        var fingerprint = Hash(oauth.AccessToken);
        if (_livePlan is { } cached && cached.AccessFingerprint == fingerprint) return cached.Plan;
        try
        {
            var response = await _http.SendAsync(new AppHttpRequest(
                "GET",
                ProfileUrl(usageUrl),
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {oauth.AccessToken!.Trim()}",
                    ["Accept"] = "application/json",
                    ["anthropic-beta"] = "oauth-2025-04-20"
                },
                TimeoutSeconds: 10), ct);
            if (!response.IsSuccess) throw new InvalidOperationException(ProviderErrors.RequestFailed(response.StatusCode));
            if (response.Json is not { } profile) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
            var plan = ClaudeUsageMapper.FormatLivePlan(profile, oauth);
            if (plan is null) AppLog.Info("claude", "live profile carries no organization plan; showing the stored plan");
            _livePlan = (fingerprint, plan);
            return plan;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return null;
            AppLog.Warn("claude", $"live plan lookup failed; showing the stored plan until the token rotates: {ex.Message}");
            _livePlan = (fingerprint, null);
            return null;
        }
    }

    private static AppHttpRequest UsageRequest(Uri url, ClaudeOAuth oauth) => new(
        "GET",
        url,
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {oauth.AccessToken!.Trim()}",
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json",
            ["anthropic-beta"] = "oauth-2025-04-20",
            ["User-Agent"] = UserAgent
        },
        TimeoutSeconds: 10);

    /// `/api/oauth/usage?cedar_ember=1&skip_spend=1`, the exact request Claude Code makes when it checks
    /// for a reset to offer. The plain usage read returns `cedar_ember` as null.
    public static Uri ResetGrantsUrl(string usageUrl)
    {
        var separator = usageUrl.Contains('?') ? '&' : '?';
        return new Uri($"{usageUrl}{separator}cedar_ember=1&skip_spend=1");
    }

    public static Uri ProfileUrl(string usageUrl)
    {
        var path = usageUrl.Split('?')[0];
        return new Uri($"{path[..(path.LastIndexOf('/') + 1)]}profile");
    }

    private static string CredentialFingerprint(ClaudeOAuth oauth) => $"{Hash(oauth.AccessToken)}:{Hash(oauth.RefreshToken)}";

    private static string Hash(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));

    private async Task RefreshTokenAsync(ClaudeOAuth oauth, (string UsageUrl, string RefreshUrl, string ClientId) config, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = oauth.RefreshToken,
            client_id = config.ClientId,
            scope = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload"
        });
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST",
            new Uri(config.RefreshUrl),
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes(payload),
            15), ct);
        if (!response.IsSuccess || response.Json is not { } body) return;
        var access = JsonUtil.String(body["access_token"]);
        if (string.IsNullOrEmpty(access)) return;

        var previousFingerprint = CredentialFingerprint(oauth);
        var previousRefresh = oauth.RefreshToken;
        oauth.AccessToken = access;
        oauth.RefreshToken = JsonUtil.String(body["refresh_token"]) ?? oauth.RefreshToken;
        if (JsonUtil.Number(body["expires_in"]) is { } expiresIn)
            oauth.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;

        // A token refresh is the same login: carry the held reset-grants answer and last-good usage over.
        var rotatedFingerprint = CredentialFingerprint(oauth);
        if (_resetGrantsCheck is { } check && check.Fingerprint == previousFingerprint)
            _resetGrantsCheck = check with { Fingerprint = rotatedFingerprint };
        if (_lastGood is { } good && good.Fingerprint == previousFingerprint)
            _lastGood = good with { Fingerprint = rotatedFingerprint };

        try
        {
            if (_auth.SaveRotated(oauth, previousRefresh))
            {
                if (previousRefresh is not null) _rotatedInMemory.Remove(previousRefresh);
            }
            else if (oauth.Source != ClaudeCredentialSource.File && previousRefresh is not null)
            {
                _rotatedInMemory[previousRefresh] = oauth;
            }
            else
            {
                AppLog.Info("claude", "rotated token not saved: the credential file changed during refresh");
            }
        }
        catch (Exception ex) { AppLog.Warn("claude", $"could not persist rotated credentials: {ex.Message}"); }
    }
}
