using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

/// <summary>
/// Local Codex spend from the CLI's session rollouts. Parsing lives in <see cref="CodexLogParser"/>,
/// growing files are read incrementally through <see cref="CodexScanCache"/>, and Codex's request
/// pricing rules in <see cref="CodexUsagePricing"/>.
/// </summary>
public static class CodexLogUsageScanner
{
    public static LogUsageScan? Scan(int daysBack = 30, DateTimeOffset? now = null, ModelPricing? pricing = null) =>
        Scan(Homes(), CodexScanCache.Shared, daysBack, now, pricing);

    public static LogUsageScan? Scan(
        IEnumerable<string> homes, CodexScanCache cache, int daysBack = 30,
        DateTimeOffset? now = null, ModelPricing? pricing = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var since = DailyUsageAccumulator.Since(daysBack, current);
        var price = pricing ?? ModelPricing.Shared;
        var files = homes
            .SelectMany(home => new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
            .SelectMany(dir => JsonlScan.FilesUnder(dir, since: since))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var events = cache.Events(files);
        if (files.Count == 0) return null;
        return Aggregate(events, since, price);
    }

    /// <summary>
    /// One turn's token usage, normalized from a <c>token_count</c> line (deltas already applied).
    /// <see cref="IsFast"/> and <see cref="IsUltrafast"/> record the service tier when the turn ran.
    /// </summary>
    public readonly record struct Event(
        DateTimeOffset Timestamp,
        string Model,
        string? PricingModel,
        long Input,
        long Cached,
        long Output,
        long Reasoning,
        long Total,
        bool IsFast,
        bool IsUltrafast = false);

    public static IEnumerable<Event> ParseFile(string path)
    {
        var parser = new CodexLogParser();
        foreach (var line in JsonlScan.ReadLines(path))
        {
            if (parser.Parse(line) is { } ev)
                yield return ev;
        }
    }

    private static IEnumerable<string> Homes()
    {
        var env = AppPaths.Env("CODEX_HOME");
        if (env is not null)
        {
            foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return AppPaths.Expand(part);
            yield break;
        }
        yield return AppPaths.Expand("~/.codex");
        yield return AppPaths.Expand("~/.config/codex");
    }

    public static LogUsageScan Aggregate(IEnumerable<Event> events, DateTimeOffset since, ModelPricing pricing)
    {
        var acc = new DailyUsageAccumulator();
        var seen = new HashSet<(DateTimeOffset, string, long, long, long, long)>();
        foreach (var ev in events.Where(e => e.Timestamp >= since))
        {
            if (!seen.Add((ev.Timestamp, ev.Model, ev.Input, ev.Cached, ev.Output, ev.Reasoning))) continue;
            var model = ev.Model.Trim();
            if (model.Length == 0) continue;
            var day = DailyUsageAccumulator.DayKey(ev.Timestamp);
            // Auto-review prices by date (dated model, then free); other slugs may carry a pricing model.
            var pricingModel = model == CodexUsagePricing.AutoReviewModel ? model : ev.PricingModel ?? model;
            var resolution = CodexUsagePricing.ResolveRates(pricing, pricingModel, ev.Timestamp);
            if (resolution.Rates is not { } rates)
            {
                if (ev.Total > 0) acc.AddUnknown(day, model);
                continue;
            }
            var fastTier = resolution.IsFastAlias ? resolution.HasBaseRates : ev.IsFast;
            var ultrafastTier = ev.IsUltrafast && (!resolution.IsFastAlias || resolution.HasBaseRates);
            // Rollouts count cached tokens inside input; the estimator takes disjoint buckets.
            var tokens = new TokenBreakdown(
                Input: (int)Math.Clamp(ev.Input - ev.Cached, 0, int.MaxValue),
                CacheRead: (int)Math.Clamp(ev.Cached, 0, int.MaxValue),
                Output: (int)Math.Clamp(ev.Output, 0, int.MaxValue));
            var cost = CodexUsagePricing.Cost(rates, tokens, resolution.RateModel, fastTier, ultrafastTier);
            acc.Add(day, ev.Total > 0 ? ev.Total : tokens.TotalTokens, cost);
        }
        return acc.Build();
    }
}
