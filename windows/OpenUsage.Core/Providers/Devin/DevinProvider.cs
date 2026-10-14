using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Devin;

public sealed class DevinAuth
{
    public string ApiKey { get; set; } = "";
    public string? ApiServerUrl { get; set; }
}

public sealed class DevinAuthStore
{
    public static readonly string DefaultApiServerUrl = "https://server.codeium.com";

    public static IEnumerable<string> CredentialPaths()
    {
        yield return AppPaths.Expand("~/.local/share/devin/credentials.toml");
        yield return Path.Combine(AppPaths.LocalAppData, "devin", "credentials.toml");
        yield return Path.Combine(AppPaths.Home, ".devin", "credentials.toml");
    }

    public static IEnumerable<string> StateDbPaths()
    {
        yield return Path.Combine(AppPaths.AppData, "Devin", "User", "globalStorage", "state.vscdb");
        yield return Path.Combine(AppPaths.AppData, "Windsurf", "User", "globalStorage", "state.vscdb");
        yield return AppPaths.Expand("~/Library/Application Support/Devin/User/globalStorage/state.vscdb");
    }

    public DevinAuth? Load() => LoadCredentialsFile() ?? LoadAppAuth();

    private static DevinAuth? LoadCredentialsFile()
    {
        foreach (var path in CredentialPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                var apiKey = ReadToml(text, "windsurf_api_key") ?? ReadToml(text, "api_key");
                if (string.IsNullOrEmpty(apiKey)) continue;
                return new DevinAuth
                {
                    ApiKey = apiKey,
                    ApiServerUrl = ReadToml(text, "api_server_url")
                };
            }
            catch (Exception ex) { AppLog.Warn("devin", $"credentials unreadable: {ex.Message}"); }
        }
        return null;
    }

    private static DevinAuth? LoadAppAuth()
    {
        foreach (var db in StateDbPaths())
        {
            var value = SqliteReader.QueryValue(db, "SELECT value FROM ItemTable WHERE key = 'windsurfAuthStatus' LIMIT 1");
            if (value is null) continue;
            var obj = JsonUtil.ParseObject(value);
            var apiKey = JsonUtil.String(obj?["apiKey"]);
            if (string.IsNullOrEmpty(apiKey)) continue;
            return new DevinAuth { ApiKey = apiKey };
        }
        return null;
    }

    private static string? ReadToml(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + " ", StringComparison.Ordinal) &&
                !trimmed.StartsWith(key + "=", StringComparison.Ordinal))
                continue;
            var idx = trimmed.IndexOf('=');
            if (idx < 0) continue;
            return trimmed[(idx + 1)..].Trim().Trim('"', '\'');
        }
        return null;
    }
}

public static class DevinUsageMapper
{
    public static (string? Plan, List<MetricLine> Lines) Map(JsonObject body)
    {
        var userStatus = JsonUtil.Object(body["userStatus"])
            ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var planStatus = JsonUtil.Object(userStatus["planStatus"]) ?? new JsonObject();
        var planInfo = JsonUtil.Object(planStatus["planInfo"]) ?? new JsonObject();
        var plan = JsonUtil.String(planInfo["planName"]) ?? "Unknown";
        var hideDaily = JsonUtil.Bool(planInfo["hideDailyQuota"]) == true;
        var dailyRemaining = JsonUtil.Number(planStatus["dailyQuotaRemainingPercent"]);
        var weeklyRemaining = JsonUtil.Number(planStatus["weeklyQuotaRemainingPercent"]);
        // A present-but-unparsable percentage is schema drift, not an omitted zero: fail loudly instead
        // of letting the exhausted-quota fallback below turn it into a credible 100% used.
        if (planStatus.ContainsKey("weeklyQuotaRemainingPercent") && weeklyRemaining is null)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var dailyReset = hideDaily ? null : Iso8601.FromUnixSeconds(JsonUtil.Number(planStatus["dailyQuotaResetAtUnix"]));
        var weeklyReset = Iso8601.FromUnixSeconds(JsonUtil.Number(planStatus["weeklyQuotaResetAtUnix"]));
        var extra = JsonUtil.Number(planStatus["overageBalanceMicros"]) is { } micros ? micros / 1_000_000.0 : (double?)null;

        var lines = new List<MetricLine>();
        if (!hideDaily && dailyRemaining is { } daily)
            lines.Add(QuotaLine("Daily quota", daily, dailyReset, MetricPeriod.DayMs));
        // Proto3 JSON omits zero values. A weekly reset identifies an existing weekly window.
        if ((weeklyRemaining ?? (weeklyReset is not null ? (double?)0 : null)) is { } weekly)
            lines.Add(QuotaLine("Weekly quota", weekly, weeklyReset, MetricPeriod.WeekMs));
        else if (hideDaily && dailyRemaining is { } fallback)
            lines.Add(QuotaLine("Weekly quota", fallback, weeklyReset, MetricPeriod.WeekMs));
        if (extra is { } extraBalance)
            lines.Add(MetricLine.Values("Extra usage balance", [new MetricValue(extraBalance, MetricKind.Dollars)]));
        if (lines.Count == 0)
            throw new InvalidOperationException("Devin quota unavailable. Try again later.");
        return (plan, lines);
    }

    private static MetricLine QuotaLine(string label, double remaining, DateTimeOffset? resetsAt, long periodMs) =>
        MetricLine.Progress(label, JsonUtil.ClampPercent(100 - remaining), 100, ProgressFormat.Percent, resetsAt, periodMs);
}

public sealed class DevinProvider : IProviderRuntime
{
    private readonly DevinAuthStore _auth = new();
    private readonly AppHttp _http;

    public DevinProvider(AppHttp http) => _http = http;

    public Provider Provider { get; } = new(
        "devin", "Devin", "devin",
        [new ProviderLink("App", "https://app.devin.ai/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("devin.daily", Provider, "Daily", "Daily quota"),
        WidgetDescriptor.Percent("devin.weekly", Provider, "Weekly", "Weekly quota"),
        WidgetDescriptor.DollarBalance("devin.extra", Provider, "Extra Usage", "left", "Extra usage balance")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.Load() is not null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var auth = _auth.Load();
        if (auth is null)
            return ProviderSnapshot.Error(Provider, "Run devin auth login or sign in to Devin and try again.");
        try
        {
            var server = string.IsNullOrWhiteSpace(auth.ApiServerUrl)
                ? DevinAuthStore.DefaultApiServerUrl
                : auth.ApiServerUrl.TrimEnd('/');
            var url = new Uri($"{server}/exa.seat_management_pb.SeatManagementService/GetUserStatus");
            var payload = JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    apiKey = auth.ApiKey,
                    ideName = "devin",
                    ideVersion = "1.108.2",
                    extensionName = "devin",
                    extensionVersion = "1.108.2",
                    locale = "en"
                }
            });
            var response = await _http.SendAsync(new AppHttpRequest(
                "POST", url,
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Connect-Protocol-Version"] = "1"
                },
                Encoding.UTF8.GetBytes(payload),
                15), cancellationToken);
            if (!response.IsSuccess)
                return ProviderSnapshot.Error(Provider, ProviderErrors.RequestFailed(response.StatusCode));
            if (response.Json is not { } body)
                return ProviderSnapshot.Error(Provider, ProviderErrors.InvalidResponse);
            var mapped = DevinUsageMapper.Map(body);
            var lines = mapped.Lines.ToList();
            MetricLine.AppendNoDataIfNeeded(lines);
            return ProviderSnapshot.Make(Provider, mapped.Plan, lines, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }
}
