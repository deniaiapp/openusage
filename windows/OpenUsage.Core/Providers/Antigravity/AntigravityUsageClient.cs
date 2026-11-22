using System.Runtime.InteropServices;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Antigravity;

public sealed class AntigravityUsageClient(AppHttp http)
{
    internal sealed class TokenRefused : Exception { }

    private static readonly string[] QuotaOrder =
    [
        AntigravityMetric.SessionLabel, AntigravityMetric.WeeklyLabel, AntigravityMetric.ArgonWeeklyLabel,
        AntigravityMetric.ClaudeLabel, AntigravityMetric.ClaudeWeeklyLabel
    ];

    private static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 => "386",
        Architecture.Arm64 => "arm64",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    };

    public async Task<(List<MetricLine> Lines, string? Plan)> QuotaAsync(string token, CancellationToken ct = default)
    {
        var buckets = new Dictionary<string, MetricLine>(StringComparer.Ordinal);
        string? plan = null;
        var refused = false;
        var accepted = false;
        var rateLimited = false;
        var authRejected = false;
        foreach (var baseUrl in AntigravityProvider.CloudCodeUrls)
        {
            var response = await SendAsync(baseUrl, "/v1internal:retrieveUserQuotaSummary", token, ct);
            accepted |= response is { IsSuccess: true };
            if (response is not { IsSuccess: true, Json: { } body })
            {
                if (response is not null && ProviderErrors.IsRateLimited(response)) rateLimited = true;
                else if (response is { StatusCode: 401 or 403 })
                {
                    authRejected = true;
                    refused |= response.StatusCode == 401;
                }
                continue;
            }
            plan ??= AntigravityUsageMapper.PlanName(body);
            foreach (var line in AntigravityUsageMapper.ParseQuotaSummary(body) ?? [])
                buckets.TryAdd(line.Label, line);
            if (AntigravityUsageMapper.SummaryBuckets.All(bucket => buckets.ContainsKey(bucket.Label))) break;
        }
        if (refused && !accepted) throw new TokenRefused();
        if (buckets.Count == 0 && rateLimited && !authRejected)
            throw new ProviderRequestException(ProviderErrors.RateLimited);
        return (QuotaOrder.Select(label => buckets.GetValueOrDefault(label)).OfType<MetricLine>().ToList(), plan);
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
                    ["User-Agent"] = $"antigravity/hub/2.24.0 (aidev_client; os_type=windows; arch={ArchitectureName})"
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
