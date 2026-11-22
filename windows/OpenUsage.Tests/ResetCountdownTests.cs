using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class ResetCountdownTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 2, 5, "2d 5h")]
    [InlineData(0, 17, 0, "17d")]
    [InlineData(0, 29, 23, "29d 23h")]
    [InlineData(1, 0, 0, "1mo")]
    [InlineData(3, 17, 0, "3mo 17d")]
    [InlineData(12, 5, 0, "1y")]
    [InlineData(14, 0, 0, "1y 2mo")]
    public void FormatsCalendarUnitsWithoutZeroTails(int months, int days, int hours, string expected)
        => Assert.Equal(expected, Formatters.CalendarDuration(
            Start, Start.AddMonths(months).AddDays(days).AddHours(hours), TimeZoneInfo.Utc));

    [Fact]
    public void RoundsUpOneMinuteAtAMonthBoundary()
        => Assert.Equal("1mo", Formatters.CalendarDuration(
            Start, Start.AddMonths(1).AddSeconds(-1), TimeZoneInfo.Utc));

    [Fact]
    public void CountsEndOfMonthAsACalendarMonth()
    {
        var january = new DateTimeOffset(2027, 1, 31, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal("1mo", Formatters.CalendarDuration(january, january.AddMonths(1), TimeZoneInfo.Utc));
    }

    [Fact]
    public void KeepsTheSoonThresholdAndRoundsShortDurationsUp()
    {
        Assert.Equal("Resets soon", Formatters.ResetRelativeLabel(Start.AddMinutes(5), Start));
        Assert.Equal("Resets in 6m", Formatters.ResetRelativeLabel(Start.AddMinutes(5).AddSeconds(1), Start));
        Assert.Equal("2d", Formatters.CompactDuration(TimeSpan.FromDays(2)));
    }
}
