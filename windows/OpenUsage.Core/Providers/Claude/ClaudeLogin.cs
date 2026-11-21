using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

internal sealed class ClaudeLogin(AppHttp http, Func<DateTimeOffset> now, TimeSpan? callbackTimeout = null)
{
    internal static readonly Uri AuthorizeUrl = new("https://claude.ai/oauth/authorize");
    internal const string Scope = "user:profile user:inference";

    public async Task<ClaudeOAuth> AuthorizeAsync(Action<Uri> openBrowser, Task<string> pastedCode, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new LoopbackSignIn(useLocalhost: true);
        var redirect = listener.RedirectUri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var uri = new UriBuilder(AuthorizeUrl) { Query =
            $"client_id={ClaudeAuthStore.ProdClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirect.AbsoluteUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}&code=true" };
        ct.ThrowIfCancellationRequested();
        openBrowser(uri.Uri);
        deadline.CancelAfter(callbackTimeout ?? TimeSpan.FromMinutes(5));
        var callback = listener.ReceiveCodeAsync(state, deadline.Token);
        string code;
        try
        {
            var source = await Task.WhenAny(callback, pastedCode).WaitAsync(deadline.Token);
            code = source == callback ? await callback : ParsePastedCode(await pastedCode, redirect, state);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderRequestException("Claude sign-in timed out. Try again.");
        }
        finally
        {
            deadline.Cancel();
            await ((Task)callback).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        ct.ThrowIfCancellationRequested();
        if (!IsToken(code)) throw new ProviderRequestException("Claude did not return a usable sign-in code.");
        return await ExchangeAsync(new
        {
            grant_type = "authorization_code",
            client_id = ClaudeAuthStore.ProdClientId,
            code,
            state,
            redirect_uri = redirect.AbsoluteUri,
            code_verifier = verifier
        }, null, ct);
    }

    public Task<ClaudeOAuth> RenewAsync(ClaudeOAuth previous, CancellationToken ct) =>
        ExchangeAsync(new
        {
            grant_type = "refresh_token",
            client_id = ClaudeAuthStore.ProdClientId,
            refresh_token = previous.RefreshToken,
            scope = Scope
        }, previous.RefreshToken, ct);

    private async Task<ClaudeOAuth> ExchangeAsync<T>(T fields, string? priorRefresh, CancellationToken ct)
    {
        var response = await http.SendAsync(new AppHttpRequest("POST", new Uri(ClaudeAuthStore.ProdRefreshUrl),
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            JsonSerializer.SerializeToUtf8Bytes(fields)), ct);
        if (response.StatusCode == 429)
            throw new ProviderRequestException("Claude is rate limiting sign-ins. Try again later.");
        if (!response.IsSuccess)
        {
            if (priorRefresh is not null && response.StatusCode is 400 or 401 or 403)
                throw new ProviderRequestException("Claude sign-in expired. Sign in again in Customize → Claude.");
            throw new ProviderRequestException($"Claude did not accept the sign-in (HTTP {response.StatusCode}). Try again.");
        }
        var json = response.Json;
        var access = JsonUtil.String(json?["access_token"]);
        var refresh = JsonUtil.String(json?["refresh_token"]) ?? priorRefresh;
        if (!IsToken(access) || !IsToken(refresh))
            throw new ProviderRequestException("Claude did not return a usable sign-in. Try again.");
        var lifetime = JsonUtil.Number(json?["expires_in"]);
        return new ClaudeOAuth
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = lifetime is > 0 and <= int.MaxValue ? now().ToUnixTimeMilliseconds() + lifetime * 1000 : null
        };
    }

    internal static bool IsToken(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static string ParsePastedCode(string input, Uri redirect, string state)
    {
        var value = input.Trim();
        string? code = null;
        string? receivedState = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            Uri.Compare(uri, redirect, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped,
                StringComparison.Ordinal) == 0 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query.GetValues("code") is [var received] && query.GetValues("state") is [var matched] &&
                query.GetValues("error") is null)
                (code, receivedState) = (received, matched);
        }
        else if (value.Split('#', 2) is [var prefix, var suffix] && !prefix.Contains("://", StringComparison.Ordinal))
        {
            (code, receivedState) = (prefix, suffix);
        }
        if (receivedState != state || !IsToken(code))
            throw new ProviderRequestException("The code did not match this Claude sign-in. Try again.");
        return code!;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
