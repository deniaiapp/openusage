using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class ProviderCompatibilityTests
{
    [Theory]
    [InlineData("""{"individualLimit":1000,"individualRemaining":900,"pooledUsed":9000}""", 1, 10)]
    [InlineData("""{"pooledLimit":5000,"pooledUsed":1000,"individualUsed":500}""", 10, 50)]
    [InlineData("""{"individualLimit":2000,"pooledRemaining":200,"pooledUsed":500}""", 0, 20)]
    [InlineData("""{"individualLimit":2000,"totalSpend":500}""", 5, 20)]
    public void CursorOnDemandSpendMatchesItsLimit(string budget, double used, double limit)
    {
        var usage = JsonUtil.ParseObject($$"""{"planUsage":{"limit":2000},"spendLimitUsage":{{budget}}}""")!;
        var lines = CursorUsageMapper.Map(usage, "Pro", null, 0).Lines;
        var meter = Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines, line => line.Label == "On-demand"));
        Assert.Equal(used, meter.Used);
        Assert.Equal(limit, meter.Limit);
    }

    [Fact]
    public void CursorUnifiedBillingUsesDashboardCreditsAndAdditionalUsageInsteadOfLegacyPools()
    {
        var usage = JsonUtil.ParseObject("""
            {"planUsage":{"totalPercentUsed":42,"autoPercentUsed":80,"apiPercentUsed":90},
             "spendLimitUsage":{"individualLimit":2000,"individualUsed":1000}}
            """)!;
        var status = JsonUtil.ParseObject("""
            {"monthlyUsage":{"usedCents":345,"limitCents":2000,
             "periodStart":"2026-11-01T00:00:00Z","resetsAt":"2026-12-01T00:00:00Z"}}
            """);

        var (_, lines) = CursorUsageMapper.Map(usage, "Pro", null, 999,
            new CursorUnifiedBilling(JsonUtil.ParseObject("""{"usdRemainingCents":789}"""), status));

        Assert.Equal(["Credits", "Total usage", "On-demand"], lines.Select(line => line.Label));
        var credits = Assert.IsType<MetricLine.ValuesLine>(lines[0]);
        Assert.Equal(7.89, Assert.Single(credits.Items).Number);
        var additional = Assert.IsType<MetricLine.ProgressLine>(lines[2]);
        Assert.Equal(3.45, additional.Used);
        Assert.Equal(20, additional.Limit);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), additional.ResetsAt);
    }

    [Theory]
    [InlineData("""{"availability":"unavailable","monthlyUsage":{"usedCents":100}}""", false)]
    [InlineData("""{"monthlyUsage":{"usedCents":100,"additionalUsageOff":true}}""", false)]
    [InlineData("""{"monthlyUsage":{"usedCents":100,"limitCents":0}}""", false)]
    [InlineData("""{"monthlyUsage":{"usedCents":100}}""", true)]
    public void CursorUnifiedBillingDoesNotInventACap(string body, bool visible)
    {
        var usage = JsonUtil.ParseObject("""{"planUsage":{"totalPercentUsed":42}}""")!;
        var lines = CursorUsageMapper.Map(usage, "Pro", null, 0,
            new CursorUnifiedBilling(null, JsonUtil.ParseObject(body))).Lines;

        if (visible)
            Assert.IsType<MetricLine.ValuesLine>(Assert.Single(lines, line => line.Label == "On-demand"));
        else
            Assert.DoesNotContain(lines, line => line.Label == "On-demand");
    }

    [Theory]
    [InlineData("""{"success":false,"code":401}""", true)]
    [InlineData("""{"success":true,"code":401}""", false)]
    [InlineData("""{"success":false,"code":500}""", false)]
    public void ZaiOnlyClassifiesAnUnsuccessful401EnvelopeAsARejectedKey(string body, bool rejected) =>
        Assert.Equal(rejected, ZaiUsageMapper.IsRejectedKey(JsonUtil.ParseObject(body)!));
}
