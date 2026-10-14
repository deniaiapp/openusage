using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Providers.OpenRouter;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Support;

namespace OpenUsage.Tests;

public class MapperTests
{
    [Fact]
    public void ClaudeMapper_ReadsSessionAndWeekly()
    {
        var json = JsonUtil.ParseObject("""
            {
              "five_hour": { "utilization": 42, "resets_at": "2026-09-13T12:00:00Z" },
              "seven_day": { "utilization": 18, "resets_at": "2026-09-20T12:00:00Z" },
              "limits": [{ "kind": "weekly_scoped", "percent": 7, "resets_at": "2026-09-20T12:00:00Z",
                           "scope": { "model": { "display_name": "Fable" } } }],
              "extra_usage": { "is_enabled": true, "used_credits": 150, "monthly_limit": 1000 }
            }
            """)!;
        var mapped = ClaudeUsageMapper.Map(json, new ClaudeOAuth { SubscriptionType = "pro", RateLimitTier = "5x" });
        Assert.Equal("Pro 5x", mapped.Plan);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Session" && p.Used == 42);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Weekly");
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Fable" && p.Used == 7);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Extra usage spent");
    }

    [Fact]
    public void CodexMapper_ClassifiesWindowsAndCredits()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "plan_type": "pro",
              "rate_limit": {
                "primary_window": { "used_percent": 12, "reset_after_seconds": 3600, "limit_window_seconds": 18000 },
                "secondary_window": { "used_percent": 40, "reset_after_seconds": 86400, "limit_window_seconds": 604800 }
              },
              "credits": { "balance": 821.4 },
              "rate_limit_reset_credits": { "available_count": 2, "credits": [
                { "status": "available", "expires_at": "2026-10-01T00:00:00Z" },
                { "status": "available", "expires_at": "2026-10-08T00:00:00Z" }
              ]}
            }
            """);
        var response = new AppHttpResponse(200, body, new Dictionary<string, string>());
        var mapped = CodexUsageMapper.Map(response, null, DateTimeOffset.Parse("2026-09-13T00:00:00Z"));
        Assert.Equal("Pro 200", mapped.Plan);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Session" && p.Used == 12);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Weekly" && p.Used == 40);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ValuesLine v && v.Label == "Credits");
        Assert.Contains(mapped.Lines, l => l is MetricLine.ValuesLine v && v.Label == "Rate Limit Resets" && v.Items[0].Number == 2);
    }

    [Fact]
    public void CodexMapper_ReadsCamelCaseWindowFields()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "plan_type": "pro",
              "rate_limit": {
                "primaryWindow": { "usedPercent": 8, "limitWindowSeconds": 18000, "reset_after_seconds": 1000 }
              }
            }
            """);
        var mapped = CodexUsageMapper.Map(
            new AppHttpResponse(200, body, new Dictionary<string, string>()),
            null,
            DateTimeOffset.Parse("2026-09-13T00:00:00Z"));
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Session" && p.Used == 8);
    }

    [Fact]
    public void OpenRouterMapper_CreditsAndKeySpend()
    {
        var credits = JsonUtil.ParseObject("""{ "total_usage": 12.5, "total_credits": 50 }""")!;
        var lines = OpenRouterUsageMapper.CreditsLines(credits);
        Assert.Contains(lines, l => l is MetricLine.ProgressLine p && p.Label == "Credits" && p.Used == 12.5 && p.Limit == 50);
        Assert.Contains(lines, l => l is MetricLine.ValuesLine v && v.Label == "Balance" && v.Items[0].Number == 37.5);

        var key = JsonUtil.ParseObject("""{ "usage_daily": 1.25, "is_free_tier": false, "limit": 10, "limit_remaining": 4 }""")!;
        var metrics = OpenRouterUsageMapper.KeyMetrics(key);
        Assert.Equal("Pay as you go", metrics.Plan);
        Assert.Contains(metrics.Lines, l => l is MetricLine.ValuesLine v && v.Label == "Today");
        Assert.Contains(metrics.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Key Limit" && p.Used == 6);
    }

    [Fact]
    public void CopilotMapper_CreditsPercent()
    {
        var body = JsonUtil.ParseObject("""
            {
              "copilot_plan": "copilot_pro",
              "quota_reset_date": "2026-10-01T00:00:00Z",
              "quota_snapshots": {
                "premium_interactions": { "entitlement": 300, "remaining": 210, "percent_remaining": 70, "overage_permitted": true, "overage_count": 0 }
              }
            }
            """)!;
        var mapped = CopilotUsageMapper.Map(body);
        Assert.Equal("Copilot Pro", mapped.Plan);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ProgressLine p && p.Label == "Credits" && p.Used == 30);
        Assert.Contains(mapped.Lines, l => l is MetricLine.ValuesLine v && v.Label == "Extra Usage");
    }

    [Fact]
    public void GrokDecoder_WeeklyPool()
    {
        var body = JsonUtil.ParseObject("""
            { "config": {
                "creditUsagePercent": 22.5,
                "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-09-07T00:00:00Z", "end": "2026-09-14T00:00:00Z" },
                "onDemandCap": { "val": 2500 }
            }}
            """)!;
        var lines = GrokUsageMapper.MapCreditsConfig(body);
        Assert.Contains(lines, l => l is MetricLine.ProgressLine p && p.Label == "Weekly limit" && p.Used == 22.5);
        Assert.Contains(lines, l => l is MetricLine.BadgeLine b && b.Message == "2500 cap");
    }

    [Fact]
    public void ZaiMapper_SessionAndWeeklyWindows()
    {
        var quota = JsonUtil.ParseObject("""
            { "data": { "limits": [
                { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "percentage": 11 },
                { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "percentage": 44 },
                { "type": "TIME_LIMIT", "used": 2, "limit": 30 }
            ]}}
            """)!;
        var lines = ZaiUsageMapper.MapQuota(quota);
        Assert.Contains(lines, l => l is MetricLine.ProgressLine p && p.Label == "Session" && p.Used == 11);
        Assert.Contains(lines, l => l is MetricLine.ProgressLine p && p.Label == "Weekly" && p.Used == 44);
        Assert.Contains(lines, l => l is MetricLine.ProgressLine p && p.Label == "Web searches");
    }

    [Fact]
    public void Pace_ProjectsBehindWhenBurnIsTooFast()
    {
        var now = DateTimeOffset.Parse("2026-09-13T10:00:00Z");
        var resets = now.AddHours(1);
        var result = Pace.Evaluate(used: 90, limit: 100, resetsAt: resets, periodDuration: TimeSpan.FromHours(5), now: now);
        Assert.NotNull(result);
        Assert.Equal(Pace.Status.Behind, result!.Status);
    }

    [Fact]
    public void MetricFormatter_AbbreviatesThousands()
    {
        Assert.Equal("$1.2K", MetricFormatter.Number(1200, MetricKind.Dollars, MetricFormatter.Style.Row));
        Assert.Equal("42%", MetricFormatter.Number(42, MetricKind.Percent, MetricFormatter.Style.Row));
        Assert.Equal("1.5M", MetricFormatter.Number(1_500_000, MetricKind.Count, MetricFormatter.Style.Tray));
    }

    [Fact]
    public void AppPaths_ExpandsHome()
    {
        var expanded = AppPaths.Expand("~/.claude/.credentials.json");
        Assert.Contains(".claude", expanded);
        Assert.False(expanded.StartsWith('~'));
    }
}
