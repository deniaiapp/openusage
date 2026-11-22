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
            if (date - current <= TimeSpan.FromMinutes(5)) return Imminent;
            return CalendarDuration(current, date, TimeZoneInfo.Local);
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
        if (span <= TimeSpan.Zero) return null;
        return FormatMinutes((long)Math.Ceiling(span.TotalMinutes));
    }

    internal static string CalendarDuration(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var totalMinutes = (long)Math.Ceiling((end - start).TotalMinutes);
        var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var roundedEnd = new DateTimeOffset(Math.Min(start.UtcTicks + totalMinutes * TimeSpan.TicksPerMinute,
            DateTimeOffset.MaxValue.UtcTicks), TimeSpan.Zero);
        var localEnd = TimeZoneInfo.ConvertTime(roundedEnd, zone).DateTime;
        var months = (localEnd.Year - localStart.Year) * 12 + localEnd.Month - localStart.Month;
        if (months <= 0) return FormatMinutes(totalMinutes);

        long MonthBoundaryTicks(int count)
        {
            var boundary = localStart.AddMonths(count);
            return boundary.Ticks - zone.GetUtcOffset(boundary).Ticks;
        }

        var boundaryTicks = MonthBoundaryTicks(months);
        if (boundaryTicks > roundedEnd.UtcTicks)
        {
            if (--months == 0) return FormatMinutes(totalMinutes);
            boundaryTicks = MonthBoundaryTicks(months);
        }
        var years = months / 12;
        if (years > 0) return Pair(years, "y", months % 12, "mo");
        return Pair(months, "mo", (roundedEnd.UtcTicks - boundaryTicks) / TimeSpan.TicksPerDay, "d");
    }

    private static string FormatMinutes(long totalMinutes)
    {
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;
        if (days > 0) return Pair(days, "d", hours, "h");
        if (hours > 0) return Pair(hours, "h", minutes, "m");
        return $"{minutes}m";
    }

    private static string Pair(long major, string majorUnit, long minor, string minorUnit) =>
        minor > 0 ? $"{major}{majorUnit} {minor}{minorUnit}" : $"{major}{majorUnit}";
}
