using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

/// <summary>
/// The hub's <c>api-call</c> endpoint sends a request upstream as one of its logins, putting that login's
/// token in place of <see cref="TokenPlaceholder"/>. Neither the management key nor a body is logged.
/// </summary>
internal sealed partial class CliProxyHubClient
{
    public const string TokenPlaceholder = "$TOKEN$";

    public async Task<AppHttpResponse> CallAsync(
        Guid hubId, CliProxyHubAddress address, string key, string authIndex, AppHttpRequest request,
        string upstream, CancellationToken ct)
    {
        var host = address.BaseUrl.Host;
        if (!address.AllowsManagementKey())
            throw new ProviderRequestException($"Use HTTPS for the CLIProxyAPI hub at {host}.");
        key = key.Trim();
        if (key.Length == 0 || key.Any(char.IsControl))
            throw new ProviderRequestException("Enter a valid CLIProxyAPI management key.");

        var payload = new JsonObject
        {
            ["auth_index"] = authIndex,
            ["method"] = request.Method,
            ["url"] = request.Url.AbsoluteUri,
            ["header"] = new JsonObject((request.Headers ?? new Dictionary<string, string>())
                .Select(header => KeyValuePair.Create(header.Key, (JsonNode?)header.Value)))
        };
        if (request.Body is not null) payload["data"] = Encoding.UTF8.GetString(request.Body);
        var management = new AppHttpRequest("POST", new Uri(address.BaseUrl, "v0/management/api-call"),
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {key}",
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/json"
            },
            Encoding.UTF8.GetBytes(payload.ToJsonString()), request.TimeoutSeconds + 10);

        var response = await _gate.SendAsync(hubId, key, host, async () =>
        {
            try { return await _http.SendAsync(management, ct); }
            catch (ProviderRequestException ex) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn("cliproxy", $"{host} api-call unreachable ({ex.InnerException?.GetType().Name})");
                throw new ProviderRequestException($"Can't reach the CLIProxyAPI hub at {host}.", ex);
            }
            catch (ProviderRequestException) { throw new OperationCanceledException(ct); }
        }, ct);
        if (CliProxyHubRefusal.From(response) is { } refusal)
        {
            AppLog.Error("cliproxy", $"{host} api-call -> HTTP {response.StatusCode} ({refusal})");
            var message = CliProxyHubRefusal.Message(refusal, host, upstream, response.StatusCode);
            if (refusal == CliProxyHubRefusalKind.UpstreamUnreachable)
                throw new CliProxyHubUpstreamUnavailableException(message);
            throw new ProviderRequestException(message);
        }
        return Unwrap(response.Body) ?? throw InvalidAnswer(host);
    }

    /// <summary>The upstream answer inside an <c>api-call</c> envelope: <c>{status_code, header: {name: [value]}, body}</c>.</summary>
    internal static AppHttpResponse? Unwrap(byte[] body)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(body); }
        catch (JsonException) { return null; }
        if (envelope?.StatusCode is not { } status || status is < 100 or > 599) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in envelope.Header ?? [])
            if (values is { Count: > 0 }) headers[name] = string.Join(", ", values);
        return new AppHttpResponse(status, Encoding.UTF8.GetBytes(envelope.Body ?? ""), headers);
    }

    private static ProviderRequestException InvalidAnswer(string host)
    {
        AppLog.Error("cliproxy", $"{host} sent an api-call answer OpenUsage can't read");
        return new ProviderRequestException($"The CLIProxyAPI hub at {host} sent an answer OpenUsage can't read.");
    }

    /// <summary>Lets a hub be called again after its key was replaced or the hub was removed.</summary>
    public void Reopen(Guid hubId) => _gate.Reopen(hubId);

    private sealed record Envelope(
        [property: JsonPropertyName("status_code")] int? StatusCode,
        [property: JsonPropertyName("header")] Dictionary<string, List<string>?>? Header,
        [property: JsonPropertyName("body")] string? Body);
}

internal enum CliProxyHubRefusalKind
{
    KeyRejected, Banned, RemoteManagementOff, ManagementRefused, ManagementOff, LoginMissing, NoToken, UpstreamUnreachable, Failed
}

internal sealed class CliProxyHubUpstreamUnavailableException(string message) : ProviderRequestException(message);

/// <summary>A management answer that isn't a success, sorted by what the user can do about it.</summary>
internal static class CliProxyHubRefusal
{
    public static CliProxyHubRefusalKind? From(AppHttpResponse response)
    {
        if (response.IsSuccess) return null;
        var message = (JsonUtil.String(response.Json?["error"]) ?? "").ToLowerInvariant();
        return response.StatusCode switch
        {
            401 => CliProxyHubRefusalKind.KeyRejected,
            403 when message.StartsWith("ip banned", StringComparison.Ordinal) => CliProxyHubRefusalKind.Banned,
            403 when message.Contains("remote management disabled") => CliProxyHubRefusalKind.RemoteManagementOff,
            403 => CliProxyHubRefusalKind.ManagementRefused,
            404 => CliProxyHubRefusalKind.ManagementOff,
            400 when message.Contains("auth credential not found") => CliProxyHubRefusalKind.LoginMissing,
            400 when message.Contains("token") => CliProxyHubRefusalKind.NoToken,
            502 => CliProxyHubRefusalKind.UpstreamUnreachable,
            _ => CliProxyHubRefusalKind.Failed
        };
    }

    /// <summary>A refusal that a repeat would turn into another strike toward the hub's IP ban.</summary>
    public static bool ClosesGate(CliProxyHubRefusalKind kind) =>
        kind is CliProxyHubRefusalKind.KeyRejected or CliProxyHubRefusalKind.Banned;

    public static string Message(CliProxyHubRefusalKind kind, string host, string? upstream = null, int status = 0) => kind switch
    {
        CliProxyHubRefusalKind.KeyRejected =>
            $"The CLIProxyAPI hub at {host} rejected the management key. Replace it in Customize → CLIProxyAPI Hubs.",
        CliProxyHubRefusalKind.Banned =>
            $"The CLIProxyAPI hub at {host} blocked this PC after too many wrong keys. Wait 30 minutes, then replace the management key.",
        CliProxyHubRefusalKind.RemoteManagementOff =>
            $"The CLIProxyAPI hub at {host} only allows management from its own machine. Turn on allow-remote in its config.",
        CliProxyHubRefusalKind.ManagementRefused =>
            $"The CLIProxyAPI hub at {host} refused management access. Check its access settings and key.",
        CliProxyHubRefusalKind.ManagementOff =>
            $"The CLIProxyAPI hub at {host} has its management API turned off. Set a management key in its config.",
        CliProxyHubRefusalKind.LoginMissing => $"This login is no longer on the CLIProxyAPI hub at {host}.",
        CliProxyHubRefusalKind.NoToken =>
            $"The CLIProxyAPI hub at {host} has no working sign-in for this login. Sign it in again on the hub.",
        CliProxyHubRefusalKind.UpstreamUnreachable => $"The CLIProxyAPI hub at {host} couldn't reach {upstream ?? "the provider"}.",
        _ => $"The CLIProxyAPI hub at {host} answered with an error (HTTP {status})."
    };
}

/// <summary>
/// Keeps a wrong management key to one strike per hub. CLIProxyAPI bans an IP for 30 minutes after five
/// wrong keys and every hub account refreshes at once, so a hub's calls go one at a time. Once the hub
/// rejects a key or bans this PC, calls with that key fail without being sent until the hub is reopened.
/// </summary>
internal sealed class CliProxyHubGate
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _turns = [];
    private readonly Dictionary<Guid, (string Key, string Message)> _refused = [];

    public async Task<AppHttpResponse> SendAsync(
        Guid hubId, string key, string host, Func<Task<AppHttpResponse>> send, CancellationToken ct)
    {
        ThrowIfRefused(hubId, key);
        SemaphoreSlim turn;
        lock (_lock)
        {
            if (!_turns.TryGetValue(hubId, out turn!)) _turns[hubId] = turn = new SemaphoreSlim(1, 1);
        }
        await turn.WaitAsync(ct);
        try
        {
            ThrowIfRefused(hubId, key);
            var response = await send();
            if (CliProxyHubRefusal.From(response) is { } kind && CliProxyHubRefusal.ClosesGate(kind))
            {
                var message = CliProxyHubRefusal.Message(kind, host);
                lock (_lock) _refused[hubId] = (key, message);
                AppLog.Error("cliproxy", $"{host} {kind}; not calling it again until its key is replaced");
                throw new ProviderRequestException(message);
            }
            return response;
        }
        finally { turn.Release(); }
    }

    public void Reopen(Guid hubId)
    {
        lock (_lock) _refused.Remove(hubId);
    }

    private void ThrowIfRefused(Guid hubId, string key)
    {
        lock (_lock)
        {
            if (_refused.TryGetValue(hubId, out var refused) && refused.Key == key)
                throw new ProviderRequestException(refused.Message);
        }
    }
}

/// <summary>
/// The HTTP transport of an account mirrored from a hub: every request its unchanged provider code sends
/// goes out through the hub's <c>api-call</c> as the linked login.
/// </summary>
internal sealed class CliProxyHubTunnel(
    CliProxyHubClient client, Guid hubId, CliProxyHubAddress address, Func<string?> key, string authIndex,
    string upstream) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(", ", header.Value);
        byte[]? body = null;
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);
            body = await request.Content.ReadAsByteArrayAsync(ct);
        }
        var hubKey = key() ?? throw new ProviderRequestException(
            $"The CLIProxyAPI hub at {address.BaseUrl.Host} is no longer connected.");
        var forwarded = new AppHttpRequest(request.Method.Method, request.RequestUri!, headers, body);
        var answer = await client.CallAsync(hubId, address, hubKey, authIndex, forwarded, upstream, ct);

        var response = new HttpResponseMessage((HttpStatusCode)answer.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(answer.Body)
        };
        foreach (var (name, value) in answer.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
                response.Content.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }
}
