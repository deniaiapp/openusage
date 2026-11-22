using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Grok;

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

public sealed partial class GrokProvider : IProviderRuntime
{
    public static readonly Uri SettingsUrl = new("https://cli-chat-proxy.grok.com/v1/settings");
    public static readonly Uri RefreshUrl = new("https://auth.x.ai/oauth2/token");
    public static readonly Uri CreditsConfigUrl = new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");

    private readonly AppHttp _http;

    public GrokProvider(AppHttp http, ApiKeyStore? keys = null)
    {
        _http = http;
        _keys = keys;
    }

    public Provider Provider { get; } = new(
        "grok", "Grok", "grok",
        [new ProviderLink("Console", "https://console.x.ai/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("grok.weekly", Provider, "Weekly", "Weekly limit"),
        WidgetDescriptor.Badge("grok.payAsYouGo", Provider, "Pay as you go")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HasAppCredential);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (HasAppCredential)
        {
            try { return await RefreshConnectedAsync(cancellationToken); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { return ProviderSnapshot.Error(Provider, ex); }
        }

        return ProviderSnapshot.Error(Provider, "Sign in to Grok in Customize → Grok.");
    }

    private Task<AppHttpResponse> ReadCreditsAsync(string token, CancellationToken ct) =>
        _http.SendAsync(new AppHttpRequest("GET", CreditsConfigUrl, AuthHeaders(token), TimeoutSeconds: 10), ct);

    internal async Task<ProviderSnapshot> RefreshWithHubTokenAsync(string token, string expiredMessage, CancellationToken ct) =>
        await MapUsageAsync(await ReadCreditsAsync(token, ct), token, expiredMessage, ct);

    private async Task<ProviderSnapshot> MapUsageAsync(AppHttpResponse credits, string token, string expiredMessage, CancellationToken ct)
    {
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
                new ProviderRequestException(expiredMessage),
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

    private static Dictionary<string, string> AuthHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}",
        ["xai-grok-cli"] = "1",
        ["Accept"] = "application/json"
    };
}
