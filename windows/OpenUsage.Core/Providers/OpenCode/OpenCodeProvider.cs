using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.OpenCode;

public static class OpenCodePaths
{
    public static string DataDirectory()
    {
        if (AppPaths.Env("OPENCODE_DATA_DIR") is { } overrideDir) return AppPaths.Expand(overrideDir);
        if (AppPaths.Env("XDG_DATA_HOME") is { } xdg) return Path.Combine(AppPaths.Expand(xdg), "opencode");
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppPaths.Home, ".local", "share", "opencode"),
                     Path.Combine(AppPaths.LocalAppData, "opencode"),
                     Path.Combine(AppPaths.AppData, "opencode")
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(AppPaths.Home, ".local", "share", "opencode");
    }

    public static string AuthFile(string dataDirectory) => Path.Combine(dataDirectory, "auth.json");

    /// <summary>
    /// Every <c>opencode*.db</c> in the data directory, path-sorted. OpenCode partitions its database by
    /// release channel (<c>opencode.db</c>, <c>opencode-next.db</c>, …), so all channels are read. The
    /// <c>.db</c> suffix excludes the <c>-wal</c>/<c>-shm</c> sidecars. A missing directory is empty.
    /// </summary>
    public static IReadOnlyList<string> DatabaseFiles(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return [];
        return Directory.GetFiles(dataDirectory, "opencode*.db")
            .Where(p => p.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}

public static class OpenCodeErrors
{
    public const string NotLoggedIn = "OpenCode Go not detected. Log in with OpenCode Go and refresh.";
    public const string CredentialsUnreadable = "Couldn't read OpenCode's auth.json. Check its file permissions or log into OpenCode Go again.";
    public const string Unauthorized = "OpenCode Go key was rejected. Log into OpenCode Go again.";
    public const string NoGoSubscription = "No OpenCode Go subscription on this key.";
}

/// <summary>
/// Reads the OpenCode Go key already on the machine. OpenCode 2 keeps it in each channel database's
/// <c>credential</c> table; OpenCode 1 in <c>auth.json</c>. OpenCode 2 imports the file once per database
/// (recorded as a migration) and never deletes it, so after that import later logins and logouts only
/// show up in the table — and OpenCode 1.18+ already creates an empty table while still using the file.
/// </summary>
public sealed class OpenCodeAuthStore
{
    public const string CredentialsImportedMigration = "20260805200742_import_legacy_credentials";

    private const string GoKeyPrefix = "key:";
    private const string ImportedPrefix = "imported:";

    private const string GoKeySql = $"""
        SELECT '{GoKeyPrefix}' || COALESCE((
          SELECT json_extract(value,'$.key')
          FROM credential
          WHERE integration_id = 'opencode-go' AND (active IS NULL OR active = 1)
          ORDER BY active DESC, time_updated DESC, id DESC
          LIMIT 1), '');
        """;

    private const string ImportedSql =
        $"SELECT '{ImportedPrefix}' || EXISTS(SELECT 1 FROM migration WHERE id = '{CredentialsImportedMigration}');";

    private readonly string? _dataDirectory;

    public OpenCodeAuthStore(string? dataDirectory = null) => _dataDirectory = dataDirectory;

    private string DataDirectory => _dataDirectory ?? OpenCodePaths.DataDirectory();

    /// <summary>
    /// The non-empty <c>opencode-go</c> key, or null when the user isn't logged into OpenCode Go. The
    /// first imported database holding a key wins; <c>auth.json</c> stands in only when no database has
    /// imported credentials, so an OpenCode 2 logout isn't revived by the stale file. Unreadable storage
    /// throws so it's never mistaken for logout.
    /// </summary>
    public string? GoApiKey()
    {
        var dir = DataDirectory;
        var hasCredentialTable = false;
        string? failure = null;
        foreach (var path in OpenCodePaths.DatabaseFiles(dir))
        {
            var tables = SqliteReader.QueryValue(path,
                "SELECT 'tables:' || COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('migration','credential');");
            if (tables is null)
            {
                failure = Path.GetFileName(path);
                continue;
            }
            if (tables != "tables:2") continue;
            var imported = SqliteReader.QueryValue(path, ImportedSql);
            if (imported is null)
            {
                failure = Path.GetFileName(path);
                continue;
            }
            if (imported != ImportedPrefix + "1") continue;
            var value = SqliteReader.QueryValue(path, GoKeySql);
            if (value is null || !value.StartsWith(GoKeyPrefix, StringComparison.Ordinal))
            {
                failure = Path.GetFileName(path);
                continue;
            }
            hasCredentialTable = true;
            var key = value[GoKeyPrefix.Length..].Trim();
            if (key.Length > 0) return key;
        }
        if (failure is not null)
        {
            AppLog.Warn("opencode", $"credential database unreadable: {failure}");
            throw new InvalidOperationException(OpenCodeErrors.CredentialsUnreadable);
        }
        if (hasCredentialTable) return null;
        return AuthFileGoKey(dir);
    }

    private static string? AuthFileGoKey(string dir)
    {
        var path = OpenCodePaths.AuthFile(dir);
        if (!File.Exists(path)) return null;
        JsonObject? obj;
        try { obj = JsonUtil.ParseObject(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            AppLog.Warn("opencode", $"auth.json unreadable: {ex.Message}");
            throw new InvalidOperationException(OpenCodeErrors.CredentialsUnreadable, ex);
        }
        if (obj is null)
        {
            AppLog.Warn("opencode", "auth.json is not a JSON object");
            throw new InvalidOperationException(OpenCodeErrors.CredentialsUnreadable);
        }
        // Reads only the `opencode-go` entry, tolerant of unrelated sibling entries.
        return JsonUtil.String(JsonUtil.Object(obj["opencode-go"])?["key"]);
    }
}

public static class OpenCodeUsageMapper
{
    /// <summary>
    /// Maximum distance from exactly one full period for a zero-usage rolling reset to count as the
    /// untouched-window placeholder. OpenCode rounds to whole seconds and HTTP <c>Date</c> headers carry
    /// whole-second precision, so two seconds covers both rounding steps.
    /// </summary>
    public static readonly TimeSpan PlaceholderResetTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compares two server-authored instants when possible (the response's <c>Date</c> header), which
    /// removes local clock skew from the placeholder test; falls back to the local capture time.
    /// </summary>
    public static List<MetricLine> MeterLines(AppHttpResponse response, DateTimeOffset capturedAt)
    {
        var body = response.Json ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var reference = HttpDate(response.Header("date")) ?? capturedAt;
        return MeterLines(body, reference);
    }

    public static List<MetricLine> MeterLines(JsonObject body, DateTimeOffset capturedAt)
    {
        var usage = JsonUtil.Object(body["usage"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return
        [
            Window(usage["rolling"], "Session", MetricPeriod.SessionMs, capturedAt, dropsPlaceholderReset: true),
            Window(usage["weekly"], "Weekly", MetricPeriod.WeekMs, capturedAt),
            Window(usage["monthly"], "Monthly", MetricPeriod.MonthMs, capturedAt)
        ];
    }

    public static string? ErrorType(AppHttpResponse response) =>
        JsonUtil.String(JsonUtil.Object(response.Json?["error"])?["type"]);

    /// <summary>
    /// One window. <paramref name="dropsPlaceholderReset"/> is for the rolling session only: with no calls
    /// in the window the API answers <c>resetsAt = now + 5h</c>, re-derived on every request. That
    /// placeholder is dropped at capture time so the Session row reads "Not started" off the missing
    /// reset, while a sub-1% session (whole-percent 0, reset anchored to its oldest call) keeps its
    /// countdown. Weekly and monthly resets are calendar instants and keep theirs.
    /// </summary>
    private static MetricLine Window(JsonNode? raw, string label, long periodMs, DateTimeOffset capturedAt,
        bool dropsPlaceholderReset = false)
    {
        var obj = JsonUtil.Object(raw) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var percent = JsonUtil.Number(obj["percent"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var reportedReset = Iso8601.Parse(JsonUtil.String(obj["resetsAt"]))
            ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var used = JsonUtil.ClampPercent(percent);
        DateTimeOffset? resetsAt = reportedReset;
        if (dropsPlaceholderReset && used == 0)
        {
            var distance = (reportedReset - capturedAt - TimeSpan.FromMilliseconds(periodMs)).Duration();
            if (distance <= PlaceholderResetTolerance) resetsAt = null;
        }
        return MetricLine.Progress(label, used, 100, ProgressFormat.Percent, resetsAt, periodMs);
    }

    private static DateTimeOffset? HttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParseExact(value.Trim(), "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}

public sealed class OpenCodeProvider : IProviderRuntime
{
    public static readonly Uri UsageUrl = new("https://opencode.ai/zen/go/v1/usage");
    private readonly OpenCodeAuthStore _auth;
    private readonly AppHttp _http;

    public OpenCodeProvider(AppHttp http, string? dataDirectory = null)
    {
        _http = http;
        _auth = new OpenCodeAuthStore(dataDirectory);
    }

    public Provider Provider { get; } = new(
        "opencode", "OpenCode", "opencode",
        [new ProviderLink("Zen", "https://opencode.ai/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("opencode.session", Provider, "Session", sessionStartSignal: SessionStartSignal.MissingResetDate),
        WidgetDescriptor.Percent("opencode.weekly", Provider, "Weekly"),
        WidgetDescriptor.Percent("opencode.monthly", Provider, "Monthly")
    ];

    /// <summary>
    /// Unreadable credentials still count as detected so refresh can surface the error.
    /// </summary>
    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                return _auth.GoApiKey() is not null;
            }
            catch (Exception)
            {
                return true;
            }
        }, cancellationToken);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var refreshedAt = DateTimeOffset.Now;
        string? key;
        try { key = await Task.Run(() => _auth.GoApiKey(), cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
        if (key is null) return ProviderSnapshot.Error(Provider, OpenCodeErrors.NotLoggedIn);

        var (meters, error) = await FetchGoMeters(key, cancellationToken);
        if (meters is not null)
            return ProviderSnapshot.Make(Provider, "Go", meters, refreshedAt);

        var reason = error ?? OpenCodeErrors.NoGoSubscription;
        AppLog.Warn("opencode", $"Go meters unavailable: {reason}");
        return ProviderSnapshot.Error(Provider, reason);
    }

    /// <summary>The Go meters, or null with an error (null error = no Go subscription on this key).</summary>
    private async Task<(List<MetricLine>? Meters, string? Error)> FetchGoMeters(string key, CancellationToken ct)
    {
        AppHttpResponse response;
        try
        {
            response = await _http.SendAsync(new AppHttpRequest(
                "GET", UsageUrl,
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {key}",
                    ["Accept"] = "application/json"
                },
                TimeoutSeconds: 15), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn("opencode", $"Go usage request failed: {ex.Message}");
            return (null, ProviderErrors.ConnectionFailed);
        }
        if (response.StatusCode == 401) return (null, OpenCodeErrors.Unauthorized);
        if (response.StatusCode == 403 && OpenCodeUsageMapper.ErrorType(response) == "EntitlementError")
        {
            AppLog.Info("opencode", "Go usage endpoint: no active subscription");
            return (null, null);
        }
        if (!response.IsSuccess) return (null, ProviderErrors.RequestFailed(response.StatusCode));
        try { return (OpenCodeUsageMapper.MeterLines(response, DateTimeOffset.Now), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
