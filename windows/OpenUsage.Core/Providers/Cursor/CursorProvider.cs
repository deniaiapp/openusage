using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed class CursorAuthState
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string Source { get; set; } = "sqlite";
}

public sealed class CursorAuthStore
{
    public const string AccessTokenKey = "cursorAuth/accessToken";
    public const string RefreshTokenKey = "cursorAuth/refreshToken";
    public const string MembershipTypeKey = "cursorAuth/stripeMembershipType";
    public const string ClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";

    public static IEnumerable<string> StateDbPaths()
    {
        yield return Path.Combine(AppPaths.AppData, "Cursor", "User", "globalStorage", "state.vscdb");
        yield return AppPaths.Expand("~/Library/Application Support/Cursor/User/globalStorage/state.vscdb");
    }

    public string? StateDbPath => StateDbPaths().FirstOrDefault(File.Exists);

    public CursorAuthState? Load()
    {
        string? sqliteAccess = null, sqliteRefresh = null, membership = null;
        if (StateDbPath is { } db)
        {
            sqliteAccess = ReadState(db, AccessTokenKey);
            sqliteRefresh = ReadState(db, RefreshTokenKey);
            membership = ReadState(db, MembershipTypeKey)?.Trim().ToLowerInvariant();
        }
        var keychainAccess = WindowsCredentialStore.Read("cursor-access-token");
        var keychainRefresh = WindowsCredentialStore.Read("cursor-refresh-token");

        if (sqliteAccess is not null || sqliteRefresh is not null)
        {
            if ((keychainAccess is not null || keychainRefresh is not null) &&
                membership == "free" &&
                Jwt.Subject(sqliteAccess) is { } sqliteSub &&
                Jwt.Subject(keychainAccess) is { } keySub &&
                sqliteSub != keySub)
            {
                return new CursorAuthState { AccessToken = keychainAccess, RefreshToken = keychainRefresh, Source = "keychain" };
            }
            return new CursorAuthState { AccessToken = sqliteAccess, RefreshToken = sqliteRefresh, Source = "sqlite" };
        }
        if (keychainAccess is not null || keychainRefresh is not null)
            return new CursorAuthState { AccessToken = keychainAccess, RefreshToken = keychainRefresh, Source = "keychain" };
        return null;
    }

    public void SaveAccessToken(string token, CursorAuthState state)
    {
        if (state.Source == "sqlite" && StateDbPath is { } db)
        {
            SqliteReader.Execute(db,
                $"INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('{SqliteReader.Escape(AccessTokenKey)}', '{SqliteReader.Escape(token)}');");
        }
        state.AccessToken = token;
    }

    private static string? ReadState(string db, string key) =>
        SqliteReader.QueryValue(db, $"SELECT value FROM ItemTable WHERE key = '{SqliteReader.Escape(key)}' LIMIT 1;");
}

public static class CursorUsageMapper
{
    public static (string? Plan, List<MetricLine> Lines) Map(
        JsonObject usage, string? planName, JsonObject? creditGrants, double stripeBalanceCents,
        CursorUnifiedBilling? unified = null)
    {
        if (JsonUtil.Bool(usage["enabled"]) == false)
            throw new InvalidOperationException("No active Cursor subscription.");
        var planUsage = JsonUtil.Object(usage["planUsage"])
            ?? throw new InvalidOperationException("No active Cursor subscription.");

        var limit = JsonUtil.Number(planUsage["limit"]);
        var totalPercentUsed = JsonUtil.Number(planUsage["totalPercentUsed"]);
        var hasModelPools = HasModelPools(planUsage, limit);
        if (limit is null && totalPercentUsed is null && !hasModelPools)
            throw new InvalidOperationException("Total usage limit missing from API response.");

        var lines = new List<MetricLine>();
        if (unified is null)
            AppendCredits(creditGrants, stripeBalanceCents, lines);
        else if (unified.CreditBalance is { } balance)
            lines.Add(MetricLine.Values("Credits",
                [new MetricValue(JsonUtil.CentsToDollars(Math.Max(0, JsonUtil.Number(balance["usdRemainingCents"]) ?? 0)), MetricKind.Dollars)]));

        var planUsedCents = JsonUtil.Number(planUsage["totalSpend"])
            ?? ((limit ?? 0) - (JsonUtil.Number(planUsage["remaining"]) ?? 0));
        var computedPercent = limit is { } lim && lim > 0 ? planUsedCents / lim * 100 : 0;
        var totalUsagePercent = totalPercentUsed ?? computedPercent;
        var cycle = BillingCycle(usage);
        var spendLimitUsage = JsonUtil.Object(usage["spendLimitUsage"]);
        var normalizedPlan = planName?.Trim().ToLowerInvariant() ?? "";
        var isTeam = normalizedPlan == "team"
            || JsonUtil.String(spendLimitUsage?["limitType"])?.ToLowerInvariant() == "team"
            || (JsonUtil.Number(spendLimitUsage?["pooledLimit"]) ?? 0) > 0;

        if (isTeam && hasModelPools)
        {
            // Modern Teams seats still report the old $20 allowance. Structured pool data wins;
            // never synthesize a total from stale dollars when Cursor omits totalPercentUsed.
            if (totalPercentUsed is { } percent)
                lines.Add(MetricLine.Progress("Total usage", percent, 100, ProgressFormat.Percent, cycle.ResetsAt, cycle.PeriodMs));
        }
        else if (isTeam)
        {
            if (limit is not { } teamLimit)
                throw new InvalidOperationException("Cursor request-based usage data unavailable. Try again later.");
            lines.Add(MetricLine.Progress("Total usage", JsonUtil.CentsToDollars(planUsedCents),
                JsonUtil.CentsToDollars(teamLimit), ProgressFormat.Dollars, cycle.ResetsAt, cycle.PeriodMs));
        }
        else
        {
            lines.Add(MetricLine.Progress("Total usage", totalUsagePercent, 100, ProgressFormat.Percent,
                cycle.ResetsAt, cycle.PeriodMs));
        }

        if (unified is not null)
        {
            if (MapAdditionalUsage(unified.UsageStatus) is { } additionalUsage)
                lines.Add(additionalUsage);
            return (PlanLabel(planName), lines);
        }

        if (JsonUtil.Number(planUsage["autoPercentUsed"]) is { } autoPercent)
            lines.Add(MetricLine.Progress("Cursor Models", autoPercent, 100, ProgressFormat.Percent, cycle.ResetsAt, cycle.PeriodMs));
        if (JsonUtil.Number(planUsage["apiPercentUsed"]) is { } apiPercent)
            lines.Add(MetricLine.Progress("Other Models", apiPercent, 100, ProgressFormat.Percent, cycle.ResetsAt, cycle.PeriodMs));

        if (spendLimitUsage is not null)
        {
            var (onDemandLimit, spent) = OnDemandBudget(spendLimitUsage, isTeam);
            if (onDemandLimit > 0)
                lines.Add(MetricLine.Progress("On-demand", JsonUtil.CentsToDollars(spent),
                    JsonUtil.CentsToDollars(onDemandLimit), ProgressFormat.Dollars));
            else if (spent > 0)
                lines.Add(MetricLine.Values("On-demand", [new MetricValue(JsonUtil.CentsToDollars(spent), MetricKind.Dollars)]));
        }

        return (PlanLabel(planName), lines);
    }

    private static (double Limit, double Spent) OnDemandBudget(JsonObject usage, bool isTeam)
    {
        var (limit, reported, remaining) = JsonUtil.Number(usage["individualLimit"]) is { } personal
            ? (personal, isTeam || JsonUtil.Number(usage["pooledUsed"]) > 0
                ? new[] { "individualUsed" } : new[] { "individualUsed", "totalSpend" }, "individualRemaining")
            : JsonUtil.Number(usage["pooledLimit"]) is > 0 and var pooled
                ? (pooled, new[] { "pooledUsed" }, "pooledRemaining")
                : (0, new[] { "individualUsed", "pooledUsed", "totalSpend" }, null);
        var spent = reported.Select(field => JsonUtil.Number(usage[field]) ?? 0).FirstOrDefault(value => value > 0);
        if (spent == 0 && remaining is not null && JsonUtil.Number(usage[remaining]) is { } left)
            spent = Math.Max(0, limit - left);
        return (limit, spent);
    }

    private static MetricLine? MapAdditionalUsage(JsonObject? status)
    {
        if (status is null || (JsonUtil.String(status["availability"]) ?? "available") != "available"
            || JsonUtil.Object(status["monthlyUsage"]) is not { } monthly
            || JsonUtil.Bool(monthly["additionalUsageOff"]) == true)
            return null;
        var limit = JsonUtil.Number(monthly["limitCents"]);
        if (limit == 0) return null;
        var used = JsonUtil.CentsToDollars(Math.Max(0, JsonUtil.Number(monthly["usedCents"]) ?? 0));
        if (limit is not > 0)
            return MetricLine.Values("On-demand", [new MetricValue(used, MetricKind.Dollars)]);
        var reset = Iso8601.Parse(JsonUtil.String(monthly["resetsAt"]));
        var start = Iso8601.Parse(JsonUtil.String(monthly["periodStart"]));
        var periodMs = reset is { } end && start is { } beginning && end > beginning
            ? (long)(end - beginning).TotalMilliseconds : MetricPeriod.MonthMs;
        return MetricLine.Progress("On-demand", used, JsonUtil.CentsToDollars(limit.Value),
            ProgressFormat.Dollars, reset, periodMs);
    }

    public static MetricLine? MapGrokBot(JsonObject? usage)
    {
        if (usage is null) return null;
        if (JsonUtil.Bool(usage["usesPooledEnterpriseAllowance"]) == true) return null;
        if (JsonUtil.Bool(usage["hasNonZeroIncludedLimit"]) == false) return null;
        if (JsonUtil.Bool(usage["includedLimitZero"]) == true) return null;
        if (JsonUtil.Number(usage["usagePercent"]) is not { } percent || percent < 0) return null;
        var reset = Iso8601.Parse(JsonUtil.String(usage["nextResetTimestampUtc"]));
        var start = Iso8601.Parse(JsonUtil.String(usage["currentPeriodStart"]));
        var period = start is { } s && reset is { } r && r > s
            ? (int)(r - s).TotalMilliseconds
            : MetricPeriod.WeekMs;
        return MetricLine.Progress("Grok Bot usage", JsonUtil.ClampPercent(percent), 100, ProgressFormat.Percent, reset, period);
    }

    /// <summary>
    /// Two usable model-pool percentages (Cursor Models / Other Models). Legacy team accounts send
    /// zero placeholders beside positive spend; those keep the dollar meter.
    /// </summary>
    public static bool HasModelPools(JsonObject planUsage, double? limit)
    {
        if (PoolPercent(planUsage["autoPercentUsed"]) is not { } auto ||
            PoolPercent(planUsage["apiPercentUsed"]) is not { } api)
            return false;
        var spent = JsonUtil.Number(planUsage["totalSpend"])
            ?? ((limit ?? 0) - (JsonUtil.Number(planUsage["remaining"]) ?? limit ?? 0));
        return auto > 0 || api > 0 || spent == 0;
    }

    private static double? PoolPercent(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out _)) return null;
        return JsonUtil.Number(node) is { } percent && double.IsFinite(percent) && percent >= 0 ? percent : null;
    }

    public static double StripeBalanceCents(JsonObject? body)
    {
        if (body is null) return 0;
        var balance = JsonUtil.Number(body["customerBalance"]);
        return balance is { } value && value < 0 ? Math.Abs(value) : 0;
    }

    private static void AppendCredits(JsonObject? creditGrants, double stripeBalanceCents, List<MetricLine> lines)
    {
        var hasGrants = JsonUtil.Bool(creditGrants?["hasCreditGrants"]) == true;
        var grantTotal = hasGrants ? JsonUtil.Number(creditGrants?["totalCents"]) ?? 0 : 0;
        var grantUsed = hasGrants ? JsonUtil.Number(creditGrants?["usedCents"]) ?? 0 : 0;
        var valid = hasGrants && grantTotal > 0;
        var combined = (valid ? grantTotal : 0) + stripeBalanceCents;
        var remaining = Math.Max(0, combined - (valid ? grantUsed : 0));
        if (combined <= 0) return;
        lines.Add(MetricLine.Values("Credits", [new MetricValue(JsonUtil.CentsToDollars(remaining), MetricKind.Dollars)]));
    }

    private static (DateTimeOffset? ResetsAt, long PeriodMs) BillingCycle(JsonObject usage)
    {
        var start = JsonUtil.Number(usage["billingCycleStart"]);
        var end = JsonUtil.Number(usage["billingCycleEnd"]);
        if (start is { } s && end is { } e && e > s)
            return (DateTimeOffset.FromUnixTimeMilliseconds((long)e), (long)(e - s));
        return (end is { } endMs ? DateTimeOffset.FromUnixTimeMilliseconds((long)endMs) : null, MetricPeriod.MonthMs);
    }

    private static string? PlanLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return JsonUtil.TitleCase(name);
    }
}

public sealed record CursorUnifiedBilling(JsonObject? CreditBalance, JsonObject? UsageStatus);

public sealed class CursorProvider : IProviderRuntime
{
    public static readonly Uri UsageUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
    public static readonly Uri GrokBotUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus");
    public static readonly Uri PlanUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetPlanInfo");
    public static readonly Uri RefreshUrl = new("https://api2.cursor.sh/oauth/token");
    public static readonly Uri CreditsUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCreditGrantsBalance");
    public static readonly Uri StripeUrl = new("https://cursor.com/api/auth/stripe");
    private static readonly Uri UnifiedCreditsUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetComplatCreditBalance");
    private static readonly Uri UsageStatusUrl = new("https://cursor.com/api/dashboard/v2/usage-status?scope=user");

    private readonly CursorAuthStore _auth = new();
    private readonly AppHttp _http;
    private readonly CursorUsageHistoryStore _historyStore;
    private readonly TimeSpan _spendHistoryBudget;
    private readonly Func<ModelPricing> _pricing;
    private BillingPlan? _lastBillingPlan;

    private sealed record BillingPlan(string CredentialHash, string? Name, bool IsMerged);

    public CursorProvider(
        AppHttp http,
        CursorUsageHistoryStore? historyStore = null,
        TimeSpan? spendHistoryBudget = null,
        Func<ModelPricing>? pricing = null)
    {
        _http = http;
        _historyStore = historyStore ?? new CursorUsageHistoryStore();
        _spendHistoryBudget = spendHistoryBudget ?? TimeSpan.FromSeconds(20);
        _pricing = pricing ?? (() => ModelPricing.Shared);
    }

    public Provider Provider { get; } = new(
        "cursor", "Cursor", "cursor",
        [
            new ProviderLink("Status", "https://status.cursor.com/"),
            new ProviderLink("Dashboard", "https://www.cursor.com/dashboard")
        ]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("cursor.usage", Provider, "Total Usage", "Total usage"),
        WidgetDescriptor.Percent("cursor.auto", Provider, "Cursor Models"),
        WidgetDescriptor.Percent("cursor.api", Provider, "Other Models"),
        WidgetDescriptor.Percent("cursor.grokBot", Provider, "Grok Bot", "Grok Bot usage"),
        WidgetDescriptor.BoundedDollars("cursor.onDemand", Provider, "Extra Usage", 100, "On-demand", valueWord: "spent"),
        WidgetDescriptor.BoundedCount("cursor.requests", Provider, "Requests", 500, "requests", MetricPeriod.MonthMs),
        WidgetDescriptor.DollarBalance("cursor.credits", Provider, "Credits", "left"),
        ..SpendTileMapper.HistoryWidgets(Provider, CursorUsageHistoryNote)
    ];

    public const string CursorUsageHistoryNote = "From your Cursor usage history.";

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_auth.Load() is not null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var state = _auth.Load();
        if (state is null)
            return ProviderSnapshot.Error(Provider, "Not logged in. Sign in via Cursor app or run `agent login`.");
        try
        {
            return await ProbeAsync(state, cancellationToken);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    internal async Task<ProviderSnapshot> ProbeAsync(CursorAuthState state, CancellationToken ct)
    {
        var access = state.AccessToken?.Trim();
        if (string.IsNullOrEmpty(access) || NeedsRefresh(access))
        {
            if (string.IsNullOrEmpty(state.RefreshToken))
                throw new InvalidOperationException("Session expired. Sign in via Cursor app or run `agent login`.");
            access = await RefreshTokenAsync(state, ct);
        }

        var usageResponse = await ConnectPost(UsageUrl, access, ct);
        if (usageResponse.StatusCode is 401 or 403 && !string.IsNullOrEmpty(state.RefreshToken))
        {
            access = await RefreshTokenAsync(state, ct);
            usageResponse = await ConnectPost(UsageUrl, access, ct);
        }
        if (usageResponse.StatusCode is 401 or 403)
            throw new InvalidOperationException("Session expired. Sign in via Cursor app or run `agent login`.");
        if (!usageResponse.IsSuccess)
            throw new InvalidOperationException(ProviderErrors.RequestFailed(usageResponse.StatusCode));
        var usage = usageResponse.Json ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);

        var plan = await ReadBillingPlanAsync(access, ct);
        var planName = plan?.Name;
        JsonObject? grok = null;
        (string? Plan, List<MetricLine> Lines) mapped;
        if (plan?.IsMerged == true)
        {
            var balance = await ReadOptionalAsync("credit balance", () => ConnectPost(UnifiedCreditsUrl, access, ct), ct);
            var status = await ReadDashboardAsync("usage status", UsageStatusUrl, access, ct);
            mapped = CursorUsageMapper.Map(usage, planName, null, 0, new CursorUnifiedBilling(balance, status));
        }
        else
        {
            var credits = await ReadOptionalAsync("credits", () => ConnectPost(CreditsUrl, access, ct), ct);
            grok = await ReadOptionalAsync("Grok Bot usage", () => ConnectPost(GrokBotUrl, access, ct), ct);
            var stripe = await ReadDashboardAsync("Stripe balance", StripeUrl, access, ct);
            mapped = CursorUsageMapper.Map(usage, planName, credits, CursorUsageMapper.StripeBalanceCents(stripe));
        }

        var lines = mapped.Lines.ToList();
        if (CursorUsageMapper.MapGrokBot(grok) is { } grokLine)
            lines.Add(grokLine);
        await AppendSpendLinesAsync(lines, access, ct);
        MetricLine.AppendNoDataIfNeeded(lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, lines, DateTimeOffset.Now);
    }

    private async Task<BillingPlan?> ReadBillingPlanAsync(string access, CancellationToken ct)
    {
        var credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access)));
        var previous = _lastBillingPlan is { } cached && cached.CredentialHash == credential ? cached : null;
        var response = await ReadOptionalAsync("plan", () => ConnectPost(PlanUrl, access, ct), ct);
        var name = new[]
        {
            JsonUtil.String(JsonUtil.Object(response?["planInfo"])?["planName"]),
            JsonUtil.String(response?["planName"]),
            JsonUtil.String(response?["membershipType"])
        }.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        var merged = JsonUtil.Bool(response?["isMerged"]);
        if (merged is null && string.IsNullOrWhiteSpace(name))
        {
            if (response is not null) AppLog.Warn("cursor", "plan response did not identify the billing plan");
            return previous;
        }
        ct.ThrowIfCancellationRequested();
        return _lastBillingPlan = new(credential, name ?? previous?.Name, merged ?? previous?.IsMerged ?? false);
    }

    private async Task<JsonObject?> ReadDashboardAsync(string label, Uri url, string access, CancellationToken ct)
    {
        if (CursorSession.From(access) is not { } session)
        {
            AppLog.Warn("cursor", $"could not prepare {label} request from the current session");
            return null;
        }
        return await ReadOptionalAsync(label, () => _http.SendAsync(new AppHttpRequest(
            "GET", url,
            new Dictionary<string, string> { ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}" },
            TimeoutSeconds: 10), ct), ct);
    }

    private static async Task<JsonObject?> ReadOptionalAsync(string label, Func<Task<AppHttpResponse>> send, CancellationToken ct)
    {
        try
        {
            var response = await send();
            if (response.IsSuccess && response.Json is { } data) return data;
            AppLog.Warn("cursor", $"{label} request did not return usable data (HTTP {response.StatusCode})");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("cursor", $"{label} fetch failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Spend history is best-effort and never affects the live Cursor usage mapping.</summary>
    private async Task AppendSpendLinesAsync(List<MetricLine> lines, string accessToken, CancellationToken ct)
    {
        if (CursorSession.From(accessToken) is not { } session)
        {
            AppLog.Warn("cursor", "usage events could not be prepared from the current session");
            return;
        }
        try
        {
            var now = DateTimeOffset.Now;
            var fetcher = new CursorSpendHistoryFetcher(_http, _historyStore, _spendHistoryBudget);
            if (await fetcher.RefreshAsync(session, now, ct) is not { } days) return;
            CursorSpendMapper.AppendSpendLines(CursorSpendMapper.Rows(days, _pricing()), now, lines);
        }
        catch (Exception ex)
        {
            AppLog.Warn("cursor", $"usage history failed: {ex.Message}");
        }
    }

    private static bool NeedsRefresh(string accessToken)
    {
        var exp = Jwt.Expiration(accessToken);
        return exp is null || exp.Value - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);
    }

    private async Task<string> RefreshTokenAsync(CursorAuthState state, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            client_id = CursorAuthStore.ClientId,
            refresh_token = state.RefreshToken
        });
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST", RefreshUrl,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes(payload), 15), ct);
        if (!response.IsSuccess)
            throw new InvalidOperationException("Token expired. Sign in via Cursor app or run `agent login`.");
        var access = JsonUtil.String(response.Json?["access_token"]);
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Token expired. Sign in via Cursor app or run `agent login`.");
        try { _auth.SaveAccessToken(access, state); }
        catch (Exception ex) { AppLog.Warn("cursor", $"could not persist rotated token: {ex.Message}"); }
        state.AccessToken = access;
        return access;
    }

    private Task<AppHttpResponse> ConnectPost(Uri url, string accessToken, CancellationToken ct) =>
        _http.SendAsync(new AppHttpRequest(
            "POST", url,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["Content-Type"] = "application/json",
                ["Connect-Protocol-Version"] = "1"
            },
            Encoding.UTF8.GetBytes("{}"),
            10), ct);
}
