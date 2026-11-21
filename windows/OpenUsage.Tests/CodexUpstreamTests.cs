using System.Text;
using System.Text.Json.Nodes;
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
