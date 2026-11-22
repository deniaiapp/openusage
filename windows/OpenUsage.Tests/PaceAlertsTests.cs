using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using Xunit;

namespace OpenUsage.Tests;

public sealed class PaceAlertsTests
{
    [Fact]
    public void WarnsOnceUntilPaceRecoversAndPrimesNewlyVisibleMetrics()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new PaceAlerts();
        var reset = now.AddHours(20);
        IReadOnlyList<PaceAlert> Read(double used) => tracker.Observe([Sample("a", used, reset)], now);

        Assert.Empty(Read(10));
        Assert.Equal(PaceAlertKind.BurningFast, Assert.Single(Read(18)).Kind);
        Assert.Empty(Read(16));
        Assert.Empty(Read(18));
        Assert.Empty(Read(14));
        Assert.Equal(PaceAlertKind.BurningFast, Assert.Single(Read(18)).Kind);
        Assert.Empty(tracker.Observe([], now));
        Assert.Empty(Read(18));
    }

    [Fact]
    public void WarnsOncePerLongNonMoneyWindowWhenMostOfItWillBeUnused()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new PaceAlerts();
        var reset = now.AddHours(25);
        var cand = Sample("week", 40, reset, days: 7);
        var day = Sample("day", 0, reset);
        var dollars = Sample("spend", 40, reset, days: 7, kind: MetricKind.Dollars);

        Assert.Empty(tracker.Observe([cand, day, dollars], now));
        var alert = Assert.Single(tracker.Observe([cand, day, dollars], now.AddHours(2)));
        Assert.Equal(new PaceAlert(PaceAlertKind.UseItOrLoseIt, "week"), alert);
        Assert.Empty(tracker.Observe([cand with { Data = cand.Data! with { Used = 52 } }], now.AddHours(3)));
        Assert.Empty(tracker.Observe([cand with { Data = null }], now.AddHours(3)));
        Assert.Empty(tracker.Observe([cand], now.AddHours(4)));

        var next = Sample("week", 10, reset.AddDays(7), days: 7);
        Assert.Empty(tracker.Observe([next], reset.AddDays(1)));
        Assert.Equal(PaceAlertKind.UseItOrLoseIt, Assert.Single(tracker.Observe([next], reset.AddDays(6))).Kind);
    }

    private static PaceAlertMetric Sample(string id, double used, DateTimeOffset reset, int days = 1, MetricKind kind = MetricKind.Percent) =>
        new(id, id, new WidgetData { Title = id, Kind = kind, Used = used, Limit = 100, ResetsAt = reset, PeriodDurationMs = days * MetricPeriod.DayMs });
}
