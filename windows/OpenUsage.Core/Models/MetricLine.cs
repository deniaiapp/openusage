namespace OpenUsage.Core.Models;

public abstract record MetricLine(string Label)
{
    public string? Title { get; init; }
    public const string ErrorBadgeLabel = "Error";
    public static readonly MetricLine NoUsageData = new BadgeLine("Status", "No usage data", "#A3A3A3");

    public bool IsError => this is BadgeLine badge && badge.Label == ErrorBadgeLabel;

    public sealed record TextLine(string Label, string Value, string? ColorHex = null, string? Subtitle = null) : MetricLine(Label);

    public sealed record ValuesLine(
        string Label,
        IReadOnlyList<MetricValue> Items,
        string? ColorHex = null,
        IReadOnlyList<DateTimeOffset>? ExpiriesAt = null,
        IReadOnlyList<string>? UnknownModels = null) : MetricLine(Label);

    public sealed record ProgressLine(
        string Label,
        double Used,
        double Limit,
        ProgressFormat Format,
        DateTimeOffset? ResetsAt = null,
        long? PeriodDurationMs = null,
        string? ColorHex = null) : MetricLine(Label);

    public sealed record BadgeLine(string Label, string Message, string? ColorHex = null, string? Subtitle = null) : MetricLine(Label);

    public sealed record ChartLine(string Label, IReadOnlyList<MetricChartPoint> Points, string? Note = null) : MetricLine(Label);

    public static MetricLine Text(string label, string value, string? colorHex = null, string? subtitle = null) =>
        new TextLine(label, value, colorHex, subtitle);

    public static MetricLine Values(
        string label,
        IReadOnlyList<MetricValue> values,
        string? colorHex = null,
        IReadOnlyList<DateTimeOffset>? expiriesAt = null,
        IReadOnlyList<string>? unknownModels = null) =>
        new ValuesLine(label, values, colorHex, expiriesAt, unknownModels);

    public static MetricLine Progress(
        string label,
        double used,
        double limit,
        ProgressFormat format,
        DateTimeOffset? resetsAt = null,
        long? periodDurationMs = null,
        string? colorHex = null) =>
        new ProgressLine(label, used, limit, format, resetsAt, periodDurationMs, colorHex);

    public static MetricLine Badge(string label, string text, string? colorHex = null, string? subtitle = null) =>
        new BadgeLine(label, text, colorHex, subtitle);

    public static MetricLine Chart(string label, IReadOnlyList<MetricChartPoint> points, string? note = null) =>
        new ChartLine(label, points, note);

    public static void AppendNoDataIfNeeded(List<MetricLine> lines)
    {
        if (lines.Count == 0) lines.Add(NoUsageData);
    }
}

public sealed record MetricChartPoint(double Value, string Label, string? ValueLabel = null);

public sealed record ProviderLink(string Label, string Url);

public sealed record Provider(string Id, string DisplayName, string IconId, IReadOnlyList<ProviderLink>? Links = null)
{
    public IReadOnlyList<ProviderLink> VisibleLinks =>
        (Links ?? []).Where(link =>
        {
            var label = link.Label.Trim();
            var url = link.Url.Trim();
            return label.Length > 0 && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        }).ToList();
}
