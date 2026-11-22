using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

internal sealed class CodexDeviceLogin(AppHttp http, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private static readonly Uri StartUrl = new("https://auth.openai.com/api/accounts/deviceauth/usercode");
    private static readonly Uri PollUrl = new("https://auth.openai.com/api/accounts/deviceauth/token");
    private const string Callback = "https://auth.openai.com/deviceauth/callback";
    public const string VerificationUrl = "https://auth.openai.com/codex/device";
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    public async Task<CodexAuth> AuthorizeAsync(Action<string> showCode, CancellationToken ct)
    {
        var start = await PostJsonAsync(StartUrl, new { client_id = CodexProvider.ClientId }, ct);
        if (start.StatusCode is 400 or 403)
            throw new ProviderRequestException("Couldn't start ChatGPT sign-in. Check that device code authorization is enabled in ChatGPT security settings.");
        if (!start.IsSuccess)
            throw new ProviderRequestException($"Couldn't start ChatGPT sign-in (HTTP {start.StatusCode}). Try again later.");
        var id = Token(start.Json?["device_auth_id"]);
        var userCode = Token(start.Json?["user_code"]);
        if (id is null || userCode is not { Length: <= 32 } ||
            userCode.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw InvalidResponse();

        var lifetime = TimeSpan.FromMinutes(15);
        var delay = TimeSpan.FromSeconds(Math.Clamp(JsonUtil.Number(start.Json?["interval"]) ?? 5, 1, lifetime.TotalSeconds));
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(lifetime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        showCode(userCode);
        try
        {
            while (delay < lifetime - Stopwatch.GetElapsedTime(started))
            {
                await _wait(delay, linked.Token);
                var poll = await PostJsonAsync(PollUrl, new { device_auth_id = id, user_code = userCode }, linked.Token);
                if (poll.IsSuccess)
                {
                    var code = Token(poll.Json?["authorization_code"]) ?? throw InvalidResponse();
                    var verifier = Token(poll.Json?["code_verifier"]) ?? throw InvalidResponse();
                    var exchange = await PostTokenAsync(http, new()
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["code_verifier"] = verifier,
                        ["redirect_uri"] = Callback
                    }, linked.Token);
                    if (!exchange.IsSuccess)
                        throw new ProviderRequestException($"ChatGPT couldn't complete sign-in (HTTP {exchange.StatusCode}).");
                    return ParseTokens(exchange.Json, previous: null);
                }

                switch (JsonUtil.String(poll.Json?["error"]))
                {
                    case "expired_token": throw Expired();
                    case "access_denied": throw new ProviderRequestException("ChatGPT sign-in was denied.");
                    case "slow_down": delay += TimeSpan.FromSeconds(5); break;
                    case "deviceauth_authorization_pending": break;
                    default:
                        if (poll.StatusCode is not (403 or 404))
                            throw new ProviderRequestException($"ChatGPT couldn't complete sign-in (HTTP {poll.StatusCode}).");
                        break;
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

    internal static CodexAuth ParseTokens(JsonObject? json, CodexAuth? previous)
    {
        var access = Token(json?["access_token"]) ?? throw InvalidResponse();
        var refresh = json?.ContainsKey("refresh_token") == true
            ? Token(json["refresh_token"])
            : previous?.RefreshToken;
        if (refresh is null) throw InvalidResponse();
        var claims = JsonUtil.Object(Jwt.Payload(access)?["https://api.openai.com/auth"]);
        return new CodexAuth
        {
            AccessToken = access,
            RefreshToken = refresh,
            AccountId = Token(claims?["chatgpt_account_id"]) ?? previous?.AccountId
        };
    }

    private static string? Token(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var token) &&
        !string.IsNullOrEmpty(token) && !token.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch))
            ? token : null;

    private async Task<AppHttpResponse> PostJsonAsync(Uri url, object fields, CancellationToken ct)
    {
        using var content = JsonContent.Create(fields);
        return await PostAsync(http, url, content, ct);
    }

    internal static async Task<AppHttpResponse> PostTokenAsync(AppHttp http, Dictionary<string, string> form, CancellationToken ct)
    {
        form.Add("client_id", CodexProvider.ClientId);
        using var content = new FormUrlEncodedContent(form);
        return await PostAsync(http, CodexProvider.RefreshUrl, content, ct);
    }

    private static async Task<AppHttpResponse> PostAsync(AppHttp http, Uri url, HttpContent content, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(new AppHttpRequest("POST", url, new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = content.Headers.ContentType!.ToString(),
                ["User-Agent"] = "OpenUsage"
            }, await content.ReadAsByteArrayAsync(ct), 15), ct);
            ct.ThrowIfCancellationRequested();
            return response;
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static ProviderRequestException Expired() => new("The ChatGPT sign-in code expired. Start sign-in again.");
    private static ProviderRequestException InvalidResponse() => new("ChatGPT returned an invalid sign-in response. Try again.");
}
