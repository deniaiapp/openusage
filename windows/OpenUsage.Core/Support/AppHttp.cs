using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenUsage.Core.Support;

public sealed record AppHttpRequest(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Body = null,
    int TimeoutSeconds = 15);

public sealed record AppHttpResponse(int StatusCode, byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public JsonObject? Json => JsonUtil.ParseObject(Body);
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    internal TimeSpan? RetryAfter(DateTimeOffset now)
    {
        var value = Header("Retry-After")?.Trim();
        if (!double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            if (!DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date)) return null;
            seconds = (date - now).TotalSeconds;
        }
        return double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromHours(1).TotalSeconds))
            : null;
    }
}

public sealed class AppHttp : IDisposable
{
    private readonly HttpClient _client;

    public AppHttp(HttpClient? client = null)
    {
        _client = client ?? CreateDefaultClient();
    }

    public static HttpClient CreateDefaultClient() => CreateDefaultClient(allowRedirects: true);

    internal static HttpClient CreateDefaultClient(bool allowRedirects)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowRedirects
        };
        ApplyProxy(handler);
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<AppHttpResponse> SendAsync(AppHttpRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Headers is not null)
        {
            foreach (var (key, value) in request.Headers)
            {
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Body is not null)
                    continue;
                message.Headers.TryAddWithoutValidation(key, value);
            }
        }
        if (request.Body is not null)
        {
            var content = new ByteArrayContent(request.Body);
            if (request.Headers is not null &&
                request.Headers.TryGetValue("Content-Type", out var contentType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            message.Content = content;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            using var response = await _client.SendAsync(message, cts.Token);
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            foreach (var header in response.Content.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            return new AppHttpResponse((int)response.StatusCode, body, headers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new ProviderRequestException("Couldn't reach the provider. Check your connection.", ex);
        }
    }

    public void Dispose() => _client.Dispose();

    private static void ApplyProxy(HttpClientHandler handler)
    {
        try
        {
            foreach (var configPath in new[]
                     {
                         Path.Combine(AppPaths.Home, ".openusage", "config.json"),
                         Path.Combine(AppPaths.OpenUsageDir, "config.json")
                     })
            {
                if (!File.Exists(configPath)) continue;
                var json = JsonUtil.ParseObject(File.ReadAllText(configPath));
                var proxy = json?["proxy"]?.ToString() ?? JsonUtil.String(json?["proxyUrl"]);
                if (string.IsNullOrWhiteSpace(proxy)) continue;
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("http", $"proxy config unreadable: {ex.Message}");
        }
    }
}

public class ProviderRequestException : Exception
{
    public ProviderRequestException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ProviderErrors
{
    internal const string RateLimited = "Provider is rate-limiting usage requests. Try again later.";

    internal static bool IsRateLimited(AppHttpResponse response) =>
        response.StatusCode == 429 || (response.StatusCode == 403 &&
            (response.Header("x-ratelimit-remaining")?.Trim() == "0" || response.Header("retry-after") is not null));

    public static string ConnectionFailed => "Couldn't reach the provider. Check your connection.";
    public static string InvalidResponse => "Usage data unavailable. Try again later.";
    public static string RequestFailed(int status) => $"Request failed (HTTP {status}).";

    internal static bool IsOffline(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is SocketException { SocketErrorCode: SocketError.NetworkDown or SocketError.NetworkUnreachable })
                return true;
        return false;
    }
}

public static class AuthRetry
{
    public static bool IsAuthFailure(AppHttpResponse response) => response.StatusCode is 401 or 403;

    public static void RequireSuccess(AppHttpResponse response, Exception authExpired, Func<int, Exception> requestFailed)
    {
        if (IsAuthFailure(response)) throw authExpired;
        if (!response.IsSuccess) throw requestFailed(response.StatusCode);
    }

    public static async Task<AppHttpResponse> FetchAsync(
        string token,
        Func<string, Task<AppHttpResponse>> attempt,
        Func<Task<string>> refreshAccessToken,
        Exception authExpired)
    {
        var response = await attempt(token);
        if (!IsAuthFailure(response)) return response;
        AppLog.Info("auth", $"{response.StatusCode} -> refreshing token, retrying once");
        var refreshed = await refreshAccessToken();
        var retried = await attempt(refreshed);
        if (IsAuthFailure(retried)) throw authExpired;
        return retried;
    }
}
