using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed partial class CodexProvider : IProviderRuntime
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly Uri RefreshUrl = new("https://auth.openai.com/oauth/token");
    public static readonly Uri UsageUrl = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri ResetCreditsUrl = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");

    private readonly AppHttp _http;
    private readonly CodexTokenHistory _history = new();

    public CodexProvider(AppHttp http, ApiKeyStore? keys = null)
    {
        _http = http;
        _keys = keys;
    }

    public Provider Provider { get; } = new(
        "codex", "Codex", "codex",
        [
            new ProviderLink("Status", "https://status.openai.com/"),
            new ProviderLink("Dashboard", "https://chatgpt.com/settings/usage")
        ]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("codex.session", Provider, "Session"),
        WidgetDescriptor.Percent("codex.weekly", Provider, "Weekly"),
        WidgetDescriptor.Percent("codex.spark", Provider, "Spark"),
        WidgetDescriptor.Percent("codex.sparkWeekly", Provider, "Spark Weekly"),
        WidgetDescriptor.Values("codex.credits", Provider, "Credits") with { HideWhenMissing = true },
        WidgetDescriptor.BoundedCount("codex.creditLimit", Provider, "Credit Limit", 0, "credits")
            with { HideWhenMissing = true },
        WidgetDescriptor.Values("codex.rateLimitResets", Provider, "Rate Limit Resets",
            valueWord: "available", traySuffix: "resets", showsResetExpiries: true),
        TokenHistoryDescriptor("codex.tokensToday", "Today"),
        TokenHistoryDescriptor("codex.tokensYesterday", "Yesterday"),
        TokenHistoryDescriptor("codex.tokensLast30", "Last 30 Days")
    ];

    private WidgetDescriptor TokenHistoryDescriptor(string id, string title)
    {
        var descriptor = WidgetDescriptor.Values(id, Provider, title, isUsagePeriod: true, traySuffix: "tokens");
        return descriptor with
        {
            HideWhenMissing = true,
            Sample = descriptor.Sample with { Kind = MetricKind.Count, CountSuffix = "tokens" }
        };
    }

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HasAppCredential);

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        HasAppCredential
            ? RefreshAppCredentialAsync(cancellationToken)
            : Task.FromResult(ProviderSnapshot.Error(Provider, "Not logged in. Open Customize → Codex to sign in."));

    internal async Task<ProviderSnapshot> FetchUsageAsync(CodexAuth auth, CancellationToken ct, string? expiredMessage = null)
    {
        var usage = await SendAuthGet(UsageUrl, auth, ct);
        return await ReadAppUsageAsync(usage, auth, ct, expiredMessage);
    }

    private async Task<ProviderSnapshot> MapUsageAsync(AppHttpResponse usage, CodexAuth auth, CancellationToken ct)
    {
        AppHttpResponse? resets = null;
        try { resets = await SendAuthGet(ResetCreditsUrl, auth, ct, extra: true); }
        catch (Exception ex) { AppLog.Warn("codex", $"reset credits fetch failed: {ex.Message}"); }

        var mapped = CodexUsageMapper.Map(usage, resets, DateTimeOffset.Now);
        var lines = mapped.Lines.ToList();
        var history = await _history.RefreshAsync(auth.AccessToken!, auth.AccountId, DateTimeOffset.UtcNow,
            (url, token) => SendAuthGet(url, auth, token, timeoutSeconds: 5), ct);
        AddTokenCount("Today", history.Today);
        AddTokenCount("Yesterday", history.Yesterday);
        AddTokenCount("Last 30 Days", history.Last30);
        ct.ThrowIfCancellationRequested();
        MetricLine.AppendNoDataIfNeeded(lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, lines, DateTimeOffset.Now);

        void AddTokenCount(string label, double? value)
        {
            if (value is { } count)
                lines.Add(MetricLine.Values(label, [new MetricValue(count, MetricKind.Count, "tokens")]));
        }
    }

    private static bool NeedsRefresh(CodexAuth auth)
    {
        if (Jwt.Expiration(auth.AccessToken) is { } exp)
            return exp - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);
        return false;
    }

    private Task<AppHttpResponse> SendAuthGet(
        Uri url, CodexAuth auth, CancellationToken ct, bool extra = false, int timeoutSeconds = 10)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {auth.AccessToken}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "OpenUsage"
        };
        if (!string.IsNullOrEmpty(auth.AccountId))
            headers["ChatGPT-Account-Id"] = auth.AccountId;
        if (extra)
        {
            headers["OpenAI-Beta"] = "codex-1";
            headers["originator"] = "Codex Desktop";
        }
        return _http.SendAsync(new AppHttpRequest("GET", url, headers, TimeoutSeconds: timeoutSeconds), ct);
    }
}
