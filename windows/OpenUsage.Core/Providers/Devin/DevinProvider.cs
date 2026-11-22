using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Devin;

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
    private const string StoreKey = "devin.session";
    internal static readonly Uri StatusUrl = new(
        "https://server.codeium.com/exa.seat_management_pb.SeatManagementService/GetUserStatus");
    private readonly AppHttp _http;
    private readonly Func<string?> _readToken;
    private readonly Action<string> _saveToken;
    private readonly SemaphoreSlim _credentialGate = new(1);

    public DevinProvider(AppHttp http, ApiKeyStore keys)
        : this(http, () => keys.Get(StoreKey), token => keys.Set(StoreKey, token)) { }

    internal DevinProvider(AppHttp http, Func<string?> readToken, Action<string> saveToken)
    {
        _http = http;
        _readToken = readToken;
        _saveToken = saveToken;
    }

    public Provider Provider { get; } = new(
        "devin", "Devin", "devin",
        [new ProviderLink("App", "https://app.devin.ai/")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("devin.daily", Provider, "Daily", "Daily quota"),
        WidgetDescriptor.Percent("devin.weekly", Provider, "Weekly", "Weekly quota"),
        WidgetDescriptor.DollarBalance("devin.extra", Provider, "Extra Usage", "left", "Extra usage balance")
    ];

    public bool IsSignedIn => _readToken() is not null;

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsSignedIn);

    public async Task<ProviderSnapshot> ConnectAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        var token = await new DevinLogin(_http).SignInAsync(openBrowser, ct);
        await _credentialGate.WaitAsync(ct);
        try
        {
            var snapshot = await ReadUsageAsync(token, ct);
            ct.ThrowIfCancellationRequested();
            _saveToken(token);
            return snapshot;
        }
        finally { _credentialGate.Release(); }
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            return _readToken() is { } token
                ? await ReadUsageAsync(token, cancellationToken)
                : ProviderSnapshot.Error(Provider, "Sign in to Devin from Customize.");
        }
        catch (ProviderRequestException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) { return ProviderSnapshot.Error(Provider, ex); }
        finally { _credentialGate.Release(); }
    }

    private async Task<ProviderSnapshot> ReadUsageAsync(string token, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            metadata = new
            {
                apiKey = token,
                ideName = "devin",
                ideVersion = "1.108.2",
                extensionName = "devin",
                extensionVersion = "1.108.2",
                locale = "en"
            }
        });
        var response = await _http.SendAsync(new AppHttpRequest(
            "POST", StatusUrl,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Connect-Protocol-Version"] = "1"
            }, payload), ct);
        AuthRetry.RequireSuccess(response, new ProviderRequestException("Devin sign-in expired. Sign in again from Customize."),
            status => new ProviderRequestException(ProviderErrors.RequestFailed(status)));
        if (response.Json is not { } body)
            throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        var mapped = DevinUsageMapper.Map(body);
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, DateTimeOffset.Now);
    }
}
