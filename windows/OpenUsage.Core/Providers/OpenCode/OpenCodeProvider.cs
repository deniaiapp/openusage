using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.OpenCode;

internal static class OpenCodeErrors
{
    internal const string NoGoSubscription = "No OpenCode Go subscription on this key.";
}

public static class OpenCodeUsageMapper
{
    /// <summary>
    /// Maximum distance from exactly one full period for a zero-usage rolling reset to count as the
    /// untouched-window placeholder. OpenCode rounds to whole seconds and HTTP <c>Date</c> headers carry
    /// whole-second precision, so two seconds covers both rounding steps.
    /// </summary>
    public static readonly TimeSpan PlaceholderResetTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compares two server-authored instants when possible (the response's <c>Date</c> header), which
    /// removes local clock skew from the placeholder test; falls back to the local capture time.
    /// </summary>
    public static List<MetricLine> MeterLines(AppHttpResponse response, DateTimeOffset capturedAt)
    {
        var body = response.Json ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var reference = HttpDate(response.Header("date")) ?? capturedAt;
        return MeterLines(body, reference);
    }

    public static List<MetricLine> MeterLines(JsonObject body, DateTimeOffset capturedAt)
    {
        var usage = JsonUtil.Object(body["usage"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return
        [
            Window(usage["rolling"], "Session", MetricPeriod.SessionMs, capturedAt, dropsPlaceholderReset: true),
            Window(usage["weekly"], "Weekly", MetricPeriod.WeekMs, capturedAt),
            Window(usage["monthly"], "Monthly", MetricPeriod.MonthMs, capturedAt)
        ];
    }

    public static string? ErrorType(AppHttpResponse response) =>
        JsonUtil.String(JsonUtil.Object(response.Json?["error"])?["type"]);

    /// <summary>
    /// One window. <paramref name="dropsPlaceholderReset"/> is for the rolling session only: with no calls
    /// in the window the API answers <c>resetsAt = now + 5h</c>, re-derived on every request. That
    /// placeholder is dropped at capture time so the Session row reads "Not started" off the missing
    /// reset, while a sub-1% session (whole-percent 0, reset anchored to its oldest call) keeps its
    /// countdown. Weekly and monthly resets are calendar instants and keep theirs.
    /// </summary>
    private static MetricLine Window(JsonNode? raw, string label, long periodMs, DateTimeOffset capturedAt,
        bool dropsPlaceholderReset = false)
    {
        var obj = JsonUtil.Object(raw) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var percent = JsonUtil.Number(obj["percent"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var reportedReset = Iso8601.Parse(JsonUtil.String(obj["resetsAt"]))
            ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var used = JsonUtil.ClampPercent(percent);
        DateTimeOffset? resetsAt = reportedReset;
        if (dropsPlaceholderReset && used == 0)
        {
            var distance = (reportedReset - capturedAt - TimeSpan.FromMilliseconds(periodMs)).Duration();
            if (distance <= PlaceholderResetTolerance) resetsAt = null;
        }
        return MetricLine.Progress(label, used, 100, ProgressFormat.Percent, resetsAt, periodMs);
    }

    private static DateTimeOffset? HttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParseExact(value.Trim(), "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}

public sealed class OpenCodeProvider(AppHttp http) : IProviderRuntime
{
    public const string Id = "opencode";
    public static readonly Uri UsageUrl = new("https://opencode.ai/zen/go/v1/usage");

    public Provider Provider { get; } = new(
        Id, "OpenCode", "opencode",
        [new ProviderLink("Zen", "https://opencode.ai/")]);

    public bool IsAccountTemplate => true;

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("opencode.session", Provider, "Session", sessionStartSignal: SessionStartSignal.MissingResetDate),
        WidgetDescriptor.Percent("opencode.weekly", Provider, "Weekly"),
        WidgetDescriptor.Percent("opencode.monthly", Provider, "Monthly")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderSnapshot.Error(Provider, "Add an OpenCode Go API key in Customize to see usage."));

    internal async Task<ProviderSnapshot> RefreshWithApiKeyAsync(string key, CancellationToken ct)
    {
        var (meters, error) = await FetchGoMeters(key, ct);
        if (meters is not null) return ProviderSnapshot.Make(Provider, null, meters, DateTimeOffset.Now);
        return ProviderSnapshot.Error(Provider, error ?? OpenCodeErrors.NoGoSubscription);
    }

    internal static AppHttpRequest UsageRequest(string key) =>
        new("GET", UsageUrl,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {key}",
                ["Accept"] = "application/json"
            },
            TimeoutSeconds: 15);

    internal static bool IsNoGoSubscription(AppHttpResponse response) =>
        response.StatusCode == 403 && OpenCodeUsageMapper.ErrorType(response) == "EntitlementError";

    /// <summary>The Go meters, or null with an error (null error = no Go subscription on this key).</summary>
    private async Task<(List<MetricLine>? Meters, string? Error)> FetchGoMeters(string key, CancellationToken ct)
    {
        AppHttpResponse response;
        try
        {
            response = await http.SendAsync(UsageRequest(key), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn("opencode", $"Go usage request failed: {ex.Message}");
            return (null, ProviderErrors.ConnectionFailed);
        }
        if (response.StatusCode == 401) return (null, "OpenCode Go rejected this API key. Replace it in Customize.");
        if (IsNoGoSubscription(response))
        {
            AppLog.Info("opencode", "Go usage endpoint: no active subscription");
            return (null, null);
        }
        if (!response.IsSuccess) return (null, ProviderErrors.RequestFailed(response.StatusCode));
        try { return (OpenCodeUsageMapper.MeterLines(response, DateTimeOffset.Now), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
