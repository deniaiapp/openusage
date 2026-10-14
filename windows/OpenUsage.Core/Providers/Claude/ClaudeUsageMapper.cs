using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public static class ClaudeUsageMapper
{
    public const string RateLimitResetsLabel = "Rate Limit Resets";

    public static (string? Plan, List<MetricLine> Lines, string? Warning) Map(JsonObject body, ClaudeOAuth credentials)
    {
        var lines = new List<MetricLine>();
        AppendWindow(body["five_hour"], "Session", MetricPeriod.SessionMs, lines);
        AppendWindow(body["seven_day"], "Weekly", MetricPeriod.WeekMs, lines);
        AppendWindow(body["seven_day_sonnet"], "Sonnet", MetricPeriod.WeekMs, lines);
        AppendScopedWeekly(body["limits"], "Fable", "Fable", lines);
        AppendExtra(body["extra_usage"], lines);
        return (FormatPlan(credentials.SubscriptionType, credentials.RateLimitTier), lines, null);
    }

    public static string MissingProfileScopeWarning { get; } =
        "Re-login for live usage. Run `claude` and sign in again to restore session and weekly limits.";

    public static string RateLimitedWarning { get; } =
        "Updates blocked by Anthropic. Be patient — manual refreshes will make it worse.";

    public static MetricLine RateLimitedNote { get; } =
        MetricLine.Text("Note", "Live usage rate limited - data may be stale");

    /// The Rate Limit Resets row from the reset-grants request (`?cedar_ember=1&skip_spend=1`); `null`
    /// when the account is outside the program (no `cedar_ember` block).
    public static MetricLine? MapResetGrantsResponse(AppHttpResponse response, DateTimeOffset now)
    {
        AuthRetry.RequireSuccess(response,
            new InvalidOperationException("Session expired. Run `claude` to log in again."),
            status => new InvalidOperationException(ProviderErrors.RequestFailed(status)));
        if (response.Json is not { } body)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return ResetGrantsLine(body["cedar_ember"], now);
    }

    /// Anthropic's one-off usage-limit reset grants, shown read-only like Codex's reset credits: the row
    /// reads "N available" and each remaining reset's deadline (`ends_at`) rides along in `ExpiriesAt`.
    /// A grant with several resets left contributes one expiry per reset; grants past their deadline or
    /// with none left are skipped. An ineligible account reads "0 available"; a missing block emits no row.
    public static MetricLine? ResetGrantsLine(JsonNode? value, DateTimeOffset now)
    {
        if (JsonUtil.Object(value) is not { } obj) return null;
        var count = 0;
        var expiries = new List<DateTimeOffset>();
        if (JsonUtil.Bool(obj["eligible"]) == true && JsonUtil.Array(obj["grants"]) is { } grants)
        {
            foreach (var grant in grants.OfType<JsonObject>())
            {
                if (JsonUtil.Number(grant["resets_left"]) is not { } left || left < 1) continue;
                var resets = (int)Math.Floor(left);
                var endsAt = ResetDate(grant["ends_at"]);
                if (endsAt is { } end && end <= now) continue;
                count += resets;
                if (endsAt is { } deadline) expiries.AddRange(Enumerable.Repeat(deadline, resets));
            }
        }
        expiries.Sort();
        return MetricLine.Values(RateLimitResetsLabel,
            [new MetricValue(count, MetricKind.Count, "available")],
            expiriesAt: expiries);
    }

    /// A Rate Limit Resets line held between checks, minus every reset whose deadline has passed. Other
    /// lines pass through unchanged.
    public static MetricLine DroppingElapsedResetGrants(MetricLine line, DateTimeOffset now)
    {
        if (line is not MetricLine.ValuesLine values || values.ExpiriesAt is not { } expiries
            || !expiries.Any(e => e <= now))
            return line;
        var elapsed = expiries.Count(e => e <= now);
        var items = values.Items
            .Select(v => v.Kind == MetricKind.Count ? v with { Number = Math.Max(0, v.Number - elapsed) } : v)
            .ToList();
        return values with { Items = items, ExpiriesAt = expiries.Where(e => e > now).ToList() };
    }

    private static void AppendWindow(JsonNode? value, string label, long periodMs, List<MetricLine> lines)
    {
        if (JsonUtil.Object(value) is not { } obj) return;
        if (JsonUtil.Number(obj["utilization"]) is not { } used) return;
        lines.Add(MetricLine.Progress(label, used, 100, ProgressFormat.Percent, ResetDate(obj["resets_at"]), periodMs));
    }

    private static void AppendScopedWeekly(JsonNode? limits, string modelName, string label, List<MetricLine> lines)
    {
        if (JsonUtil.Array(limits) is not { } array) return;
        foreach (var entry in array.OfType<JsonObject>())
        {
            if (JsonUtil.String(entry["kind"]) != "weekly_scoped") continue;
            var model = JsonUtil.Object(JsonUtil.Object(entry["scope"])?["model"]);
            if (JsonUtil.String(model?["display_name"]) != modelName) continue;
            if (JsonUtil.Number(entry["percent"]) is not { } used) continue;
            lines.Add(MetricLine.Progress(label, used, 100, ProgressFormat.Percent, ResetDate(entry["resets_at"]), MetricPeriod.WeekMs));
            return;
        }
    }

    private static void AppendExtra(JsonNode? value, List<MetricLine> lines)
    {
        if (JsonUtil.Object(value) is not { } obj) return;
        if (JsonUtil.Bool(obj["is_enabled"]) != true) return;
        if (JsonUtil.Number(obj["used_credits"]) is not { } usedCents) return;
        var used = JsonUtil.CentsToDollars(usedCents);
        if (JsonUtil.Number(obj["monthly_limit"]) is { } limitCents && limitCents > 0)
            lines.Add(MetricLine.Progress("Extra usage spent", used, JsonUtil.CentsToDollars(limitCents), ProgressFormat.Dollars));
        else if (used > 0)
            lines.Add(MetricLine.Values("Extra usage spent", [new MetricValue(used, MetricKind.Dollars)]));
    }

    private static DateTimeOffset? ResetDate(JsonNode? value)
    {
        if (JsonUtil.String(value) is { } text) return Iso8601.Parse(text);
        return Iso8601.FromUnix(JsonUtil.Number(value));
    }

    public static string? FormatPlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType)) return null;
        var baseName = JsonUtil.TitleCase(subscriptionType);
        if (rateLimitTier is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(rateLimitTier, @"\d+x");
            if (match.Success) return $"{baseName} {match.Value}";
        }
        return baseName;
    }

    /// Plan label from Anthropic's live `/api/oauth/profile`, formatted like the stored one. Claude Code
    /// stamps the plan into the login at sign-in and never updates it, so the profile is what reflects an
    /// upgrade. `organization_type` arrives as `claude_max` / `claude_pro`; the prefix is dropped. Fields
    /// the profile omits fall back to the stored login. `null` when the profile has no organization.
    public static string? FormatLivePlan(JsonObject profile, ClaudeOAuth credentials)
    {
        if (JsonUtil.Object(profile["organization"]) is not { } organization) return null;
        var type = JsonUtil.String(organization["organization_type"]);
        if (type is not null && type.StartsWith("claude_", StringComparison.Ordinal)) type = type["claude_".Length..];
        return FormatPlan(
            type ?? credentials.SubscriptionType,
            JsonUtil.String(organization["rate_limit_tier"]) ?? credentials.RateLimitTier);
    }
}
