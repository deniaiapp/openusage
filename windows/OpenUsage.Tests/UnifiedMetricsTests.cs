using OpenUsage.Core.Models;
using Xunit;

namespace OpenUsage.Tests;

public sealed class UnifiedMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsExistingOrderAndInsertsMissingMetricsAtTheNextSharedMetric()
    {
        Assert.Equal(["session", "weekly", "fable", "resets", "history"],
            UnifiedMetrics.MergeOrder([
                ["weekly", "fable", "resets"],
                ["session", "weekly", "resets", "history"],
                ["weekly", "session"]
            ]));
    }

    [Fact]
    public void AveragesOnlyReportedPercentsWithTheEarliestResetAndNoPace()
    {
        var first = Percent(20) with { ResetsAt = Now.AddHours(2) };
        var second = Percent(0) with { ResetsAt = Now.AddHours(1) };
        var pooled = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([
            first, Percent(100) with { HasData = false }, second
        ]));

        Assert.Equal(10, pooled.Used);
        Assert.Equal(100, pooled.Limit);
        Assert.Equal(second.ResetsAt, pooled.ResetsAt);
        Assert.Null(pooled.PeriodDurationMs);
        Assert.Null(pooled.SessionStartSignal);
        Assert.Null(pooled.PaceTick(pooled.GetMeterState(Now), Now));
        Assert.NotNull(first.PeriodDurationMs);
    }

    [Fact]
    public void CapsEveryContributionAndDoesNotRoundRemainingRoomToSpent()
    {
        var overspent = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([Percent(120), Percent(40)]));
        Assert.Equal(70, overspent.Used, 5);

        var almost = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([Percent(100), Percent(100), Percent(99)]));
        Assert.Equal(99, almost.Used);
        Assert.False(almost.IsSpent);
        Assert.True(Assert.IsType<WidgetData>(UnifiedMetrics.Pool([Percent(100), Percent(120)])).IsSpent);
    }

    [Fact]
    public void SumsCompatibleCapsButKeepsMixedCurrenciesAndUncappedSpendSeparate()
    {
        var usd = new WidgetData { Title = "Spend", Kind = MetricKind.Dollars, Used = 3, Limit = 10, CurrencyCode = "USD" };
        var usd2 = usd with { Used = 5, Limit = 20, CurrencyCode = null };
        var eur = usd with { Used = 7, Limit = 6, CurrencyCode = "EUR" };
        var uncapped = usd with { Limit = null, Values = [new(2, MetricKind.Dollars, CurrencyCode: "USD")] };

        var bounded = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([usd, usd2]));
        Assert.Equal(8, bounded.Used);
        Assert.Equal(30, bounded.Limit);

        var mixed = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([usd, usd2, eur, uncapped]));
        Assert.Null(mixed.Limit);
        Assert.Equal([new(10, MetricKind.Dollars, CurrencyCode: "USD"), new(7, MetricKind.Dollars, CurrencyCode: "EUR")],
            mixed.Values);
    }

    [Fact]
    public void AddsResetBalancesAndPreservesSeparateExpiryEntries()
    {
        var early = Now.AddDays(1);
        var late = Now.AddDays(2);
        var first = new WidgetData
        {
            Title = "Rate Limit Resets", Kind = MetricKind.Count, ShowsResetExpiries = true,
            Values = [new(1, MetricKind.Count, "available")], ExpiriesAt = [late]
        };
        var second = first with { Values = [new(2, MetricKind.Count, "available")], ExpiriesAt = [early, late] };
        var pooled = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([first, second]));

        Assert.Equal(new(3, MetricKind.Count, "available"), Assert.Single(pooled.Values));
        Assert.Equal([early, late, late], pooled.ExpiriesAt);
    }

    [Fact]
    public void OnlyKeepsDistinctBadgeTextAndDoesNotTreatChartsAsReadings()
    {
        var badge = new WidgetData { Title = "Status", ValueTextOverride = "A" };
        Assert.Null(UnifiedMetrics.Pool([badge with { IsChart = true }, badge with { HasData = false }]));
        var pooled = Assert.IsType<WidgetData>(UnifiedMetrics.Pool([badge, badge with { ValueTextOverride = "B" }, badge]));
        Assert.Equal("A · B", pooled.ValueTextOverride);
    }

    private static WidgetData Percent(double used) => new()
    {
        Title = "Weekly", Kind = MetricKind.Percent, Used = used, Limit = 100,
        PeriodDurationMs = MetricPeriod.WeekMs, SessionStartSignal = SessionStartSignal.ZeroUsage
    };
}
