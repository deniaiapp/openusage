using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Grok;

internal sealed record GrokOAuth(string AccessToken, string RefreshToken, DateTimeOffset? ExpiresAt)
{
    internal static GrokOAuth Parse(JsonObject? body, GrokOAuth? previous = null)
    {
        var access = JsonUtil.String(body?["access_token"]);
        var refresh = JsonUtil.String(body?["refresh_token"]) ?? previous?.RefreshToken;
        if (!IsToken(access) || !IsToken(refresh))
            throw new ProviderRequestException("xAI returned invalid sign-in tokens. Try again.");
        var expiry = JsonUtil.Number(body?["expires_in"]) is > 0 and <= 31_536_000 and var seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : Jwt.Expiration(access);
        return new(access!, refresh!, expiry);
    }

    internal static bool IsToken(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch));
}

internal sealed class GrokDeviceLogin(AppHttp http, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private static readonly Uri StartUrl = new("https://auth.x.ai/oauth2/device/code");
    internal const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string SignInFailed = "xAI device sign-in failed. Try again.";
    private const string CodeExpired = "The xAI sign-in code expired. Start sign-in again.";
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    public async Task<GrokOAuth> AuthorizeAsync(Action<string, Uri> showCode, CancellationToken ct)
    {
        var start = await PostAsync(StartUrl, new()
        {
            ["client_id"] = ClientId,
            ["scope"] = "openid email offline_access grok-cli:access"
        }, ct);
        if (!start.IsSuccess) throw new ProviderRequestException(SignInFailed);
        var body = start.Json;
        var deviceCode = JsonUtil.String(body?["device_code"]);
        if (!GrokOAuth.IsToken(deviceCode) ||
            JsonUtil.String(body?["user_code"]) is not { Length: > 0 and <= 32 } userCode ||
            userCode.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') ||
            JsonUtil.Number(body?["expires_in"]) is not (> 0 and var expiresIn) ||
            (VerificationPage(body?["verification_uri_complete"]) ?? VerificationPage(body?["verification_uri"])) is not { } page)
            throw new ProviderRequestException("xAI did not return a valid sign-in code or page.");

        var expiry = TimeSpan.FromSeconds(Math.Min(expiresIn, 1800));
        var delay = TimeSpan.FromSeconds(Math.Clamp(JsonUtil.Number(body?["interval"]) ?? 5, 5, 1800));
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(expiry);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        showCode(userCode, page);
        try
        {
            while (delay < expiry - Stopwatch.GetElapsedTime(started))
            {
                await _wait(delay, linked.Token);
                var response = await PostAsync(GrokProvider.RefreshUrl, new()
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode!,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }, linked.Token);
                var tokens = response.Json;
                switch (JsonUtil.String(tokens?["error"]))
                {
                    case "authorization_pending": continue;
                    case "slow_down": delay += TimeSpan.FromSeconds(5); continue;
                    case "expired_token": throw new ProviderRequestException(CodeExpired);
                    case "access_denied": throw new ProviderRequestException("xAI sign-in was denied.");
                    case null when response.IsSuccess: return GrokOAuth.Parse(tokens);
                    default: throw new ProviderRequestException(SignInFailed);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProviderRequestException(CodeExpired);
        }
        ct.ThrowIfCancellationRequested();
        throw new ProviderRequestException(CodeExpired);
    }

    public async Task<GrokOAuth> RenewAsync(GrokOAuth previous, CancellationToken ct)
    {
        var response = await PostAsync(GrokProvider.RefreshUrl, new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = previous.RefreshToken
        }, ct);
        if (JsonUtil.String(response.Json?["error"]) == "invalid_grant")
            throw new ProviderRequestException(GrokProvider.SignInAgain);
        if (!response.IsSuccess)
            throw new ProviderRequestException($"Grok sign-in renewal failed (HTTP {response.StatusCode}). Try again later.");
        return GrokOAuth.Parse(response.Json, previous);
    }

    private static Uri? VerificationPage(JsonNode? node) =>
        JsonUtil.String(node) is { Length: > 0 } page && Uri.TryCreate(StartUrl, page, out var url) &&
        url.Scheme == Uri.UriSchemeHttps && url.Host == StartUrl.Host && url.IsDefaultPort && url.UserInfo.Length == 0
            ? url : null;

    private async Task<AppHttpResponse> PostAsync(Uri url, Dictionary<string, string> values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var form = new FormUrlEncodedContent(values);
        try
        {
            var response = await http.SendAsync(new AppHttpRequest("POST", url, new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["User-Agent"] = "OpenUsage"
            }, await form.ReadAsByteArrayAsync(ct)), ct);
            ct.ThrowIfCancellationRequested();
            return response;
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
