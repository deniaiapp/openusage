namespace OpenUsage.Core.Models;

public sealed record WidgetDescriptor
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string MetricLabel { get; init; }
    public required WidgetData Sample { get; init; }
    public bool Pinnable { get; init; } = true;
    public bool IsSpendTile { get; init; }
    public bool ShowsCountLimit { get; init; }

    public string Title => Sample.Title;

    public static WidgetDescriptor Percent(
        string id,
        Provider provider,
        string title,
        string? metricLabel = null,
        SessionStartSignal? sessionStartSignal = null) =>
        Make(id, provider, metricLabel ?? title, new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = MetricKind.Percent,
            Limit = 100,
            SessionStartSignal = sessionStartSignal
        });

    public static WidgetDescriptor BoundedDollars(
        string id,
        Provider provider,
        string title,
        double limit,
        string? metricLabel = null,
        string? limitNoun = null,
        string? valueWord = null) =>
        Make(id, provider, metricLabel ?? title, new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = MetricKind.Dollars,
            Limit = limit,
            LimitNoun = limitNoun,
            UnboundedValueWord = valueWord
        });

    public static WidgetDescriptor BoundedCount(
        string id,
        Provider provider,
        string title,
        double limit,
        string suffix,
        long? periodDurationMs = null,
        string? metricLabel = null) =>
        Make(id, provider, metricLabel ?? title, new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = MetricKind.Count,
            Limit = limit,
            CountSuffix = suffix,
            PeriodDurationMs = periodDurationMs
        });

    public static WidgetDescriptor Values(
        string id,
        Provider provider,
        string title,
        string? metricLabel = null,
        ValueSelection? selection = null,
        string? valueWord = null,
        bool isUsagePeriod = false,
        string? traySuffix = null,
        bool showsResetExpiries = false)
    {
        var sel = selection ?? ValueSelection.All;
        var kind = MetricKind.Dollars;
        var sample = new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = kind,
            UnboundedValueWord = valueWord,
            Selection = sel,
            IsUsagePeriod = isUsagePeriod,
            TraySuffix = traySuffix,
            ShowsResetExpiries = showsResetExpiries
        };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor Combined(
        string id,
        Provider provider,
        string title,
        bool isUsagePeriod = false) =>
        Values(id, provider, title, isUsagePeriod: isUsagePeriod);

    public static IReadOnlyList<WidgetDescriptor> SpendTiles(Provider provider, string? valueTooltipNote = null) =>
        new[]
        {
            Combined($"{provider.Id}.today", provider, "Today", isUsagePeriod: true),
            Combined($"{provider.Id}.yesterday", provider, "Yesterday", isUsagePeriod: true),
            Combined($"{provider.Id}.last30", provider, "Last 30 Days", isUsagePeriod: true)
        }.Select(d => d with
        {
            IsSpendTile = true,
            Sample = d.Sample with { ValueTooltipNote = valueTooltipNote }
        }).ToList();

    public static WidgetDescriptor DollarBalance(
        string id,
        Provider provider,
        string title,
        string valueWord,
        string? metricLabel = null) =>
        Make(id, provider, metricLabel ?? title, new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = MetricKind.Dollars,
            UnboundedValueWord = valueWord
        });

    public static WidgetDescriptor Badge(string id, Provider provider, string title, string? metricLabel = null) =>
        Make(id, provider, metricLabel ?? title, new WidgetData
        {
            Title = title,
            IconId = provider.IconId,
            Kind = MetricKind.Count
        });

    public static WidgetDescriptor UsageTrend(Provider provider) =>
        Make($"{provider.Id}.trend", provider, "Usage Trend", new WidgetData
        {
            Title = "Usage Trend",
            IconId = provider.IconId,
            Kind = MetricKind.Count,
            IsChart = true
        }, pinnable: false);

    private static WidgetDescriptor Make(
        string id,
        Provider provider,
        string metricLabel,
        WidgetData sample,
        bool pinnable = true) => new()
    {
        Id = id,
        ProviderId = provider.Id,
        MetricLabel = metricLabel,
        Sample = sample,
        Pinnable = pinnable
    };
}

public enum SessionStartSignal
{
    ZeroUsage,
    MissingResetDate
}
