using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Grok;

public sealed class GrokAuthState
{
    public string Token { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string ClientId { get; set; } = GrokAuthStore.DefaultClientId;
    public string Path { get; set; } = "";
    public JsonObject? Root { get; set; }
    public string EntryKey { get; set; } = "";
}

public sealed class GrokAuthStore
{
    public const string DefaultClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public static string AuthPath => AppPaths.Expand("~/.grok/auth.json");

    public IReadOnlyList<GrokAuthState> LoadCandidates()
    {
        if (!File.Exists(AuthPath)) return [];
        var root = JsonUtil.ParseObject(File.ReadAllText(AuthPath));
        if (root is null) return [];
        var list = new List<GrokAuthState>();
        foreach (var property in root)
        {
            if (property.Value is not JsonObject entry) continue;
            var token = JsonUtil.String(entry["key"]);
            if (string.IsNullOrEmpty(token)) continue;
            list.Add(new GrokAuthState
            {
                Token = token,
                RefreshToken = JsonUtil.String(entry["refresh_token"]) ?? JsonUtil.String(entry["refresh"]),
                ClientId = JsonUtil.String(entry["oidc_client_id"]) ?? DefaultClientId,
                Path = AuthPath,
                Root = root,
                EntryKey = property.Key
            });
        }
        return list;
    }

    public bool HasUsableCredentials() => LoadCandidates().Count > 0;
}

public static class GrokCreditsConfigDecoder
{
    public const string WeeklyPeriodType = "USAGE_PERIOD_TYPE_WEEKLY";

    public static (string PeriodType, double UsedPercent, DateTimeOffset Start, DateTimeOffset End, double OnDemandCap) Decode(JsonObject body)
    {
        var config = JsonUtil.Object(body["config"]) ?? throw new InvalidOperationException("Grok billing response changed.");
        var period = JsonUtil.Object(config["currentPeriod"]) ?? throw new InvalidOperationException("Grok billing response changed.");
        var periodType = JsonUtil.String(period["type"]) ?? throw new InvalidOperationException("Grok billing response changed.");
        var start = Iso8601.Parse(JsonUtil.String(period["start"])) ?? throw new InvalidOperationException("Grok billing response changed.");
        var end = Iso8601.Parse(JsonUtil.String(period["end"])) ?? throw new InvalidOperationException("Grok billing response changed.");
        if (end <= start) throw new InvalidOperationException("Grok billing response changed.");
        double percent = 0;
        if (config["creditUsagePercent"] is not null)
        {
            percent = JsonUtil.Number(config["creditUsagePercent"])
                ?? throw new InvalidOperationException("Grok billing response changed.");
        }
        double cap = 0;
        if (config["onDemandCap"] is JsonObject capObject)
        {
            cap = JsonUtil.Number(capObject["val"] ?? 0)
                ?? throw new InvalidOperationException("Grok billing response changed.");
        }
        return (periodType, percent, start, end, cap);
    }
}

public static class GrokUsageMapper
{
    public static List<MetricLine> MapCreditsConfig(JsonObject body)
    {
        var config = GrokCreditsConfigDecoder.Decode(body);
        var lines = new List<MetricLine>();
        if (config.PeriodType == GrokCreditsConfigDecoder.WeeklyPeriodType)
        {
            lines.Add(MetricLine.Progress(
                "Weekly limit",
                JsonUtil.ClampPercent(config.UsedPercent),
                100,
                ProgressFormat.Percent,
                config.End,
                (int)(config.End - config.Start).TotalMilliseconds));
        }
        var capText = config.OnDemandCap > 0
            ? $"{MetricFormatter.Number(JsonUtil.CentsToDollars(config.OnDemandCap), MetricKind.Dollars, MetricFormatter.Style.Row)} cap"
            : "Disabled";
        lines.Add(MetricLine.Badge("Pay as you go", capText, config.OnDemandCap > 0 ? "#22c55e" : "#a3a3a3"));
        return lines;
    }

    public static string? PlanName(JsonObject? body) => JsonUtil.String(body?["subscription_tier_display"]);

    /// <summary>
    /// HTTP 412 whose JSON <c>error</c> names "no personal team" is Grok's "this principal is a team,
    /// not a personal team" signal from the credits endpoint. Auth is fine; the weekly pool and
    /// pay-as-you-go cap just aren't on this surface. Other 412s stay hard failures.
    /// </summary>
    public static bool IsTeamBillingUnavailable(AppHttpResponse response) =>
        response.StatusCode == 412 &&
        JsonUtil.String(response.Json?["error"]) is { } error &&
        error.Contains("no personal team", StringComparison.OrdinalIgnoreCase);

    public const string TeamBillingUnavailableWarning =
        "Team accounts have no personal quota.";
}

public sealed class GrokProvider : IProviderRuntime
{
    public static readonly Uri SettingsUrl = new("https://cli-chat-proxy.grok.com/v1/settings");
    public static readonly Uri RefreshUrl = new("https://auth.x.ai/oauth2/token");
    public static readonly Uri CreditsConfigUrl = new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");

    private readonly GrokAuthStore _auth = new();
    private readonly AppHttp _http;

    public GrokProvider(AppHttp http) => _http = http;

    public Provider Provider { get; } = new(
        "grok", "Grok", "grok",
        [new ProviderLink("Console", "https://console.x.ai/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("grok.weekly", Provider, "Weekly", "Weekly limit"),
        WidgetDescriptor.Badge("grok.payAsYouGo", Provider, "Pay as you go")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.HasUsableCredentials());

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var candidates = _auth.LoadCandidates();
        if (candidates.Count == 0)
            return ProviderSnapshot.Error(Provider, "Grok not logged in. Run `grok login`.");
        Exception? last = null;
        foreach (var auth in candidates)
        {
            try { return await ProbeAsync(auth, cancellationToken); }
            catch (Exception ex)
            {
                last = ex;
                AppLog.Warn("grok", $"candidate failed: {ex.Message}");
            }
        }
        return ProviderSnapshot.Error(Provider, last ?? new InvalidOperationException("Grok refresh failed."));
    }

    internal async Task<ProviderSnapshot> ProbeAsync(GrokAuthState auth, CancellationToken ct)
    {
        var token = auth.Token;
        if (!string.IsNullOrEmpty(auth.RefreshToken) && Jwt.Expiration(token) is { } exp &&
            exp - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5))
        {
            try { token = await RefreshTokenAsync(auth, ct); }
            catch (ProviderRequestException ex) when (!ct.IsCancellationRequested && exp > DateTimeOffset.UtcNow)
            {
                AppLog.Warn("grok", $"early token renewal failed; keeping the current token: {ex.Message}");
            }
        }

        var credits = await _http.SendAsync(new AppHttpRequest(
            "GET", CreditsConfigUrl, AuthHeaders(token), TimeoutSeconds: 10), ct);
        List<MetricLine> lines;
        string? warning = null;
        if (GrokUsageMapper.IsTeamBillingUnavailable(credits))
        {
            AppLog.Warn("grok", "credits config unavailable: team principal has no personal team");
            lines = [];
            warning = GrokUsageMapper.TeamBillingUnavailableWarning;
        }
        else
        {
            AuthRetry.RequireSuccess(credits,
                new InvalidOperationException("Grok auth expired. Run `grok login` again."),
                status => new InvalidOperationException($"Grok billing request failed (HTTP {status}). Try again later."));
            if (credits.Json is not { } body)
                throw new InvalidOperationException("Grok billing response changed.");
            lines = GrokUsageMapper.MapCreditsConfig(body);
        }

        string? plan = null;
        try
        {
            var settings = await _http.SendAsync(new AppHttpRequest(
                "GET", SettingsUrl, AuthHeaders(token), TimeoutSeconds: 10), ct);
            plan = GrokUsageMapper.PlanName(settings.Json);
        }
        catch (Exception ex) { AppLog.Warn("grok", $"settings fetch failed: {ex.Message}"); }

        MetricLine.AppendNoDataIfNeeded(lines);
        return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now, warning);
    }

    private async Task<string> RefreshTokenAsync(GrokAuthState auth, CancellationToken ct)
    {
        var body = $"grant_type=refresh_token&client_id={WebUtility.UrlEncode(auth.ClientId)}&refresh_token={WebUtility.UrlEncode(auth.RefreshToken)}";
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST", RefreshUrl,
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            Encoding.UTF8.GetBytes(body), 15), ct);
        if (response.StatusCode is 400 or 401 or 403 && JsonUtil.String(response.Json?["error"]) == "invalid_grant")
            throw new ProviderRequestException("Grok auth expired. Run `grok login` again.");
        if (response.StatusCode == 429)
            throw new ProviderRequestException("Grok is limiting sign-in requests. Try again later.");
        if (!response.IsSuccess)
            throw new ProviderRequestException($"Grok token renewal failed (HTTP {response.StatusCode}). Try again later.");
        var access = JsonUtil.String(response.Json?["access_token"]);
        if (string.IsNullOrWhiteSpace(access))
            throw new ProviderRequestException("Grok returned an invalid sign-in response. Try again later.");
        return access;
    }

    private static Dictionary<string, string> AuthHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}",
        ["xai-grok-cli"] = "1",
        ["Accept"] = "application/json"
    };
}
