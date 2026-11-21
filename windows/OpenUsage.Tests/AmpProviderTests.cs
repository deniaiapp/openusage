using System.Net;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Amp;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class AmpProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private const string Challenge = """
        {"device_code":"fixture-device","user_code":"A+B & C","expires_in":600,"interval":1,
         "verification_uri_complete":"https://example.invalid/"}
        """;
    private const string Balance = """
        Signed in as fixture@example.invalid (example)
        **Amp Megawatt Tier**: agent usage $15.20 of $20.00 remaining (76%), orb usage 1,000.5h of 1,200h a1.small orb hours remaining (83%) - period 2026-11-09 to 2026-12-09
        Amp Free: 61% remaining today (resets daily)
        Individual credits: $57.45 remaining
        """;

    [Fact]
    public async Task ConnectUsesTheAmpVerificationPageAndValidatesBeforeSaving()
    {
        string? saved = null;
        var requests = new List<string>();
        using var http = MakeHttp(async request =>
        {
            var url = request.RequestUri!;
            var content = await request.Content!.ReadAsStringAsync();
            requests.Add(url.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            if (url.AbsolutePath == "/user_management/authorize/device")
            {
                Assert.Equal("authapi.ampcode.com", url.Host);
                Assert.Equal("client_id=client_01JNSEYM3V0J5AXK4YXNXRXTGP", content);
                return Reply(Challenge);
            }
            if (url.AbsolutePath == "/user_management/authenticate")
            {
                Assert.Equal("authapi.ampcode.com", url.Host);
                Assert.Contains("device_code=fixture-device", content);
                return Reply(Grant(AccessToken(300), "fixture-refresh"));
            }
            Assert.Null(saved);
            Assert.Equal("https://ampcode.com/api/internal?userDisplayBalanceInfo", url.AbsoluteUri);
            Assert.Equal(AccessToken(300), request.Headers.Authorization?.Parameter);
            Assert.Equal("""{"method":"userDisplayBalanceInfo","params":{}}""", content);
            return Reply(Usage(Balance));
        });
        var provider = new AmpProvider(http, () => saved, credential => saved = credential,
            new FixtureClock(), (_, _) => Task.CompletedTask);
        DevicePrompt? prompt = null;

        var snapshot = await provider.ConnectAsync(value => prompt = value, default);

        Assert.Equal(["/user_management/authorize/device", "/user_management/authenticate", "/api/internal"], requests);
        Assert.Equal("A+B & C", prompt?.Code);
        Assert.Equal("https://ampcode.com/auth/cli/device?user_code=A%2BB%20%26%20C", prompt?.VerificationUrl.AbsoluteUri);
        Assert.True(provider.HasAppCredential);
        Assert.Contains("fixture-refresh", saved);
        Assert.Equal("Megawatt", snapshot.Plan);
        Assert.Equal(["Agent Usage", "Orb Hours", "Amp Free", "Individual Credits"], snapshot.Lines.Select(line => line.Label));
        var metrics = LimitsProjection.Project(snapshot, provider.WidgetDescriptors);
        Assert.Equal(4.8, metrics["agentUsage"].Used);
        Assert.Equal(199.5, metrics["orbHours"].Used);
        Assert.Equal(39, metrics["free"].Used);
        Assert.Equal(57.45, metrics["credits"].Available);
        var billed = Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Orb Hours"));
        Assert.Equal(new DateTimeOffset(2026, 12, 9, 0, 0, 0, TimeSpan.Zero), billed.ResetsAt);
        Assert.Equal(30 * MetricPeriod.DayMs, billed.PeriodDurationMs);
        Assert.Null(Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Amp Free")).ResetsAt);
    }

    [Theory]
    [InlineData(-10, false)]
    [InlineData(300, true)]
    public async Task RenewsExpiredOrRejectedTokensExactlyOnceAndPersistsTheRotation(int initialExpiry, bool unauthorized)
    {
        var initial = AccessToken(initialExpiry);
        var updated = AccessToken(600);
        var credential = Credential(initial, "old-refresh");
        var renewals = 0;
        var reads = 0;
        using var http = MakeHttp(async request =>
        {
            if (request.RequestUri!.Host == "authapi.ampcode.com")
            {
                renewals++;
                Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
                using var fields = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                Assert.Equal("refresh_token", fields.RootElement.GetProperty("grant_type").GetString());
                Assert.Equal("old-refresh", fields.RootElement.GetProperty("refresh_token").GetString());
                return Reply(Grant(updated, "new-refresh"));
            }
            reads++;
            if (unauthorized && reads == 1)
            {
                Assert.Equal(initial, request.Headers.Authorization?.Parameter);
                return Reply("{}", HttpStatusCode.Unauthorized);
            }
            Assert.Contains("new-refresh", credential);
            Assert.Equal(updated, request.Headers.Authorization?.Parameter);
            return Reply(Usage("Amp Free: $1.00 / $4.00 remaining"));
        });
        var provider = new AmpProvider(http, () => credential, value => credential = value, new FixtureClock());

        var snapshot = await provider.RefreshAsync(default);

        Assert.Equal(1, renewals);
        Assert.Equal(unauthorized ? 2 : 1, reads);
        Assert.Equal("Free", snapshot.Plan);
        Assert.Equal(75, Assert.IsType<MetricLine.ProgressLine>(Assert.Single(snapshot.Lines)).Used);
    }

    [Fact]
    public async Task FailedReplacementLeavesThePreviousSignInInPlace()
    {
        var previous = Credential(AccessToken(300), "old-refresh");
        var saved = previous;
        using var http = MakeHttp(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/user_management/authorize/device" => Reply(Challenge),
            "/user_management/authenticate" => Reply(Grant(AccessToken(400), "other-refresh")),
            _ => Reply(Usage("Unknown balance format"))
        }));
        var provider = new AmpProvider(http, () => saved, value => saved = value,
            new FixtureClock(), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ConnectAsync(_ => { }, default));
        Assert.Equal(previous, saved);
    }

    [Fact]
    public void ExistingLayoutGetsTheAmpMetricOrderAndVisibilityWithoutTrayPins()
    {
        using var dir = new TempLayout();
        var settings = AppSettings.Load(dir.Path);
        settings.EnabledProviders.Add("amp");
        settings.EnabledMetrics.Add("claude.session");
        var layout = new LayoutStore(settings, [new AmpProvider(dir.Http, () => null, _ => { })]);

        Assert.Equal(["amp.agent", "amp.free"], layout.VisibleMetrics("amp", false).Select(metric => metric.Id));
        var expanded = layout.VisibleMetrics("amp", true).ToArray();
        Assert.Equal(["amp.agent", "amp.orbHours", "amp.free", "amp.credits"], expanded.Select(metric => metric.Id));
        Assert.All(expanded, metric => Assert.False(layout.IsPinned(metric.Id)));
    }

    private static string AccessToken(int seconds)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { exp = Now.AddSeconds(seconds).ToUnixTimeSeconds() });
        var encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"fixture.{encoded}.test";
    }
    private static string Grant(string access, string refresh) =>
        JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh });
    private static string Credential(string access, string refresh) =>
        JsonSerializer.Serialize(new { AccessToken = access, RefreshToken = refresh });
    private static string Usage(string text) => JsonSerializer.Serialize(new { result = new { displayText = text } });

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static AppHttp MakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new HttpClient(new Handler(send)));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TempLayout : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("openusage-amp");
        public string Path => System.IO.Path.Combine(_dir.FullName, "settings.json");
        public AppHttp Http { get; } = MakeHttp(_ => throw new InvalidOperationException("Unexpected request"));
        public void Dispose()
        {
            Http.Dispose();
            _dir.Delete(true);
        }
    }
}
