using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CursorBillingPlanTests
{
    [Theory]
    [InlineData(503, "{}")]
    [InlineData(200, "{}")]
    [InlineData(200, """{"planName":"pro"}""")]
    public async Task FailedPlanReadKeepsUnifiedBillingUntilCursorReportsADifferentPlan(int status, string body)
    {
        using var handler = new CursorHandler();
        using var http = new AppHttp(new HttpClient(handler));
        var provider = Provider(http);
        var login = Login("fixture-first");

        var initial = await provider.ProbeAsync(login, default);
        Assert.Equal("Pro", initial.Plan);
        Assert.Equal(7, Credits(initial));

        handler.Plan = ((HttpStatusCode)status, body);
        var held = await provider.ProbeAsync(login, default);
        Assert.Equal("Pro", held.Plan);
        Assert.Equal(8, Credits(held));
        Assert.DoesNotContain(CursorProvider.CreditsUrl.AbsolutePath, handler.Paths);

        handler.Plan = (HttpStatusCode.OK, """{"isMerged":false,"planName":"free"}""");
        var updated = await provider.ProbeAsync(login, default);
        Assert.Equal("Free", updated.Plan);
        Assert.Equal(42, Credits(updated));
    }

    [Fact]
    public async Task FailedPlanReadAfterRenewalKeepsTheSameAccountsMode()
    {
        using var handler = new CursorHandler();
        using var http = new AppHttp(new HttpClient(handler));
        var provider = Provider(http);
        Assert.Equal(7, Credits(await provider.ProbeAsync(Login("fixture-first"), default)));

        var expired = Login("fixture-first", minutesUntilExpiry: -1);
        expired.RefreshToken = "fixture-refresh";
        handler.RenewedAccessToken = Login("fixture-first", minutesUntilExpiry: 120).AccessToken;
        handler.Plan = (HttpStatusCode.ServiceUnavailable, "{}");
        Assert.Equal(8, Credits(await provider.ProbeAsync(expired, default)));
        Assert.Contains(CursorProvider.RefreshUrl.AbsolutePath, handler.Paths);
        Assert.DoesNotContain(CursorProvider.CreditsUrl.AbsolutePath, handler.Paths);
    }

    [Fact]
    public async Task FailedPlanReadOnDifferentSignInDoesNotReusePreviousPlan()
    {
        using var handler = new CursorHandler();
        using var http = new AppHttp(new HttpClient(handler));
        var provider = Provider(http);
        Assert.Equal("Pro", (await provider.ProbeAsync(Login("fixture-first"), default)).Plan);

        handler.Plan = (HttpStatusCode.ServiceUnavailable, "{}");
        var other = await provider.ProbeAsync(Login("fixture-other"), default);
        Assert.Null(other.Plan);
        Assert.Equal(42, Credits(other));
        Assert.Single(handler.Paths, path => path.EndsWith("/GetComplatCreditBalance", StringComparison.Ordinal));
    }

    private static CursorProvider Provider(AppHttp http) => new(http,
        new CursorUsageHistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

    private static double Credits(ProviderSnapshot snapshot) =>
        Assert.Single(Assert.IsType<MetricLine.ValuesLine>(snapshot.Line("Credits")).Items).Number;

    private static CursorAuthState Login(string subject, int minutesUntilExpiry = 60)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = subject,
            exp = DateTimeOffset.UtcNow.AddMinutes(minutesUntilExpiry).ToUnixTimeSeconds()
        });
        var base64 = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new CursorAuthState { AccessToken = $"fixture.{base64}.signature", Source = "fixture" };
    }

    private sealed class CursorHandler : HttpMessageHandler
    {
        private int _unifiedReads;
        public ConcurrentQueue<string> Paths { get; } = new();
        public string? RenewedAccessToken { get; set; }
        public (HttpStatusCode Status, string Body) Plan { get; set; } =
            (HttpStatusCode.OK, """{"isMerged":true,"planInfo":{"planName":"pro"}}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            var (status, body) = path switch
            {
                var p when p == CursorProvider.UsageUrl.AbsolutePath =>
                    (HttpStatusCode.OK, """{"planUsage":{"limit":2000,"totalPercentUsed":15}}"""),
                var p when p == CursorProvider.PlanUrl.AbsolutePath => Plan,
                var p when p == CursorProvider.RefreshUrl.AbsolutePath && RenewedAccessToken is { } token =>
                    (HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = token })),
                var p when p.EndsWith("/GetComplatCreditBalance", StringComparison.Ordinal) =>
                    (HttpStatusCode.OK, JsonSerializer.Serialize(new { usdRemainingCents = 600 + 100 * ++_unifiedReads })),
                var p when p == CursorProvider.CreditsUrl.AbsolutePath =>
                    (HttpStatusCode.OK, """{"hasCreditGrants":true,"totalCents":4200}"""),
                var p when p == CursorProvider.GrokBotUrl.AbsolutePath || p == CursorProvider.StripeUrl.AbsolutePath
                    || p == "/api/dashboard/v2/usage-status" => (HttpStatusCode.OK, "{}"),
                var p when p == CursorSpendHistoryFetcher.UsageEventsUrl.AbsolutePath => (HttpStatusCode.Forbidden, "{}"),
                _ => throw new InvalidOperationException($"Unexpected fixture request: {path}")
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
