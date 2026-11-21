using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Copilot;

internal sealed class CopilotDeviceLogin(AppHttp http, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    // Public device-flow client used by the upstream Copilot connection.
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string Grant = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly Uri DeviceCodeUrl = new("https://github.com/login/device/code");
    private static readonly Uri AccessTokenUrl = new("https://github.com/login/oauth/access_token");
    public const string VerificationUrl = "https://github.com/login/device";
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    public async Task<string> AuthorizeAsync(Action<string> showCode, CancellationToken ct)
    {
        var device = await PostAsync(DeviceCodeUrl, new()
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        }, ct);
        if (JsonUtil.String(device["device_code"]) is not { Length: > 0 } deviceCode ||
            JsonUtil.String(device["user_code"]) is not { Length: > 0 and <= 32 } userCode ||
            userCode.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') ||
            JsonUtil.Number(device["expires_in"]) is not (> 0 and var expiresIn))
            throw new ProviderRequestException("GitHub did not return a valid sign-in code.");

        var expiry = TimeSpan.FromSeconds(Math.Min(expiresIn, 1800));
        var delay = TimeSpan.FromSeconds(Math.Clamp(JsonUtil.Number(device["interval"]) ?? 5, 5, 1800));
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(expiry);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        showCode(userCode);
        try
        {
            while (delay < expiry - Stopwatch.GetElapsedTime(started))
            {
                await _wait(delay, linked.Token);
                var token = await PostAsync(AccessTokenUrl, new()
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = Grant
                }, linked.Token);
                if (JsonUtil.String(token["access_token"]) is { Length: > 0 } value &&
                    !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl))
                    return value;

                switch (JsonUtil.String(token["error"]))
                {
                    case "authorization_pending": break;
                    case "slow_down": delay += TimeSpan.FromSeconds(5); break;
                    case "expired_token": throw Expired();
                    case "access_denied": throw new ProviderRequestException("GitHub sign-in was denied.");
                    default: throw new ProviderRequestException("GitHub could not finish signing in. Try again.");
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Expired();
        }
        ct.ThrowIfCancellationRequested();
        throw Expired();
    }

    private static ProviderRequestException Expired() =>
        new("The GitHub sign-in code expired. Start sign-in again.");

    private async Task<JsonObject> PostAsync(Uri url, Dictionary<string, string> form, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var content = new FormUrlEncodedContent(form);
        AppHttpResponse response;
        try
        {
            response = await http.SendAsync(new AppHttpRequest("POST", url, new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["User-Agent"] = "OpenUsage"
            }, await content.ReadAsByteArrayAsync(ct)), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccess) throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
        return response.Json ?? throw new ProviderRequestException("GitHub returned an invalid sign-in response.");
    }
}
