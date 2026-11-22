using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Devin;

internal sealed class DevinLogin(AppHttp http)
{
    internal static readonly Uri TokenUrl = new("https://api.devin.ai/auth/cli/token");
    private const string SessionPrefix = "devin-session-token$";

    public async Task<string> SignInAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        string code;
        try
        {
            using var listener = new LoopbackSignIn();
            ct.ThrowIfCancellationRequested();
            openBrowser(new Uri("https://app.devin.ai/auth/cli/continue?" +
                $"redirect_uri={Uri.EscapeDataString(listener.RedirectUri.AbsoluteUri)}&state={state}" +
                $"&prompt=select_account&code_challenge={challenge}&code_challenge_method=S256&cli_pkce_marker=1"));
            code = await listener.ReceiveCodeAsync(state, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderRequestException("Devin sign-in timed out. Try again.");
        }
        ct.ThrowIfCancellationRequested();
        var response = await http.SendAsync(new("POST", TokenUrl,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            JsonSerializer.SerializeToUtf8Bytes(new { code, code_verifier = verifier })), ct);
        if (!response.IsSuccess)
            throw new ProviderRequestException($"Devin did not accept the sign-in (HTTP {response.StatusCode}). Try again.");
        if (JsonUtil.String(response.Json?["token"]) is not { } token || token.Any(char.IsControl) ||
            token == SessionPrefix)
            throw new ProviderRequestException("Devin did not return a session. Try again.");
        return token.StartsWith(SessionPrefix, StringComparison.Ordinal) ? token : SessionPrefix + token;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
