using System.Net;
using System.Text;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CliProxyHubSyncTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("openusage-hub-sync-");
    private string PathOnDisk => Path.Combine(_directory.FullName, "hubs.bin");

    [WindowsFact]
    public async Task RefreshMirrorsAConnectedHubAtStartupOnDemandAndWhenDue()
    {
        var store = new CliProxyHubStore(PathOnDisk);
        var hub = store.Add(new Uri("https://hub.example/"), "fixture-key");
        var calls = 0;
        using var client = MakeClient((request, _) =>
        {
            Assert.Equal("https://hub.example/v0/management/auth-files", request.RequestUri?.AbsoluteUri);
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            var index = ++calls;
            return Task.FromResult(Listing(index == 3 ? "[]" :
                $$"""[{"id":"oauth-login","provider":"codex","auth_index":"index-{{index}}"}]"""));
        });
        var clock = new ManualClock();
        var sync = new CliProxyHubSync(client, store, clock);

        await sync.RefreshAsync(false, default);
        var original = Assert.Single(store.ListAccounts());
        Assert.Equal(hub.Id, original.Link.HubId);
        Assert.Equal("index-1", original.Link.AuthIndex);
        await sync.RefreshAsync(false, default);
        Assert.Equal(1, calls);
        await sync.RefreshAsync(true, default);
        Assert.Equal(original with { Link = original.Link with { AuthIndex = "index-2" } },
            Assert.Single(new CliProxyHubStore(PathOnDisk).ListAccounts()));

        clock.Now = clock.Now.AddHours(1);
        await sync.RefreshAsync(false, default);
        Assert.Equal(3, calls);
        Assert.Empty(store.ListAccounts());
    }

    [WindowsFact]
    public async Task RejectedSavedKeyDoesNotEraseAccountsOrSendMoreRequestsUntilReplaced()
    {
        var store = new CliProxyHubStore(PathOnDisk);
        var hub = store.Add(new Uri("https://hub.example/"), "fixture-old-key",
            [new("oauth-login", "before", "codex", "Fixture", true, false, null)]);
        var original = Assert.Single(store.ListAccounts());
        var requests = new List<string>();
        using var client = MakeClient((request, _) =>
        {
            var key = request.Headers.Authorization!.Parameter!;
            requests.Add(key);
            return Task.FromResult(key == "fixture-old-key"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Listing("""[{"id":"oauth-login","provider":"codex","auth_index":"after"}]"""));
        });
        var sync = new CliProxyHubSync(client, store);

        await sync.RefreshAsync(false, default);
        await sync.RefreshAsync(true, default);
        Assert.Equal(["fixture-old-key"], requests);
        Assert.Equal(original, Assert.Single(store.ListAccounts()));

        await new CliProxyHubConnections(client, store).ReplaceKeyAsync(hub.Id, "fixture-new-key", default);
        await sync.RefreshAsync(true, default);
        Assert.Equal(["fixture-old-key", "fixture-new-key", "fixture-new-key"], requests);
        Assert.Equal(original with { Link = original.Link with { AuthIndex = "after" } }, Assert.Single(store.ListAccounts()));
    }

    [WindowsFact]
    public async Task AnInFlightListingCannotOverwriteASameKeyReplacement()
    {
        var store = new CliProxyHubStore(PathOnDisk);
        var hub = store.Add(new Uri("https://hub.example/"), "fixture-key");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = MakeClient((_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                return old.Task.WaitAsync(ct);
            }
            return Task.FromResult(Listing("""[{"id":"current","provider":"codex","auth_index":"accepted"}]"""));
        });
        var sync = new CliProxyHubSync(client, store);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = sync.RefreshAsync(false, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        await sync.RefreshAsync(true, timeout.Token);
        Assert.Equal(1, calls);
        await new CliProxyHubConnections(client, store).ReplaceKeyAsync(hub.Id, "fixture-key", timeout.Token);
        old.SetResult(Listing("[]"));
        await first;

        Assert.Equal("current", Assert.Single(store.ListAccounts()).Link.LoginId);
        Assert.Equal(2, calls);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private static HttpResponseMessage Listing(string files) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"files":{{files}}}""", Encoding.UTF8, "application/json")
    };

    private static CliProxyHubClient MakeClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) =>
        new(new AppHttp(new HttpClient(new FixtureHandler(reply))));

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            reply(request, cancellationToken);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
        }
    }
}
