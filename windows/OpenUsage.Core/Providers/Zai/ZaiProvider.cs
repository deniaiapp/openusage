using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Zai;

public static class ZaiUsageMapper
{
    public static bool IsNoCodingPlan(JsonObject? root) =>
        JsonUtil.Bool(root?["success"]) == false &&
        (JsonUtil.String(root?["msg"]) ?? "").Contains("coding plan", StringComparison.OrdinalIgnoreCase);

    public static (string? Plan, List<MetricLine> Lines) Map(JsonObject quotaBody, JsonObject? subscriptionBody)
    {
        var plan = PlanName(subscriptionBody);
        return (plan, MapQuota(quotaBody));
    }

    public static string? PlanName(JsonObject? body)
    {
        if (JsonUtil.Array(body?["data"]) is not { } list) return null;
        var first = list.OfType<JsonObject>().FirstOrDefault();
        return JsonUtil.String(first?["productName"]);
    }

    public static List<MetricLine> MapQuota(JsonObject root)
    {
        var container = JsonUtil.Object(root["data"]) ?? root;
        var limits = JsonUtil.Array(container["limits"])
            ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        if (limits.Count == 0) return [MetricLine.NoUsageData];

        var lines = new List<MetricLine>();
        foreach (var entry in limits.OfType<JsonObject>())
        {
            var type = JsonUtil.String(entry["type"]) ?? JsonUtil.String(entry["name"]);
            if (type is "CREDIT_LIMIT" or "TOKENS_LIMIT")
            {
                if (ClassifyTokenWindow(entry) is not { } window) continue;
                lines.Add(PercentLine(entry, window.Session ? "Session" : "Weekly", window.PeriodMs));
            }
            else if (type == "TIME_LIMIT")
            {
                lines.Add(WebSearchLine(entry));
            }
        }
        if (lines.Count == 0) return [MetricLine.NoUsageData];
        return lines;
    }

    private static (bool Session, int PeriodMs)? ClassifyTokenWindow(JsonObject entry)
    {
        var unit = JsonUtil.Number(entry["unit"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var number = JsonUtil.Number(entry["number"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        if (number <= 0) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var unitMs = unit switch
        {
            3 => 60d * 60 * 1000,
            4 => 24d * 60 * 60 * 1000,
            6 => 7d * 24 * 60 * 60 * 1000,
            5 => 30d * 24 * 60 * 60 * 1000,
            _ => (double?)null
        };
        if (unitMs is null) return null;
        var periodMs = (int)(unitMs.Value * number);
        return periodMs < 24 * 60 * 60 * 1000 ? (true, periodMs) : (false, periodMs);
    }

    private static MetricLine PercentLine(JsonObject entry, string label, int periodMs)
    {
        var percent = JsonUtil.Number(entry["percentage"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return MetricLine.Progress(label, JsonUtil.ClampPercent(percent), 100, ProgressFormat.Percent,
            Iso8601.Parse(JsonUtil.String(entry["resetAt"])) ?? Iso8601.Parse(JsonUtil.String(entry["reset_at"])),
            periodMs);
    }

    private static MetricLine WebSearchLine(JsonObject entry)
    {
        var used = JsonUtil.Number(entry["used"]) ?? JsonUtil.Number(entry["usage"]) ?? 0;
        var limit = JsonUtil.Number(entry["limit"]) ?? JsonUtil.Number(entry["quota"]) ?? 0;
        if (limit <= 0) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return MetricLine.Progress("Web searches", used, limit, ProgressFormat.Count("searches"),
            periodDurationMs: MetricPeriod.MonthMs);
    }
}

public sealed class ZaiProvider : IProviderRuntime, IApiKeyManaging
{
    public static readonly Uri SubscriptionUrl = new("https://api.z.ai/api/biz/subscription/list");
    public static readonly Uri QuotaUrl = new("https://api.z.ai/api/monitor/usage/quota/limit");

    private readonly AppHttp _http;
    private readonly ApiKeyStore _keys;

    public ZaiProvider(AppHttp http, ApiKeyStore keys)
    {
        _http = http;
        _keys = keys;
    }

    public string ProviderId => Provider.Id;
    public Provider Provider { get; } = new(
        "zai", "Z.ai", "zai",
        [new ProviderLink("Subscribe", "https://z.ai/subscribe")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("zai.session", Provider, "Session", sessionStartSignal: SessionStartSignal.ZeroUsage),
        WidgetDescriptor.Percent("zai.weekly", Provider, "Weekly"),
        WidgetDescriptor.BoundedCount("zai.webSearches", Provider, "Web Searches", 100, "searches", MetricPeriod.MonthMs, "Web searches")
    ];

    public string? LoadKey() => _keys.Get("zai") ?? AppPaths.Env("ZAI_API_KEY");
    public void SaveKey(string? key) => _keys.Set("zai", key);

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrEmpty(LoadKey()));

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var key = LoadKey();
        if (string.IsNullOrEmpty(key))
            return ProviderSnapshot.Error(Provider, "Add a Z.ai API key in Customize.");
        try
        {
            var quota = await GetAsync(QuotaUrl, key, cancellationToken);
            if (!quota.IsSuccess)
                return ProviderSnapshot.Error(Provider, ProviderErrors.RequestFailed(quota.StatusCode));
            if (quota.Json is not { } quotaBody)
                return ProviderSnapshot.Error(Provider, ProviderErrors.InvalidResponse);
            if (ZaiUsageMapper.IsNoCodingPlan(quotaBody))
                return ProviderSnapshot.Error(Provider, "No active GLM Coding Plan. Subscribe at z.ai/subscribe to see usage.");

            JsonObject? subscription = null;
            try { subscription = (await GetAsync(SubscriptionUrl, key, cancellationToken)).Json; }
            catch (Exception ex) { AppLog.Warn("zai", $"subscription fetch failed: {ex.Message}"); }

            var mapped = ZaiUsageMapper.Map(quotaBody, subscription);
            var lines = mapped.Lines.ToList();
            MetricLine.AppendNoDataIfNeeded(lines);
            return ProviderSnapshot.Make(Provider, mapped.Plan, lines, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
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
