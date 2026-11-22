using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Zai;

internal sealed class ZaiResetBalance(AppHttp http, Func<DateTimeOffset>? clock = null)
{
    internal const string Label = "Rate Limit Resets";
    internal static readonly Uri ListUrl = new("https://api.z.ai/api/biz/customer-package-reset/list?targetType=PERSONAL");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private (string Fingerprint, DateTimeOffset CheckedAt, DateTimeOffset[]? Expiries)? _check;

    internal async Task<MetricLine?> ReadAsync(string key, CancellationToken ct)
    {
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (_check is not { } check || check.Fingerprint != fingerprint || now - check.CheckedAt >= CheckInterval)
        {
            DateTimeOffset[]? expiries;
            try
            {
                var response = await ZaiProvider.GetAsync(http, ListUrl, key, ct);
                ct.ThrowIfCancellationRequested();
                if (!response.IsSuccess)
                    throw new ProviderRequestException(ProviderErrors.RequestFailed(response.StatusCode));
                expiries = ParseExpiries(response.Json);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn("zai", $"Reset list unavailable; retrying in an hour: {ex.Message}");
                expiries = null;
            }
            _check = check = (fingerprint, now, expiries);
        }
        if (check.Expiries is null) return null;
        var available = check.Expiries.Where(expiry => expiry > now).ToArray();
        return MetricLine.Values(Label, [new MetricValue(available.Length, MetricKind.Count, "available")],
            expiriesAt: available);
    }

    private static DateTimeOffset[] ParseExpiries(JsonObject? root)
    {
        if (JsonUtil.Bool(root?["success"]) == false || root?["data"] is not JsonObject data)
            throw new InvalidOperationException(ProviderErrors.InvalidResponse);
        var expiries = new List<DateTimeOffset>();
        foreach (var field in new[] { "fiveHourResets", "weekResets" })
        {
            if (!data.TryGetPropertyValue(field, out var records)) continue;
            if (records is not JsonArray items) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
            foreach (var item in items)
            {
                if (item is not JsonObject record) throw new InvalidOperationException(ProviderErrors.InvalidResponse);
                if (JsonUtil.Bool(record["available"]) != true) continue;
                if (!DateTime.TryParseExact(JsonUtil.String(record["expireTime"]), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
                    throw new InvalidOperationException(ProviderErrors.InvalidResponse);
                expiries.Add(new DateTimeOffset(expiry, TimeSpan.FromHours(8)).ToUniversalTime());
            }
        }
        return expiries.Order().ToArray();
    }
}
