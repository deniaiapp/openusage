using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
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

    // #1322
    [Fact]
    public void Save_KeepsOtherFieldsAndRefusesToOverwriteANewerLogin()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"auth_mode":"chatgpt","tokens":{"access_token":"a1","refresh_token":"r1","account_id":"acc"}}""");
            var store = new CodexAuthStore();
            var auth = CodexAuthStore.ParseFile(path)!;
            auth.AccessToken = "a2";
            auth.RefreshToken = "r2";
            Assert.True(store.Save(auth));
            var saved = JsonUtil.ParseObject(File.ReadAllText(path))!;
            Assert.Equal("chatgpt", JsonUtil.String(saved["auth_mode"]));
            Assert.Equal("a2", JsonUtil.String(saved["tokens"]!["access_token"]));
            Assert.Equal("acc", JsonUtil.String(saved["tokens"]!["account_id"]));

            File.WriteAllText(path, """{"tokens":{"access_token":"a3","refresh_token":"r3"}}""");
            auth.AccessToken = "a4";
            auth.RefreshToken = "r4";
            Assert.False(store.Save(auth));
            Assert.Equal("a3", CodexAuthStore.ParseFile(path)!.AccessToken);
        }
        finally { File.Delete(path); }
    }

    // #1317
    [Fact]
    public void KeyringAccount_MatchesCodexStoreKey()
    {
        Assert.Equal("cli|ba7816bf8f01cfea", CodexAuthStore.KeyringAccount("abc"));
        Assert.All(CodexAuthStore.KeyringAccounts("~/.codex"), a => Assert.Matches("^cli\\|[0-9a-f]{16}$", a));
    }
}
