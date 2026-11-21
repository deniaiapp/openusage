using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using Xunit;

namespace OpenUsage.Tests;

public sealed class PriorityLimitsTests
{
    [Theory]
    [InlineData(69, MeterSeverity.Normal)]
    [InlineData(70, MeterSeverity.Warning)]
    [InlineData(89, MeterSeverity.Warning)]
    [InlineData(90, MeterSeverity.Critical)]
    public void DefaultsChangeColorAtTheRemainingThresholds(int used, MeterSeverity severity)
    {
        var data = new WidgetData { Title = "Session", Kind = MetricKind.Percent, Limit = 100, Used = used };
        Assert.Equal(severity, data.GetMeterState(DateTimeOffset.UnixEpoch).Severity);
    }

    [Fact]
    public void LowBalanceStaysOrangeEvenIfOnPace()
    {
        var now = DateTimeOffset.UnixEpoch;
        var data = new WidgetData
        {
            Title = "Weekly", Kind = MetricKind.Percent, Limit = 100, Used = 74,
            ResetsAt = now.AddHours(12), PeriodDurationMs = 7 * 24 * 60 * 60 * 1000
        };
        var state = data.GetMeterState(now);
        Assert.Equal(MeterStateKind.Healthy, state.Kind);
        Assert.Equal(MeterSeverity.Warning, state.Severity);
        Assert.Equal(MeterSeverity.Normal, (data with { PriorityLimits = new(20, 10) }).GetMeterState(now).Severity);
    }

    [Fact]
    public void MovingOneThresholdKeepsThemOrderedAndSavesThePair()
    {
        var dir = Directory.CreateTempSubdirectory("openusage-thresholds-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, "{}");
            var settings = AppSettings.Load(path);
            Assert.Equal(PriorityLimits.Default, settings.PriorityLimits);
            var adjusted = settings.PriorityLimits.WithWarning(20).WithCritical(25);
            Assert.Equal(new PriorityLimits(30, 25), adjusted);
            Assert.Equal(new PriorityLimits(20, 15), adjusted.WithWarning(20));
            settings.PriorityLimits = adjusted;
            settings.Save();
            Assert.Equal(adjusted, AppSettings.Load(path).PriorityLimits);
        }
        finally { dir.Delete(recursive: true); }
    }
}
