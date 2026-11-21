using System.Diagnostics;
using System.Net.Http;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public sealed record DevicePrompt(string Code, Uri VerificationUrl);

internal sealed record DeviceGrant(string AccessToken, string? RefreshToken);

internal sealed record DeviceEndpoints(string Authority, string ClientId, Uri Start, Uri Token, Func<Uri, bool> VerifyPage);

internal sealed class DeviceAuthorization(AppHttp http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<DeviceGrant> SignInAsync(DeviceEndpoints endpoints, Action<DevicePrompt> prompt, CancellationToken ct)
    {
        var start = await PostAsync(endpoints.Start, new() { ["client_id"] = endpoints.ClientId }, ct);
        if (!start.IsSuccess) throw new ProviderRequestException(ProviderErrors.RequestFailed(start.StatusCode));
        var challenge = start.Json;
        var deviceCode = JsonUtil.String(challenge?["device_code"]);
        var userCode = JsonUtil.String(challenge?["user_code"]);
        var life = JsonUtil.Number(challenge?["expires_in"]);
        var interval = JsonUtil.Number(challenge?["interval"]) ?? 5;
        var page = new[] { "verification_uri_complete", "verification_uri" }
            .Select(key => JsonUtil.String(challenge?[key]))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null)
            .FirstOrDefault(uri => uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 }
                && endpoints.VerifyPage(uri));
        if (deviceCode is null || userCode is null || life is not (> 0 and <= 86400) || interval is not (> 0 and <= 86400) || page is null)
            throw new ProviderRequestException($"{endpoints.Authority} returned an invalid sign-in code.");

        var lifetime = TimeSpan.FromSeconds(life.Value);
        var elapsed = Stopwatch.StartNew();
        prompt(new DevicePrompt(userCode, page));
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = endpoints.ClientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        };
        var wait = delay ?? Task.Delay;
        while (elapsed.Elapsed + TimeSpan.FromSeconds(interval) < lifetime)
        {
            await wait(TimeSpan.FromSeconds(interval), ct);
            if (elapsed.Elapsed >= lifetime) break;
            var response = await PostAsync(endpoints.Token, fields, ct);
            if (elapsed.Elapsed >= lifetime) break;
            var body = response.Json;
            if (response.IsSuccess && JsonUtil.String(body?["access_token"]) is { } token)
                return new DeviceGrant(token, JsonUtil.String(body?["refresh_token"]));
            switch (JsonUtil.String(body?["error"]))
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "expired_token": throw Expired(endpoints.Authority);
                case "access_denied": throw new ProviderRequestException($"{endpoints.Authority} sign-in was denied.");
                default: throw new ProviderRequestException($"{endpoints.Authority} sign-in failed (HTTP {response.StatusCode}).");
            }
        }
        ct.ThrowIfCancellationRequested();
        throw Expired(endpoints.Authority);
    }

    public async Task<AppHttpResponse> PostAsync(Uri url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(fields);
        try
        {
            return await http.SendAsync(new("POST", url, new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Accept"] = "application/json"
            }, await form.ReadAsByteArrayAsync(ct)), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static ProviderRequestException Expired(string authority) => new($"{authority} sign-in expired. Try again.");
}
