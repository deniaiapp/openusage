using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Pricing;

public sealed class ModelRates
{
    public double InputPerMillion { get; init; }
    public double OutputPerMillion { get; init; }
    public double CacheWritePerMillion { get; init; }
    public double CacheReadPerMillion { get; init; }
    public double? InputAbove200kPerMillion { get; init; }
    public double? OutputAbove200kPerMillion { get; init; }
    public double? CacheWriteAbove200kPerMillion { get; init; }
    public double? CacheReadAbove200kPerMillion { get; init; }
    public bool CacheReadIsExplicit { get; init; } = true;
    public int LongContextThresholdTokens { get; init; } = 200_000;
    public double FastMultiplier { get; init; } = 1;

    public ModelRates Scaled(double factor) => new()
    {
        InputPerMillion = InputPerMillion * factor,
        OutputPerMillion = OutputPerMillion * factor,
        CacheWritePerMillion = CacheWritePerMillion * factor,
        CacheReadPerMillion = CacheReadPerMillion * factor,
        InputAbove200kPerMillion = InputAbove200kPerMillion * factor,
        OutputAbove200kPerMillion = OutputAbove200kPerMillion * factor,
        CacheWriteAbove200kPerMillion = CacheWriteAbove200kPerMillion * factor,
        CacheReadAbove200kPerMillion = CacheReadAbove200kPerMillion * factor,
        CacheReadIsExplicit = CacheReadIsExplicit,
        LongContextThresholdTokens = LongContextThresholdTokens,
        FastMultiplier = 1
    };

    public double CostDollars(TokenBreakdown tokens, bool applyLongContext = true)
    {
        var multiplier = tokens.IsFast ? FastMultiplier : 1;
        var longContext = applyLongContext && tokens.PromptTokens > LongContextThresholdTokens;
        double Pick(double baseRate, double? longRate) => longContext && longRate is { } value ? value : baseRate;
        var input = tokens.Input / 1_000_000.0 * Pick(InputPerMillion, InputAbove200kPerMillion);
        var cacheWrite = tokens.CacheWrite5m / 1_000_000.0 * Pick(CacheWritePerMillion, CacheWriteAbove200kPerMillion);
        var cacheWrite1h = tokens.CacheWrite1h / 1_000_000.0 * Pick(CacheWritePerMillion, CacheWriteAbove200kPerMillion) * 2;
        var cacheRead = tokens.CacheRead / 1_000_000.0 * Pick(CacheReadPerMillion, CacheReadAbove200kPerMillion);
        var output = tokens.Output / 1_000_000.0 * Pick(OutputPerMillion, OutputAbove200kPerMillion);
        return (input + cacheWrite + cacheWrite1h + cacheRead + output) * multiplier;
    }
}

public readonly record struct TokenBreakdown(
    int Input = 0,
    int CacheWrite5m = 0,
    int CacheWrite1h = 0,
    int CacheRead = 0,
    int Output = 0,
    bool IsFast = false)
{
    public int PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;
    public int TotalTokens => PromptTokens + Output;
}

public sealed class ModelPricing
{
    private readonly Dictionary<string, ModelRates> _supplement;
    private readonly Dictionary<string, ModelRates> _primary;
    private readonly Dictionary<string, ModelRates> _secondary;
    private readonly Dictionary<string, double> _fastMultipliers;
    private readonly List<(Regex Pattern, string Canonical)> _aliases;
    private readonly Dictionary<string, ModelRates?> _memo = new(StringComparer.Ordinal);

    public static ModelPricing Shared { get; } = Load();

    public ModelPricing(
        Dictionary<string, ModelRates> supplement,
        Dictionary<string, ModelRates> primary,
        Dictionary<string, ModelRates> secondary,
        Dictionary<string, double> fastMultipliers,
        List<(Regex Pattern, string Canonical)> aliases)
    {
        _supplement = supplement;
        _primary = primary;
        _secondary = secondary;
        _fastMultipliers = fastMultipliers;
        _aliases = aliases;
    }

    public static ModelPricing Load()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Resources");
            var supplementJson = File.ReadAllText(Path.Combine(dir, "pricing_supplement.json"));
            var litellm = File.ReadAllBytes(Path.Combine(dir, "pricing_litellm_snapshot.json"));
            var modelsDev = File.ReadAllBytes(Path.Combine(dir, "pricing_models_dev_snapshot.json"));
            return FromJson(supplementJson, litellm, modelsDev);
        }
        catch (Exception ex)
        {
            AppLog.Error("pricing", "could not load bundled pricing", ex);
            return new ModelPricing([], [], [], [], []);
        }
    }

    public static ModelPricing FromJson(string supplementJson, byte[] litellm, byte[] modelsDev)
    {
        var supplement = JsonSerializer.Deserialize<SupplementFile>(supplementJson, JsonOptions()) ?? new();
        var pricing = new Dictionary<string, ModelRates>(StringComparer.Ordinal);
        foreach (var (key, entry) in supplement.Pricing ?? [])
        {
            var input = entry.InputPerMillion ?? 0;
            pricing[key] = new ModelRates
            {
                InputPerMillion = input,
                OutputPerMillion = entry.OutputPerMillion ?? 0,
                CacheWritePerMillion = entry.CacheWritePerMillion ?? input,
                CacheReadPerMillion = entry.CacheReadPerMillion ?? input * 0.1,
                CacheReadIsExplicit = entry.CacheReadPerMillion is not null
            };
        }
        var aliases = new List<(Regex, string)>();
        foreach (var rule in supplement.AliasRules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern) || string.IsNullOrWhiteSpace(rule.Canonical)) continue;
            try { aliases.Add((new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), rule.Canonical)); }
            catch (Exception ex) { AppLog.Warn("pricing", $"bad alias rule {rule.Pattern}: {ex.Message}"); }
        }
        return new ModelPricing(
            pricing,
            CompactCatalog(litellm),
            CompactCatalog(modelsDev),
            supplement.FastMultipliers ?? [],
            aliases);
    }

    public string CanonicalName(string model)
    {
        foreach (var (pattern, canonical) in _aliases)
        {
            if (pattern.IsMatch(model)) return canonical;
        }
        return model;
    }

    public ModelRates? Resolve(string model)
    {
        lock (_memo)
        {
            if (_memo.TryGetValue(model, out var cached)) return cached;
        }
        var resolved = ResolveUncached(model);
        lock (_memo) _memo[model] = resolved;
        return resolved;
    }

    /// <summary>
    /// Pass <paramref name="applyLongContext"/> = false for rows that aggregate many requests
    /// (Cursor's per-day usage history): their summed size must not trigger a long-context surcharge.
    /// </summary>
    public double? EstimatedCost(string model, TokenBreakdown tokens, bool applyLongContext = true)
    {
        var rates = Resolve(model);
        return rates?.CostDollars(tokens, applyLongContext);
    }

    private ModelRates? ResolveUncached(string model)
    {
        var canonical = CanonicalName(model);
        if (_supplement.TryGetValue(canonical, out var supplement)) return ApplyFast(canonical, supplement);
        if (_primary.TryGetValue(canonical, out var primary)) return ApplyFast(canonical, primary);
        if (canonical.EndsWith("-fast", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = canonical[..^5];
            var baseRates = ResolveUncached(baseName);
            if (baseRates is not null)
            {
                var factor = _fastMultipliers.TryGetValue(baseName, out var mul) ? mul : baseRates.FastMultiplier;
                if (factor > 0 && factor != 1) return baseRates.Scaled(factor);
            }
        }
        if (_secondary.TryGetValue(canonical, out var secondary)) return ApplyFast(canonical, secondary);
        if (!string.Equals(canonical, model, StringComparison.Ordinal) && _primary.TryGetValue(model, out var raw))
            return ApplyFast(model, raw);
        return null;
    }

    private ModelRates ApplyFast(string key, ModelRates rates)
    {
        if (_fastMultipliers.TryGetValue(key, out var mul) && mul > 0)
            return new ModelRates
            {
                InputPerMillion = rates.InputPerMillion,
                OutputPerMillion = rates.OutputPerMillion,
                CacheWritePerMillion = rates.CacheWritePerMillion,
                CacheReadPerMillion = rates.CacheReadPerMillion,
                InputAbove200kPerMillion = rates.InputAbove200kPerMillion,
                OutputAbove200kPerMillion = rates.OutputAbove200kPerMillion,
                CacheWriteAbove200kPerMillion = rates.CacheWriteAbove200kPerMillion,
                CacheReadAbove200kPerMillion = rates.CacheReadAbove200kPerMillion,
                CacheReadIsExplicit = rates.CacheReadIsExplicit,
                LongContextThresholdTokens = rates.LongContextThresholdTokens,
                FastMultiplier = mul
            };
        return rates;
    }

    private static Dictionary<string, ModelRates> CompactCatalog(byte[] data)
    {
        var file = JsonSerializer.Deserialize<CompactFile>(data, JsonOptions()) ?? new();
        var entries = new Dictionary<string, ModelRates>(StringComparer.Ordinal);
        foreach (var (key, model) in file.Models ?? [])
        {
            entries[key] = new ModelRates
            {
                InputPerMillion = model.I,
                OutputPerMillion = model.O,
                CacheWritePerMillion = model.Cw,
                CacheReadPerMillion = model.Cr,
                InputAbove200kPerMillion = model.Ia,
                OutputAbove200kPerMillion = model.Oa,
                CacheWriteAbove200kPerMillion = model.Cwa,
                CacheReadAbove200kPerMillion = model.Cra,
                CacheReadIsExplicit = model.Cre ?? true,
                FastMultiplier = model.Fast ?? 1
            };
        }
        return entries;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class SupplementFile
    {
        [JsonPropertyName("pricing")] public Dictionary<string, SupplementEntry>? Pricing { get; set; }
        [JsonPropertyName("fast_multipliers")] public Dictionary<string, double>? FastMultipliers { get; set; }
        [JsonPropertyName("alias_rules")] public List<AliasRule>? AliasRules { get; set; }
    }

    private sealed class SupplementEntry
    {
        [JsonPropertyName("input_per_million")] public double? InputPerMillion { get; set; }
        [JsonPropertyName("output_per_million")] public double? OutputPerMillion { get; set; }
        [JsonPropertyName("cache_write_per_million")] public double? CacheWritePerMillion { get; set; }
        [JsonPropertyName("cache_read_per_million")] public double? CacheReadPerMillion { get; set; }
    }

    private sealed class AliasRule
    {
        [JsonPropertyName("pattern")] public string? Pattern { get; set; }
        [JsonPropertyName("canonical")] public string? Canonical { get; set; }
    }

    private sealed class CompactFile
    {
        [JsonPropertyName("models")] public Dictionary<string, CompactModel>? Models { get; set; }
    }

    private sealed class CompactModel
    {
        [JsonPropertyName("i")] public double I { get; set; }
        [JsonPropertyName("o")] public double O { get; set; }
        [JsonPropertyName("cw")] public double Cw { get; set; }
        [JsonPropertyName("cr")] public double Cr { get; set; }
        [JsonPropertyName("ia")] public double? Ia { get; set; }
        [JsonPropertyName("oa")] public double? Oa { get; set; }
        [JsonPropertyName("cwa")] public double? Cwa { get; set; }
        [JsonPropertyName("cra")] public double? Cra { get; set; }
        [JsonPropertyName("cre")] public bool? Cre { get; set; }
        [JsonPropertyName("fast")] public double? Fast { get; set; }
    }
}
