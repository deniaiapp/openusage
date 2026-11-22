using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class ProviderCompatibilityTests
{
    [Theory]
    [InlineData("""{"pooledLimit":5000,"pooledUsed":1000}""", "Team Spend")]
    [InlineData("""{"pooledUsed":1000}""", "Team Spend")]
    [InlineData("""{"individualLimit":2000,"individualUsed":1000,"pooledLimit":5000}""", "Extra Usage")]
    public async Task CursorSpendTitlePersistsWithoutChangingItsMetricIdentity(string budget, string title)
    {
        var usage = JsonUtil.ParseObject($$"""{"planUsage":{"limit":2000},"spendLimitUsage":{{budget}}}""")!;
        var mapped = CursorUsageMapper.Map(usage, "Team", null, 0);
        var runtime = new CursorFixtureRuntime(mapped.Lines);
        var dir = Directory.CreateTempSubdirectory("openusage-cursor-title-");
        try
        {
            var settings = AppSettings.Load(Path.Combine(dir.FullName, "settings.json"));
            var layout = new LayoutStore(settings, [runtime]);
            var store = new WidgetDataStore(layout, settings, dir.FullName);
            var metric = Assert.Single(runtime.WidgetDescriptors);
            Assert.Equal("Extra Usage", store.Resolve(metric).Title);

            await store.RefreshProviderAsync("cursor");

            Assert.Equal(title, store.Resolve(metric).Title);
            var reloaded = new WidgetDataStore(layout, settings, dir.FullName);
            Assert.Equal(title, reloaded.Resolve(metric).Title);
            Assert.NotNull(reloaded.Snapshot("cursor")?.Line("On-demand"));
        }
        finally { dir.Delete(recursive: true); }
    }

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
    [InlineData("""{"planUsage":{"limit":1000}}""", false)]
    [InlineData("""{"planUsage":{"totalPercentUsed":25}}""", false)]
    [InlineData("""{"planUsage":{"autoPercentUsed":25,"apiPercentUsed":0}}""", false)]
    [InlineData("""{"enabled":false}""", false)]
    [InlineData("""{"planUsage":{}}""", true)]
    [InlineData("""{}""", true)]
    public void CursorOnlyRequestsTheFallbackWithoutAUsablePlanMeter(string usage, bool expected) =>
        Assert.Equal(expected, CursorUsageMapper.NeedsRequestFallback(JsonUtil.ParseObject(usage)!, "Pro"));

    [Theory]
    [InlineData("""{"gpt-4":{"numRequests":120,"maxRequestUsage":500}}""", 120)]
    [InlineData("""{"gpt-4":{"numRequestsTotal":27,"maxRequestUsage":500}}""", 27)]
    [InlineData("""{"gpt-4":{"numRequests":0,"numRequestsTotal":27,"maxRequestUsage":500}}""", 0)]
    public void CursorMapsProviderReportedRequestLimits(string body, double used)
    {
        var lines = CursorRequestUsageMapper.Map(JsonUtil.ParseObject(body))!;
        Assert.Equal(["Total usage", "Requests"], lines.Select(line => line.Label));
        Assert.All(lines, line =>
        {
            var meter = Assert.IsType<MetricLine.ProgressLine>(line);
            Assert.Equal(used, meter.Used);
            Assert.Equal(500, meter.Limit);
            Assert.Equal(ProgressFormat.Count("requests"), meter.Format);
            Assert.Equal(MetricPeriod.MonthMs, meter.PeriodDurationMs);
            Assert.Null(meter.ResetsAt);
        });
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", 31)]
    [InlineData("2026-01-31T00:00:00Z", "2026-02-28T00:00:00Z", 28)]
    public void CursorRequestResetUsesTheNextCalendarMonth(string start, string expected, int days)
    {
        var requestUsage = JsonUtil.ParseObject($$"""
            {"gpt-4":{"numRequests":120,"maxRequestUsage":500},"startOfMonth":"{{start}}"}
            """);
        var total = Assert.IsType<MetricLine.ProgressLine>(CursorRequestUsageMapper.Map(requestUsage)![0]);
        Assert.Equal(Iso8601.Parse(expected), total.ResetsAt);
        Assert.Equal((long)TimeSpan.FromDays(days).TotalMilliseconds, total.PeriodDurationMs);
    }

    [Theory]
    [InlineData("""{"gpt-4":{"numRequests":12}}""")]
    [InlineData("""{"gpt-4":{"maxRequestUsage":0}}""")]
    public void CursorDoesNotInventARequestCap(string body) =>
        Assert.Null(CursorRequestUsageMapper.Map(JsonUtil.ParseObject(body)));

    [Theory]
    [InlineData("""{"planUsage":{},"billingCycleStart":1788220800000,"billingCycleEnd":1790812800000}""", true)]
    [InlineData("""{"planUsage":{"totalPercentUsed":42}}""", false)]
    public async Task CursorReadsTheRequestCapWithTheCurrentDashboardSession(string usage, bool needsFallback)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = "fixture." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"sub":"auth0|test+user","exp":{{expires}}}""")).TrimEnd('=') + ".fixture";
        var dashboardCalls = 0;
        using var client = new HttpClient(new CursorFixtureHandler(request =>
        {
            if (request.RequestUri == CursorProvider.UsageUrl) return (HttpStatusCode.OK, usage);
            if (request.RequestUri == CursorProvider.PlanUrl) return (HttpStatusCode.OK, """{"planInfo":{"planName":"Pro"}}""");
            if (request.RequestUri!.AbsolutePath == "/api/usage")
            {
                dashboardCalls++;
                Assert.Equal("cursor.com", request.RequestUri.Host);
                Assert.Equal("?user=test%2Buser", request.RequestUri.Query);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"WorkosCursorSessionToken=test+user%3A%3A{token}",
                    Assert.Single(request.Headers.GetValues("Cookie")));
                return (HttpStatusCode.OK, """{"gpt-4":{"numRequests":120,"maxRequestUsage":500},"startOfMonth":"2026-10-01T00:00:00Z"}""");
            }
            return (HttpStatusCode.NotFound, "{}");
        }));
        var history = new CursorUsageHistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var provider = new CursorProvider(new AppHttp(client), history);

        var snapshot = await provider.ProbeAsync(new CursorAuthState { AccessToken = token }, CancellationToken.None);

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(needsFallback ? 1 : 0, dashboardCalls);
        var total = Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Total usage"));
        Assert.Equal(needsFallback ? 120 : 42, total.Used);
        Assert.Equal(needsFallback ? 500 : 100, total.Limit);
        if (needsFallback)
        {
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), total.ResetsAt);
            Assert.Equal((long)TimeSpan.FromDays(31).TotalMilliseconds, total.PeriodDurationMs);
        }
        var descriptor = Assert.Single(provider.WidgetDescriptors, d => d.Id == "cursor.usage");
        Assert.Equal(needsFallback ? "120 of 500 requests used" : "42% used",
            WidgetData.Resolve(descriptor, total, WidgetDisplayMode.Used, ResetDisplayMode.Relative, false).Headline);
        Assert.Equal(needsFallback ? "380 of 500 requests left" : "58% left",
            WidgetData.Resolve(descriptor, total, WidgetDisplayMode.Remaining, ResetDisplayMode.Relative, false).Headline);
    }

    private sealed class CursorFixtureHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = send(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData("""{"success":false,"code":401}""", true)]
    [InlineData("""{"success":true,"code":401}""", false)]
    [InlineData("""{"success":false,"code":500}""", false)]
    public void ZaiOnlyClassifiesAnUnsuccessful401EnvelopeAsARejectedKey(string body, bool rejected) =>
        Assert.Equal(rejected, ZaiUsageMapper.IsRejectedKey(JsonUtil.ParseObject(body)!));

    private sealed class CursorFixtureRuntime(IReadOnlyList<MetricLine> lines) : IProviderRuntime
    {
        public Provider Provider { get; } = new("cursor", "Cursor", "cursor");
        public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
            [WidgetDescriptor.BoundedDollars("cursor.onDemand", Provider, "Extra Usage", 100, "On-demand")];
        public Task<ProviderSnapshot> RefreshAsync(CancellationToken ct) =>
            Task.FromResult(ProviderSnapshot.Make(Provider, "Team", lines, DateTimeOffset.UtcNow));
        public Task<bool> HasLocalCredentialsAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
