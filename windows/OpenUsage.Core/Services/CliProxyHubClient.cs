using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class CliProxyHubAddress
{
    private static readonly string[] PanelSuffixes = ["/management.html", "/v0/management", "/v8/management", "/v1"];

    private CliProxyHubAddress(Uri baseUrl) => BaseUrl = baseUrl;

    public Uri BaseUrl { get; }
    public Uri ListingUrl => new(BaseUrl, "v0/management/auth-files");

    public static CliProxyHubAddress? Parse(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || text.Any(char.IsControl) || text.Contains('\\')) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic || url.UserInfo.Length > 0 ||
            url.Port <= 0)
        {
            return null;
        }

        var path = url.AbsolutePath.TrimEnd('/');
        while (PanelSuffixes.FirstOrDefault(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) is { } suffix)
            path = path[..^suffix.Length].TrimEnd('/');
        return new CliProxyHubAddress(new UriBuilder(url) { Path = path + "/", Query = "", Fragment = "" }.Uri);
    }

    public bool AllowsManagementKey()
    {
        if (BaseUrl.Scheme == Uri.UriSchemeHttps) return true;
        var host = BaseUrl.IdnHost.TrimEnd('.');
        if (BaseUrl.HostNameType == UriHostNameType.Dns)
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        return (bytes[0], bytes[1]) is (10 or 127, _) or (192, 168) or (169, 254) or (172, >= 16 and <= 31) or (100, >= 64 and <= 127);
    }
}

internal sealed record CliProxyHubLogin(
    string Id,
    string? AuthIndex,
    string Provider,
    string Label,
    bool IsOAuth,
    bool IsDisabled,
    string? ChatGptAccountId)
{
    public string? ProviderId => IsOAuth ? Provider switch
    {
        "claude" or "codex" or "antigravity" or "devin" => Provider,
        "xai" => "grok",
        _ => null
    } : null;

    public bool IsUnsupported => !IsDisabled && ProviderId is null && Provider is not ("" or "unknown");
}

internal sealed class CliProxyHubClient : IDisposable
{
    private readonly AppHttp _http;

    public CliProxyHubClient() : this(new AppHttp(AppHttp.CreateDefaultClient(allowRedirects: false))) { }

    internal CliProxyHubClient(AppHttp http) => _http = http;

    public async Task<IReadOnlyList<CliProxyHubLogin>> ListAsync(CliProxyHubAddress address, string key, CancellationToken ct)
    {
        if (!address.AllowsManagementKey())
            throw new ProviderRequestException("Use HTTPS for a CLIProxyAPI hub outside your local network or Tailscale.");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ProviderRequestException("Enter a valid CLIProxyAPI management key.");

        AppHttpResponse response;
        try
        {
            response = await _http.SendAsync(new AppHttpRequest("GET", address.ListingUrl,
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {key.Trim()}", ["Accept"] = "application/json" }), ct);
        }
        catch (ProviderRequestException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        if (!response.IsSuccess)
        {
            throw new ProviderRequestException(response.StatusCode switch
            {
                401 => "The CLIProxyAPI hub rejected the management key.",
                403 => "The CLIProxyAPI hub refused management access. Check its access settings and key.",
                404 => "The CLIProxyAPI hub's management API was not found. Check the address and management settings.",
                >= 300 and < 400 => "The CLIProxyAPI hub redirected the request. Enter the final management address.",
                _ => ProviderErrors.RequestFailed(response.StatusCode)
            });
        }

        List<LoginEntry?>? files;
        try { files = JsonSerializer.Deserialize<Listing>(response.Body)?.Files; }
        catch (JsonException) { throw InvalidListing(); }
        if (files is null) throw InvalidListing();

        var logins = new List<CliProxyHubLogin>();
        foreach (var entry in files)
        {
            if (entry is null) throw InvalidListing();
            if (entry.Id is not { Length: > 0 } id) continue;
            var provider = (entry.Provider ?? entry.Type ?? "").ToLowerInvariant();
            var label = new[] { entry.Email, entry.Account, entry.Label, entry.Name }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? id;
            logins.Add(new CliProxyHubLogin(id, entry.AuthIndex is { Length: > 0 } ? entry.AuthIndex : null, provider, label,
                entry.AccountType is null || entry.AccountType.Equals("oauth", StringComparison.OrdinalIgnoreCase),
                entry.Disabled == true, entry.IdToken?.ChatGptAccountId));
        }

        return logins;
    }

    private static ProviderRequestException InvalidListing() =>
        new("The CLIProxyAPI hub sent a login list OpenUsage can't read.");

    private sealed record Listing([property: JsonPropertyName("files")] List<LoginEntry?>? Files);

    private sealed record LoginEntry(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("auth_index")] string? AuthIndex,
        [property: JsonPropertyName("provider")] string? Provider,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("account")] string? Account,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("account_type")] string? AccountType,
        [property: JsonPropertyName("disabled")] bool? Disabled,
        [property: JsonPropertyName("id_token")] IdTokenClaims? IdToken);

    private sealed record IdTokenClaims(
        [property: JsonPropertyName("chatgpt_account_id")] string? ChatGptAccountId);

    public void Dispose() => _http.Dispose();
}
