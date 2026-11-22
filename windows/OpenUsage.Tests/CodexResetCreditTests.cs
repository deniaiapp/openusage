using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public class CodexResetCreditTests
{
    [Theory]
    [InlineData("1", "2", 200, 1, "2026-11-22")]
    [InlineData("0", "2", 200, 0, null)]
    [InlineData("2", "2", 200, 2, "2026-11-23")]
    [InlineData("null", "2", 200, 2, "2026-11-23")]
    [InlineData("42e24", "2", 200, 2, "2026-11-23")]
    [InlineData("1", "42e24", 200, 1, "2026-11-22")]
    [InlineData("1", "1", 503, 1, "2026-11-22")]
    [InlineData("1.7", "-1", 200, 1, "2026-11-22")]
    [InlineData("null", "42e24", 200, null, null)]
    public void Map_UsesCurrentResetCountAndOnlyMatchingExpiries(
        string usageCount, string listedCount, int listStatus, int? expectedCount, string? expectedExpiry)
    {
        var now = DateTimeOffset.Parse("2026-11-21T00:00:00Z");
        var usage = Response(200, $$"""
            {
              "rate_limit": {
                "primary_window": {"used_percent":31},
                "secondary_window": {"used_percent":47}
              },
              "rate_limit_reset_credits": {
                "available_count": {{usageCount}},
                "credits": [{"status":"available","expires_at":"2026-11-22T00:00:00Z"}]
              }
            }
            """);
        var list = Response(listStatus, $$"""
            {
              "available_count": {{listedCount}},
              "credits": [
                {"status":"used","expires_at":"2026-11-24T00:00:00Z"},
                {"status":"available","expires_at":"2026-11-23T00:00:00Z"}
              ]
            }
            """);

        var (_, lines) = CodexUsageMapper.Map(usage, list, now);
        Assert.Collection(lines.Take(2),
            session =>
            {
                Assert.Equal("Session", session.Label);
                Assert.Equal(31, Assert.IsType<MetricLine.ProgressLine>(session).Used);
            },
            weekly =>
            {
                Assert.Equal("Weekly", weekly.Label);
                Assert.Equal(47, Assert.IsType<MetricLine.ProgressLine>(weekly).Used);
            });
        if (expectedCount is not { } count)
        {
            Assert.Equal(2, lines.Count);
            return;
        }

        var resets = Assert.IsType<MetricLine.ValuesLine>(Assert.Single(lines.Skip(2)));
        Assert.Equal("Rate Limit Resets", resets.Label);
        Assert.Equal(count, Assert.Single(resets.Items).Number);
        if (expectedExpiry is null)
            Assert.Empty(resets.ExpiriesAt!);
        else
            Assert.Equal(DateTimeOffset.Parse(expectedExpiry + "T00:00:00Z"), Assert.Single(resets.ExpiriesAt!));
    }

    private static AppHttpResponse Response(int status, string body) =>
        new(status, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>());
}
