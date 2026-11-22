using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

public static class AntigravityMetric
{
    public const string GeminiId = "antigravity.geminiPro";
    public const string GeminiWeeklyId = "antigravity.geminiWeekly";
    public const string ClaudeId = "antigravity.claude";
    public const string ClaudeWeeklyId = "antigravity.claudeWeekly";
    public const string SessionLabel = "Session";
    public const string WeeklyLabel = "Weekly";
    public const string ClaudeLabel = "Claude";
    public const string ClaudeWeeklyLabel = "Claude Weekly";
}

public sealed class AntigravityAuthStore
{
    public const string CacheRelative = "antigravity/auth.json";
    internal const string ProtectedCacheRelative = "antigravity/auth.bin";
    private readonly string _legacyPath;
    private readonly string _protectedPath;
    private readonly Func<string, string?> _readCredential;

    public AntigravityAuthStore() : this(AppPaths.OpenUsageDir, WindowsCredentialStore.Read) { }

    internal AntigravityAuthStore(string appDirectory, Func<string, string?> readCredential)
    {
        _legacyPath = Path.Combine(appDirectory, CacheRelative);
        _protectedPath = Path.Combine(appDirectory, ProtectedCacheRelative);
        _readCredential = readCredential;
    }

    public (string? Access, string? Refresh, DateTimeOffset? Expiry)? Load(bool migrateCache = false)
    {
        foreach (var target in new[] { "gemini:antigravity", "gemini", "Antigravity" })
        {
            var raw = _readCredential(target);
            var token = Extract(raw);
            if (token is null) continue;
            if (migrateCache && File.Exists(_legacyPath)) LoadCache(migrate: true);
            return token;
        }
        return LoadCache(migrateCache);
    }

    public void SaveCache(string? access, string? refresh, DateTimeOffset? expiry)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                access_token = access,
                refresh_token = refresh,
                expiry = expiry?.ToString("O")
            });
            var encrypted = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_protectedPath)!);
            AtomicFile.WriteAllBytes(_protectedPath, encrypted);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            AppLog.Warn("antigravity", "could not save the protected token cache");
            return;
        }
        RemoveLegacyCache();
    }

    private (string? Access, string? Refresh, DateTimeOffset? Expiry)? LoadCache(bool migrate)
    {
        if (File.Exists(_protectedPath))
        {
            try
            {
                var encrypted = File.ReadAllBytes(_protectedPath);
                var payload = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                if (JsonUtil.ParseObject(Encoding.UTF8.GetString(payload)) is { } json && FromObject(json) is { } token)
                {
                    if (migrate && File.Exists(_legacyPath)) RemoveLegacyCache();
                    return token;
                }
                AppLog.Warn("antigravity", "the protected token cache is invalid");
            }
            catch (Exception ex) when (IsCacheFailure(ex))
            {
                AppLog.Warn("antigravity", "could not open the protected token cache");
            }
        }
        if (!File.Exists(_legacyPath)) return null;
        try
        {
            var token = Extract(File.ReadAllText(_legacyPath));
            if (migrate && token is { } legacy)
                SaveCache(legacy.Access, legacy.Refresh, legacy.Expiry);
            return token;
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            AppLog.Warn("antigravity", "could not read the previous token cache");
            return null;
        }
    }

    private void RemoveLegacyCache()
    {
        try { File.Delete(_legacyPath); }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            AppLog.Warn("antigravity", "could not remove the previous token cache");
        }
    }

    private static bool IsCacheFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException;

    public static (string? Access, string? Refresh, DateTimeOffset? Expiry)? Extract(string? raw)
    {
        var text = WindowsCredentialStore.UnwrapGoKeyring(raw);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var obj = JsonUtil.ParseObject(text);
        if (obj is null)
        {
            if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return (text["Bearer ".Length..].Trim(), null, null);
            if (!text.StartsWith('{')) return (text.Trim(), null, null);
            return null;
        }
        return FromObject(obj);
    }

    private static (string? Access, string? Refresh, DateTimeOffset? Expiry)? FromObject(JsonObject obj)
    {
        var source = JsonUtil.Object(obj["token"]) ?? obj;
        var access = First(source, "access_token", "accessToken", "token", "id_token", "bearerToken");
        var refresh = First(source, "refresh_token", "refreshToken");
        var expiry = Iso8601.Parse(First(source, "expiry", "expires_at", "expiresAt"));
        if (access is null && refresh is null)
        {
            foreach (var key in new[] { "tokens", "oauth", "oauth2", "credentials", "auth" })
            {
                if (JsonUtil.Object(obj[key]) is { } nested && FromObject(nested) is { } inner)
                    return inner;
            }
            return null;
        }
        return (access, refresh, expiry);
    }

    private static string? First(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = JsonUtil.String(obj[key]);
            if (value is not null) return value;
        }
        return null;
    }
}

public static class AntigravityUsageMapper
{
    public static readonly (string BucketId, string Label, long PeriodMs)[] SummaryBuckets =
    [
        ("gemini-5h", AntigravityMetric.SessionLabel, MetricPeriod.SessionMs),
        ("gemini-weekly", AntigravityMetric.WeeklyLabel, MetricPeriod.WeekMs),
        ("3p-5h", AntigravityMetric.ClaudeLabel, MetricPeriod.SessionMs),
        ("3p-weekly", AntigravityMetric.ClaudeWeeklyLabel, MetricPeriod.WeekMs)
    ];

    public static List<MetricLine>? ParseQuotaSummary(JsonObject root)
    {
        var groups = JsonUtil.Array(JsonUtil.Object(root["response"])?["groups"]) ?? JsonUtil.Array(root["groups"]);
        if (groups is null) return null;
        var pooled = new Dictionary<string, (double Fraction, DateTimeOffset? Reset)>(StringComparer.Ordinal);
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (JsonUtil.String(group["displayName"]) == "Gemini 4 Argon") continue;
            if (JsonUtil.Array(group["buckets"]) is not { } buckets) continue;
            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                var id = JsonUtil.String(bucket["bucketId"]);
                if (id is null || SummaryBuckets.All(s => s.BucketId != id) || pooled.ContainsKey(id)) continue;
                if (JsonUtil.Number(bucket["remainingFraction"]) is not { } fraction || !double.IsFinite(fraction))
                    continue;
                pooled[id] = (fraction, Iso8601.Parse(JsonUtil.String(bucket["resetTime"])));
            }
        }
        return SummaryBuckets
            .Where(spec => pooled.ContainsKey(spec.BucketId))
            .Select(spec => Line(spec.Label, pooled[spec.BucketId].Fraction, pooled[spec.BucketId].Reset, spec.PeriodMs))
            .ToList();
    }

    public static MetricLine Line(string label, double remainingFraction, DateTimeOffset? reset, long periodMs)
    {
        var used = (1 - Math.Clamp(remainingFraction, 0, 1)) * 100;
        return MetricLine.Progress(label, used, 100, ProgressFormat.Percent, reset, periodMs);
    }

    public static string? PlanName(JsonObject? root)
    {
        var status = JsonUtil.Object(root?["userStatus"]) ?? JsonUtil.Object(JsonUtil.Object(root?["response"])?["userStatus"]) ?? root;
        var tier = JsonUtil.String(JsonUtil.Object(status?["userTier"])?["name"]);
        var plan = JsonUtil.String(JsonUtil.Object(JsonUtil.Object(status?["planStatus"])?["planInfo"])?["planName"]);
        var raw = tier ?? plan;
        return string.IsNullOrWhiteSpace(raw) ? null : JsonUtil.TitleCase(raw);
    }
}

public sealed class AntigravityProvider : IProviderRuntime
{
    public static readonly string[] CloudCodeUrls =
    [
        "https://daily-cloudcode-pa.googleapis.com",
        "https://cloudcode-pa.googleapis.com"
    ];
    public const string GoogleClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    public const string GoogleClientSecret = "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";

    private readonly AntigravityAuthStore _auth = new();
    private readonly AppHttp _http;
    private readonly AntigravityUsageClient _usage;

    public AntigravityProvider(AppHttp http)
    {
        _http = http;
        _usage = new AntigravityUsageClient(http);
    }

    public Provider Provider { get; } = new(
        "antigravity", "Antigravity", "antigravity",
        [new ProviderLink("Status", "https://status.antigravity.google/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent(AntigravityMetric.GeminiId, Provider, AntigravityMetric.SessionLabel, sessionStartSignal: SessionStartSignal.ZeroUsage),
        WidgetDescriptor.Percent(AntigravityMetric.GeminiWeeklyId, Provider, AntigravityMetric.WeeklyLabel),
        WidgetDescriptor.Percent(AntigravityMetric.ClaudeId, Provider, AntigravityMetric.ClaudeLabel, sessionStartSignal: SessionStartSignal.ZeroUsage),
        WidgetDescriptor.Percent(AntigravityMetric.ClaudeWeeklyId, Provider, AntigravityMetric.ClaudeWeeklyLabel)
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.Load() is not null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var loaded = _auth.Load(migrateCache: true);
        if (loaded is null)
            return ProviderSnapshot.Error(Provider, "Start Antigravity or run `agy` and try again.");
        try
        {
            var access = loaded.Value.Access;
            var refresh = loaded.Value.Refresh;
            if (string.IsNullOrEmpty(access) || (loaded.Value.Expiry is { } exp && exp <= DateTimeOffset.UtcNow.AddMinutes(5)))
            {
                if (string.IsNullOrEmpty(refresh))
                    return ProviderSnapshot.Error(Provider, "Couldn't read Antigravity credentials. Sign in to Antigravity again.");
                var rotated = await RefreshGoogleToken(refresh, cancellationToken);
                if (rotated is null)
                    return ProviderSnapshot.Error(Provider, "Antigravity login expired. Sign in again.");
                access = rotated.Value.Access;
                refresh = rotated.Value.Refresh ?? refresh;
                _auth.SaveCache(access, refresh, DateTimeOffset.UtcNow.AddSeconds(rotated.Value.ExpiresIn));
            }

            var (lines, plan) = await _usage.QuotaAsync(access!, cancellationToken);
            if (lines.Count == 0)
                plan ??= await _usage.FallbackPlanAsync(access!, cancellationToken);
            MetricLine.AppendNoDataIfNeeded(lines);
            return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    private async Task<(string Access, string? Refresh, double ExpiresIn)?> RefreshGoogleToken(string refreshToken, CancellationToken ct)
    {
        var body = "grant_type=refresh_token" +
                   $"&client_id={WebUtility.UrlEncode(GoogleClientId)}" +
                   $"&client_secret={WebUtility.UrlEncode(GoogleClientSecret)}" +
                   $"&refresh_token={WebUtility.UrlEncode(refreshToken)}";
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST",
            new Uri("https://oauth2.googleapis.com/token"),
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            Encoding.UTF8.GetBytes(body),
            15), ct);
        if (!response.IsSuccess) return null;
        var access = JsonUtil.String(response.Json?["access_token"]);
        if (string.IsNullOrEmpty(access)) return null;
        return (access, JsonUtil.String(response.Json?["refresh_token"]), JsonUtil.Number(response.Json?["expires_in"]) ?? 3600);
    }
}
