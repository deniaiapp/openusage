using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubTunnelTests
{
    private static readonly Guid HubId = Guid.NewGuid();
    private static readonly CliProxyHubAddress Address = CliProxyHubAddress.Parse("https://hub.example/proxy")!;

    [Fact]
    public async Task ProviderRequestsGoThroughApiCallAsTheLinkedLogin()
    {
        JsonObject? sent = null;
        using var hub = MakeAsyncClient(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://hub.example/proxy/v0/management/api-call", request.RequestUri?.AbsoluteUri);
            Assert.Equal("fixture-management-key", request.Headers.Authorization?.Parameter);
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            return Reply(HttpStatusCode.OK, """
                {"status_code":429,"header":{"Retry-After":["120"],"X-Empty":[]},"body":"{\"usage\":1}"}
                """);
        });
        using var tunneled = new AppHttp(new HttpClient(new CliProxyHubTunnel(
            hub, HubId, Address, () => "fixture-management-key", "fixture-index", "Claude")));

        var response = await tunneled.SendAsync(new AppHttpRequest("POST", new Uri("https://provider.example/usage?x=1"),
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {CliProxyHubClient.TokenPlaceholder}",
                ["Content-Type"] = "application/json"
            }, Encoding.UTF8.GetBytes("{\"q\":true}")));

        Assert.Equal("fixture-index", JsonUtil.String(sent!["auth_index"]));
        Assert.Equal("POST", JsonUtil.String(sent["method"]));
        Assert.Equal("https://provider.example/usage?x=1", JsonUtil.String(sent["url"]));
        Assert.Equal("Bearer $TOKEN$", JsonUtil.String(sent["header"]!["Authorization"]));
        Assert.Equal("{\"q\":true}", JsonUtil.String(sent["data"]));
        Assert.Equal(429, response.StatusCode);
        Assert.Equal("120", response.Header("Retry-After"));
        Assert.Null(response.Header("X-Empty"));
        Assert.Equal(1, JsonUtil.Number(response.Json?["usage"]));
    }

    [Fact]
    public async Task GetRequestsSendNoData()
    {
        JsonObject? sent = null;
        using var hub = MakeAsyncClient(async (request, ct) =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            return Reply(HttpStatusCode.OK, """{"status_code":200,"body":"{}"}""");
        });
        var response = await hub.CallAsync(HubId, Address, "fixture-key", "idx",
            new AppHttpRequest("GET", new Uri("https://provider.example/")), "Claude", default);
        Assert.Equal(200, response.StatusCode);
        Assert.False(sent!.ContainsKey("data"));
    }

    [Fact]
    public async Task UpstreamAuthFailureDoesNotBlockOtherLoginsUsingTheSameHubKey()
    {
        var calls = 0;
        using var hub = MakeClient(_ => ++calls == 1
            ? Reply(HttpStatusCode.OK, """{"status_code":401}""")
            : Reply(HttpStatusCode.OK, """{"status_code":200}"""));

        Assert.Equal(401, (await Call(hub, "fixture-key")).StatusCode);
        Assert.Equal(200, (await Call(hub, "fixture-key")).StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ADisconnectedAccountSendsNoRequest()
    {
        using var hub = MakeClient(_ => throw new InvalidOperationException("Unexpected request"));
        using var tunneled = new AppHttp(new HttpClient(new CliProxyHubTunnel(
            hub, HubId, Address, () => null, "fixture-index", "Claude")));
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            tunneled.SendAsync(new AppHttpRequest("GET", new Uri("https://provider.example/usage"))));
        Assert.Contains("no longer connected", failure.Message);
    }

    [Theory]
    [InlineData(403, """{"error":"IP banned due to too many failures"}""", "blocked this PC")]
    [InlineData(403, """{"error":"remote management disabled"}""", "allow-remote")]
    [InlineData(404, "fixture-response", "management API turned off")]
    [InlineData(400, """{"error":"auth credential not found"}""", "no longer on the CLIProxyAPI hub")]
    [InlineData(400, """{"error":"missing access token"}""", "no working sign-in")]
    [InlineData(502, "fixture-response", "couldn't reach Claude")]
    [InlineData(500, "fixture-response", "HTTP 500")]
    public async Task RefusalsSayWhatToFixWithoutEchoingTheResponse(int status, string body, string expected)
    {
        using var hub = MakeClient(_ => Reply((HttpStatusCode)status, body));
        var failure = status == 502
            ? await Assert.ThrowsAsync<CliProxyHubUpstreamUnavailableException>(() => Call(hub, "fixture-key"))
            : await Assert.ThrowsAsync<ProviderRequestException>(() => Call(hub, "fixture-key"));
        Assert.Contains(expected, failure.Message);
        Assert.Contains("hub.example", failure.Message);
        Assert.DoesNotContain("fixture", failure.Message);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, """{"error":"IP banned"}""")]
    public async Task ARefusedKeyIsNotSentAgainUntilTheHubIsReopened(int status, string body)
    {
        var keys = new List<string>();
        var completeFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hub = MakeAsyncClient(async (request, ct) =>
        {
            var key = request.Headers.Authorization!.Parameter!;
            keys.Add(key);
            if (key != "rejected-fixture")
                return Reply(HttpStatusCode.OK, """{"status_code":200,"body":"{}"}""");
            await completeFirstRequest.Task.WaitAsync(ct);
            return Reply((HttpStatusCode)status, body);
        });

        var calls = Enumerable.Range(0, 3).Select(_ => Call(hub, "rejected-fixture")).ToArray();
        Assert.Equal(["rejected-fixture"], keys);
        completeFirstRequest.SetResult();
        foreach (var call in calls) await Assert.ThrowsAsync<ProviderRequestException>(() => call);
        Assert.Equal(["rejected-fixture"], keys);

        Assert.Equal(200, (await Call(hub, "replacement-fixture")).StatusCode);
        hub.Reopen(HubId);
        await Assert.ThrowsAsync<ProviderRequestException>(() => Call(hub, "rejected-fixture"));
        Assert.Equal(["rejected-fixture", "replacement-fixture", "rejected-fixture"], keys);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"status_code":"200"}""")]
    [InlineData("""{"status_code":700}""")]
    public async Task RejectsAnswersItCannotRead(string body)
    {
        using var hub = MakeClient(_ => Reply(HttpStatusCode.OK, body));
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() => Call(hub, "fixture-key"));
        Assert.Contains("can't read", failure.Message);
    }

    [Fact]
    public async Task DoesNotSendTheKeyToAnUntrustedPlainHttpHub()
    {
        using var hub = MakeClient(_ => throw new InvalidOperationException("Unexpected request"));
        await Assert.ThrowsAsync<ProviderRequestException>(() => hub.CallAsync(HubId,
            CliProxyHubAddress.Parse("http://hub.example")!, "fixture-key", "idx",
            new AppHttpRequest("GET", new Uri("https://provider.example/")), "Claude", default));
    }

    [Fact]
    public async Task AnUnreachableHubIsNamed()
    {
        using var hub = MakeClient(_ => throw new HttpRequestException("fixture-failure"));
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() => Call(hub, "fixture-key"));
        Assert.Equal("Can't reach the CLIProxyAPI hub at hub.example.", failure.Message);
    }

    [Fact]
    public async Task ListingAndUsageShareTheSavedKeyGate()
    {
        var calls = 0;
        using var hub = MakeClient(_ =>
        {
            calls++;
            return Reply(HttpStatusCode.Unauthorized, "{}");
        });

        await Assert.ThrowsAsync<ProviderRequestException>(() => hub.ListAsync(Address, "fixture-key", default, HubId));
        await Assert.ThrowsAsync<ProviderRequestException>(() => Call(hub, "fixture-key"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AReopenedGateIgnoresOldRefusalsAndDropsQueuedCalls()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var hub = MakeAsyncClient((_, ct) => Interlocked.Increment(ref calls) == 1
            ? first.Task.WaitAsync(ct)
            : Task.FromResult(Reply(HttpStatusCode.OK, """{"status_code":200,"body":"{}"}""")));

        var pending = Call(hub, "fixture-key");
        var queued = Call(hub, "fixture-key");
        hub.Reopen(HubId);
        first.SetResult(Reply(HttpStatusCode.Unauthorized, "{}"));

        await Assert.ThrowsAsync<ProviderRequestException>(() => pending);
        await Assert.ThrowsAsync<ProviderRequestException>(() => queued);
        Assert.Equal(200, (await Call(hub, "fixture-key")).StatusCode);
        Assert.Equal(2, calls);
    }

    private static Task<AppHttpResponse> Call(CliProxyHubClient hub, string key) =>
        hub.CallAsync(HubId, Address, key, "idx", new AppHttpRequest("GET", new Uri("https://provider.example/")),
            "Claude", default);

    private static CliProxyHubClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        MakeAsyncClient((request, _) => Task.FromResult(reply(request)));

    private static CliProxyHubClient MakeAsyncClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) =>
        new(new AppHttp(new HttpClient(new FixtureHandler(reply))));

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            reply(request, cancellationToken);
    }
}
