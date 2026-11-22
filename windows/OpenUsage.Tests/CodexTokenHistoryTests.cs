using System.Globalization;
using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CodexTokenHistoryTests
{
    private static readonly DateTimeOffset BeforeUtcMidnight =
        new(2026, 11, 22, 8, 55, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task ReadsUtcWindowAndRevisitsRecentDaysAtMidnight()
    {
        var history = new CodexTokenHistory();
        var urls = new List<Uri>();
        Task<AppHttpResponse> Fetch(Uri url, CancellationToken _)
        {
            urls.Add(url);
            return Task.FromResult(urls.Count == 1
                ? Response(200, """{"data":[{"date":"2026-10-23","totals":{"text_total_tokens":100}},{"date":"2026-11-21","totals":{"text_total_tokens":12}}]}""")
                : Response(200, """{"data":[{"date":"2026-11-21","totals":{"text_total_tokens":15}},{"date":"2026-11-22","totals":{"text_total_tokens":0}}]}"""));
        }

        var first = await history.RefreshAsync("fixture", "workspace", BeforeUtcMidnight, Fetch, default);
        Assert.Equal((12d, null, 112d), first);
        Assert.Equal(first, await history.RefreshAsync("fixture", "workspace", BeforeUtcMidnight.AddMinutes(1), Fetch, default));
        Assert.Single(urls);
        Assert.Equal("chatgpt.com", urls[0].Host);
        Assert.Equal("/backend-api/wham/analytics/daily-workspace-usage-counts", urls[0].AbsolutePath);
        Assert.Equal("?start_date=2026-10-23&end_date=2026-11-21&group_by=day&workspace_user=true", urls[0].Query);

        var next = await history.RefreshAsync("fixture", "workspace", BeforeUtcMidnight.AddMinutes(5), Fetch, default);
        Assert.Equal((0d, 15d, 15d), next);
        Assert.Equal("?start_date=2026-11-21&end_date=2026-11-22&group_by=day&workspace_user=true", urls[1].Query);
    }

    [Fact]
    public async Task DefersThrottledReadsAcrossMidnightWithoutLeakingAnotherWorkspace()
    {
        var history = new CodexTokenHistory();
        var requests = 0;
        Task<AppHttpResponse> Fetch(Uri _, CancellationToken __) => Task.FromResult(++requests switch
        {
            1 => Response(200, """{"data":[{"date":"2026-11-21","totals":{"text_total_tokens":20}}]}"""),
            2 => Response(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "7200" }),
            _ => Response(200, """{"data":[]}""")
        });
        var first = await history.RefreshAsync("fixture", "workspace-a", BeforeUtcMidnight.AddHours(-1), Fetch, default);
        Assert.Equal((20d, null, 20d), first);
        Assert.Equal(first, await history.RefreshAsync("fixture", "workspace-a", BeforeUtcMidnight, Fetch, default));
        var throttled = await history.RefreshAsync("fixture", "workspace-a", BeforeUtcMidnight.AddHours(1), Fetch, default);
        Assert.Equal((null, 20d, 20d), throttled);
        Assert.Equal(2, requests);

        var switched = await history.RefreshAsync("fixture", "workspace-b", BeforeUtcMidnight.AddHours(1), Fetch, default);
        Assert.Equal((null, null, null), switched);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LiveCardShowsOnlyReportedTokensAfterResetsWithoutPinning(bool available)
    {
        using var http = new AppHttp(new HttpClient(new CodexFixture(available)));
        var provider = new CodexProvider(http);
        var cache = Directory.CreateTempSubdirectory();
        try
        {
            var settings = AppSettings.Fresh();
            settings.EnabledProviders.Add("codex");
            var layout = new LayoutStore(settings, [provider]);
            var data = new WidgetDataStore(layout, settings, cache.FullName);
            var snapshot = await provider.FetchUsageAsync(new CodexAuth
            {
                AccessToken = "fixture",
                AccountId = "workspace"
            }, default);
            data.ReplaceAccountSnapshot(snapshot);

            var expanded = data.ResolveCardRows(layout.OnDemandMetrics("codex"))
                .SkipWhile(row => row.Descriptor.Id != "codex.credits").ToArray();
            Assert.Equal(available
                    ? new[] { "Credits", "Credit Limit", "Rate Limit Resets", "Today", "Yesterday", "Last 30 Days" }
                    : ["Credits", "Credit Limit", "Rate Limit Resets"],
                expanded.Select(row => row.Descriptor.Title));
            Assert.Equal(new[] { "codex.session", "codex.weekly" }, layout.PinnedDescriptors().Select(d => d.Id).Order());
            Assert.Equal(31d, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Session")).Used);
            Assert.Equal(47d, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Weekly")).Used);
            var history = expanded.Skip(3);
            Assert.All(history, row =>
            {
                Assert.False(row.Data.IsBounded);
                Assert.Equal("tokens", Assert.Single(row.Data.SelectedValues).Label);
            });
            if (available)
                Assert.Equal(new[] { 500d, 0d, 500d }, history.Select(row => row.Data.SelectedValues.Single().Number));
        }
        finally { cache.Delete(recursive: true); }
    }

    private static AppHttpResponse Response(int status, string body, Dictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(body), headers ?? []);

    private sealed class CodexFixture(bool historyAvailable) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("chatgpt.com", request.RequestUri!.Host);
            Assert.Equal("fixture", request.Headers.Authorization!.Parameter);
            Assert.Equal("workspace", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/backend-api/wham/usage" => Reply(HttpStatusCode.OK, """
                    {"rate_limit":{"primary_window":{"used_percent":31},"secondary_window":{"used_percent":47}},
                     "credits":{"balance":"10"},"spend_control":{"individual_limit":{"used":12.5,"limit":100.75}},
                     "rate_limit_reset_credits":{"available_count":1}}
                    """),
                "/backend-api/wham/rate-limit-reset-credits" => Reply(HttpStatusCode.OK, """{"available_count":1}"""),
                "/backend-api/wham/analytics/daily-workspace-usage-counts" => History(request.RequestUri.Query),
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            });
        }

        private HttpResponseMessage History(string query)
        {
            if (!historyAvailable)
                return Reply(HttpStatusCode.ServiceUnavailable, "{}");
            var today = Assert.Single(query.TrimStart('?').Split('&'), part => part.StartsWith("end_date="))[9..];
            var yesterday = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                .AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Reply(HttpStatusCode.OK, $$$"""
                {"data":[
                  {"date":"{{{today}}}","totals":{"text_total_tokens":500}},
                  {"date":"{{{yesterday}}}","totals":{"text_total_tokens":0}}
                ]}
                """);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
