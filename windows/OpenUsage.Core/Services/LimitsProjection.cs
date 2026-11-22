using OpenUsage.Core.Models;

namespace OpenUsage.Core.Services;

internal sealed record LimitResource(
    string Kind, string Unit, double? Used = null, double? Available = null,
    double? Limit = null, double? Remaining = null, double? Utilization = null,
    DateTimeOffset? ResetsAt = null, double? WindowSeconds = null, DateTimeOffset[]? ExpiresAt = null);

internal static class LimitsProjection
{
    private sealed record Spec(string Key, string Unit, bool IsProgress = true,
        MetricKind? ValueKind = null, string? ValueLabel = null, bool IsBalance = false);

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["claude.session"] = new("session", "percent"),
        ["claude.weekly"] = new("weekly", "percent"),
        ["claude.sonnet"] = new("sonnet", "percent"),
        ["claude.fable"] = new("fable", "percent"),
        ["claude.extra"] = new("extraUsage", "usd", ValueKind: MetricKind.Dollars),
        ["claude.rateLimitResets"] = ResetSpec,
        ["codex.session"] = new("session", "percent"),
        ["codex.weekly"] = new("weekly", "percent"),
        ["codex.spark"] = new("spark", "percent"),
        ["codex.sparkWeekly"] = new("sparkWeekly", "percent"),
        ["codex.credits"] = new("credits", "credits", false, MetricKind.Count, "credits", true),
        ["codex.creditLimit"] = new("creditLimit", "credits"),
        ["codex.rateLimitResets"] = ResetSpec,
        ["cursor.usage"] = new("totalUsage", "percent"),
        ["cursor.auto"] = new("autoUsage", "percent"),
        ["cursor.api"] = new("apiUsage", "percent"),
        ["cursor.grokBot"] = new("grokBot", "percent"),
        ["cursor.onDemand"] = new("onDemand", "usd", ValueKind: MetricKind.Dollars),
        ["cursor.requests"] = new("requests", "requests"),
        ["cursor.credits"] = DollarBalance("credits"),
        ["amp.agent"] = new("agentUsage", "usd"),
        ["amp.orbHours"] = new("orbHours", "hours"),
        ["amp.free"] = new("free", "percent"),
        ["amp.credits"] = DollarBalance("credits"),
        ["antigravity.geminiPro"] = new("geminiSession", "percent"),
        ["antigravity.geminiWeekly"] = new("geminiWeekly", "percent"),
        ["antigravity.claude"] = new("nonGeminiSession", "percent"),
        ["antigravity.claudeWeekly"] = new("nonGeminiWeekly", "percent"),
        ["copilot.premium"] = new("premiumCredits", "credits", ValueKind: MetricKind.Count),
        ["copilot.extra"] = new("extraUsage", "count", false, MetricKind.Count),
        ["copilot.chat"] = new("chat", "percent"),
        ["copilot.completions"] = new("completions", "percent"),
        ["devin.daily"] = new("daily", "percent"),
        ["devin.weekly"] = new("weekly", "percent"),
        ["devin.extra"] = DollarBalance("extraUsageBalance"),
        ["grok.weekly"] = new("weekly", "percent"),
        ["muse.session"] = new("session", "percent"),
        ["muse.weekly"] = new("weekly", "percent"),
        ["ollama.session"] = new("session", "percent"),
        ["ollama.weekly"] = new("weekly", "percent"),
        ["ollama.monthly"] = new("monthly", "usd"),
        ["ollama.purchasedCredits"] = DollarBalance("purchasedCredits"),
        ["opencode.session"] = new("session", "percent"),
        ["opencode.weekly"] = new("weekly", "percent"),
        ["opencode.monthly"] = new("monthly", "percent"),
        ["openrouter.balance"] = DollarBalance("balance"),
        ["openrouter.keyLimit"] = new("keyLimit", "usd"),
        ["zai.session"] = new("session", "percent"),
        ["zai.weekly"] = new("weekly", "percent"),
        ["zai.webSearches"] = new("webSearches", "searches")
    };

    private static Spec ResetSpec => new("rateLimitResets", "resets", false, MetricKind.Count, "available", true);
    private static Spec DollarBalance(string key) => new(key, "usd", false, MetricKind.Dollars, IsBalance: true);

    internal static bool HasProvider(string providerId) =>
        Specs.Keys.Any(id => id.StartsWith(providerId + ".", StringComparison.Ordinal)) &&
        !providerId.Contains('.');

    public static Dictionary<string, LimitResource> Project(ProviderSnapshot snapshot, IReadOnlyList<WidgetDescriptor> descriptors)
    {
        var result = new Dictionary<string, LimitResource>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var scope = descriptor.Id.IndexOf('@');
            var separator = descriptor.Id.IndexOf('.');
            var metricId = scope < 0 || separator <= scope ? descriptor.Id : descriptor.Id[..scope] + descriptor.Id[separator..];
            if (!Specs.TryGetValue(metricId, out var spec)) continue;
            var kind = spec.IsBalance ? "balance" : "consumption";
            switch (snapshot.Line(descriptor.MetricLabel))
            {
                case MetricLine.ProgressLine meter when spec.IsProgress &&
                    double.IsFinite(meter.Used) && double.IsFinite(meter.Limit):
                    var used = Math.Max(0, meter.Used);
                    var limit = Math.Max(0, meter.Limit);
                    var unit = meter.Format.FormatKind switch
                    {
                        ProgressFormat.Kind.Percent => "percent",
                        ProgressFormat.Kind.Dollars => "usd",
                        _ => meter.Format.Suffix?.Trim() is { Length: > 0 } suffix ? suffix : spec.Unit
                    };
                    var ratio = limit > 0 ? used / limit : double.NaN;
                    result[spec.Key] = new(kind, unit, Used: used, Limit: limit,
                        Remaining: Math.Max(0, limit - used), Utilization: double.IsFinite(ratio) ? ratio : null,
                        ResetsAt: meter.ResetsAt, WindowSeconds: meter.PeriodDurationMs / 1000.0);
                    break;
                case MetricLine.ValuesLine values when spec.ValueKind is { } valueKind:
                    if (values.Items.FirstOrDefault(v => v.Kind == valueKind &&
                        (spec.ValueLabel is null || v.Label == spec.ValueLabel)) is { } value && double.IsFinite(value.Number))
                    {
                        result[spec.Key] = new(kind, spec.Unit,
                            Used: spec.IsBalance ? null : value.Number,
                            Available: spec.IsBalance ? value.Number : null,
                            ExpiresAt: values.ExpiriesAt is { Count: > 0 } expiries ? expiries.Order().ToArray() : null);
                    }
                    break;
            }
        }
        return result;
    }
}
