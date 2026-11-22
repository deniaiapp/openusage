using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CodexTokenReportTests
{
    [Fact]
    public void AddsReportedTokensByUtcDateAndKeepsReportedZero()
    {
        var counts = CodexTokenReport.Parse(JsonUtil.ParseObject("""
            {"data":[
                {"date":"2026-11-22T00:00:00Z","totals":{"text_total_tokens":3000000000}},
                {"date":"2026-11-22","totals":{"text_total_tokens":"42"}},
                {"date":"2026-11-21","totals":{"text_total_tokens":0}},
                {"date":"2026-11-20","totals":{"text_total_tokens":null,"turns":8}},
                {"date":"2026-11-19","totals":{"uncached_text_input_tokens":50}}
            ]}
            """));

        Assert.NotNull(counts);
        Assert.Equal(2, counts.Count);
        Assert.Equal(3000000042d, counts[new DateOnly(2026, 11, 22)]);
        Assert.Equal(0d, counts[new DateOnly(2026, 11, 21)]);
    }

    [Fact]
    public void EmptyReportDoesNotInventZeroDays()
    {
        var counts = CodexTokenReport.Parse(JsonUtil.ParseObject("""{"data":[]}"""));
        Assert.NotNull(counts);
        Assert.Empty(counts);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[null]}""")]
    [InlineData("""{"data":[{"date":"2026-11-31","totals":{"text_total_tokens":42}}]}""")]
    [InlineData("""{"data":[{"date":"2026-11-22"}]}""")]
    [InlineData("""{"data":[{"date":"2026-11-22","totals":{"text_total_tokens":-1}}]}""")]
    [InlineData("""{"data":[{"date":"2026-11-22","totals":{"text_total_tokens":true}}]}""")]
    [InlineData("""{"data":[{"date":"2026-11-22","totals":{"text_total_tokens":"Infinity"}}]}""")]
    public void RejectsMalformedReport(string json) =>
        Assert.Null(CodexTokenReport.Parse(JsonUtil.ParseObject(json)));
}
