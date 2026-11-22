using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public sealed partial class ClaudeProvider : IProviderRuntime
{
    /// Matches Claude Code's own `claude-cli/<version> (external, cli)` format. Anthropic decides
    /// reset-grant eligibility by client surface; anything else reads `ineligible_reason: "surface"`.
    public const string UserAgent = "claude-cli/2.1.294 (external, cli)";

    /// Anthropic throttles the reset-grants request when it's polled, so it runs at most once an hour
    /// per login and the last answer is held in between.
    public static readonly TimeSpan ResetGrantsCheckInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    private readonly AppHttp _http;
    private readonly Func<DateTimeOffset> _now;
    private (string Fingerprint, string? Plan, List<MetricLine> Lines)? _lastGood;
    private (string Fingerprint, DateTimeOffset Until)? _rateLimit;
    private (string AccessFingerprint, string? Plan)? _livePlan;
    private (string Fingerprint, DateTimeOffset CheckedAt, MetricLine? Line)? _resetGrantsCheck;

    public ClaudeProvider(
        AppHttp http,
        Func<DateTimeOffset>? now = null,
        ApiKeyStore? keys = null) : this(http, LoadAppCredential(keys), now, keys) { }

    internal ClaudeProvider(
        AppHttp http,
        ClaudeOAuth? credential,
        Func<DateTimeOffset>? now = null,
        ApiKeyStore? keys = null)
    {
        _http = http;
        _now = now ?? (() => DateTimeOffset.Now);
        _keys = keys;
        _connected = credential;
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
        WidgetDescriptor.BoundedDollars("claude.extra", Provider, "Extra Usage", 100, "Extra usage spent", valueWord: "spent") with
        {
            PlanTitle = ClaudeUsageMapper.ExtraUsageTitle
        },
        WidgetDescriptor.Values("claude.rateLimitResets", Provider, ClaudeUsageMapper.RateLimitResetsLabel,
            valueWord: "available", traySuffix: "resets", showsResetExpiries: true)
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsSignedIn);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (_connected is not { } connected)
                return ProviderSnapshot.Error(Provider, "Sign in to Claude in Customize → Claude.");
            try { return await ProbeAsync(connected, cancellationToken); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn("claude", $"app sign-in refresh failed ({ex.GetType().Name})");
                return ProviderSnapshot.Error(Provider, ex);
            }
        }
        finally { _authGate.Release(); }
    }

    internal Task<ProviderSnapshot> RefreshWithHubTokenAsync(string token, string expiredMessage, CancellationToken ct) =>
        ProbeAsync(new ClaudeOAuth { AccessToken = token }, ct, allowRenewal: false, cacheLivePlan: false,
            expiredMessage: expiredMessage);

    private async Task<ProviderSnapshot> ProbeAsync(ClaudeOAuth oauth, CancellationToken ct,
        bool validateReplacement = false, bool allowRenewal = true, bool cacheLivePlan = true, string? expiredMessage = null)
    {
        var fingerprint = CredentialFingerprint(oauth);
        if (!validateReplacement && _rateLimit is { } limit && limit.Fingerprint == fingerprint && _now() < limit.Until)
            return HeldUsage(fingerprint);

        if (allowRenewal && !validateReplacement && oauth.ExpiresAt is { } expiry &&
            expiry - _now().ToUnixTimeMilliseconds() <= TimeSpan.FromMinutes(5).TotalMilliseconds)
        {
            oauth = await RenewAppCredentialAsync(oauth, ct);
            return await ProbeAsync(oauth, ct, allowRenewal: false);
        }

        var response = await _http.SendAsync(UsageRequest(new Uri(UsageUrl), oauth), ct);

        if (response.StatusCode == 429)
        {
            if (validateReplacement)
                return ProviderSnapshot.Error(Provider, "Rate limited, try again later.");

            var now = _now();
            var delay = response.RetryAfter(now) ?? RateLimitCooldown;
            _rateLimit = (fingerprint, now.Add(delay > RateLimitCooldown ? delay : RateLimitCooldown));
            return HeldUsage(fingerprint);
        }

        if (response.StatusCode is 401 or 403)
        {
            if (!validateReplacement && allowRenewal)
            {
                oauth = await RenewAppCredentialAsync(oauth, ct);
                return await ProbeAsync(oauth, ct, allowRenewal: false);
            }
            throw new ProviderRequestException(expiredMessage ?? "Claude sign-in was not accepted. Sign in again in Customize → Claude.");
        }
        if (!response.IsSuccess)
            throw new InvalidOperationException(ProviderErrors.RequestFailed(response.StatusCode));
        if (response.Json is not { } body)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);

        var mapped = ClaudeUsageMapper.Map(body, oauth);
        var lines = mapped.Lines.ToList();
        var plan = validateReplacement
            ? await ResolveLivePlanAsync(oauth, UsageUrl, ct, requireProfile: true) ?? mapped.Plan
            : mapped.Plan;
        // Both run only after the usage call succeeded: the token is known-good, so a failure here costs
        // the resets row or the live plan label, never the bars.
        if (await ResetGrantsLineAsync(oauth, UsageUrl, ct) is { } resets) lines.Add(resets);
        if (!validateReplacement)
            plan = await ResolveLivePlanAsync(oauth, UsageUrl, ct, cacheByToken: cacheLivePlan) ?? mapped.Plan;
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

    /// The Rate Limit Resets row, asking Anthropic at most once an hour per login. A connection failure
    /// before the first response leaves the check due for the next refresh.
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
            if (previous.Fingerprint is null &&
                ex is ProviderRequestException { InnerException: HttpRequestException { StatusCode: null } or OperationCanceledException })
            {
                AppLog.Warn("claude", $"reset grants lookup failed; retrying on the next refresh: {ex.Message}");
                return held;
            }
            AppLog.Warn("claude", $"reset grants lookup failed; retrying in an hour: {ex.Message}");
            _resetGrantsCheck = (fingerprint, now, previous.Line);
            return held;
        }
    }

    /// The plan Anthropic's profile reports for this access token, looked up at most once per token
    /// (a failed lookup included, which keeps the stored plan until the token rotates).
    private async Task<string?> ResolveLivePlanAsync(ClaudeOAuth oauth, string usageUrl, CancellationToken ct,
        bool requireProfile = false, bool cacheByToken = true)
    {
        var fingerprint = Hash(oauth.AccessToken);
        if (cacheByToken && !requireProfile && _livePlan is { } cached && cached.AccessFingerprint == fingerprint)
            return cached.Plan;
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
            if (requireProfile && (profile["account"] is not JsonObject account ||
                string.IsNullOrWhiteSpace(JsonUtil.String(account["uuid"]))))
                throw new InvalidOperationException(ProviderErrors.InvalidResponse);
            var plan = ClaudeUsageMapper.FormatLivePlan(profile, oauth);
            if (plan is null) AppLog.Info("claude", "live profile carries no organization plan; showing the stored plan");
            if (cacheByToken) _livePlan = (fingerprint, plan);
            return plan;
        }
        catch (Exception ex)
        {
            if (requireProfile)
            {
                ct.ThrowIfCancellationRequested();
                throw new ProviderRequestException("Claude didn’t return your profile. Try signing in again.", ex);
            }
            if (ct.IsCancellationRequested) return null;
            var retry = cacheByToken ? "when the token rotates" : "on the next refresh";
            AppLog.Warn("claude", $"live plan lookup failed; retrying {retry}: {ex.Message}");
            if (cacheByToken) _livePlan = (fingerprint, null);
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

}
