using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed class CodexAuth
{
    public const string CredentialManagerPath = "credential-manager";

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public string? AccountId { get; set; }
    public string? ApiKey { get; set; }
    public string Path { get; set; } = "";

    /// <summary>The tokens last read from (or written to) the source. A rotated token whose save
    /// failed keeps serving this refresh; only a source that moved past this baseline is adopted.</summary>
    public (string? Access, string? Refresh) Baseline { get; set; }

    public bool IsFile => !string.IsNullOrEmpty(Path) && Path != CredentialManagerPath;
}

public sealed class CodexAuthStore
{
    public const string CredentialService = "Codex Auth";
    public static readonly string[] DefaultHomes = ["~/.config/codex", "~/.codex"];

    public IReadOnlyList<CodexAuth> LoadCandidates()
    {
        var list = new List<CodexAuth>();
        foreach (var home in AuthHomes())
        {
            var path = AppPaths.Expand(Path.Combine(home, "auth.json"));
            var auth = ParseFile(path);
            if (auth is not null) list.Add(auth);
        }
        if (LoadCredentialManagerAuth() is { } fromCred) list.Add(fromCred);
        return list;
    }

    public bool HasUsableCredentials() => LoadCandidates().Any(c => !string.IsNullOrEmpty(c.AccessToken));

    /// <summary>
    /// The Codex CLI keyring item for the configured home only (#1317): its target is
    /// <c>cli|&lt;store key&gt;.Codex Auth</c>, so an item for another Codex home is never picked
    /// just because it shares the service name.
    /// </summary>
    private static CodexAuth? LoadCredentialManagerAuth()
    {
        var home = AppPaths.Env("CODEX_HOME") ?? "~/.codex";
        var targets = KeyringAccounts(home).Select(account => $"{account}.{CredentialService}").ToArray();
        var raw = WindowsCredentialStore.UnwrapGoKeyring(WindowsCredentialStore.ReadAny(targets));
        return ParseRaw(raw, CodexAuth.CredentialManagerPath);
    }

    /// <summary>
    /// Mirrors Codex's <c>compute_store_key</c>: SHA-256 of the canonical home, truncated to 16 hex
    /// characters. Rust's canonicalize returns a verbatim (<c>\\?\</c>) path on Windows, so both
    /// spellings are tried; an unresolvable home hashes as given.
    /// </summary>
    public static IReadOnlyList<string> KeyringAccounts(string codexHome)
    {
        var expanded = AppPaths.Expand(codexHome);
        var paths = new List<string>();
        if (Directory.Exists(expanded))
        {
            var canonical = expanded;
            try
            {
                if (new DirectoryInfo(expanded).ResolveLinkTarget(true) is { } target)
                    canonical = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) { AppLog.Warn("codex", $"could not resolve Codex home link: {ex.Message}"); }
            canonical = canonical.TrimEnd(Path.DirectorySeparatorChar);
            paths.Add(@"\\?\" + canonical);
            paths.Add(canonical);
        }
        else
        {
            paths.Add(expanded);
        }
        return paths.Distinct(StringComparer.Ordinal).Select(KeyringAccount).ToList();
    }

    public static string KeyringAccount(string canonicalHome)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalHome));
        return "cli|" + Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Writes rotated tokens back into the auth file they came from, keeping the file's other fields.
    /// Re-reads the file first and refuses the write when another tool changed it after this refresh
    /// started (#1322). Returns whether the write happened.
    /// </summary>
    public bool Save(CodexAuth auth)
    {
        if (!auth.IsFile) return false;
        var text = File.Exists(auth.Path) ? File.ReadAllText(auth.Path) : null;
        var onDisk = ParseRaw(text, auth.Path);
        if (onDisk is not null && (onDisk.AccessToken, onDisk.RefreshToken) != auth.Baseline)
        {
            AppLog.Warn("codex", "auth file changed during refresh; leaving the newer login untouched");
            return false;
        }

        var root = JsonUtil.ParseObject(text) ?? new JsonObject();
        var tokens = JsonUtil.Object(root["tokens"]);
        if (tokens is null)
        {
            tokens = new JsonObject();
            root["tokens"] = tokens;
        }
        tokens["access_token"] = auth.AccessToken;
        tokens["refresh_token"] = auth.RefreshToken;
        if (auth.IdToken is not null) tokens["id_token"] = auth.IdToken;
        if (auth.AccountId is not null) tokens["account_id"] = auth.AccountId;
        root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllText(auth.Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        auth.Baseline = (auth.AccessToken, auth.RefreshToken);
        return true;
    }

    private static IEnumerable<string> AuthHomes()
    {
        var overrideHome = AppPaths.Env("CODEX_HOME");
        if (overrideHome is not null) yield return overrideHome;
        foreach (var home in DefaultHomes) yield return home;
    }

    public static CodexAuth? ParseFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseRaw(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("codex", $"could not read {path}: {ex.Message}");
            return null;
        }
    }

    private static CodexAuth? ParseRaw(string? text, string path)
    {
        var obj = JsonUtil.ParseObject(text);
        if (obj is null) return null;
        var tokens = JsonUtil.Object(obj["tokens"]) ?? obj;
        var access = JsonUtil.String(tokens["access_token"]) ?? JsonUtil.String(tokens["accessToken"]);
        var apiKey = JsonUtil.String(obj["OPENAI_API_KEY"]) ?? JsonUtil.String(obj["apiKey"]);
        if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(apiKey)) return null;
        var refresh = JsonUtil.String(tokens["refresh_token"]) ?? JsonUtil.String(tokens["refreshToken"]);
        return new CodexAuth
        {
            AccessToken = access,
            RefreshToken = refresh,
            IdToken = JsonUtil.String(tokens["id_token"]) ?? JsonUtil.String(tokens["idToken"]),
            AccountId = JsonUtil.String(tokens["account_id"]) ?? JsonUtil.String(tokens["accountId"]),
            ApiKey = apiKey,
            Path = path,
            Baseline = (access, refresh)
        };
    }
}
