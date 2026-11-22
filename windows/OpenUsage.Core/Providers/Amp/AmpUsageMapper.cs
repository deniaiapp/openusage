using System.Globalization;
using System.Text.RegularExpressions;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Amp;

internal static partial class AmpUsageMapper
{
    public static (string? Plan, IReadOnlyList<MetricLine> Lines) Map(AppHttpResponse response)
    {
        var result = JsonUtil.Object(response.Json?["result"]);
        var text = JsonUtil.String(result?["displayText"])?.Replace("**", "", StringComparison.Ordinal)
            ?? throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        string? plan = null;
        var lines = new List<MetricLine>();

        var tier = Tier().Match(text);
        if (tier.Success && Amount(tier, "left") is { } left && Amount(tier, "limit") is > 0 and var limit)
        {
            plan = tier.Groups["plan"].Value;
            var rest = tier.Groups["rest"].Value;
            var (reset, duration) = BillingPeriod(rest);
            lines.Add(MetricLine.Progress("Agent Usage", Math.Round(limit - left, 2), limit, ProgressFormat.Dollars, reset, duration));
            var orb = Orb().Match(rest);
            if (orb.Success && Amount(orb, "left") is { } orbLeft && Amount(orb, "limit") is > 0 and var orbLimit)
                lines.Add(MetricLine.Progress("Orb Hours", Math.Round(orbLimit - orbLeft, 1), orbLimit, ProgressFormat.Count("hours"), reset, duration));
        }

        var freeMoney = FreeMoney().Match(text);
        var freePercent = FreePercent().Match(text);
        if (freeMoney.Success && Amount(freeMoney, "left") is { } freeLeft && Amount(freeMoney, "limit") is > 0 and var freeLimit)
            AddFree((freeLimit - freeLeft) / freeLimit * 100);
        else if (freePercent.Success && Amount(freePercent, "left") is { } percent)
            AddFree(100 - percent);

        var credits = Credits().Match(text);
        if (credits.Success && Amount(credits, "left") is { } balance)
            lines.Add(MetricLine.Values("Individual Credits", [new(balance, MetricKind.Dollars)]));

        if (lines.Count == 0) throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        return (plan, lines);

        void AddFree(double used)
        {
            plan ??= "Free";
            lines.Add(MetricLine.Progress("Amp Free", used, 100, ProgressFormat.Percent));
        }
    }

    private static double? Amount(Match match, string name) =>
        double.TryParse(match.Groups[name].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var amount) && double.IsFinite(amount) ? amount : null;

    private static (DateTimeOffset? Reset, long? Duration) BillingPeriod(string text)
    {
        var dates = Period().Match(text);
        if (dates.Success && DateOnly.TryParseExact(dates.Groups["start"].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
            DateOnly.TryParseExact(dates.Groups["end"].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && end > start)
        {
            return (new(end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), (end.DayNumber - start.DayNumber) * MetricPeriod.DayMs);
        }
        return (null, null);
    }

    [GeneratedRegex(@"Amp (?<plan>[^\r\n]+?) Tier: agent usage \$(?<left>[0-9.,]+) of \$(?<limit>[0-9.,]+) remaining(?<rest>[^\r\n]*)")]
    private static partial Regex Tier();
    [GeneratedRegex(@"orb usage (?<left>[0-9.,]+)h of (?<limit>[0-9.,]+)h")]
    private static partial Regex Orb();
    [GeneratedRegex(@"period (?<start>\d{4}-\d{2}-\d{2}) to (?<end>\d{4}-\d{2}-\d{2})")]
    private static partial Regex Period();
    [GeneratedRegex(@"Amp Free: \$(?<left>[0-9.,]+)\s*/\s*\$(?<limit>[0-9.,]+) remaining")]
    private static partial Regex FreeMoney();
    [GeneratedRegex(@"Amp Free: (?<left>[0-9.]+)% remaining")]
    private static partial Regex FreePercent();
    [GeneratedRegex(@"Individual credits: \$(?<left>[0-9.,]+) remaining")]
    private static partial Regex Credits();
}
