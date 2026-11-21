using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.OpenRouter;

public static class OpenRouterUsageMapper
{
    internal static readonly (string Field, string Label)[] SpendPeriods =
        [("usage_daily", "Today"), ("usage_weekly", "This Week"), ("usage_monthly", "This Month")];

    public static List<MetricLine> CreditsLines(JsonObject data)
    {
        if (JsonUtil.Number(data["total_usage"]) is not { } totalUsage) return [];
        var used = Math.Max(0, totalUsage);
        var totalCredits = Math.Max(0, JsonUtil.Number(data["total_credits"]) ?? 0);
        var lines = new List<MetricLine>();
        if (totalCredits > 0)
            lines.Add(MetricLine.Progress("Credits", used, totalCredits, ProgressFormat.Dollars));
        lines.Add(MetricLine.Values("Balance", [new MetricValue(Math.Max(0, totalCredits - used), MetricKind.Dollars)]));
        return lines;
    }

    public static (string? Plan, List<MetricLine> Lines) KeyMetrics(
        JsonObject data, bool includeSpend = true, DateTimeOffset? now = null)
    {
        var lines = new List<MetricLine>();
        if (includeSpend)
            foreach (var (field, label) in SpendPeriods)
                if (JsonUtil.Number(data[field]) is { } amount)
                    lines.Add(SpendLine(label, amount));
        if (JsonUtil.Number(data["limit"]) is { } limit && limit > 0)
        {
            var remaining = Math.Max(0, JsonUtil.Number(data["limit_remaining"]) ?? 0);
            var (reset, period) = KeyLimitWindow(JsonUtil.String(data["limit_reset"]), now ?? DateTimeOffset.UtcNow);
            lines.Add(MetricLine.Progress("Key Limit", Math.Max(0, limit - remaining), limit, ProgressFormat.Dollars,
                reset, period));
        }
        var plan = JsonUtil.Bool(data["is_free_tier"]) switch
        {
            true => "Free tier",
            false => "Pay as you go",
            _ => null
        };
        return (plan, lines);
    }

    private static (DateTimeOffset? Reset, long? PeriodMs) KeyLimitWindow(string? interval, DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        switch (interval)
        {
            case "daily":
                return (midnight.AddDays(1), MetricPeriod.DayMs);
            case "weekly":
                var monday = midnight.AddDays(-(((int)midnight.DayOfWeek + 6) % 7));
                return (monday.AddDays(7), MetricPeriod.WeekMs);
            case "monthly":
                var first = midnight.AddDays(1 - midnight.Day);
                var next = first.AddMonths(1);
                return (next, (next - first).Ticks / TimeSpan.TicksPerMillisecond);
            default:
                return (null, null);
        }
    }

    internal static MetricLine SpendLine(string label, double amount) =>
        MetricLine.Values(label, [new MetricValue(Math.Max(0, amount), MetricKind.Dollars)]);
}

public sealed class OpenRouterProvider : IProviderRuntime, IApiKeyManaging
{
    private const int KeysPageSize = 100;
    private const int MaxKeysPages = 50;
    public static readonly Uri CreditsUrl = new("https://openrouter.ai/api/v1/credits");
    public static readonly Uri KeyUrl = new("https://openrouter.ai/api/v1/key");

    private readonly AppHttp _http;
    private readonly ApiKeyStore _keys;

    public OpenRouterProvider(AppHttp http, ApiKeyStore keys)
    {
        _http = http;
        _keys = keys;
    }

    public string ProviderId => Provider.Id;
    public Provider Provider { get; } = new(
        "openrouter", "OpenRouter", "openrouter",
        [
            new ProviderLink("Activity", "https://openrouter.ai/activity"),
            new ProviderLink("Credits", "https://openrouter.ai/settings/credits")
        ]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.BoundedDollars("openrouter.credits", Provider, "Credits", 100, limitNoun: "purchased"),
        WidgetDescriptor.DollarBalance("openrouter.balance", Provider, "Balance", "left"),
        WidgetDescriptor.Values("openrouter.today", Provider, "Today", isUsagePeriod: true),
        WidgetDescriptor.Values("openrouter.week", Provider, "This Week", isUsagePeriod: true),
        WidgetDescriptor.Values("openrouter.month", Provider, "This Month", isUsagePeriod: true),
        WidgetDescriptor.BoundedDollars("openrouter.keyLimit", Provider, "Key Limit", 100)
    ];

    public string? LoadKey() => _keys.Get("openrouter");
    public void SaveKey(string? key) => _keys.Set("openrouter", key);

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrEmpty(LoadKey()));

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshWithKeyAsync(LoadKey(), cancellationToken);

    internal async Task<ProviderSnapshot> RefreshWithKeyAsync(
        string? key, CancellationToken cancellationToken, bool requireKey = false)
    {
        if (string.IsNullOrEmpty(key))
            return ProviderSnapshot.Error(Provider, "Add an OpenRouter API key in Customize.");
        try
        {
            var lines = new List<MetricLine>();
            string? plan = null;
            try
            {
                var credits = await GetAsync(CreditsUrl, key, cancellationToken);
                if (credits.IsSuccess && JsonUtil.Object(credits.Json?["data"]) is { } data)
                    lines.AddRange(OpenRouterUsageMapper.CreditsLines(data));
                else if (!credits.IsSuccess)
                    AppLog.Warn("openrouter", $"credits endpoint HTTP {credits.StatusCode}");
            }
            catch (Exception ex) { AppLog.Warn("openrouter", $"credits fetch failed: {ex.Message}"); }

            try
            {
                var keyResponse = await GetAsync(KeyUrl, key, cancellationToken);
                if (keyResponse.IsSuccess && JsonUtil.Object(keyResponse.Json?["data"]) is { } data)
                {
                    var management = requireKey && JsonUtil.Bool(data["is_management_key"]) == true;
                    var metrics = OpenRouterUsageMapper.KeyMetrics(data, includeSpend: !management);
                    plan = metrics.Plan;
                    lines.AddRange(metrics.Lines);
                    if (management) lines.AddRange(await ListedKeysSpendAsync(key, cancellationToken));
                }
                else if (requireKey)
                {
                    var reason = AuthRetry.IsAuthFailure(keyResponse) ? "OpenRouter rejected your API key."
                        : keyResponse.IsSuccess ? ProviderErrors.InvalidResponse
                        : ProviderErrors.RequestFailed(keyResponse.StatusCode);
                    return ProviderSnapshot.Error(Provider, reason);
                }
            }
            catch (Exception ex) when (!requireKey) { AppLog.Warn("openrouter", $"key fetch failed: {ex.Message}"); }

            MetricLine.AppendNoDataIfNeeded(lines);
            return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    private async Task<IEnumerable<MetricLine>> ListedKeysSpendAsync(string key, CancellationToken ct)
    {
        var periods = OpenRouterUsageMapper.SpendPeriods;
        var totals = new double[periods.Length];
        for (var page = 0; page < MaxKeysPages; page++)
        {
            var url = new Uri($"https://openrouter.ai/api/v1/keys?include_disabled=true&include_expired=true&offset={page * KeysPageSize}");
            var response = await GetAsync(url, key, ct);
            if (!response.IsSuccess)
                throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
            if (JsonUtil.Array(response.Json?["data"]) is not { } keys)
                throw new ProviderRequestException(ProviderErrors.InvalidResponse);
            foreach (var keyData in keys)
            {
                if (keyData is not JsonObject entry)
                    throw new ProviderRequestException(ProviderErrors.InvalidResponse);
                for (var i = 0; i < periods.Length; i++)
                {
                    if (JsonUtil.Number(entry[periods[i].Field]) is not { } amount)
                        throw new ProviderRequestException(ProviderErrors.InvalidResponse);
                    totals[i] += Math.Max(0, amount);
                }
            }
            if (keys.Count < KeysPageSize)
                return periods.Select((period, i) => OpenRouterUsageMapper.SpendLine(period.Label, totals[i]));
        }
        throw new ProviderRequestException(ProviderErrors.InvalidResponse);
    }

    private Task<AppHttpResponse> GetAsync(Uri url, string apiKey, CancellationToken ct) =>
        _http.SendAsync(new AppHttpRequest(
            "GET", url,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {apiKey}",
                ["Accept"] = "application/json"
            },
            TimeoutSeconds: 15), ct);
}
