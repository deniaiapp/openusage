using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Amp;

public sealed class AmpProvider : IDeviceSignInProvider
{
    private const string StoreKey = "amp.oauth";
    private static readonly Uri UsageUrl = new("https://ampcode.com/api/internal?userDisplayBalanceInfo");
    private static readonly DeviceEndpoints Endpoints = new("Amp", "client_01JNSEYM3V0J5AXK4YXNXRXTGP",
        new("https://authapi.ampcode.com/user_management/authorize/device"),
        new("https://authapi.ampcode.com/user_management/authenticate"),
        uri => uri.Host == "ampcode.com",
        code => new($"https://ampcode.com/auth/cli/device?user_code={Uri.EscapeDataString(code)}"));
    private readonly AppHttp _http;
    private readonly DeviceAuthorization _authorization;
    private readonly Func<string?> _readCredential;
    private readonly Action<string> _saveCredential;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _credentialGate = new(1);

    public AmpProvider(AppHttp http, ApiKeyStore keys) :
        this(http, () => keys.Get(StoreKey), credential => keys.Set(StoreKey, credential)) { }

    internal AmpProvider(AppHttp http, Func<string?> readCredential, Action<string> saveCredential,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _authorization = new(http, delay);
        _readCredential = readCredential;
        _saveCredential = saveCredential;
        _clock = clock ?? TimeProvider.System;
    }

    public Provider Provider { get; } = new("amp", "Amp", "amp",
        [new("Usage Dashboard", "https://ampcode.com/settings")]);
    public string SignInAuthority => Endpoints.Authority;
    public bool HasAppCredential => ReadCredential() is not null;
    public IReadOnlyList<WidgetDescriptor> WidgetDescriptors =>
    [
        WidgetDescriptor.BoundedDollars("amp.agent", Provider, "Agent Usage", 20),
        WidgetDescriptor.BoundedCount("amp.orbHours", Provider, "Orb Hours", 750, "hours"),
        WidgetDescriptor.Percent("amp.free", Provider, "Amp Free"),
        WidgetDescriptor.DollarBalance("amp.credits", Provider, "Individual Credits", "left")
    ];

    public Task<bool> HasLocalCredentialsAsync(CancellationToken ct) => Task.FromResult(HasAppCredential);

    public async Task<ProviderSnapshot> ConnectAsync(Action<DevicePrompt> showCode, CancellationToken ct)
    {
        var grant = await _authorization.SignInAsync(Endpoints, showCode, ct);
        if (grant.RefreshToken is not { } refreshToken)
            throw new ProviderRequestException("Amp did not return a refresh token. Sign in again.");
        await _credentialGate.WaitAsync(ct);
        try
        {
            var usage = Map(await FetchAsync(grant.AccessToken, ct));
            ct.ThrowIfCancellationRequested();
            SaveCredential(new(grant.AccessToken, refreshToken));
            return usage;
        }
        finally { _credentialGate.Release(); }
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
    {
        await _credentialGate.WaitAsync(ct);
        try
        {
            var credential = ReadCredential() ?? throw SignInRequired();
            var renewed = ExpiresNear(credential.AccessToken);
            if (renewed) credential = await RenewAsync(credential.RefreshToken, ct);
            var response = await FetchAsync(credential.AccessToken, ct);
            if (!renewed && AuthRetry.IsAuthFailure(response))
            {
                credential = await RenewAsync(credential.RefreshToken, ct);
                response = await FetchAsync(credential.AccessToken, ct);
            }
            return Map(response);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (ProviderRequestException ex) { return ProviderSnapshot.Error(Provider, ex.Message); }
        finally { _credentialGate.Release(); }
    }

    private bool ExpiresNear(string token)
    {
        var expiry = JsonUtil.Number(Jwt.Payload(token)?["exp"]);
        return expiry is null || expiry.Value <= _clock.GetUtcNow().ToUnixTimeSeconds() + 30;
    }

    private async Task<Credential> RenewAsync(string refreshToken, CancellationToken ct)
    {
        var response = await _http.SendAsync(new("POST", Endpoints.Token, JsonHeaders, JsonSerializer.SerializeToUtf8Bytes(
            new { client_id = Endpoints.ClientId, grant_type = "refresh_token", refresh_token = refreshToken })), ct);
        var json = response.Json;
        if (AuthRetry.IsAuthFailure(response) || JsonUtil.String(json?["error"]) == "invalid_grant")
            throw SignInRequired();
        if (!response.IsSuccess) throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
        if (JsonUtil.String(json?["access_token"]) is not { } access || JsonUtil.String(json?["refresh_token"]) is not { } refresh)
            throw new ProviderRequestException("Amp returned an incomplete sign-in response. Sign in again.");
        var credential = new Credential(access, refresh);
        SaveCredential(credential);
        return credential;
    }

    private async Task<AppHttpResponse> FetchAsync(string token, CancellationToken ct)
    {
        try
        {
            var headers = new Dictionary<string, string>(JsonHeaders) { ["Authorization"] = $"Bearer {token}" };
            return await _http.SendAsync(new("POST", UsageUrl, headers,
                """{"method":"userDisplayBalanceInfo","params":{}}"""u8.ToArray()), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private ProviderSnapshot Map(AppHttpResponse response)
    {
        AuthRetry.RequireSuccess(response, SignInRequired(), code => new ProviderRequestException(ProviderErrors.RequestFailed(code)));
        var usage = AmpUsageMapper.Map(response);
        return ProviderSnapshot.Make(Provider, usage.Plan, usage.Lines, _clock.GetUtcNow());
    }

    private Credential? ReadCredential()
    {
        if (_readCredential() is not { } serialized) return null;
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(serialized);
            return string.IsNullOrWhiteSpace(credential?.AccessToken) || string.IsNullOrWhiteSpace(credential.RefreshToken)
                ? null : credential;
        }
        catch (JsonException ex)
        {
            AppLog.Error("amp", "Could not read the saved Amp sign-in.", ex);
            return null;
        }
    }

    private void SaveCredential(Credential credential) => _saveCredential(JsonSerializer.Serialize(credential));
    private static ProviderRequestException SignInRequired() => new("Sign in to Amp in Customize.");
    private static readonly Dictionary<string, string> JsonHeaders = new()
    {
        ["Accept"] = "application/json",
        ["Content-Type"] = "application/json"
    };
    private sealed record Credential(string AccessToken, string RefreshToken);
}
