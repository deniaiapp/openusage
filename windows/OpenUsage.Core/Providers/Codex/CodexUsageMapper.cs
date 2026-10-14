using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public static class CodexUsageMapper
{
    public const double CreditUsdRate = 0.04;

    public static (string? Plan, List<MetricLine> Lines) Map(AppHttpResponse response, AppHttpResponse? resetCredits, DateTimeOffset now)
    {
        AuthRetry.RequireSuccess(response,
            new InvalidOperationException("Token expired. Run `codex` to log in again."),
            status => new InvalidOperationException(ProviderErrors.RequestFailed(status)));
        if (response.Json is not { } body)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);

        var lines = new List<MetricLine>();
        var rateLimit = JsonUtil.Object(body["rate_limit"]);
        lines.AddRange(ClassifiedWindowLines(rateLimit,
            JsonUtil.Number(ToNode(response.Header("x-codex-primary-used-percent"))),
            JsonUtil.Number(ToNode(response.Header("x-codex-secondary-used-percent"))),
            "Session", "Weekly", now));
        lines.AddRange(SparkLines(body, now));

        if (ReadResetCredits(body, resetCredits) is { } resets)
        {
            lines.Add(MetricLine.Values("Rate Limit Resets",
                [new MetricValue(resets.Count, MetricKind.Count, "available")],
                expiriesAt: resets.Expiries));
        }

        if (ReadCreditsRemaining(response, body) is { } remaining)
            lines.Add(MetricLine.Values("Credits", CreditValues(remaining)));

        return (FormatPlan(JsonUtil.String(body["plan_type"])), lines);
    }

    public static IReadOnlyList<MetricValue> CreditValues(double remaining)
    {
        var credits = Math.Max(0, (int)Math.Floor(remaining));
        return
        [
            new MetricValue(credits * CreditUsdRate, MetricKind.Dollars),
            new MetricValue(credits, MetricKind.Count, "credits")
        ];
    }

    private static JsonNode? ToNode(string? value) =>
        value is null ? null : JsonValue.Create(value);

    private static IEnumerable<MetricLine> ClassifiedWindowLines(
        JsonObject? rateLimit, double? primaryHeader, double? secondaryHeader,
        string sessionLabel, string weeklyLabel, DateTimeOffset now)
    {
        var candidates = new List<(JsonObject Window, double? Used, bool SessionFallback)>();
        AddCandidate(rateLimit?["primary_window"] ?? rateLimit?["primaryWindow"], primaryHeader, true);
        AddCandidate(rateLimit?["secondary_window"] ?? rateLimit?["secondaryWindow"], secondaryHeader, false);

        MetricLine? Pick(bool session)
        {
            var exact = candidates.FirstOrDefault(c => ExactKind(c.Window) == session);
            var fallback = candidates.FirstOrDefault(c => ExactKind(c.Window) is null && c.SessionFallback == session);
            var chosen = exact.Window is not null ? exact : fallback.Window is not null ? fallback : default;
            if (chosen.Window is null && chosen.Used is null) return null;
            if (chosen.Used is not { } used) return null;
            var period = ReadPeriodMs(chosen.Window) ?? (session ? MetricPeriod.SessionMs : MetricPeriod.WeekMs);
            return MetricLine.Progress(session ? sessionLabel : weeklyLabel, used, 100, ProgressFormat.Percent,
                ResetDate(chosen.Window, now), period);

            bool? ExactKind(JsonObject? window)
            {
                var periodMs = ReadPeriodMs(window);
                if (periodMs == MetricPeriod.SessionMs) return true;
                if (periodMs == MetricPeriod.WeekMs) return false;
                return null;
            }
        }

        void AddCandidate(JsonNode? value, double? header, bool sessionFallback)
        {
            var window = JsonUtil.Object(value) ?? (header is null ? null : new JsonObject());
            if (window is null) return;
            candidates.Add((window, WindowUsedPercent(window) ?? header, sessionFallback));
        }

        foreach (var line in new[] { Pick(true), Pick(false) })
            if (line is not null) yield return line;
    }

    private static IEnumerable<MetricLine> SparkLines(JsonObject body, DateTimeOffset now)
    {
        if (JsonUtil.Array(body["additional_rate_limits"]) is not { } entries) yield break;
        var spark = entries.OfType<JsonObject>().FirstOrDefault(IsSpark);
        if (spark is null) yield break;
        var rateLimit = JsonUtil.Object(spark["rate_limit"]);
        foreach (var line in ClassifiedWindowLines(rateLimit, null, null, "Spark", "Spark Weekly", now))
            yield return line;
    }

    private static bool IsSpark(JsonObject entry)
    {
        foreach (var key in new[] { "limit_name", "metered_feature" })
        {
            var value = JsonUtil.String(entry[key])?.ToLowerInvariant();
            if (value?.Contains("spark") == true) return true;
        }
        return false;
    }

    private static double? WindowUsedPercent(JsonObject window) =>
        JsonUtil.Number(window["used_percent"])
        ?? JsonUtil.Number(window["usedPercent"])
        ?? JsonUtil.Number(window["percent_used"]);

    private static DateTimeOffset? ResetDate(JsonObject? window, DateTimeOffset now)
    {
        if (window is null) return null;
        if (JsonUtil.Number(window["reset_at"]) is { } resetAt)
            return DateTimeOffset.FromUnixTimeSeconds((long)resetAt);
        if (JsonUtil.Number(window["reset_after_seconds"]) is { } after)
            return now.AddSeconds(after);
        return null;
    }

    private static int? ReadPeriodMs(JsonObject? window)
    {
        if (window is null) return null;
        var seconds = JsonUtil.Number(window["limit_window_seconds"])
            ?? JsonUtil.Number(window["limitWindowSeconds"])
            ?? JsonUtil.Number(window["window_seconds"]);
        if (seconds is null) return null;
        return (int)(seconds.Value * 1000);
    }

    public static (int Count, List<DateTimeOffset> Expiries)? ReadResetCredits(JsonObject body, AppHttpResponse? resetCredits)
    {
        JsonObject? source = null;
        if (resetCredits is { IsSuccess: true } && resetCredits.Json is { } dedicated &&
            JsonUtil.Number(dedicated["available_count"]) is not null)
        {
            source = dedicated;
        }
        source ??= JsonUtil.Object(body["rate_limit_reset_credits"]);
        if (source is null || JsonUtil.Number(source["available_count"]) is not { } count || count < 0)
            return null;
        var expiries = new List<DateTimeOffset>();
        if (JsonUtil.Array(source["credits"]) is { } credits)
        {
            foreach (var credit in credits.OfType<JsonObject>())
            {
                var status = JsonUtil.String(credit["status"]);
                if (status is not null && status != "available") continue;
                var expiry = Iso8601.Parse(JsonUtil.String(credit["expires_at"]))
                    ?? Iso8601.FromUnixSeconds(JsonUtil.Number(credit["expires_at"]));
                if (expiry is { } date) expiries.Add(date);
            }
            expiries.Sort();
        }
        return ((int)Math.Floor(count), expiries);
    }

    private static double? ReadCreditsRemaining(AppHttpResponse response, JsonObject body)
    {
        if (JsonUtil.Object(body["credits"]) is { } credits)
        {
            if (JsonUtil.Number(credits["balance"]) is { } balance) return balance;
            if (JsonUtil.Bool(credits["has_credits"]) == false) return 0;
        }
        return JsonUtil.Number(ToNode(response.Header("x-codex-credits-balance")));
    }

    public static string? FormatPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // Current Pro names (#1332), matching OpenAI's published plan names.
        return trimmed.ToLowerInvariant() switch
        {
            "prolite" => "Pro 100",
            "pro" => "Pro 200",
            "promax" => "Pro 500",
            "self_serve_business_prolite" => "Business Premium",
            _ => JsonUtil.TitleCase(trimmed)
        };
    }
}
