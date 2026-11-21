using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.OpenRouter;

internal sealed class OpenRouterActivity(
    Func<Uri, string, CancellationToken, Task<AppHttpResponse>> get,
    TimeProvider clock)
{
    internal static readonly Uri Url = new("https://openrouter.ai/api/v1/activity");
    private readonly object _gate = new();
    private readonly Dictionary<string, ActivityRead> _reads = new(StringComparer.Ordinal);

    private sealed record ActivityRead(DateOnly Today, DateTimeOffset ReadAt, MetricLine[] Lines)
    {
        public bool IsFresh(DateOnly today, DateTimeOffset now) =>
            Today == today && now - ReadAt < TimeSpan.FromHours(1);
    }

    public async Task<MetricLine[]> ReadAsync(string key, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_gate)
        {
            foreach (var expired in _reads.Where(pair => !pair.Value.IsFresh(today, now)).Select(pair => pair.Key).ToArray())
                _reads.Remove(expired);
            if (_reads.TryGetValue(id, out var cached)) return cached.Lines;
        }

        try
        {
            var response = await get(Url, key, ct);
            if (!response.IsSuccess)
            {
                AppLog.Warn("openrouter", $"activity endpoint HTTP {response.StatusCode}");
                return [];
            }
            if (Parse(response.Json, today) is not { } lines)
            {
                AppLog.Warn("openrouter", "activity response was invalid");
                return [];
            }
            lock (_gate)
                _reads[id] = new ActivityRead(today, now, lines);
            return lines;
        }
        catch (ProviderRequestException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("openrouter", "activity request failed");
            return [];
        }
    }

    internal static MetricLine[]? Parse(JsonObject? response, DateOnly today)
    {
        if (JsonUtil.Array(response?["data"]) is not { } rows) return null;
        var yesterday = today.AddDays(-1);
        var firstDay = today.AddDays(-30);
        double daily = 0, monthly = 0;
        foreach (var item in rows)
        {
            if (item is not JsonObject row
                || JsonUtil.String(row["date"]) is not { } date
                || !TryReadDay(date, out var day)
                || JsonUtil.Number(row["usage"]) is not double amount
                || amount < 0)
                return null;
            if (day < firstDay || day > yesterday) continue;
            if (day == yesterday) daily += amount;
            monthly += amount;
            if (!double.IsFinite(monthly)) return null;
        }
        return [OpenRouterUsageMapper.SpendLine("Yesterday", daily),
            OpenRouterUsageMapper.SpendLine("Prior 30 Days", monthly)];
    }

    private static bool TryReadDay(string value, out DateOnly day)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return true;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dateTime)) return false;
        day = DateOnly.FromDateTime(dateTime);
        return true;
    }
}
