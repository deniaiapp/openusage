using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public enum ClaudeCredentialSource
{
    CredentialManager,
    File,
    Environment
}

public sealed class ClaudeOAuth
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public double? ExpiresAt { get; set; }
    public string? SubscriptionType { get; set; }
    public string? RateLimitTier { get; set; }
    public List<string>? Scopes { get; set; }

    /// Where this login was read from. Only a file login is written back after a token rotation.
    public ClaudeCredentialSource Source { get; set; } = ClaudeCredentialSource.File;
}

public sealed class ClaudeCredentialsFile
{
    public ClaudeOAuth? ClaudeAiOauth { get; set; }
}

public sealed class ClaudeAuthStore
{
    public const string DefaultHome = "~/.claude";
    public const string CredentialFileName = ".credentials.json";
    public const string ProdBaseApi = "https://api.anthropic.com";
    public const string ProdRefreshUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ProdClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly string? _configDirOverride;
    private readonly bool _readCredentialManager;

    /// `configDirOverride` and `readCredentialManager: false` isolate tests from the real machine login.
    public ClaudeAuthStore(string? configDirOverride = null, bool readCredentialManager = true)
    {
        _configDirOverride = configDirOverride;
        _readCredentialManager = readCredentialManager;
    }

    public string CredentialsPath()
    {
        var home = _configDirOverride ?? AppPaths.Env("CLAUDE_CONFIG_DIR") ?? DefaultHome;
        return AppPaths.Expand(Path.Combine(home, CredentialFileName));
    }

    public IReadOnlyList<ClaudeOAuth> LoadCandidates()
    {
        var list = new List<ClaudeOAuth>();
        if (_readCredentialManager)
        {
            foreach (var service in KeychainServiceCandidates())
            {
                var raw = WindowsCredentialStore.UnwrapGoKeyring(WindowsCredentialStore.Read(service));
                var oauth = Parse(raw, ClaudeCredentialSource.CredentialManager);
                if (oauth?.AccessToken is { Length: > 0 }) list.Add(oauth);
            }
        }
        var fileOauth = Parse(ReadFile(CredentialsPath()), ClaudeCredentialSource.File);
        if (fileOauth?.AccessToken is { Length: > 0 }) list.Add(fileOauth);

        var envToken = AppPaths.Env("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrEmpty(envToken) && list.Count == 0)
        {
            list.Add(new ClaudeOAuth { AccessToken = envToken, Source = ClaudeCredentialSource.Environment });
        }
        return list;
    }

    public bool HasUsableCredentials() => LoadCandidates().Count > 0;

    public bool NeedsRefresh(ClaudeOAuth oauth)
    {
        if (oauth.ExpiresAt is not { } expiresAt) return false;
        return expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= 5 * 60 * 1000;
    }

    /// Writes a rotated token back into Claude Code's credential file. Claude Code owns that document —
    /// MCP logins and fields OpenUsage doesn't model live beside the OAuth login — so this re-reads the
    /// latest file and replaces only the three fields the refresh endpoint returns (access token, refresh
    /// token, expiry), leaving everything else untouched. Nothing is written when the file no longer
    /// holds the login that was refreshed (Claude Code signed in again or rotated it meanwhile, so its
    /// newer login wins) or when the login didn't come from the file. Returns whether it was written.
    public bool SaveRotated(ClaudeOAuth oauth, string? previousRefreshToken)
    {
        if (oauth.Source != ClaudeCredentialSource.File) return false;
        var path = CredentialsPath();
        var root = JsonUtil.ParseObject(ReadFile(path));
        if (root?["claudeAiOauth"] is not JsonObject stored) return false;
        var storedRefresh = JsonUtil.String(stored["refreshToken"]);
        if (previousRefreshToken is not null && storedRefresh != previousRefreshToken) return false;

        if (oauth.AccessToken is { } access) stored["accessToken"] = access;
        if (oauth.RefreshToken is { } refresh) stored["refreshToken"] = refresh;
        if (oauth.ExpiresAt is { } expiresAt) stored["expiresAt"] = (long)expiresAt;

        var temp = path + ".openusage.tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    public (string UsageUrl, string RefreshUrl, string ClientId) OAuthConfig()
    {
        var baseApi = AppPaths.Env("CLAUDE_LOCAL_OAUTH_API_BASE") ?? ProdBaseApi;
        var refresh = AppPaths.Env("CLAUDE_CODE_CUSTOM_OAUTH_URL") ?? ProdRefreshUrl;
        return ($"{baseApi.TrimEnd('/')}/api/oauth/usage", refresh, ProdClientId);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static IEnumerable<string> KeychainServiceCandidates()
    {
        yield return "Claude Code-credentials";
        yield return "Claude Code";
        var configDir = AppPaths.Env("CLAUDE_CONFIG_DIR");
        if (configDir is not null)
            yield return $"Claude Code-credentials-{Math.Abs(configDir.GetHashCode(StringComparison.Ordinal)):x}";
    }

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static ClaudeOAuth? Parse(string? text, ClaudeCredentialSource source)
    {
        var obj = JsonUtil.ParseObject(text);
        if (obj is null) return null;
        var nested = JsonUtil.Object(obj["claudeAiOauth"]) ?? JsonUtil.Object(obj["claude_ai_oauth"]) ?? obj;
        var access = JsonUtil.String(nested["accessToken"]) ?? JsonUtil.String(nested["access_token"]);
        if (string.IsNullOrEmpty(access)) return null;
        return new ClaudeOAuth
        {
            AccessToken = access,
            RefreshToken = JsonUtil.String(nested["refreshToken"]) ?? JsonUtil.String(nested["refresh_token"]),
            ExpiresAt = JsonUtil.Number(nested["expiresAt"]) ?? JsonUtil.Number(nested["expires_at"]),
            SubscriptionType = JsonUtil.String(nested["subscriptionType"]) ?? JsonUtil.String(nested["subscription_type"]),
            RateLimitTier = JsonUtil.String(nested["rateLimitTier"]) ?? JsonUtil.String(nested["rate_limit_tier"]),
            Scopes = JsonUtil.Array(nested["scopes"])?.Select(JsonUtil.String).OfType<string>().ToList(),
            Source = source
        };
    }
}
