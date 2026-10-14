using OpenUsage.Core.Pricing;

namespace OpenUsage.Core.Providers.Codex;

/// <summary>
/// Codex-specific request pricing. The shared catalogs provide base model rates; Codex layers its own
/// long-context, prompt-cache, priority (Fast) and Ultrafast rules on top, plus the dated
/// <c>codex-auto-review</c> estimates that end when auto-review became free.
/// </summary>
public static class CodexUsagePricing
{
    public const string AutoReviewModel = "codex-auto-review";

    /// <summary>Luna Reserve keeps its <c>gpt-reserve</c> slug in breakdowns while using Luna's rates.</summary>
    public const string ReserveModel = "gpt-reserve";
    public const string ReservePricingModel = "gpt-5.6-luna";

    /// <summary>
    /// Auto-review is free from October 6, 2026. The announcement gives a calendar date, not an
    /// activation time, so the cutoff is that UTC day's start; earlier requests keep their estimates.
    /// </summary>
    public static readonly DateTimeOffset AutoReviewFreeSince = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The model auto-review ran on before it became free, newest release first.</summary>
    private static readonly (DateTimeOffset ReleasedOn, string Model)[] AutoReviewFallbacks =
    [
        (Utc(2026, 7, 9), "gpt-5.6-luna"),
        (Utc(2026, 4, 23), "gpt-5.5"),
        (Utc(2026, 3, 5), "gpt-5.4"),
        (Utc(2026, 2, 5), "gpt-5.3-codex"),
        (Utc(2025, 12, 11), "gpt-5.2-codex"),
        (Utc(2025, 11, 13), "gpt-5.1-codex"),
        (Utc(2025, 9, 15), "gpt-5-codex"),
        (Utc(2025, 8, 7), "gpt-5")
    ];

    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    public static bool IsFreeAutoReview(string model, DateTimeOffset at) =>
        model == AutoReviewModel && at >= AutoReviewFreeSince;

    /// <summary>The model whose rates price this request: auto-review's dated model, otherwise itself.</summary>
    public static string PricingModel(string model, DateTimeOffset at)
    {
        if (model != AutoReviewModel || at >= AutoReviewFreeSince) return model;
        foreach (var (releasedOn, fallback) in AutoReviewFallbacks)
            if (at >= releasedOn) return fallback;
        return "gpt-5";
    }

    /// <summary>
    /// The catalog lookup Codex layers its rules on. <see cref="Rates"/> is null when the model cannot
    /// be priced. <see cref="RateModel"/> is the unscaled base model that selects Codex's rules.
    /// </summary>
    public readonly record struct RateResolution(ModelRates? Rates, string RateModel, bool IsFastAlias, bool HasBaseRates);

    /// <summary>
    /// Codex speed is a provider tier, not Cursor's <c>-fast</c> price variant: a fast alias resolves
    /// through its unscaled base rates so the Codex multiplier applies once. A fast-only model with no
    /// base entry keeps its already-scaled rate and gets no second multiplier.
    /// </summary>
    public static RateResolution ResolveRates(ModelPricing pricing, string model, DateTimeOffset at)
    {
        if (IsFreeAutoReview(model, at))
            return new RateResolution(new ModelRates(), model, false, true);
        var effective = PricingModel(model, at);
        var canonical = pricing.CanonicalName(effective);
        var isFastAlias = canonical.EndsWith("-fast", StringComparison.Ordinal);
        var rateModel = isFastAlias ? canonical[..^"-fast".Length] : canonical;
        var baseRates = pricing.Resolve(rateModel);
        return new RateResolution(baseRates ?? pricing.Resolve(effective), rateModel, isFastAlias, baseRates is not null);
    }

    /// <summary>Prices a request whose <paramref name="tokens"/> input excludes cached tokens.</summary>
    public static double Cost(ModelRates rates, TokenBreakdown tokens, string model, bool fastTier, bool ultrafastTier = false)
    {
        var baseModel = DatedBaseModel(model);
        var multiplier = ultrafastTier && UltrafastMultiplier(baseModel) is { } ultra
            ? ultra
            : PriorityMultiplier(baseModel, rates);
        var adjusted = Adjusted(rates, baseModel, multiplier);
        return adjusted.CostDollars(tokens with { IsFast = fastTier || ultrafastTier });
    }

    /// <summary>Ultrafast is 6x for GPT-6 Astra only; other models on Ultrafast use their Fast multiplier.</summary>
    public static double? UltrafastMultiplier(string baseModel) => baseModel == "gpt-6-astra" ? 6 : null;

    public static double PriorityMultiplier(string baseModel, ModelRates rates) => baseModel switch
    {
        "gpt-5.5" or "gpt-5.5-pro" => 2.5,
        "gpt-5.4" or "gpt-5.4-pro"
            or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna"
            or "gpt-6-astra" or "gpt-6.1-sol" or "gpt-6-sol" or "gpt-6-luna" => 2,
        _ => rates.FastMultiplier == 1 ? 2 : rates.FastMultiplier
    };

    /// <summary>Applies every model-derived Codex adjustment in one pass.</summary>
    private static ModelRates Adjusted(ModelRates rates, string baseModel, double fastMultiplier)
    {
        var longContext = LongContextRates(baseModel);
        var input = rates.InputPerMillion;
        var inputAbove = longContext?.Input ?? rates.InputAbove200kPerMillion;
        var cacheRead = rates.CacheReadPerMillion;
        var cacheReadAbove = longContext?.CacheRead ?? rates.CacheReadAbove200kPerMillion;
        // No published cache discount, or the catalog gave no explicit cache-read rate: cached input
        // is estimated at the full input rate.
        if (HasNoCacheDiscount(baseModel) || !rates.CacheReadIsExplicit)
        {
            cacheRead = input;
            cacheReadAbove = inputAbove;
        }
        return new ModelRates
        {
            InputPerMillion = input,
            OutputPerMillion = rates.OutputPerMillion,
            CacheWritePerMillion = rates.CacheWritePerMillion,
            CacheReadPerMillion = cacheRead,
            InputAbove200kPerMillion = inputAbove,
            OutputAbove200kPerMillion = longContext?.Output ?? rates.OutputAbove200kPerMillion,
            CacheWriteAbove200kPerMillion = rates.CacheWriteAbove200kPerMillion,
            CacheReadAbove200kPerMillion = cacheReadAbove,
            CacheReadIsExplicit = rates.CacheReadIsExplicit,
            LongContextThresholdTokens = longContext is null ? rates.LongContextThresholdTokens : 272_000,
            FastMultiplier = fastMultiplier
        };
    }

    private static bool HasNoCacheDiscount(string baseModel) => baseModel is "gpt-5.4-pro" or "gpt-5.5-pro";

    /// <summary>Per-million rates above 272k input tokens, applied to the whole request.</summary>
    private static (double Input, double Output, double CacheRead)? LongContextRates(string baseModel) => baseModel switch
    {
        "gpt-5.4" => (5, 22.5, 0.5),
        "gpt-5.4-pro" => (60, 270, 60),
        "gpt-5.5" => (10, 45, 1),
        "gpt-5.5-pro" => (60, 270, 60),
        "gpt-5.6-sol" => (8, 30, 0.8),
        "gpt-5.6-terra" => (4, 18, 0.4),
        "gpt-5.6-luna" => (0.4, 1.8, 0.04),
        // Above 272k: 2x input and cache, 1.5x output.
        "gpt-6-astra" => (20, 75, 2),
        "gpt-6.1-sol" => (4, 15, 0.2),
        "gpt-6-sol" => (4, 15, 0.4),
        "gpt-6-luna" => (0.2, 0.75, 0.02),
        _ => null
    };

    /// <summary>Strips a trailing <c>-YYYY-MM-DD</c> or <c>-YYYYMMDD</c> snapshot suffix.</summary>
    public static string DatedBaseModel(string model)
    {
        foreach (var pattern in new[] { "-dddd-dd-dd", "-dddddddd" })
        {
            if (model.Length <= pattern.Length) continue;
            var suffix = model.AsSpan(model.Length - pattern.Length);
            var matches = true;
            for (var i = 0; i < pattern.Length && matches; i++)
                matches = pattern[i] == 'd' ? char.IsAsciiDigit(suffix[i]) : suffix[i] == pattern[i];
            if (matches) return model[..^pattern.Length];
        }
        return model;
    }
}
