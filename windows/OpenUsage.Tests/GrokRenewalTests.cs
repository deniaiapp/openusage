using System.Net;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class GrokRenewalTests
{
    [Theory]
    [InlineData(400, """{"error":"invalid_grant"}""", "Sign in again in Customize")]
    [InlineData(401, """{"error":"invalid_grant"}""", "Sign in again in Customize")]
    [InlineData(403, """{"error":"invalid_grant"}""", "Sign in again in Customize")]
    [InlineData(401, """{"error":"invalid_client"}""", "renewal failed (HTTP 401)")]
    [InlineData(429, """{"error":"temporarily_unavailable"}""", "renewal failed (HTTP 429)")]
    [InlineData(503, """{"error":"temporarily_unavailable"}""", "renewal failed (HTTP 503)")]
    [InlineData(200, """{"access_token":" "}""", "invalid sign-in tokens")]
    public async Task RenewalFailureOnlyAsksForLoginWhenTheGrantWasRefused(int status, string body, string expected)
    {
        using var http = Http(request =>
        {
            Assert.Equal(GrokProvider.RefreshUrl, request.RequestUri);
            return Response(status, body);
        });

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new GrokDeviceLogin(http).RenewAsync(Auth(DateTimeOffset.UtcNow.AddMinutes(-1)), default));

        Assert.Contains(expected, error.Message);
        Assert.Equal(expected.StartsWith("Sign in", StringComparison.Ordinal),
            error.Message.Contains("Sign in again in Customize"));
    }

    [WindowsFact]
    public async Task KeepsStillValidAppTokenDuringAnOutageButPersistsSuccessfulRenewal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "keys.bin");
            var keys = new ApiKeyStore(path);
            var auth = Auth(DateTimeOffset.UtcNow.AddMinutes(4));
            var renewalSucceeds = false;
            var calls = new List<Uri>();
            using var http = Http(request =>
            {
                calls.Add(request.RequestUri!);
                if (request.RequestUri == GrokProvider.RefreshUrl)
                {
                    if (!renewalSucceeds) throw new HttpRequestException("fixture offline");
                    return Response(200, """{"access_token":"fixture-new-token","expires_in":3600}""");
                }
                Assert.Equal(renewalSucceeds ? "fixture-new-token" : auth.AccessToken, request.Headers.Authorization?.Parameter);
                return request.RequestUri == GrokProvider.CreditsConfigUrl
                    ? Response(200, """
                        {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-11-17T00:00:00Z","end":"2026-11-24T00:00:00Z"},"creditUsagePercent":25}}
                        """)
                    : Response(200, """{"subscription_tier_display":"Fixture Plan"}""");
            });

            keys.Set("grok.oauth", JsonSerializer.Serialize(auth));
            foreach (var succeeds in new[] { false, true })
            {
                renewalSucceeds = succeeds;
                calls.Clear();
                var snapshot = await new GrokProvider(http, new ApiKeyStore(path)).RefreshAsync();
                Assert.False(snapshot.IsError);
                Assert.Equal("Fixture Plan", snapshot.Plan);
                Assert.NotNull(snapshot.Line("Weekly limit"));
                Assert.Equal(new[] { GrokProvider.RefreshUrl, GrokProvider.CreditsConfigUrl, GrokProvider.SettingsUrl }, calls);
                var saved = JsonSerializer.Deserialize<GrokOAuth>(new ApiKeyStore(path).Get("grok.oauth")!);
                Assert.Equal(succeeds ? "fixture-new-token" : auth.AccessToken, saved!.AccessToken);
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExpiredTokenDoesNotHideTheTransportFailure()
    {
        using var http = Http(request =>
        {
            Assert.Equal(GrokProvider.RefreshUrl, request.RequestUri);
            throw new HttpRequestException("fixture offline");
        });
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new GrokDeviceLogin(http).RenewAsync(Auth(DateTimeOffset.UtcNow.AddMinutes(-1)), default));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.DoesNotContain("grok login", error.Message);
    }

    private static GrokOAuth Auth(DateTimeOffset expiry)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}")).TrimEnd('=');
        return new GrokOAuth($"fixture.{payload}.signature", "fixture-refresh-token", expiry);
    }

    private static AppHttp Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new HttpClient(new StubHandler(send)));

    private static HttpResponseMessage Response(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI is required.";
        }
    }
}
