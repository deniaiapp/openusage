using System.Net;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Antigravity;
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
                {"displayName":"Gemini 4 Argon","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.1}]},
                {"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.997,"resetTime":"2026-11-21T03:00:00Z"}]}
            ]}
            """, """
            {"groups":[{"buckets":[
                {"bucketId":"gemini-5h","remainingFraction":1.0},
                {"bucketId":"gemini-weekly","remainingFraction":0.8}
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
        var provider = new AntigravityProvider(http);
        var widget = WidgetData.Resolve(provider.WidgetDescriptors[0], session, WidgetDisplayMode.Remaining, ResetDisplayMode.Relative, false);
        Assert.True(widget.HasResetLabel(DateTimeOffset.Parse("2026-11-21T01:00:00Z")));
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

    [Theory]
    [InlineData(200, false, false)]
    [InlineData(200, true, false)]
    [InlineData(200, true, true)]
    [InlineData(400, false, false)]
    [InlineData(503, false, false)]
    public async Task RenewsARefusedAccessTokenAtMostOnce(int renewalStatus, bool renewedRefused, bool expired)
    {
        using var handler = new RenewalHandler(renewalStatus, renewedRefused);
        using var http = new AppHttp(new HttpClient(handler));
        var provider = new AntigravityProvider(http);
        var expiry = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1);
        var saved = new List<(string Access, string Refresh)>();
        var read = () => provider.ReadQuotaAsync(
            ("fixture-old", "fixture-refresh", expiry),
            (access, refresh, _) => saved.Add((access, refresh)),
            CancellationToken.None);

        if (renewalStatus == 200 && !renewedRefused)
        {
            var (lines, _) = await read();
            Assert.Equal(25, Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines)).Used);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ProviderRequestException>(read);
            Assert.Equal(renewalStatus == 503
                ? "Antigravity token renewal failed (HTTP 503). Try again later."
                : "Antigravity login expired. Sign in again.", error.Message);
        }

        var expected = new List<string>();
        if (!expired) expected.AddRange(["fixture-old", "fixture-old"]);
        expected.Add("renewal");
        if (renewalStatus == 200)
        {
            expected.AddRange(["fixture-renewed", "fixture-renewed"]);
            Assert.Equal(("fixture-renewed", "fixture-rotated"), Assert.Single(saved));
        }
        else Assert.Empty(saved);
        Assert.Equal(expected, handler.Requests);
    }

    [Fact]
    public async Task AcceptsOneHostsQuotaWithoutRenewingWhenTheOtherRejectsTheToken()
    {
        using var handler = new Handler("{}", """
            {"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.75}]}]}
            """, HttpStatusCode.Unauthorized);
        using var http = new AppHttp(new HttpClient(handler));
        var provider = new AntigravityProvider(http);
        var (lines, _) = await provider.ReadQuotaAsync(("fixture-old", "fixture-refresh", null),
            (_, _, _) => throw new Exception("Usable quota should not require token renewal."), CancellationToken.None);

        Assert.Equal(2, handler.Hosts.Count);
        Assert.Equal(25, Assert.IsType<MetricLine.ProgressLine>(Assert.Single(lines)).Used);
    }

    private sealed class RenewalHandler(int renewalStatus, bool renewedRefused) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                Requests.Add("renewal");
                Assert.Contains("refresh_token=fixture-refresh", await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage((HttpStatusCode)renewalStatus)
                {
                    Content = new StringContent(renewalStatus == 200
                        ? """{"access_token":"fixture-renewed","refresh_token":"fixture-rotated"}"""
                        : """{"error":"invalid_grant"}""")
                };
            }
            Assert.Equal("/v1internal:retrieveUserQuotaSummary", request.RequestUri.AbsolutePath);
            var token = request.Headers.Authorization!.Parameter!;
            Requests.Add(token);
            return token == "fixture-renewed" && !renewedRefused
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0.75}]}]}""")
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
    }

    private sealed class Handler(string daily, string production, HttpStatusCode dailyStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            Assert.Equal("/v1internal:retrieveUserQuotaSummary", request.RequestUri.AbsolutePath);
            var isDaily = host == "daily-cloudcode-pa.googleapis.com";
            return Task.FromResult(new HttpResponseMessage(isDaily ? dailyStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(isDaily ? daily : production)
            });
        }
    }
}
