using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

internal sealed class CursorBrowserLogin(AppHttp http, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    internal async Task<CursorAuthState> AuthorizeAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var id = Guid.NewGuid().ToString();
        var browserUrl = new Uri($"https://cursor.com/loginDeepControl?challenge={challenge}&uuid={id}&mode=login&redirectTarget=cli");
        var pollUrl = new Uri($"https://api2.cursor.sh/auth/poll?uuid={id}&verifier={verifier}");
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(Lifetime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        openBrowser(browserUrl);
        try
        {
            while (PollInterval < Lifetime - Stopwatch.GetElapsedTime(started))
            {
                await _wait(PollInterval, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                var response = await http.SendAsync(new AppHttpRequest("GET", pollUrl, TimeoutSeconds: 10), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (response.StatusCode is 202 or 404) continue;
                if (!response.IsSuccess)
                    throw new ProviderRequestException($"Cursor sign-in failed (HTTP {response.StatusCode}). Try again.");
                var json = response.Json;
                if (Token(json?["accessToken"]) is not { } access || Token(json?["refreshToken"]) is not { } refresh)
                    throw new ProviderRequestException("Cursor returned an invalid sign-in response. Try again.");
                return new CursorAuthState { AccessToken = access, RefreshToken = refresh, Source = CursorProvider.BrowserCredentialSource };
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ProviderRequestException) && linked.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
        ct.ThrowIfCancellationRequested();
        throw new ProviderRequestException("The Cursor sign-in expired. Start sign-in again.");
    }

    internal static string? Token(System.Text.Json.Nodes.JsonNode? node) =>
        JsonUtil.String(node) is { } value && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl) ? value : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
