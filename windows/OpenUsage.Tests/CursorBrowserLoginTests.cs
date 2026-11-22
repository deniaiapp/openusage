using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Xunit;

namespace OpenUsage.Tests;

public sealed class CursorBrowserLoginTests
{
    [Fact]
    public async Task PollUsesMatchingPkceAndWaitsForAuthorization()
    {
        Uri? browser = null;
        var polls = 0;
        using var http = MakeHttp(request =>
        {
            Assert.Equal("https://api2.cursor.sh/auth/poll", request.RequestUri!.GetLeftPart(UriPartial.Path));
            var auth = Query(browser!);
            var poll = Query(request.RequestUri);
            Assert.Equal(auth["uuid"], poll["uuid"]);
            Assert.False(auth.ContainsKey("verifier"));
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(poll["verifier"])))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_'), auth["challenge"]);
            return ++polls switch
            {
                1 => (404, "{}"),
                2 => (202, "{}"),
                _ => (200, """{"accessToken":"fixture-access","refreshToken":"fixture-refresh"}""")
            };
        });
        var result = await new CursorBrowserLogin(http, NoWait).AuthorizeAsync(url => browser = url, CancellationToken.None);
        Assert.Equal("https://cursor.com/loginDeepControl", browser!.GetLeftPart(UriPartial.Path));
        Assert.Equal(3, polls);
        Assert.Equal(CursorProvider.BrowserCredentialSource, result.Source);
        Assert.Equal("fixture-access", result.AccessToken);
        Assert.Equal("fixture-refresh", result.RefreshToken);
    }

    [Theory]
    [InlineData(403, "{}", "HTTP 403")]
    [InlineData(429, "{}", "HTTP 429")]
    [InlineData(200, """{"accessToken":"fixture-access","refreshToken":"bad token"}""", "invalid sign-in response")]
    [InlineData(200, "[]", "invalid sign-in response")]
    public async Task PollRejectsUnusableResponses(int status, string body, string message)
    {
        using var http = MakeHttp(_ => (status, body));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            new CursorBrowserLogin(http, NoWait).AuthorizeAsync(_ => { }, CancellationToken.None));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task CanceledBrowserPromptDoesNotPoll()
    {
        using var ct = new CancellationTokenSource();
        using var http = MakeHttp(_ => throw new InvalidOperationException("Unexpected poll."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CursorBrowserLogin(http, NoWait).AuthorizeAsync(_ => ct.Cancel(), ct.Token));
    }

    [WindowsFact]
    public async Task RejectedReplacementKeepsEncryptedConnectionAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "keys.bin");
            var existing = Account("first", DateTimeOffset.UtcNow.AddDays(30));
            var rejected = Account("rejected", DateTimeOffset.UtcNow.AddDays(30));
            using var http = MakeHttp(request =>
            {
                if (request.RequestUri != CursorProvider.UsageUrl) return (200, "{}");
                return request.Headers.Authorization?.Parameter == existing.AccessToken
                    ? (200, Usage)
                    : (503, "{}");
            });
            var cursor = new CursorProvider(http, historyStore: new(directory.FullName), keys: new ApiKeyStore(path));
            var snapshot = await cursor.ConnectAccountAsync(existing, CancellationToken.None);
            Assert.False(snapshot.IsError);
            Assert.True(cursor.HasAppCredential);
            Assert.DoesNotContain("first-refresh", Encoding.UTF8.GetString(File.ReadAllBytes(path)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => cursor.ConnectAccountAsync(rejected, CancellationToken.None));
            var reopened = new CursorProvider(http, historyStore: new(directory.FullName), keys: new ApiKeyStore(path));
            Assert.True(reopened.HasAppCredential);
            Assert.False((await reopened.RefreshAsync()).IsError);
        }
        finally { directory.Delete(recursive: true); }
    }

    [WindowsFact]
    public async Task BrowserTokenRenewsWellBeforeExpiryAndBecomesTheNextRefreshToken()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "keys.bin");
            var existing = Account("first", DateTimeOffset.UtcNow.AddDays(2));
            var renewed = Account("renewed", DateTimeOffset.UtcNow.AddDays(30));
            var attempts = 0;
            var rejectUsageOnce = false;
            using var http = new AppHttp(new HttpClient(new StubHandler((request, form) =>
            {
                if (request.RequestUri == CursorProvider.RefreshUrl)
                {
                    var body = JsonUtil.ParseObject(form);
                    Assert.Equal(++attempts == 1 ? existing.RefreshToken : renewed.AccessToken, JsonUtil.String(body?["refresh_token"]));
                    return (200, $$"""{"access_token":"{{renewed.AccessToken}}"}""");
                }
                if (request.RequestUri != CursorProvider.UsageUrl) return (200, "{}");
                Assert.Equal(renewed.AccessToken, request.Headers.Authorization?.Parameter);
                if (!rejectUsageOnce) return (200, Usage);
                rejectUsageOnce = false;
                return (401, "{}");
            })));
            var cursor = new CursorProvider(http, historyStore: new(directory.FullName), keys: new ApiKeyStore(path));
            await cursor.ConnectAccountAsync(existing, CancellationToken.None);
            rejectUsageOnce = true;
            var reopened = new CursorProvider(http, historyStore: new(directory.FullName), keys: new ApiKeyStore(path));
            Assert.False((await reopened.RefreshAsync()).IsError);
            Assert.Equal(2, attempts);
        }
        finally { directory.Delete(recursive: true); }
    }

    private const string Usage = """{"enabled":true,"planUsage":{"totalPercentUsed":55}}""";

    private static CursorAuthState Account(string label, DateTimeOffset expiry)
    {
        var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}")).TrimEnd('=');
        return new CursorAuthState
        {
            AccessToken = $"{label}.{claims}.fixture",
            RefreshToken = $"{label}-refresh",
            Source = CursorProvider.BrowserCredentialSource
        };
    }

    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&').Select(pair => pair.Split('='))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private static Task NoWait(TimeSpan _, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static AppHttp MakeHttp(Func<HttpRequestMessage, (int Status, string Body)> respond) =>
        new(new HttpClient(new StubHandler((request, _) => respond(request))));

    private sealed class StubHandler(Func<HttpRequestMessage, string?, (int Status, string Body)> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, body) = respond(request, form);
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI is required.";
        }
    }
}
