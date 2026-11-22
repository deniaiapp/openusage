using System.Net;
using System.Text;
using Xunit;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Providers.OpenCode;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Tests;

/// <summary>Regression tests for upstream Ollama, OpenCode, and Devin fixes ported to Windows.</summary>
public class MiscProvidersUpstreamTests
{
    // MARK: Ollama (/api/balance)

    [Fact]
    public void Ollama_CurrentPlanMapsMonthlyCreditsAndPurchasedCredits()
    {
        var balance = JsonUtil.ParseObject("""
            {"included":{"balance_usd":72.5,"allowance_usd":100,
                         "period":{"from":"2026-09-15T09:30:00Z","until":"2026-10-15T09:30:00Z"}},
             "purchased":{"balance_usd":25}}
            """);
        var mapped = OllamaUsageMapper.Map(balance, """{"Plan":"pro"}""");
        Assert.Equal("Pro", mapped.Plan.Name);
        Assert.False(mapped.Plan.IsUnreadable);
        Assert.Equal(["Monthly", "Purchased Credits"], mapped.Lines.Select(l => l.Label));
        var monthly = Assert.IsType<MetricLine.ProgressLine>(mapped.Lines[0]);
        Assert.Equal(27.5, monthly.Used, 6);
        Assert.Equal(100, monthly.Limit);
        Assert.Equal(ProgressFormat.Dollars, monthly.Format);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T09:30:00Z"), monthly.ResetsAt);
        Assert.Equal(30L * 24 * 60 * 60 * 1000, monthly.PeriodDurationMs);
        var purchased = Assert.IsType<MetricLine.ValuesLine>(mapped.Lines[1]);
        Assert.Equal(25, purchased.Items[0].Number);
    }

    [Fact]
    public void Ollama_LegacyLabelsMatchDescriptorsInOrder()
    {
        var lines = OllamaUsageMapper.BalanceLines(JsonUtil.ParseObject("""
            {"included":{"session":{"remaining_percent":75,"resets_at":"2026-10-01T07:00:00Z"},
                         "weekly":{"remaining_percent":40},
                         "balance_usd":10,"allowance_usd":20},
             "purchased":{"balance_usd":0}}
            """));
        var provider = new OllamaProvider(new AppHttp(new HttpClient()));
        Assert.Equal(provider.WidgetDescriptors.Select(d => d.MetricLabel), lines.Select(l => l.Label));
        var session = Assert.IsType<MetricLine.ProgressLine>(lines[0]);
        Assert.Equal(25, session.Used);
        Assert.Equal(OllamaUsageMapper.SessionPeriodMs, session.PeriodDurationMs);
        // No reset → no period, so the row can't show a static "Resets in 7d".
        var weekly = Assert.IsType<MetricLine.ProgressLine>(lines[1]);
        Assert.Equal(60, weekly.Used);
        Assert.Null(weekly.PeriodDurationMs);
    }

    [Fact]
    public void Ollama_AllowanceStaysWithinMeterAndMissingIncludedIsInvalid()
    {
        var unused = OllamaUsageMapper.BalanceLines(JsonUtil.ParseObject("""{"included":{"balance_usd":80,"allowance_usd":60}}"""));
        var overspent = OllamaUsageMapper.BalanceLines(JsonUtil.ParseObject("""{"included":{"balance_usd":-5,"allowance_usd":60}}"""));
        Assert.Equal(0, Assert.IsType<MetricLine.ProgressLine>(unused.Single()).Used);
        Assert.Equal(60, Assert.IsType<MetricLine.ProgressLine>(overspent.Single()).Used);
        Assert.Throws<InvalidOperationException>(() => OllamaUsageMapper.BalanceLines(JsonUtil.ParseObject("""{"purchased":{"balance_usd":5}}""")));
        Assert.Throws<InvalidOperationException>(() => OllamaUsageMapper.BalanceLines(JsonUtil.ParseObject("""{"included":{"allowance_usd":0,"balance_usd":0}}""")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17.5)]
    public void Ollama_FreeAccountReportsPurchasedCreditsWithoutIncludedAllowance(double credits)
    {
        var balance = new System.Text.Json.Nodes.JsonObject
        {
            ["included"] = new System.Text.Json.Nodes.JsonObject { ["allowance_usd"] = 0 },
            ["purchased"] = new System.Text.Json.Nodes.JsonObject { ["balance_usd"] = credits }
        };
        var purchased = Assert.IsType<MetricLine.ValuesLine>(Assert.Single(OllamaUsageMapper.BalanceLines(balance)));
        Assert.Equal("Purchased Credits", purchased.Label);
        Assert.Equal(credits, Assert.Single(purchased.Items).Number);
    }

    [Theory]
    [InlineData("""{"plan":"max"}""", "Max", false)]
    [InlineData("""{"Plan":"  "}""", null, false)]
    [InlineData("""{"Plan":5}""", null, true)]
    [InlineData("""{"Name":"x"}""", null, true)]
    [InlineData("not json", null, true)]
    public void Ollama_PlanOutcomes(string body, string? name, bool unreadable)
    {
        var plan = OllamaUsageMapper.Plan(body);
        Assert.Equal(name, plan.Name);
        Assert.Equal(unreadable, plan.IsUnreadable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{}""")]
    [InlineData(HttpStatusCode.OK, """{"Plan":42}""")]
    public async Task Ollama_PlanFailureWarnsButKeepsMeters(HttpStatusCode accountStatus, string accountBody)
    {
        var http = new AppHttp(new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == OllamaProvider.BalancePath
                ? (HttpStatusCode.OK, """{"included":{"balance_usd":50,"allowance_usd":60}}""")
                : (accountStatus, accountBody))));
        var snapshot = await new OllamaProvider(http).RefreshWithApiKeyAsync("fixture-key", default);
        Assert.False(snapshot.IsError);
        Assert.Null(snapshot.Plan);
        Assert.Equal(OllamaProvider.PlanWarning, snapshot.Warning);
        Assert.Equal(10, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Monthly")).Used);
    }

    [Fact]
    public void Ollama_DefaultLayoutMirrorsUpstream()
    {
        Assert.Contains("ollama.monthly", DefaultLayout.MetricIds);
        Assert.Contains("ollama.purchasedCredits", DefaultLayout.MetricIds);
        Assert.DoesNotContain("ollama.last4Weeks", DefaultLayout.MetricIds);
        Assert.Equal(["ollama.monthly"], DefaultLayout.PinnedMetricIds.Where(id => id.StartsWith("ollama.")));
        Assert.Equal(["ollama.purchasedCredits"], DefaultLayout.ExpandedMetricIds.Where(id => id.StartsWith("ollama.")));
    }

    // MARK: OpenCode

    private static readonly DateTimeOffset Captured = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    private static System.Text.Json.Nodes.JsonObject GoUsage(double rollingPercent, DateTimeOffset rollingReset) =>
        JsonUtil.ParseObject($$"""
            {"usage":{
              "rolling":{"percent":{{rollingPercent}},"resetsAt":"{{rollingReset:O}}"},
              "weekly":{"percent":0,"resetsAt":"{{Captured.AddDays(7):O}}"},
              "monthly":{"percent":0,"resetsAt":"{{Captured.AddDays(30):O}}"}
            }
            }
            """)!;

    [Fact]
    public void OpenCode_UntouchedSessionDropsPlaceholderReset()
    {
        var lines = OpenCodeUsageMapper.MeterLines(GoUsage(0, Captured.AddHours(5).AddSeconds(1)), Captured);
        Assert.Null(Assert.IsType<MetricLine.ProgressLine>(lines[0]).ResetsAt);
        // Weekly and monthly keep their calendar resets even at a full period out.
        Assert.NotNull(Assert.IsType<MetricLine.ProgressLine>(lines[1]).ResetsAt);
    }

    [Fact]
    public void OpenCode_SubOnePercentSessionKeepsCountdown()
    {
        var reset = Captured.AddHours(4);
        var lines = OpenCodeUsageMapper.MeterLines(GoUsage(0, reset), Captured);
        Assert.Equal(reset, Assert.IsType<MetricLine.ProgressLine>(lines[0]).ResetsAt);
        var provider = new OpenCodeProvider(new AppHttp(new HttpClient()));
        Assert.Equal(SessionStartSignal.MissingResetDate, provider.WidgetDescriptors[0].Sample.SessionStartSignal);
    }

    [Fact]
    public void OpenCode_PlaceholderUsesServerDateHeader()
    {
        var serverNow = Captured;
        var body = Encoding.UTF8.GetBytes(GoUsage(0, serverNow.AddHours(5)).ToJsonString());
        var response = new AppHttpResponse(200, body,
            new Dictionary<string, string> { ["Date"] = serverNow.ToString("r") });
        // Local clock is ten minutes off; the server's Date header still identifies the placeholder.
        var lines = OpenCodeUsageMapper.MeterLines(response, serverNow.AddMinutes(10));
        Assert.Null(Assert.IsType<MetricLine.ProgressLine>(lines[0]).ResetsAt);
    }

    [Fact]
    public async Task OpenCodeDoesNotReadExternalSignInsOrRefreshTheLegacyCard()
    {
        var directory = Directory.CreateTempSubdirectory("openusage-opencode-");
        var previous = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        try
        {
            const string fixture = """{"opencode-go":{"key":"external-fixture-key"}}""";
            var authFile = Path.Combine(directory.FullName, "auth.json");
            File.WriteAllText(authFile, fixture);
            File.WriteAllText(Path.Combine(directory.FullName, "opencode.db"), "fixture database");
            Environment.SetEnvironmentVariable("OPENCODE_DATA_DIR", directory.FullName);

            var requests = 0;
            using var http = new AppHttp(new HttpClient(new StubHandler(_ =>
            {
                requests++;
                return (HttpStatusCode.OK, "{}");
            })));
            var provider = new OpenCodeProvider(http);
            var settings = AppSettings.Load(Path.Combine(directory.FullName, "settings.json"));
            settings.EnabledProviders.Add(provider.Provider.Id);
            var layout = new LayoutStore(settings, [provider]);

            Assert.False(await provider.HasLocalCredentialsAsync());
            Assert.Empty(layout.VisibleProviders);
            var snapshot = await provider.RefreshAsync();
            Assert.True(snapshot.IsError);
            Assert.Contains("Add an OpenCode Go API key", Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
            Assert.Equal(0, requests);
            Assert.Equal(fixture, File.ReadAllText(authFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_DATA_DIR", previous);
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "OpenCode Go rejected this API key. Replace it in Customize.")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"type":"EntitlementError"}}""", "No OpenCode Go subscription on this key.")]
    public async Task OpenCodeAppOwnedKeyRejectionStaysOnItsUsageEndpoint(
        HttpStatusCode status, string body, string message)
    {
        var requests = 0;
        using var http = new AppHttp(new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.Equal(OpenCodeProvider.UsageUrl, request.RequestUri);
            Assert.Equal("Bearer fixture-key", request.Headers.Authorization?.ToString());
            return (status, body);
        })));

        var snapshot = await new OpenCodeProvider(http).RefreshWithApiKeyAsync("fixture-key", default);

        Assert.Equal(1, requests);
        Assert.True(snapshot.IsError);
        Assert.Equal(message, Assert.IsType<MetricLine.BadgeLine>(Assert.Single(snapshot.Lines)).Message);
    }

    // MARK: Devin

    private static System.Text.Json.Nodes.JsonObject DevinBody(bool hideDaily, string weeklyField) =>
        JsonUtil.ParseObject($$"""
            {"userStatus":{"planStatus":{
                "planInfo":{"hideDailyQuota":{{(hideDaily ? "true" : "false")}},"planName":"Max"},
                "dailyQuotaRemainingPercent":100,
                "dailyQuotaResetAtUnix":"1788940800",
                {{weeklyField}}
                "weeklyQuotaResetAtUnix":"1789286400",
                "overageBalanceMicros":"-44347"
            }
            }
            }
            """)!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Devin_OmittedWeeklyRemainingWithResetMeansExhausted(bool hideDaily)
    {
        var mapped = DevinUsageMapper.Map(DevinBody(hideDaily, ""));
        var weekly = Assert.IsType<MetricLine.ProgressLine>(mapped.Lines.Single(l => l.Label == "Weekly quota"));
        Assert.Equal(100, weekly.Used);
        Assert.Equal(100, weekly.Limit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_286_400), weekly.ResetsAt);
        Assert.Equal(MetricPeriod.WeekMs, weekly.PeriodDurationMs);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void Devin_MalformedWeeklyRemainingThrowsInsteadOfExhausted(string malformed)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DevinUsageMapper.Map(DevinBody(false, $"\"weeklyQuotaRemainingPercent\":{malformed},")));
    }

    // MARK: Helpers

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
