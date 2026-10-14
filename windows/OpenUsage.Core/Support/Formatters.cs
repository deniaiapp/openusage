using System.Globalization;
using OpenUsage.Core.Models;

namespace OpenUsage.Core.Support;

public static class Formatters
{
    public const string Imminent = "soon";

    public static string Currency(double amount, int fractionDigits = 2)
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        return amount.ToString($"C{fractionDigits}", culture);
    }

    public static string MonthDayLabel(DateTimeOffset date) =>
        date.ToLocalTime().ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));

    public static string? DeadlineLabel(
        string prefix,
        DateTimeOffset date,
        ResetDisplayMode mode,
        DateTimeOffset? now = null)
    {
        var when = WhenLabel(date, mode, now);
        if (when is null) return null;
        if (when == Imminent) return $"{prefix} {when}";
        return mode == ResetDisplayMode.Relative ? $"{prefix} in {when}" : $"{prefix} {when}";
    }

    public static string? WhenLabel(DateTimeOffset date, ResetDisplayMode mode, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        if (mode == ResetDisplayMode.Relative)
        {
            var seconds = (date - current).TotalSeconds;
            if (seconds <= 5 * 60) return Imminent;
            return CompactDuration(TimeSpan.FromSeconds(seconds));
        }

        if ((date - current).TotalSeconds <= 0) return Imminent;
        var localDate = date.ToLocalTime().Date;
        var localNow = current.ToLocalTime().Date;
        var dayDiff = (localDate - localNow).Days;
        var time = date.ToLocalTime().ToString("t");
        if (dayDiff <= 0) return $"today at {time}";
        if (dayDiff == 1) return $"tomorrow at {time}";
        return $"{MonthDayLabel(date)} at {time}";
    }

    public static string? ResetRelativeLabel(DateTimeOffset resetsAt, DateTimeOffset? now = null) =>
        DeadlineLabel("Resets", resetsAt, ResetDisplayMode.Relative, now);

    public static string? ResetAbsoluteLabel(DateTimeOffset resetsAt, DateTimeOffset? now = null) =>
        DeadlineLabel("Resets", resetsAt, ResetDisplayMode.Absolute, now);

    public static string? CompactDuration(TimeSpan span)
    {
        if (span.TotalSeconds <= 0 || double.IsNaN(span.TotalSeconds)) return null;
        var totalMinutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;
        if (days > 0) return $"{days}d {hours}h";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }
}
