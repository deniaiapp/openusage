using OpenUsage.Core.Support;

namespace OpenUsage.Core.Models;

public sealed record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<MetricLine> Lines,
    DateTimeOffset RefreshedAt,
    string? Plan = null,
    string? Warning = null,
    string? ErrorCategory = null)
{
    internal const string OfflineCategory = "offline";

    public MetricLine? Line(string label) => Lines.FirstOrDefault(l => l.Label == label);

    public bool IsError => Lines.Any(l => l.IsError);

    public static ProviderSnapshot Make(
        Provider provider,
        string? plan,
        IReadOnlyList<MetricLine> lines,
        DateTimeOffset refreshedAt,
        string? warning = null) =>
        new(provider.Id, provider.DisplayName, lines, refreshedAt, plan, warning);

    public static ProviderSnapshot Error(Provider provider, Exception error) =>
        ProviderErrors.IsOffline(error)
            ? Error(provider, ProviderErrors.ConnectionFailed, OfflineCategory)
            : Error(provider, error.Message);

    public static ProviderSnapshot Error(Provider provider, string message, string? category = null) =>
        new(
            provider.Id,
            provider.DisplayName,
            [MetricLine.Badge(MetricLine.ErrorBadgeLabel, message, "#EF4444")],
            DateTimeOffset.Now,
            ErrorCategory: category ?? "other");
}
