using System.Net;
using System.Text;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Muse;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class MuseProviderTests
{
    private const string Challenge = """
        {"device_code":"fixture-device","user_code":"MUSE-CODE","interval":1,"expires_in":600,
         "verification_uri_complete":"https://auth.meta.com/oauth/device/?code=MUSE-CODE"}
        """;
    private const string Account = """
        {"is_subs_active":true,"subs_tier_name":"Pro","user_email":"fixture@example.invalid","key":"do-not-store",
         "subs_usage":{"window":{"used_percent":25},"weekly":{"used_percent":40}}}
        """;

    [Fact]
    public async Task SignInPollsAndValidatesUsageBeforeSavingOnlyTheAuthorizedToken()
    {
        var polls = 0;
        var waits = new List<TimeSpan>();
        string? saved = null;
        using var http = MakeHttp(async request =>
        {
            var content = await request.Content!.ReadAsStringAsync();
            return request.RequestUri!.AbsolutePath switch
            {
                "/oidc/device/authorization/" => content == "client_id=1031625952748946"
                    ? Reply(Challenge) : throw new InvalidOperationException("Unexpected start fields"),
                "/oidc/device/token/" => TokenReply(),
                "/muse-code/key" => UsageReply(),
                _ => throw new InvalidOperationException("Unexpected request")
            };

            HttpResponseMessage TokenReply()
            {
                Assert.Contains("device_code=fixture-device", content);
                Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", content);
                return ++polls switch
                {
                    1 => Reply("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest),
                    2 => Reply("""{"error":"slow_down"}""", HttpStatusCode.BadRequest),
                    _ => Reply("""{"access_token":"fixture-auth"}""")
                };
            }

            HttpResponseMessage UsageReply()
            {
                Assert.Null(saved);
                Assert.Equal("fixture-auth", request.Headers.Authorization?.Parameter);
                Assert.Equal("""{"onboard":false}""", content);
                return Reply(Account);
            }
        });
        var provider = new MuseProvider(http, () => saved, token => saved = token,
            (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        DevicePrompt? prompt = null;
        var snapshot = await provider.ConnectAsync(value => prompt = value, default);

        Assert.Equal("MUSE-CODE", prompt?.Code);
        Assert.Equal("auth.meta.com", prompt?.VerificationUrl.Host);
        Assert.Equal([1d, 1d, 6d], waits.Select(wait => wait.TotalSeconds));
        Assert.Equal("fixture-auth", saved);
        Assert.Equal("Pro", snapshot.Plan);
        var resources = LimitsProjection.Project(snapshot, provider.WidgetDescriptors);
        Assert.Equal(25, resources["session"].Used);
        Assert.Equal(40, resources["weekly"].Used);
    }

    [Theory]
    [InlineData("""{"is_subs_active":false}""")]
    [InlineData("""{"is_subs_active":true,"subs_usage":{"weekly":{}}}""")]
    public async Task InvalidReplacementLeavesConnectedTokenUntouched(string account)
    {
        using var http = MakeHttp(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/oidc/device/authorization/" => Reply(Challenge),
            "/oidc/device/token/" => Reply("""{"access_token":"fixture-new"}"""),
            "/muse-code/key" => Reply(account),
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var token = "fixture-old";
        var provider = new MuseProvider(http, () => token, updated => token = updated, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ConnectAsync(_ => { }, default));
        Assert.Equal("fixture-old", token);
    }

    [Fact]
    public async Task RejectsForeignVerificationPageWithoutPromptingOrPolling()
    {
        using var http = MakeHttp(request =>
        {
            Assert.Equal("/oidc/device/authorization/", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Challenge.Replace("auth.meta.com", "meta.com.example.invalid")));
        });
        var provider = new MuseProvider(http, () => null, _ => throw new InvalidOperationException("Unexpected save"));
        await Assert.ThrowsAsync<ProviderRequestException>(() => provider.ConnectAsync(_ => throw new InvalidOperationException("Unexpected prompt"), default));
    }

    [Fact]
    public async Task IdleUsageKeepsOnlyLastWindowsThatHaveNotReset()
    {
        using var http = MakeHttp(_ => Task.FromResult(Reply("""{"is_subs_active":true}""")));
        var provider = new MuseProvider(http, () => "fixture-auth", _ => { });
        provider.AttachSnapshots(() => ProviderSnapshot.Make(provider.Provider, null, [
            MetricLine.Progress("Session", 25, 100, ProgressFormat.Percent, DateTimeOffset.UtcNow.AddMinutes(-1), MetricPeriod.SessionMs),
            MetricLine.Progress("Weekly", 40, 100, ProgressFormat.Percent, DateTimeOffset.UtcNow.AddDays(2), MetricPeriod.WeekMs)
        ], DateTimeOffset.UtcNow));
        var snapshot = await provider.RefreshAsync(default);
        Assert.Equal(0, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Session")).Used);
        Assert.Equal(40, Assert.IsType<MetricLine.ProgressLine>(snapshot.Line("Weekly")).Used);
    }

    [Fact]
    public void ExistingLayoutSeedsApprovedMuseDefaultsInOrder()
    {
        using var http = MakeHttp(_ => throw new InvalidOperationException("Unexpected request"));
        var provider = new MuseProvider(http, () => null, _ => { });
        var dir = Directory.CreateTempSubdirectory("openusage-muse");
        try
        {
            var settings = AppSettings.Load(Path.Combine(dir.FullName, "settings.json"));
            settings.EnabledProviders.Add("muse");
            settings.EnabledMetrics.Add("claude.session");
            settings.PinnedMetrics.Add("claude.session");
            var layout = new LayoutStore(settings, [provider]);
            Assert.Equal(["muse.session", "muse.weekly"], layout.VisibleMetrics("muse", false).Select(metric => metric.Id));
            Assert.True(layout.IsPinned("muse.session"));
            Assert.False(layout.IsPinned("muse.weekly"));
        }
        finally { dir.Delete(true); }
    }

    private static AppHttp MakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => new(new HttpClient(new Handler(handler)));

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
