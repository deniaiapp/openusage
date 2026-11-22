using System.Net;
using System.Text;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubClientTests
{
    [Theory]
    [InlineData(" hub.example/management.html?view=logins#section ", "https://hub.example/v0/management/auth-files")]
    [InlineData("https://hub.example:9443/proxy/v0/management/", "https://hub.example:9443/proxy/v0/management/auth-files")]
    [InlineData("https://hub.example/proxy%20hub/v1", "https://hub.example/proxy%20hub/v0/management/auth-files")]
    [InlineData("http://[::1]:8317/v8/management", "http://[::1]:8317/v0/management/auth-files")]
    public void AddressPreservesTheHubPrefix(string input, string listing) =>
        Assert.Equal(listing, CliProxyHubAddress.Parse(input)?.ListingUrl.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("file:///tmp/")]
    [InlineData("https://user:password@hub.example")]
    [InlineData("https://hub.example:0")]
    public void AddressRejectsUnusableOrCredentialBearingUrls(string input) =>
        Assert.Null(CliProxyHubAddress.Parse(input));

    [Theory]
    [InlineData("https://hub.example", true)]
    [InlineData("http://localhost:8317", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("http://[fe80::1]", true)]
    [InlineData("http://192.168.1.23", true)]
    [InlineData("http://100.64.0.1", true)]
    [InlineData("http://hub.local", true)]
    [InlineData("http://hub.net-example.ts.net", true)]
    [InlineData("http://hub.local.example", false)]
    [InlineData("http://hub.example", false)]
    [InlineData("http://100.128.0.1", false)]
    [InlineData("http://[::ffff:203.0.113.1]", false)]
    public void OnlyTrustedPlainHttpAddressesMayReceiveTheKey(string input, bool allowed) =>
        Assert.Equal(allowed, CliProxyHubAddress.Parse(input)!.AllowsManagementKey());

    [Fact]
    public async Task ReadsTheHubLoginListingWithoutExposingOtherFields()
    {
        using var client = MakeClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://hub.example/proxy/v0/management/auth-files", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-management-key", request.Headers.Authorization?.Parameter);
            return Reply(HttpStatusCode.OK, """
                {"files":[
                  {"id":"one","auth_index":"idx","type":"CoDeX","email":" fixture@example.invalid ",
                   "id_token":{"chatgpt_account_id":"workspace"}},
                  {"id":"two","provider":"xai","account":"Grok"},
                  {"id":"three","provider":"claude","account_type":"api-key"},
                  {"id":"four","provider":"other","disabled":true},
                  {"id":"five","provider":"unknown"},
                  {"auth_index":"without-id"}
                ]}
                """);
        });

        var logins = await client.ListAsync(CliProxyHubAddress.Parse("hub.example/proxy")!, " fixture-management-key ", default);
        Assert.Equal(new CliProxyHubLogin("one", "idx", "codex", "fixture@example.invalid", true, false, "workspace"), logins[0]);
        Assert.Equal("codex", logins[0].ProviderId);
        Assert.Equal("grok", logins[1].ProviderId);
        Assert.Null(logins[1].AuthIndex);
        Assert.Equal("three", Assert.Single(logins, login => login.IsUnsupported).Id);
        Assert.Equal(5, logins.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"files\":[null]}")]
    [InlineData("{\"files\":[{\"id\":\"a\",\"auth_index\":123}]}")]
    public async Task RejectsMalformedListingsInsteadOfReportingNoLogins(string body)
    {
        using var client = MakeClient(_ => Reply(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.ListAsync(CliProxyHubAddress.Parse("hub.example")!, "fixture-key", default));
    }

    [Fact]
    public async Task RejectsUnsafeAddressBeforeSendingAnything()
    {
        using var client = MakeClient(_ => throw new InvalidOperationException("Unexpected request"));
        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.ListAsync(CliProxyHubAddress.Parse("http://hub.example")!, "fixture-key", default));
    }

    [Fact]
    public async Task AcceptsAValidEmptyListing()
    {
        using var client = MakeClient(_ => Reply(HttpStatusCode.OK, """{"files":[]}"""));
        Assert.Empty(await client.ListAsync(CliProxyHubAddress.Parse("hub.example")!, "fixture-key", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "redirected")]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task ReportsManagementFailuresWithoutEchoingTheResponse(HttpStatusCode status, string expected)
    {
        using var client = MakeClient(_ => Reply(status, "fixture-response"));
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.ListAsync(CliProxyHubAddress.Parse("hub.example")!, "fixture-key", default));
        Assert.Contains(expected, failure.Message);
        Assert.DoesNotContain("fixture", failure.Message);
    }

    private static CliProxyHubClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new AppHttp(new HttpClient(new FixtureHandler(reply))));

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
