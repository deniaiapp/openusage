using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Muse;

public sealed class MuseProvider : IDeviceSignInProvider
{
    private const string StoreKey = "muse.oauth";
    private static readonly Uri UsageUrl = new("https://api.meta.ai/muse-code/key");
    private static readonly DeviceEndpoints Endpoints = new("Meta", "1031625952748946",
        new("https://auth.meta.com/oidc/device/authorization/"), new("https://auth.meta.com/oidc/device/token/"),
        uri => uri.Host == "meta.com" || uri.Host.EndsWith(".meta.com", StringComparison.OrdinalIgnoreCase));
    private readonly AppHttp _http;
    private readonly DeviceAuthorization _authorization;
    private readonly Func<string?> _readToken;
    private readonly Action<string> _saveToken;
    private Func<ProviderSnapshot?>? _lastSnapshot;

    public MuseProvider(AppHttp http, ApiKeyStore keys) :
        this(http, () => keys.Get(StoreKey), token => keys.Set(StoreKey, token)) { }

    internal MuseProvider(AppHttp http, Func<string?> readToken, Action<string> saveToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _authorization = new(http, delay);
        _readToken = readToken;
        _saveToken = saveToken;
    }

    public Provider Provider { get; } = new("muse", "Muse Code", "muse");
    public string SignInAuthority => Endpoints.Authority;
    public bool HasAppCredential => _readToken() is not null;
    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.Percent("muse.session", Provider, "Session"),
        WidgetDescriptor.Percent("muse.weekly", Provider, "Weekly")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(HasAppCredential);

    internal void AttachSnapshots(Func<ProviderSnapshot?> current) => _lastSnapshot = current;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_readToken() is not { } token) return ProviderSnapshot.Error(Provider, "Sign in to Muse Code in Customize.");
        try { return await ReadUsageAsync(token, _lastSnapshot?.Invoke(), cancellationToken); }
        catch (ProviderRequestException ex) { return ProviderSnapshot.Error(Provider, ex.Message); }
    }

    public async Task<ProviderSnapshot> ConnectAsync(Action<DevicePrompt> showCode, CancellationToken cancellationToken)
    {
        var grant = await _authorization.SignInAsync(Endpoints, showCode, cancellationToken);
        var usage = await ReadUsageAsync(grant.AccessToken, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _saveToken(grant.AccessToken);
        return usage;
    }

    private async Task<ProviderSnapshot> ReadUsageAsync(string token, ProviderSnapshot? lastKnown, CancellationToken ct)
    {
        AppHttpResponse response;
        try
        {
            response = await _http.SendAsync(new("POST", UsageUrl, new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}",
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
                ["x-api-version"] = "1.0.0"
            }, """{"onboard":false}"""u8.ToArray()), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        AuthRetry.RequireSuccess(response, new ProviderRequestException("Muse Code sign-in expired. Sign in again in Customize."),
            status => new ProviderRequestException(ProviderErrors.RequestFailed(status)));
        Account account;
        try
        {
            account = JsonSerializer.Deserialize<Account>(response.Body) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ProviderRequestException(ProviderErrors.InvalidResponse);
        }
        if (!account.IsSubscribed) throw new ProviderRequestException("No Muse Code subscription on this account.");

        var now = DateTimeOffset.UtcNow;
        return ProviderSnapshot.Make(Provider, account.Plan, [
            Map("Session", account.Usage?.Session, MetricPeriod.SessionMs),
            Map("Weekly", account.Usage?.Weekly, MetricPeriod.WeekMs)
        ], now);

        MetricLine Map(string label, Window? window, long period)
        {
            if (window is not null)
            {
                var reset = window.Reset is >= -62_135_596_800 and <= 253_402_300_799d and var seconds
                    ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : (DateTimeOffset?)null;
                return MetricLine.Progress(label, window.Used, 100, ProgressFormat.Percent, reset, period);
            }
            if (account.Usage is null && lastKnown?.Line(label) is MetricLine.ProgressLine prior
                && (prior.ResetsAt is null || prior.ResetsAt > now)) return prior;
            return MetricLine.Progress(label, 0, 100, ProgressFormat.Percent, periodDurationMs: period);
        }
    }

    private sealed record Account(
        [property: JsonRequired, JsonPropertyName("is_subs_active")] bool IsSubscribed,
        [property: JsonPropertyName("subs_tier_name")] string? Plan,
        [property: JsonPropertyName("subs_usage")] Usage? Usage);

    private sealed record Usage(
        [property: JsonPropertyName("window")] Window? Session,
        [property: JsonPropertyName("weekly")] Window? Weekly);

    private sealed record Window(
        [property: JsonRequired, JsonPropertyName("used_percent")] double Used,
        [property: JsonPropertyName("resets_at")] double? Reset);
}
