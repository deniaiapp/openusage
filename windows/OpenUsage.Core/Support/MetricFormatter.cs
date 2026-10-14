using System.Globalization;
using OpenUsage.Core.Models;

namespace OpenUsage.Core.Support;

public static class MetricFormatter
{
    public enum Style { Tray, Row, Full }

    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo("en-US");

    public static string Number(double value, MetricKind kind, Style style)
    {
        switch (kind)
        {
            case MetricKind.Percent:
                return $"{(int)Math.Round(JsonUtil.ClampPercent(value))}%";
            case MetricKind.Dollars:
                if (Math.Abs(value) >= 1000 && style != Style.Full)
                    return "$" + Compact(value);
                return style switch
                {
                    Style.Tray => "$" + value.ToString("0", Locale),
                    _ => Formatters.Currency(value, 2)
                };
            default:
                if (style != Style.Full && Math.Abs(value) >= 1000)
                    return Compact(value);
                return value.ToString("0.#", Locale);
        }
    }

    public static string String(MetricValue value, Style style)
    {
        var text = Number(value.Number, value.Kind, style);
        return string.IsNullOrEmpty(value.Label) ? text : $"{text} {value.Label}";
    }

    private static string Compact(double value)
    {
        var sign = value < 0 ? "-" : "";
        var abs = Math.Abs(value);
        string suffix;
        double scaled;
        if (abs >= 1_000_000_000)
        {
            scaled = abs / 1_000_000_000;
            suffix = "B";
        }
        else if (abs >= 1_000_000)
        {
            scaled = abs / 1_000_000;
            suffix = "M";
        }
        else
        {
            scaled = abs / 1_000;
            suffix = "K";
        }
        var formatted = scaled < 10
            ? scaled.ToString("0.#", Locale)
            : scaled.ToString("0.#", Locale);
        return sign + formatted + suffix;
    }
}
