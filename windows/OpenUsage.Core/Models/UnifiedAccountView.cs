namespace OpenUsage.Core.Models;

public sealed record UnifiedAccountView(
    Provider Provider,
    IReadOnlyList<Provider> Accounts,
    string? Plan,
    IReadOnlyList<UnifiedAccountMetric> Metrics);

public sealed record UnifiedAccountMetric(
    string Key,
    WidgetDescriptor Descriptor,
    WidgetData Data,
    bool OnDemand,
    IReadOnlyList<string> CopyIds);
