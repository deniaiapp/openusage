using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Providers.OpenCode;
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
        var key = new OllamaSigningKey { PublicKeyBase64 = "cHVi", Seed = new byte[32] };
        var snapshot = await new OllamaProvider(http, () => key).RefreshAsync();
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
    public void OpenCode_V1DatabaseWithEmptyCredentialTableKeepsAuthJson()
    {
        using var dir = new TempDir();
        dir.Db("opencode.db", "CREATE TABLE credential(id TEXT, integration_id TEXT, value TEXT, active INTEGER, time_updated INTEGER);");
        dir.AuthJson("""{"opencode-go":{"key":"file-key"},"other":5}""");
        Assert.Equal("file-key", new OpenCodeAuthStore(dir.Path).GoApiKey());
    }

    [Fact]
    public void OpenCode_ImportedDatabaseWinsAndLogoutIsRespected()
    {
        using var dir = new TempDir();
        dir.AuthJson("""{"opencode-go":{"key":"stale-file-key"}}""");
        dir.Db("opencode.db", ImportedCredentialSchema +
            "INSERT INTO credential VALUES('c1','opencode-go','{\"key\":\"db-key\"}',1,10);");
        Assert.Equal("db-key", new OpenCodeAuthStore(dir.Path).GoApiKey());

        dir.Exec("opencode.db", "DELETE FROM credential;");
        Assert.Null(new OpenCodeAuthStore(dir.Path).GoApiKey());
    }

    [Fact]
    public void OpenCode_ScanUnionsV1AndV2AndCountsMigratedRowsOnce()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Now;
        var ms = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        dir.Db("opencode.db", $$$"""
            CREATE TABLE message(id TEXT, time_created INTEGER, data TEXT);
            CREATE TABLE session_message(id TEXT, time_created INTEGER, data TEXT, type TEXT);
            INSERT INTO message VALUES('m1',{{{ms}}},'{"role":"assistant","providerID":"opencode-go","modelID":"glm","cost":1.0,"tokens":{"total":100}}');
            INSERT INTO session_message VALUES('m1',{{{ms}}},'{"model":{"id":"glm","providerID":"opencode-go"},"cost":1.0,"tokens":{"total":100}}','assistant');
            INSERT INTO session_message VALUES('m2',{{{ms}}},'{"model":{"id":"glm","providerID":"opencode"},"cost":2.0,"tokens":{"cache":{"read":5},"input":10,"output":5}}','assistant');
            INSERT INTO session_message VALUES('m3',{{{ms}}},'{"model":{"id":"glm","providerID":"opencode"},"cost":0.5,"status":"completed","tokens":{"total":30}}','compaction');
            INSERT INTO session_message VALUES('m4',{{{ms}}},'{"model":{"id":"glm","providerID":"opencode"},"cost":9.0,"status":"running"}','compaction');
            INSERT INTO session_message VALUES('m5',{{{ms}}},'{"model":{"id":"gpt","providerID":"openai"},"cost":9.0}','assistant');
            """);
        var scan = OpenCodeUsageScanner.Scan(now: now, dataDirectory: dir.Path);
        Assert.NotNull(scan);
        var today = scan!.Series.Daily.Single(d => d.Date == DailyUsageAccumulatorDay(now));
        Assert.Equal(3.5, today.CostUsd!.Value, 6);
        Assert.Equal(150, today.TotalTokens);
        Assert.True(OpenCodeUsageScanner.HasHostedUsage(dir.Path));
    }

    [Fact]
    public async Task OpenCode_GoFailureKeepsLocalSpendTiles()
    {
        using var dir = new TempDir();
        var ms = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        dir.AuthJson("""{"opencode-go":{"key":"k"}}""");
        dir.Db("opencode.db", $$$"""
            CREATE TABLE message(id TEXT, time_created INTEGER, data TEXT);
            INSERT INTO message VALUES('m1',{{{ms}}},'{"role":"assistant","providerID":"opencode-go","cost":1.25,"tokens":{"total":10}}');
            """);
        var http = new AppHttp(new HttpClient(new StubHandler(_ => (HttpStatusCode.Unauthorized, "{}"))));
        var snapshot = await new OpenCodeProvider(http, dir.Path).RefreshAsync();
        Assert.False(snapshot.IsError);
        Assert.NotNull(snapshot.Line("Today"));
        Assert.Null(snapshot.Line("Session"));

        using var empty = new TempDir();
        empty.AuthJson("""{"opencode-go":{"key":"k"}}""");
        var failed = await new OpenCodeProvider(http, empty.Path).RefreshAsync();
        Assert.True(failed.IsError);
        Assert.Contains("rejected", ((MetricLine.BadgeLine)failed.Lines[0]).Message);
    }

    private const string ImportedCredentialSchema = $"""
        CREATE TABLE migration(id TEXT);
        INSERT INTO migration VALUES('{OpenCodeAuthStore.CredentialsImportedMigration}');
        CREATE TABLE credential(id TEXT, integration_id TEXT, value TEXT, active INTEGER, time_updated INTEGER);
        """;

    private static string DailyUsageAccumulatorDay(DateTimeOffset date) =>
        OpenUsage.Core.Providers.DailyUsageAccumulator.DayKey(date);

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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("openusage-misc-").FullName;

        public void AuthJson(string json) => File.WriteAllText(System.IO.Path.Combine(Path, "auth.json"), json);

        public void Db(string name, string sql) => Exec(name, sql);

        public void Exec(string name, string sql)
        {
            using (var connection = new SqliteConnection($"Data Source={System.IO.Path.Combine(Path, name)};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
