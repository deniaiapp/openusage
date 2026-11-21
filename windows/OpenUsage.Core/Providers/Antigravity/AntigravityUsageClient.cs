using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

public sealed class AntigravityUsageClient(AppHttp http)
{
    internal sealed class TokenRefused : Exception { }

    public async Task<(List<MetricLine> Lines, string? Plan)> QuotaAsync(string token, CancellationToken ct = default)
    {
        var buckets = new Dictionary<string, MetricLine>(StringComparer.Ordinal);
        string? plan = null;
        var refused = false;
        var accepted = false;
        foreach (var baseUrl in AntigravityProvider.CloudCodeUrls)
        {
            var response = await SendAsync(baseUrl, "/v1internal:retrieveUserQuotaSummary", token, ct);
            refused |= response is { StatusCode: 401 };
            accepted |= response is { IsSuccess: true };
            if (response is not { IsSuccess: true, Json: { } body }) continue;
            plan ??= AntigravityUsageMapper.PlanName(body);
            foreach (var line in AntigravityUsageMapper.ParseQuotaSummary(body) ?? [])
                buckets.TryAdd(line.Label, line);
            if (buckets.Count == AntigravityUsageMapper.SummaryBuckets.Length) break;
        }
        if (refused && !accepted) throw new TokenRefused();
        return (AntigravityUsageMapper.SummaryBuckets
            .Select(bucket => buckets.GetValueOrDefault(bucket.Label)).OfType<MetricLine>().ToList(), plan);
    }

    public async Task<string?> FallbackPlanAsync(string token, CancellationToken ct)
    {
        foreach (var baseUrl in AntigravityProvider.CloudCodeUrls)
        {
            var response = await SendAsync(baseUrl, "/v1internal:retrieveUserQuota", token, ct);
            if (response is { StatusCode: 401 or 403 }) break;
            if (response is { IsSuccess: true }) return AntigravityUsageMapper.PlanName(response.Json);
        }
        return null;
    }

    private async Task<AppHttpResponse?> SendAsync(string baseUrl, string path, string token, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(new AppHttpRequest(
                "POST", new Uri(baseUrl + path),
                new Dictionary<string, string>
                {
                    ["Accept"] = "application/json",
                    ["Content-Type"] = "application/json",
                    ["Authorization"] = $"Bearer {token}",
                    ["User-Agent"] = "antigravity"
                },
                "{}"u8.ToArray()), ct);
        }
        catch (ProviderRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Warn("antigravity", $"{path} via {baseUrl} failed: {ex.Message}");
            return null;
        }
    }
}
