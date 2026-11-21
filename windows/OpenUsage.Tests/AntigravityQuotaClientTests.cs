using System.Net;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class AntigravityQuotaClientTests
{
    [Fact]
    public async Task DailyKeepsItsActiveSessionWhenProductionAddsMissingBuckets()
    {
        using var handler = new Handler("""
            {"groups":[
                {"displayName":"Gemini 4 Argon","buckets":[
                    {"bucketId":"gemini-5h","remainingFraction":0.1},
                    {"bucketId":"gemini-argon-wtu-weekly","window":"weekly","remainingFraction":0.5,"resetTime":"2026-11-27T03:00:00Z"}
                ]},
                {"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.997,"resetTime":"2026-11-21T03:00:00Z"}]}
            ]}
            """, """
            {"groups":[{"buckets":[
                {"bucketId":"gemini-5h","remainingFraction":1.0},
                {"bucketId":"gemini-weekly","remainingFraction":0.8},
                {"bucketId":"3p-5h","remainingFraction":0.7}
            ]}]}
            """);
        using var http = new AppHttp(new HttpClient(handler));
        var (lines, _) = await new AntigravityUsageClient(http).QuotaAsync("fixture-token");

        Assert.Equal(new[] { "daily-cloudcode-pa.googleapis.com", "cloudcode-pa.googleapis.com" }, handler.Hosts);
        var session = Assert.IsType<MetricLine.ProgressLine>(lines[0]);
        Assert.Equal(0.3, session.Used, 6);
        Assert.NotNull(session.ResetsAt);
        var weekly = Assert.IsType<MetricLine.ProgressLine>(lines[1]);
        Assert.Equal("Weekly", weekly.Label);
        Assert.Equal(20, weekly.Used, 6);
        Assert.Equal(new[] { "Session", "Weekly", "Argon Weekly", "Claude" }, lines.Select(line => line.Label));
        var argon = Assert.IsType<MetricLine.ProgressLine>(lines[2]);
        Assert.Equal(50, argon.Used);
        Assert.Equal(MetricPeriod.WeekMs, argon.PeriodDurationMs);
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T03:00:00Z"), argon.ResetsAt);
        var provider = new AntigravityProvider(http);
        var snapshot = ProviderSnapshot.Make(provider.Provider, null, lines, DateTimeOffset.UnixEpoch);
        var limit = LimitsProjection.Project(snapshot, provider.WidgetDescriptors)["argonWeekly"];
        Assert.Equal("percent", limit.Unit);
        Assert.Equal(50d, limit.Used);
        Assert.Equal(argon.ResetsAt, limit.ResetsAt);
        Assert.Equal(MetricPeriod.WeekMs / 1000d, limit.WindowSeconds);
        var widget = WidgetData.Resolve(provider.WidgetDescriptors[0], session, WidgetDisplayMode.Remaining, ResetDisplayMode.Relative, false);
        Assert.True(widget.HasResetLabel(DateTimeOffset.Parse("2026-11-21T01:00:00Z")));
    }

    [Theory]
    [InlineData("""{"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.1}""")]
    [InlineData("""{"bucketId":"argon","window":"weekly","remainingFraction":0.1,"disabled":true}""")]
    [InlineData("""{"bucketId":"argon","window":"weekly","remainingFraction":0.1,"disabled":"false"}""")]
    [InlineData("""{"bucketId":"argon","window":"weekly"}""")]
    [InlineData("""{"bucketId":"argon","window":"5h","remainingFraction":0.1}""")]
    [InlineData("""{"bucketId":"argon","window":"weekly","remainingFraction":0.1},{"bucketId":"other","window":"weekly","remainingFraction":0.9}""")]
    public void UnavailableArgonDoesNotChangeSharedWeeklyQuota(string argonBuckets)
    {
        var body = JsonUtil.ParseObject($$$"""
            {"response":{"groups":[
                {"displayName":"Gemini 4 Argon","buckets":[{{{argonBuckets}}}]},
                {"buckets":[{"bucketId":"gemini-weekly","remainingFraction":0.5}]}
            ]}}
            """);
        var weekly = Assert.IsType<MetricLine.ProgressLine>(
            Assert.Single(AntigravityUsageMapper.ParseQuotaSummary(body!)!));
        Assert.Equal("Weekly", weekly.Label);
        Assert.Equal(50, weekly.Used);
    }

    [Fact]
    public void ArgonWeeklyFollowsWeeklyAndIsOnDemandByDefault()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-argon-layout-");
        try
        {
            using var http = new AppHttp();
            var provider = new AntigravityProvider(http);
            var layout = new LayoutStore(AppSettings.Load(Path.Combine(directory.FullName, "settings.json")), [provider]);
            var titles = layout.VisibleMetrics(provider.Provider.Id, true).Select(d => d.Title).ToList();
            var weekly = titles.IndexOf("Weekly");
            Assert.True(weekly >= 0);
            Assert.Equal("Argon Weekly", titles[weekly + 1]);
            Assert.True(layout.IsOnDemand(AntigravityMetric.ArgonWeeklyId));
            Assert.False(layout.IsPinned(AntigravityMetric.ArgonWeeklyId));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CompleteDailySummaryNeedsNoFallback()
    {
        using var handler = new Handler("""
            {"groups":[{"buckets":[
                {"bucketId":"gemini-5h","remainingFraction":0.9},
                {"bucketId":"gemini-weekly","remainingFraction":0.8},
                {"bucketId":"3p-5h","remainingFraction":0.7},
                {"bucketId":"3p-weekly","remainingFraction":0.6}
            ]}]}
            """, "{}");
        using var http = new AppHttp(new HttpClient(handler));
        var (lines, _) = await new AntigravityUsageClient(http).QuotaAsync("fixture-token");
        Assert.Equal(4, lines.Count);
        Assert.Equal("daily-cloudcode-pa.googleapis.com", Assert.Single(handler.Hosts));
    }

    [Fact]
    public async Task UnavailableDailyEndpointFallsBackToProduction()
    {
        using var handler = new Handler("{}", """
            {"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.75}]}]}
            """, HttpStatusCode.ServiceUnavailable);
        using var http = new AppHttp(new HttpClient(handler));
        var (lines, _) = await new AntigravityUsageClient(http).QuotaAsync("fixture-token");
        Assert.Equal(2, handler.Hosts.Count);
        Assert.Equal(25, Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines)).Used);
    }

    private sealed class Handler(string daily, string production, HttpStatusCode dailyStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            Assert.Equal("/v1internal:retrieveUserQuotaSummary", request.RequestUri.AbsolutePath);
            var userAgent = Assert.Single(request.Headers.GetValues("User-Agent"));
            Assert.StartsWith("antigravity/hub/", userAgent);
            Assert.Contains("os_type=windows; arch=", userAgent);
            var isDaily = host == "daily-cloudcode-pa.googleapis.com";
            return Task.FromResult(new HttpResponseMessage(isDaily ? dailyStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(isDaily ? daily : production)
            });
        }
    }
}
