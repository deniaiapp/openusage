using System.Globalization;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Ollama;

/// <summary>
/// What an account response said about the plan. "No badge" has two causes: an account with no plan
/// (ordinary) and a response OpenUsage couldn't read (worth a warning, since the badge just disappears).
/// </summary>
public sealed record OllamaPlan(string? Name, bool IsUnreadable = false)
{
    public static readonly OllamaPlan Absent = new((string?)null);
    public static readonly OllamaPlan Unreadable = new(null, true);
    public static OllamaPlan Named(string name) => new(name);
}

/// <summary>
/// Maps ollama.com <c>GET /api/balance</c> plus the plan name from <c>POST /api/me</c>.
/// Current plans carry a monthly dollar allowance (<c>included.balance_usd</c> left of
/// <c>included.allowance_usd</c> over <c>included.period</c>); legacy plans keep session and weekly limits
/// as <c>remaining_percent</c> with <c>resets_at</c>. Both may carry <c>purchased.balance_usd</c>.
/// See https://docs.ollama.com/api/balance.
/// </summary>
public static class OllamaUsageMapper
{
    public const long SessionPeriodMs = 5L * 60 * 60 * 1000;
    public const long WeeklyPeriodMs = 7L * 24 * 60 * 60 * 1000;

    /// <summary>
    /// <paramref name="accountBody"/> is null when the account request itself failed, which the provider
    /// already reports — classifying it as unreadable here would warn about the same thing twice.
    /// </summary>
    public static (OllamaPlan Plan, List<MetricLine> Lines) Map(JsonObject? balance, string? accountBody)
    {
        var plan = accountBody is null ? OllamaPlan.Absent : Plan(accountBody);
        return (plan, BalanceLines(balance));
    }

    /// <summary>Session and Weekly (legacy plans) or Monthly (current plans), then Purchased Credits.</summary>
    public static List<MetricLine> BalanceLines(JsonObject? root)
    {
        // Free accounts can have an empty included allowance and only report purchased credits.
        var included = JsonUtil.Object(root?["included"]) ?? throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var lines = new List<MetricLine>();
        if (LegacyLine(included["session"], "Session", SessionPeriodMs) is { } session) lines.Add(session);
        if (LegacyLine(included["weekly"], "Weekly", WeeklyPeriodMs) is { } weekly) lines.Add(weekly);
        if (MonthlyLine(included) is { } monthly) lines.Add(monthly);
        if (PurchasedLine(root!["purchased"]) is { } purchased) lines.Add(purchased);
        if (lines.Count == 0) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        return lines;
    }

    /// <summary>
    /// The account's plan, title-cased for the badge. ollama.com capitalizes its keys (<c>Plan</c>); the
    /// local Ollama proxy lowercases them, so both are accepted. A body that isn't an account, or whose
    /// plan field is missing or not a string, is unreadable; an explicitly empty plan is just absent.
    /// </summary>
    public static OllamaPlan Plan(string body)
    {
        var root = JsonUtil.ParseObject(body);
        if (root is null) return OllamaPlan.Unreadable;
        var raw = root["Plan"] ?? root["plan"];
        if (raw is not JsonValue value || !value.TryGetValue<string>(out var text)) return OllamaPlan.Unreadable;
        var name = text.Trim();
        if (name.Length == 0) return OllamaPlan.Absent;
        return OllamaPlan.Named(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()));
    }

    /// <summary>A legacy limit as a used meter. Ollama reports what remains, so used = 100 - remaining.</summary>
    private static MetricLine? LegacyLine(JsonNode? entry, string label, long periodMs)
    {
        var obj = JsonUtil.Object(entry);
        if (obj is null || JsonUtil.Number(obj["remaining_percent"]) is not { } remaining) return null;
        // The period only pairs with a real reset; alone it would render a static "Resets in 5h".
        var resetsAt = Iso8601.Parse(JsonUtil.String(obj["resets_at"]));
        return MetricLine.Progress(label, JsonUtil.ClampPercent(100 - remaining), 100, ProgressFormat.Percent,
            resetsAt, resetsAt is null ? null : periodMs);
    }

    /// <summary>The monthly allowance as a dollar meter: used = allowance - balance, resetting at period.until.</summary>
    private static MetricLine? MonthlyLine(JsonObject included)
    {
        if (JsonUtil.Number(included["allowance_usd"]) is not { } allowance || allowance <= 0) return null;
        if (JsonUtil.Number(included["balance_usd"]) is not { } balance) return null;
        var period = JsonUtil.Object(included["period"]);
        var from = Iso8601.Parse(JsonUtil.String(period?["from"]));
        var until = Iso8601.Parse(JsonUtil.String(period?["until"]));
        long? periodMs = from is { } f && until is { } u && u > f ? (long)(u - f).TotalMilliseconds : null;
        return MetricLine.Progress("Monthly", Math.Min(allowance, Math.Max(0, allowance - balance)), allowance,
            ProgressFormat.Dollars, until, periodMs);
    }

    /// <summary>Unexpired purchased credits as an unbounded dollar row. A real zero is shown.</summary>
    private static MetricLine? PurchasedLine(JsonNode? purchased)
    {
        var obj = JsonUtil.Object(purchased);
        if (obj is null || JsonUtil.Number(obj["balance_usd"]) is not { } balance) return null;
        return MetricLine.Values("Purchased Credits", [new MetricValue(Math.Max(0, balance), MetricKind.Dollars)]);
    }
}

public sealed class OllamaProvider(AppHttp http) : IProviderRuntime
{
    internal const string Id = "ollama";
    public const string Host = "https://ollama.com";
    public const string BalancePath = "/api/balance";
    public const string AccountPath = "/api/me";

    /// <summary>
    /// One notice for every way the plan can go missing, so a failed request and an unreadable response
    /// read the same — the badge is gone for a reason, and the meters are still current.
    /// </summary>
    public const string PlanWarning = "Couldn't read your Ollama plan. Usage below is still up to date.";

    public Provider Provider { get; } = new(
        Id, "Ollama", "ollama",
        [
            new ProviderLink("Usage", "https://ollama.com/settings"),
            new ProviderLink("API Keys", "https://ollama.com/settings/keys")
        ]);
    public bool IsAccountTemplate => true;

    // Session and Weekly exist only on legacy plans; current plans get monthly credits instead.
    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("ollama.session", Provider, "Session"),
        WidgetDescriptor.Percent("ollama.weekly", Provider, "Weekly"),
        WidgetDescriptor.BoundedDollars("ollama.monthly", Provider, "Monthly", 60, limitNoun: "included"),
        WidgetDescriptor.DollarBalance("ollama.purchasedCredits", Provider, "Purchased Credits", valueWord: "left")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderSnapshot.Error(Provider, "Add an Ollama Cloud API key in Customize to see usage."));

    internal async Task<ProviderSnapshot> RefreshWithApiKeyAsync(string key, CancellationToken ct)
    {
        AppHttpResponse balance;
        try { balance = await http.SendAsync(ApiKeyRequest("GET", BalancePath, key), ct); }
        catch (Exception ex)
        {
            AppLog.Warn("ollama", $"balance request failed: {ex.Message}");
            return ProviderSnapshot.Error(Provider, ProviderErrors.ConnectionFailed);
        }
        if (balance.StatusCode is 401 or 403)
            return ProviderSnapshot.Error(Provider, "Ollama rejected this API key. Replace it in Customize.");
        if (!balance.IsSuccess)
            return ProviderSnapshot.Error(Provider, ProviderErrors.RequestFailed(balance.StatusCode));

        // The account call is best-effort (plan badge only): it must not blank out the meters, but a
        // failure is logged and explained with an amber notice instead of silently dropping the badge.
        string? accountBody = null;
        string? warning = null;
        try
        {
            var account = await http.SendAsync(ApiKeyRequest("POST", AccountPath, key), ct);
            if (account.IsSuccess)
            {
                accountBody = account.Text;
            }
            else
            {
                AppLog.Warn("ollama", $"plan lookup failed (HTTP {account.StatusCode}); meters unaffected");
                warning = PlanWarning;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ollama", $"plan lookup failed ({ex.Message}); meters unaffected");
            warning = PlanWarning;
        }

        try
        {
            var mapped = OllamaUsageMapper.Map(balance.Json, accountBody);
            if (warning is null && mapped.Plan.IsUnreadable)
            {
                AppLog.Warn("ollama", "plan response unreadable; meters unaffected");
                warning = PlanWarning;
            }
            return ProviderSnapshot.Make(Provider, mapped.Plan.Name, mapped.Lines, DateTimeOffset.Now, warning);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    internal static AppHttpRequest ApiKeyRequest(string method, string path, string key) =>
        new(method, new Uri(Host + path), new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {key}",
            ["Accept"] = "application/json"
        }, TimeoutSeconds: 15);

}
