using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class DevinSignInTests
{
    [Theory]
    [InlineData("fixture-session")]
    [InlineData("devin-session-token$fixture-session")]
    public async Task ValidatedCallbackSavesSessionAndRefreshesUsage(string returnedToken)
    {
        const string session = "devin-session-token$fixture-session";
        Uri? prompt = null;
        string? stored = null;
        int exchanges = 0, statusReads = 0;
        using var http = new AppHttp(new HttpClient(new Fixture(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri == DevinLogin.TokenUrl)
            {
                exchanges++;
                Assert.Equal("fixture-code", (string?)body?["code"]);
                var verifier = Assert.IsType<string>((string?)body?["code_verifier"]);
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Assert.Equal(challenge, HttpUtility.ParseQueryString(prompt!.Query)["code_challenge"]);
                return Response(HttpStatusCode.OK, $"{{\"token\":\"{returnedToken}\"}}");
            }
            statusReads++;
            Assert.Equal(DevinProvider.StatusUrl, request.RequestUri);
            Assert.Equal(session, (string?)body?["metadata"]?["apiKey"]);
            return Response(HttpStatusCode.OK,
                """{"userStatus":{"planStatus":{"weeklyQuotaRemainingPercent":48,"planInfo":{"planName":"Fixture Plan"}}}}""");
        })));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new DevinProvider(http, () => stored, token => stored = token);
        Assert.False(await provider.HasLocalCredentialsAsync(deadline.Token));
        Assert.True((await provider.RefreshAsync(deadline.Token)).IsError);

        var connect = provider.ConnectAsync(url => prompt = url, deadline.Token);
        var browser = Assert.IsType<Uri>(prompt);
        Assert.Equal("https://app.devin.ai/auth/cli/continue", browser.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(browser.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("select_account", query["prompt"]);
        Assert.Equal("1", query["cli_pkce_marker"]);
        var redirect = new Uri(query["redirect_uri"]!);
        Assert.Equal("http", redirect.Scheme);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.Equal("/callback", redirect.AbsolutePath);
        using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await loopback.GetAsync(
            redirect + "?state=wrong&code=bogus", deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await loopback.GetAsync(
            redirect + $"?state={query["state"]}&code=one&code=two&state=wrong", deadline.Token)).StatusCode);
        Assert.Equal(0, exchanges);
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync(
            redirect + $"?state={query["state"]}&code=fixture-code", deadline.Token)).StatusCode);

        Assert.False((await connect).IsError);
        Assert.Equal(session, stored);
        Assert.True(await provider.HasLocalCredentialsAsync(deadline.Token));
        Assert.False((await provider.RefreshAsync(deadline.Token)).IsError);
        Assert.Equal(1, exchanges);
        Assert.Equal(2, statusReads);
    }

    [Fact]
    public async Task RejectedNewSessionKeepsPreviousSession()
    {
        string? stored = "existing-fixture";
        Uri? redirect = null;
        using var http = new AppHttp(new HttpClient(new Fixture((request, _) => Task.FromResult(
            request.RequestUri == DevinLogin.TokenUrl
                ? Response(HttpStatusCode.OK, """{"token":"new-fixture"}""")
                : Response(HttpStatusCode.Unauthorized, "{}")))));
        var provider = new DevinProvider(http, () => stored, token => stored = token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = provider.ConnectAsync(url =>
        {
            var data = HttpUtility.ParseQueryString(url.Query);
            redirect = new Uri(data["redirect_uri"] + $"?state={data["state"]}&code=fixture-code");
        }, deadline.Token);
        using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var callback = await loopback.GetAsync(redirect, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        await Assert.ThrowsAsync<ProviderRequestException>(async () => await connect);
        Assert.Equal("existing-fixture", stored);
    }

    [Fact]
    public async Task CancellationDoesNotStartAnExchange()
    {
        using var http = new AppHttp(new HttpClient(new Fixture((_, _) => throw new InvalidOperationException())));
        using var cancellation = new CancellationTokenSource();
        var login = new DevinLogin(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            login.SignInAsync(_ => cancellation.Cancel(), cancellation.Token));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }
}
