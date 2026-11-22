using System.Net;
using System.Text;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class GrokRenewalTests
{
    [Theory]
    [InlineData(400, """{"error":"invalid_grant"}""", "Run `grok login` again")]
    [InlineData(401, """{"error":"invalid_grant"}""", "Run `grok login` again")]
    [InlineData(403, """{"error":"invalid_grant"}""", "Run `grok login` again")]
    [InlineData(401, """{"error":"invalid_client"}""", "renewal failed (HTTP 401)")]
    [InlineData(429, """{"error":"temporarily_unavailable"}""", "limiting sign-in requests")]
    [InlineData(503, """{"error":"invalid_grant"}""", "renewal failed (HTTP 503)")]
    [InlineData(200, """{"access_token":" "}""", "invalid sign-in response")]
    public async Task RenewalFailureOnlyAsksForLoginWhenTheGrantWasRefused(int status, string body, string expected)
    {
        using var http = Http(request =>
        {
            Assert.Equal(GrokProvider.RefreshUrl, request.RequestUri);
            return Response(status, body);
        });

        var provider = new GrokProvider(http);
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            provider.ProbeAsync(Auth(DateTimeOffset.UtcNow.AddMinutes(-1)), default));

        Assert.Contains(expected, error.Message);
        Assert.Equal(expected.StartsWith("Run", StringComparison.Ordinal),
            error.Message.Contains("Run `grok login` again"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsStillValidTokenDuringAnOutageButUsesNewTokenAfterSuccessfulRenewal(bool renewalSucceeds)
    {
        var auth = Auth(DateTimeOffset.UtcNow.AddMinutes(4));
        var calls = new List<Uri>();
        using var http = Http(request =>
        {
            calls.Add(request.RequestUri!);
            if (request.RequestUri == GrokProvider.RefreshUrl)
            {
                if (!renewalSucceeds) throw new HttpRequestException("fixture offline");
                return Response(200, """{"access_token":"fixture-new-token"}""");
            }
            Assert.Equal(renewalSucceeds ? "fixture-new-token" : auth.Token, request.Headers.Authorization?.Parameter);
            return request.RequestUri == GrokProvider.CreditsConfigUrl
                ? Response(200, """
                    {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-11-17T00:00:00Z","end":"2026-11-24T00:00:00Z"},"creditUsagePercent":25}}
                    """)
                : Response(200, """{"subscription_tier_display":"Fixture Plan"}""");
        });

        var snapshot = await new GrokProvider(http).ProbeAsync(auth, default);

        Assert.False(snapshot.IsError);
        Assert.Equal("Fixture Plan", snapshot.Plan);
        Assert.NotNull(snapshot.Line("Weekly limit"));
        Assert.Equal(new[] { GrokProvider.RefreshUrl, GrokProvider.CreditsConfigUrl, GrokProvider.SettingsUrl }, calls);
    }

    [Fact]
    public async Task ExpiredTokenDoesNotHideTheTransportFailure()
    {
        using var http = Http(request =>
        {
            Assert.Equal(GrokProvider.RefreshUrl, request.RequestUri);
            throw new HttpRequestException("fixture offline");
        });
        var provider = new GrokProvider(http);

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            provider.ProbeAsync(Auth(DateTimeOffset.UtcNow.AddMinutes(-1)), default));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.DoesNotContain("grok login", error.Message);
    }

    private static GrokAuthState Auth(DateTimeOffset expiry)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}")).TrimEnd('=');
        return new GrokAuthState { Token = $"fixture.{payload}.signature", RefreshToken = "fixture-refresh-token" };
    }

    private static AppHttp Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new HttpClient(new StubHandler(send)));

    private static HttpResponseMessage Response(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
