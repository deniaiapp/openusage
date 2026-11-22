using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class CodexCreditLimitTests
{
    [Theory]
    [InlineData("""{"used":"12.5","limit":"100.75","reset_at":"1800000000"}""", 12.5, 100.75, 1800000000)]
    [InlineData("""{"used":0,"limit":0}""", 0, 0, null)]
    [InlineData("""{"used":125,"limit":100,"reset_at":1e25}""", 125, 100, null)]
    [InlineData("""{"used":1,"limit":10,"reset_at":"not-a-date"}""", 1, 10, null)]
    public void Map_ReadsOnlyReportedWorkspaceLimit(string individualLimit, double used, double cap, int? resetAt)
    {
        var lines = Map(individualLimit);
        var limit = Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines, line => line.Label == "Credit Limit"));
        Assert.Equal(used, limit.Used);
        Assert.Equal(cap, limit.Limit);
        Assert.Equal(ProgressFormat.Count("credits"), limit.Format);
        Assert.Equal(resetAt is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null, limit.ResetsAt);
        Assert.Contains(lines, line => line is MetricLine.ProgressLine { Label: "Session", Used: 31 });
        Assert.Contains(lines, line => line is MetricLine.ProgressLine { Label: "Weekly", Used: 47 });
        Assert.Contains(lines, line => line is MetricLine.ValuesLine { Label: "Credits" });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"limit":100}""")]
    [InlineData("""{"used":12,"limit":-1}""")]
    [InlineData("""{"used":-1,"limit":100}""")]
    [InlineData("""{"used":"Infinity","limit":100}""")]
    [InlineData("""{"used":12,"limit":"unknown"}""")]
    public void Map_OmitsMissingOrInvalidWorkspaceLimit(string individualLimit) =>
        Assert.DoesNotContain(Map(individualLimit), line => line.Label == "Credit Limit");

    [Fact]
    public void Card_OnlyShowsReportedLimitBetweenExistingRowsWithoutDefaultPin()
    {
        var dir = Directory.CreateTempSubdirectory("openusage-codex-limit-");
        try
        {
            var settingsPath = Path.Combine(dir.FullName, "settings.json");
            var settings = AppSettings.Load(settingsPath);
            settings.EnabledMetrics.Remove("codex.creditLimit");
            settings.ExpandedMetrics.Remove("codex.creditLimit");
            settings.SeededDefaultMetrics!.Remove("codex.creditLimit");
            settings.Save();

            var provider = new CodexProvider(new AppHttp());
            settings = AppSettings.Load(settingsPath);
            var layout = new LayoutStore(settings, [provider]);
            var data = new WidgetDataStore(layout, settings, dir.FullName);
            var rows = layout.OnDemandMetrics(provider.Provider.Id);
            Assert.DoesNotContain("codex.creditLimit", settings.PinnedMetrics);
            Assert.DoesNotContain(layout.VisibleMetrics(provider.Provider.Id, false), row => row.Id == "codex.creditLimit");

            var reported = ProviderSnapshot.Make(provider.Provider, null, Map("""{"used":0,"limit":100}"""),
                DateTimeOffset.UnixEpoch);
            data.ReplaceAccountSnapshot(reported);
            var visible = data.ResolveCardRows(rows);
            Assert.Equal(new[] { "Credits", "Credit Limit", "Rate Limit Resets" },
                visible.Where(row => row.Descriptor.Title is "Credits" or "Credit Limit" or "Rate Limit Resets")
                    .Select(row => row.Descriptor.Title));
            Assert.Equal(0, Assert.Single(visible, row => row.Descriptor.Title == "Credit Limit").Data.Used);
            var resource = LimitsProjection.Project(reported, provider.WidgetDescriptors)["creditLimit"];
            Assert.Equal(("credits", 0d, 100d), (resource.Unit, resource.Used!.Value, resource.Limit!.Value));

            var missing = ProviderSnapshot.Make(provider.Provider, null, Map("null"), DateTimeOffset.UnixEpoch);
            data.ReplaceAccountSnapshot(missing);
            Assert.DoesNotContain(data.ResolveCardRows(rows), row => row.Descriptor.Title == "Credit Limit");
            Assert.DoesNotContain(LimitsProjection.Project(missing, provider.WidgetDescriptors).Keys,
                key => key == "creditLimit");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static List<MetricLine> Map(string individualLimit)
    {
        var body = $$$"""
            {
              "rate_limit":{"primary_window":{"used_percent":31},"secondary_window":{"used_percent":47}},
              "credits":{"balance":"10"},
              "rate_limit_reset_credits":{"available_count":2},
              "spend_control":{"individual_limit":{{{individualLimit}}} }
            }
            """;
        return CodexUsageMapper.Map(
            new AppHttpResponse(200, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>()),
            null, DateTimeOffset.UnixEpoch).Lines;
    }
}
