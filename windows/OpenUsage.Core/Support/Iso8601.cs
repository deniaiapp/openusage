using System.Globalization;

namespace OpenUsage.Core.Support;

public static class Iso8601
{
    public static DateTimeOffset? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return null;
    }

    public static DateTimeOffset? FromUnix(double? value)
    {
        if (value is not { } number || !double.IsFinite(number)) return null;
        var milliseconds = Math.Abs(number) < 1e10 ? number * 1000 : number;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    public static DateTimeOffset? FromUnixSeconds(double? value)
    {
        if (value is not { } number || !double.IsFinite(number)) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)number);
    }
}
