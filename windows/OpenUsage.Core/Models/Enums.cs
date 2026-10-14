namespace OpenUsage.Core.Models;

public enum MetricKind
{
    Percent,
    Dollars,
    Count
}

public enum WidgetDisplayMode
{
    Used,
    Remaining
}

public enum ResetDisplayMode
{
    Relative,
    Absolute
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum DensitySetting
{
    Regular,
    Compact
}

public static class MetricPeriod
{
    public const long SessionMs = 5L * 60 * 60 * 1000;
    public const long DayMs = 24L * 60 * 60 * 1000;
    public const long WeekMs = 7 * DayMs;
    public const long MonthMs = 30 * DayMs;
}

public static class WidgetDisplayModeText
{
    public static string Label(this WidgetDisplayMode mode) => mode == WidgetDisplayMode.Remaining ? "Left" : "Used";
}
