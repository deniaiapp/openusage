using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

/// <summary>Regression tests for Codex fixes ported from the macOS app.</summary>
public class CodexUpstreamTests
{
    // #1332
    [Theory]
    [InlineData("prolite", "Pro 100")]
    [InlineData("pro", "Pro 200")]
    [InlineData("promax", "Pro 500")]
    [InlineData("  PROLITE\n", "Pro 100")]
    [InlineData(" ProMax ", "Pro 500")]
    [InlineData("self_serve_business_prolite", "Business Premium")]
    [InlineData("promax_future", "Promax Future")]
    [InlineData("plus", "Plus")]
    public void PlanNames_UseCurrentProNames(string raw, string expected)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["plan_type"] = raw }.ToJsonString());
        var mapped = CodexUsageMapper.Map(new AppHttpResponse(200, body, new Dictionary<string, string>()), null, DateTimeOffset.Now);
        Assert.Equal(expected, mapped.Plan);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n")]
    public void PlanNames_MissingPlanInventsNothing(string? raw) => Assert.Null(CodexUsageMapper.FormatPlan(raw));

    // #1364
    [Fact]
    public void DashboardLink_PointsAtSettingsUsage()
    {
        using var http = new AppHttp();
        var link = Assert.Single(new CodexProvider(http).Provider.Links!, l => l.Label == "Dashboard");
        Assert.Equal("https://chatgpt.com/settings/usage", link.Url);
    }

    [Theory]
    [InlineData("""{"credits":{"has_credits":false}}""", null, null, null)]
    [InlineData("""{"credits":{"has_credits":false,"balance":250}}""", "true", "500", null)]
    [InlineData("""{"credits":{"balance":250}}""", "false", "500", null)]
    [InlineData("{}", "false", "500", null)]
    [InlineData("""{"credits":{"has_credits":true,"balance":0}}""", "false", "500", 0d)]
    [InlineData("""{"credits":{"has_credits":true}}""", "true", "0", 0d)]
    [InlineData("{}", null, "250", 10d)]
    [InlineData("{}", "true", null, null)]
    public void Credits_RespectAvailabilityAndKeepReportedZero(
        string body, string? hasCreditsHeader, string? balanceHeader, double? expectedDollars)
    {
        var headers = new Dictionary<string, string> { ["x-codex-primary-used-percent"] = "30" };
        if (hasCreditsHeader is not null) headers["x-codex-credits-has-credits"] = hasCreditsHeader;
        if (balanceHeader is not null) headers["x-codex-credits-balance"] = balanceHeader;
        var response = new AppHttpResponse(200, Encoding.UTF8.GetBytes(body), headers);

        var (_, lines) = CodexUsageMapper.Map(response, null, DateTimeOffset.UtcNow);

        var session = Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines, line => line.Label == "Session"));
        Assert.Equal(30, session.Used);
        if (expectedDollars is null)
            Assert.DoesNotContain(lines, line => line.Label == "Credits");
        else
        {
            var credits = Assert.IsType<MetricLine.ValuesLine>(Assert.Single(lines, line => line.Label == "Credits"));
            Assert.Equal(expectedDollars.Value, Assert.Single(credits.Items, value => value.Kind == MetricKind.Dollars).Number);
        }
    }

    [Theory]
    [InlineData("""{"has_credits":false}""", null)]
    [InlineData("""{"has_credits":false,"balance":250}""", null)]
    [InlineData("""{"has_credits":true,"balance":0}""", 0d)]
    [InlineData("""{"has_credits":true,"balance":325}""", 13d)]
    public void CreditsCard_HidesOnlyMissingCreditsInBothPlacements(string credits, double? dollars)
    {
        var response = new AppHttpResponse(200, Encoding.UTF8.GetBytes($$"""{"credits":{{credits}}}"""),
            new Dictionary<string, string>
            {
                ["x-codex-primary-used-percent"] = "31",
                ["x-codex-secondary-used-percent"] = "47"
            });
        using var http = new AppHttp();
        var provider = new CodexProvider(http);
        const string creditId = "codex.credits";
        var settings = new AppSettings
        {
            EnabledMetrics = ["codex.session", "codex.weekly", "codex.spark", creditId],
            ExpandedMetrics = [creditId],
            PinnedMetrics = [creditId],
            SeededDefaultMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal)
        };
        var layout = new LayoutStore(settings, [provider]);
        var cache = Directory.CreateTempSubdirectory();
        try
        {
            var data = new WidgetDataStore(layout, settings, cache.FullName);
            var now = DateTimeOffset.UtcNow;
            var (plan, lines) = CodexUsageMapper.Map(response, null, now);
            data.ReplaceAccountSnapshot(ProviderSnapshot.Make(provider.Provider, plan, lines, now));

            var onDemand = data.ResolveCardRows(layout.OnDemandMetrics(provider.Provider.Id));
            if (dollars is { } value)
                Assert.Equal(value, Assert.Single(onDemand).Data.SelectedValues.Single(v => v.Kind == MetricKind.Dollars).Number);
            else
                Assert.Empty(onDemand);
            Assert.True(layout.IsOnDemand(creditId));

            settings.ExpandedMetrics.Remove(creditId);
            var always = data.ResolveCardRows(layout.VisibleMetrics(provider.Provider.Id, false));
            Assert.Equal(dollars.HasValue ? new[] { "Session", "Weekly", "Spark", "Credits" } : ["Session", "Weekly", "Spark"],
                always.Select(row => row.Descriptor.Title));
            Assert.Equal(31, always[0].Data.Used);
            Assert.Equal(47, always[1].Data.Used);
            Assert.False(always[2].Data.HasData);
            Assert.True(layout.IsMetricEnabled(creditId));
            Assert.True(layout.IsPinned(creditId));
        }
        finally { cache.Delete(recursive: true); }
    }

}
