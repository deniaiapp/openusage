using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Copilot;

public static class CopilotUsageMapper
{
    public static (string? Plan, List<MetricLine> Lines) Map(JsonObject body)
    {
        var plan = PlanLabel(JsonUtil.String(body["copilot_plan"]));
        var resetsAt = Iso8601.Parse(JsonUtil.String(body["quota_reset_date"]))
            ?? ParseDay(JsonUtil.String(body["limited_user_reset_date"]));
        var lines = new List<MetricLine>();
        var snapshots = JsonUtil.Object(body["quota_snapshots"]);
        var premium = snapshots?["premium_interactions"];
        var credits = SnapshotLine("Credits", premium, resetsAt);
        if (credits is not null) lines.Add(credits);
        if (credits is not null && OverageLine(premium) is { } extra) lines.Add(extra);
        if (SnapshotLine("Chat", snapshots?["chat"], resetsAt) is { } chat) lines.Add(chat);
        if (SnapshotLine("Completions", snapshots?["completions"], resetsAt) is { } completions) lines.Add(completions);

        if (lines.Count == 0)
        {
            var limited = JsonUtil.Object(body["limited_user_quotas"]);
            var monthly = JsonUtil.Object(body["monthly_quotas"]);
            if (LimitedLine("Chat", limited?["chat"], monthly?["chat"], resetsAt) is { } legacyChat) lines.Add(legacyChat);
            if (LimitedLine("Completions", limited?["completions"], monthly?["completions"], resetsAt) is { } legacyComp) lines.Add(legacyComp);
        }

        if (lines.Count == 0)
        {
            if (JsonUtil.Bool(body["token_based_billing"]) != true)
                throw new InvalidOperationException("Copilot quota unavailable. Try again later.");
            if (JsonUtil.Object(premium) is { } snapshot &&
                JsonUtil.Number(snapshot["credits_used"]) is { } used && used > 0)
            {
                lines.Add(MetricLine.Values("Credits", [new MetricValue(used, MetricKind.Count)]));
            }
        }
        return (plan, lines);
    }

    private static MetricLine? SnapshotLine(string label, JsonNode? raw, DateTimeOffset? resetsAt)
    {
        if (JsonUtil.Object(raw) is not { } snapshot) return null;
        var entitlement = JsonUtil.Number(snapshot["entitlement"]);
        var remaining = JsonUtil.Number(snapshot["remaining"]);
        if (JsonUtil.Bool(snapshot["unlimited"]) == true || entitlement == -1 || remaining == -1) return null;
        if (entitlement == 0) return null;
        double usedPercent;
        if (JsonUtil.Number(snapshot["percent_remaining"]) is { } percentRemaining)
            usedPercent = JsonUtil.ClampPercent(100 - percentRemaining);
        else if (entitlement is { } e && e > 0 && remaining is { } r)
            usedPercent = JsonUtil.ClampPercent(100 - r / e * 100);
        else
            return null;
        return MetricLine.Progress(label, usedPercent, 100, ProgressFormat.Percent, resetsAt, MetricPeriod.MonthMs);
    }

    private static MetricLine? OverageLine(JsonNode? raw)
    {
        if (JsonUtil.Object(raw) is not { } snapshot) return null;
        if (JsonUtil.Bool(snapshot["overage_permitted"]) != true) return null;
        var overage = Math.Max(0, JsonUtil.Number(snapshot["overage_count"]) ?? 0);
        return MetricLine.Values("Extra Usage", [new MetricValue(overage, MetricKind.Count)]);
    }

    private static MetricLine? LimitedLine(string label, JsonNode? remainingNode, JsonNode? totalNode, DateTimeOffset? resetsAt)
    {
        if (JsonUtil.Number(totalNode) is not { } total || total <= 0) return null;
        if (JsonUtil.Number(remainingNode) is not { } remaining) return null;
        var used = Math.Max(0, total - remaining);
        return MetricLine.Progress(label, JsonUtil.ClampPercent(used / total * 100), 100, ProgressFormat.Percent, resetsAt, MetricPeriod.MonthMs);
    }

    private static string? PlanLabel(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : JsonUtil.TitleCase(raw);

    private static DateTimeOffset? ParseDay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, out var date)) return date;
        return Iso8601.Parse(raw);
    }
}

public sealed class CopilotProvider : IProviderRuntime
{
    public static readonly Uri UsageUrl = new("https://api.github.com/copilot_internal/user");
    public const string VerificationUrl = CopilotDeviceLogin.VerificationUrl;
    private const string CredentialKey = "copilot.oauth";
    private readonly AppHttp _http;
    private readonly ApiKeyStore? _keys;
    private readonly CopilotDeviceLogin _signIn;

    public CopilotProvider(AppHttp http, ApiKeyStore? keys = null) : this(http, keys, new CopilotDeviceLogin(http)) { }

    internal CopilotProvider(AppHttp http, ApiKeyStore? keys, CopilotDeviceLogin signIn)
    {
        _http = http;
        _keys = keys;
        _signIn = signIn;
    }

    public bool HasAppCredential => _keys?.Get(CredentialKey) is not null;

    internal async Task<ProviderSnapshot> ConnectAsync(Action<string> showCode, CancellationToken ct)
    {
        var keys = _keys ?? throw new InvalidOperationException("OpenUsage could not open its credentials store.");
        var token = await _signIn.AuthorizeAsync(showCode, ct);
        ProviderSnapshot snapshot;
        try { snapshot = await FetchUsageAsync(token, ct); }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
        keys.Set(CredentialKey, token);
        return snapshot;
    }

    public Provider Provider { get; } = new(
        "copilot", "Copilot", "copilot",
        [new ProviderLink("Dashboard", "https://github.com/settings/copilot")]);

    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("copilot.premium", Provider, "Credits"),
        WidgetDescriptor.Values("copilot.extra", Provider, "Extra Usage"),
        WidgetDescriptor.Percent("copilot.chat", Provider, "Chat"),
        WidgetDescriptor.Percent("copilot.completions", Provider, "Completions")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HasAppCredential);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var token = _keys?.Get(CredentialKey);
        if (token is null)
            return ProviderSnapshot.Error(Provider, "Sign in to GitHub in Customize → Copilot.");
        try
        {
            return await FetchUsageAsync(token, cancellationToken);
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(Provider, ex);
        }
    }

    private async Task<ProviderSnapshot> FetchUsageAsync(string token, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(new AppHttpRequest(
            "GET", UsageUrl,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"token {token}",
                ["Accept"] = "application/json",
                ["Editor-Version"] = "vscode/1.96.2",
                ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
                ["User-Agent"] = "GitHubCopilotChat/0.26.7",
                ["X-Github-Api-Version"] = "2025-04-01"
            },
            TimeoutSeconds: 15), cancellationToken);
        if (response.StatusCode is 401 or 403)
            throw new ProviderRequestException("GitHub rejected this sign-in. Verify that the account has a Copilot seat and sign in again in Customize.");
        if (!response.IsSuccess)
            throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
        if (response.Json is not { } body)
            throw new ProviderRequestException("Copilot usage response invalid. Try again later.");
        var mapped = CopilotUsageMapper.Map(body);
        if (mapped.Plan is null && mapped.Lines.Count == 0)
            throw new ProviderRequestException("GitHub did not return any Copilot usage information. Check that the account has a Copilot seat.");
        MetricLine.AppendNoDataIfNeeded(mapped.Lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, DateTimeOffset.Now);
    }
}
