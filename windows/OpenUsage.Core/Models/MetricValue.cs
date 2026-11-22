namespace OpenUsage.Core.Models;

public sealed record MetricValue(double Number, MetricKind Kind, string? Label = null, string? CurrencyCode = null);

public abstract record ValueSelection
{
    public static readonly ValueSelection All = new AllSelection();

    public static ValueSelection Kind(MetricKind kind) => new KindSelection(kind);

    public abstract IReadOnlyList<MetricValue> Apply(IReadOnlyList<MetricValue> values);

    private sealed record AllSelection : ValueSelection
    {
        public override IReadOnlyList<MetricValue> Apply(IReadOnlyList<MetricValue> values) => values;
    }

    private sealed record KindSelection(MetricKind Only) : ValueSelection
    {
        public override IReadOnlyList<MetricValue> Apply(IReadOnlyList<MetricValue> values) =>
            values.Where(v => v.Kind == Only).ToList();
    }
}

public sealed record ProgressFormat
{
    public enum Kind { Percent, Dollars, Count }

    public Kind FormatKind { get; }
    public string? Suffix { get; }
    public string? CurrencyCode { get; }

    private ProgressFormat(Kind formatKind, string? suffix, string? currencyCode = null)
    {
        FormatKind = formatKind;
        Suffix = suffix;
        CurrencyCode = currencyCode;
    }

    public static ProgressFormat Percent { get; } = new(Kind.Percent, null);
    public static ProgressFormat Dollars { get; } = new(Kind.Dollars, null);
    public static ProgressFormat Currency(string? code) => code is null or "USD" ? Dollars : new(Kind.Dollars, null, code);
    public static ProgressFormat Count(string suffix) => new(Kind.Count, suffix);

    public MetricKind MetricKind => FormatKind switch
    {
        Kind.Percent => Models.MetricKind.Percent,
        Kind.Dollars => Models.MetricKind.Dollars,
        _ => Models.MetricKind.Count
    };
}
