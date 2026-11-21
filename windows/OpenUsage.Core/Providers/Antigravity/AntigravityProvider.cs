using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
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

    public (string? Access, string? Refresh, DateTimeOffset? Expiry)? Load()
    {
        foreach (var target in new[] { "gemini:antigravity", "gemini", "Antigravity" })
        {
            var raw = WindowsCredentialStore.Read(target);
            var token = Extract(raw);
            if (token is not null) return token;
        }
        return LoadCache();
    }

    public void SaveCache(string access, string? refresh, DateTimeOffset? expiry)
    {
        try
        {
            var dir = Path.Combine(AppPaths.OpenUsageDir, "antigravity");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "auth.json"), JsonSerializer.Serialize(new
            {
                access_token = access,
                refresh_token = refresh,
                expiry = expiry?.ToString("O")
            }));
        }
        catch (Exception ex) { AppLog.Warn("antigravity", $"could not cache token: {ex.Message}"); }
    }

    private static (string? Access, string? Refresh, DateTimeOffset? Expiry)? LoadCache()
    {
        var path = Path.Combine(AppPaths.OpenUsageDir, CacheRelative);
        if (!File.Exists(path)) return null;
        try { return Extract(File.ReadAllText(path)); }
        catch { return null; }
    }

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
        WidgetDescriptor.Percent(AntigravityMetric.ClaudeWeeklyId, Provider, AntigravityMetric.ClaudeWeeklyLabel),
        ..SpendTileMapper.HistoryWidgets(Provider)
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.Load() is not null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var loaded = _auth.Load();
        if (loaded is null)
            return ProviderSnapshot.Error(Provider, "Start Antigravity or run `agy` and try again.");
        try
        {
            var (lines, plan) = await ReadQuotaAsync(loaded.Value,
                (access, refresh, expiry) => _auth.SaveCache(access, refresh, expiry), cancellationToken);
            await SpendTileMapper.AppendScanAsync(
                lines,
                () => AntigravityDbUsageScanner.Scan(),
                "From your Antigravity conversations (estimated)",
                "antigravity",
                cancellationToken);
            MetricLine.AppendNoDataIfNeeded(lines);
            return ProviderSnapshot.Make(Provider, plan, lines, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    internal async Task<(List<MetricLine> Lines, string? Plan)> ReadQuotaAsync(
        (string? Access, string? Refresh, DateTimeOffset? Expiry) auth,
        Action<string, string, DateTimeOffset> save,
        CancellationToken ct)
    {
        var renewed = string.IsNullOrEmpty(auth.Access) || auth.Expiry <= DateTimeOffset.UtcNow.AddMinutes(5);
        var access = renewed ? await RenewAccessAsync(auth.Refresh, save, ct) : auth.Access!;
        try
        {
            return await ReadWithTokenAsync(access, ct);
        }
        catch (AntigravityUsageClient.TokenRefused)
        {
            if (renewed) throw ExpiredSignIn();
        }

        AppLog.Info("antigravity", "quota request refused; renewing sign-in once");
        access = await RenewAccessAsync(auth.Refresh, save, ct);
        try { return await ReadWithTokenAsync(access, ct); }
        catch (AntigravityUsageClient.TokenRefused) { throw ExpiredSignIn(); }
    }

    private async Task<(List<MetricLine> Lines, string? Plan)> ReadWithTokenAsync(string access, CancellationToken ct)
    {
        var (lines, plan) = await _usage.QuotaAsync(access, ct);
        if (lines.Count == 0)
            plan ??= await _usage.FallbackPlanAsync(access, ct);
        return (lines, plan);
    }

    private static ProviderRequestException ExpiredSignIn() =>
        new("Antigravity login expired. Sign in again.");

    private async Task<string> RenewAccessAsync(string? refreshToken, Action<string, string, DateTimeOffset> save, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw ExpiredSignIn();
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
        ct.ThrowIfCancellationRequested();
        if (response.StatusCode is 400 or 401 or 403 && JsonUtil.String(response.Json?["error"]) == "invalid_grant")
            throw ExpiredSignIn();
        if (!response.IsSuccess)
            throw new ProviderRequestException($"Antigravity token renewal failed (HTTP {response.StatusCode}). Try again later.");
        var access = JsonUtil.String(response.Json?["access_token"]);
        if (string.IsNullOrWhiteSpace(access))
            throw new ProviderRequestException("Antigravity returned an invalid token renewal response. Try again later.");
        var expires = JsonUtil.Number(response.Json?["expires_in"]);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(expires is > 0 and <= int.MaxValue ? expires.Value : 3600);
        var rotated = JsonUtil.String(response.Json?["refresh_token"]);
        save(access, string.IsNullOrWhiteSpace(rotated) ? refreshToken : rotated, expiry);
        return access;
    }
}
