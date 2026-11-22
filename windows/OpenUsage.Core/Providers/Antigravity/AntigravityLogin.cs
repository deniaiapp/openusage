using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

internal sealed record AntigravitySignIn(string Access, string Refresh, DateTimeOffset ExpiresAt);

internal sealed class AntigravityLogin(AppHttp http, string clientId = AntigravityProvider.GoogleClientId,
    string clientSecret = AntigravityProvider.GoogleClientSecret)
{
    private static readonly Uri AuthorizeUrl = new("https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly Uri TokenUrl = new("https://oauth2.googleapis.com/token");
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    internal const int CallbackPort = 51121;
    private const string CallbackPath = "/oauth-callback";

    public async Task<AntigravitySignIn> AuthorizeAsync(Action<Uri> openBrowser, CancellationToken ct, int port = CallbackPort)
    {
        ct.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var callback = StartCallback(port);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var query = HttpUtility.ParseQueryString("");
        query["client_id"] = clientId;
        query["redirect_uri"] = callback.RedirectUri.AbsoluteUri;
        query["response_type"] = "code";
        query["scope"] = Scope;
        query["state"] = state;
        query["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        query["code_challenge_method"] = "S256";
        query["access_type"] = "offline";
        query["prompt"] = "consent";
        ct.ThrowIfCancellationRequested();
        openBrowser(new UriBuilder(AuthorizeUrl) { Query = query.ToString() }.Uri);
        try
        {
            var code = await callback.ReceiveCodeAsync(state, deadline.Token);
            return await TokenAsync(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callback.RedirectUri.AbsoluteUri,
                ["code_verifier"] = verifier
            }, null, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderRequestException("Google sign-in timed out. Try again.");
        }
    }

    internal Task<AntigravitySignIn> RenewAsync(string refresh, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, refresh, ct);

    private async Task<AntigravitySignIn> TokenAsync(Dictionary<string, string> fields, string? existingRefresh, CancellationToken ct)
    {
        fields["client_id"] = clientId;
        fields["client_secret"] = clientSecret;
        using var form = new FormUrlEncodedContent(fields);
        var bytes = await form.ReadAsByteArrayAsync(ct);
        AppHttpResponse response;
        try
        {
            response = await http.SendAsync(new AppHttpRequest("POST", TokenUrl,
                new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }, bytes), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
        var body = response.IsSuccess ? response.Json : null;
        var access = JsonUtil.String(body?["access_token"]);
        var refresh = JsonUtil.String(body?["refresh_token"]) ?? existingRefresh;
        var expires = body?["expires_in"] is { } value ? JsonUtil.Number(value) : 3600;
        if (!IsToken(access) || !IsToken(refresh) || expires is not (> 0 and <= 86_400))
            throw new ProviderRequestException(existingRefresh is null
                ? "Google did not return a usable Antigravity sign-in. Try again."
                : "Couldn't renew the Antigravity sign-in. Try again or reconnect in Customize.");
        return new AntigravitySignIn(access!, refresh!, DateTimeOffset.UtcNow.AddSeconds(expires.Value));
    }

    internal static bool IsToken(string? token) => !string.IsNullOrWhiteSpace(token) && !token.Any(char.IsWhiteSpace) &&
        !token.Any(char.IsControl);

    private static LoopbackSignIn StartCallback(int port)
    {
        try { return new LoopbackSignIn(useLocalhost: true, port: port, path: CallbackPath); }
        catch (SocketException) { throw new ProviderRequestException("Couldn't start the local Google sign-in callback. Close any other Antigravity sign-in and try again."); }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
