using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

internal sealed class CodexTokenHistory
{
    private const int WindowDays = 30;
    private static readonly TimeSpan ReadInterval = TimeSpan.FromHours(1);
    private static readonly Uri CountsUrl =
        new("https://chatgpt.com/backend-api/wham/analytics/daily-workspace-usage-counts");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<DateOnly, double> _days = [];
    private (string User, string? Workspace)? _account;
    private DateOnly? _lastAttemptDay;
    private DateOnly? _lastReadDay;
    private DateTimeOffset _nextRead;
    private DateTimeOffset _throttledUntil;

    public async Task<(double? Today, double? Yesterday, double? Last30)> RefreshAsync(
        string accessToken,
        string? accountId,
        DateTimeOffset now,
        Func<Uri, CancellationToken, Task<AppHttpResponse>> fetch,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var user = Jwt.Subject(accessToken) is { } subject ? $"sub:{subject}" :
                "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
            var account = (user, accountId);
            if (_account != account)
                Reset(account);

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var previous = today.AddDays(-1);
            var oldest = today.AddDays(1 - WindowDays);
            if (now >= _throttledUntil && (now >= _nextRead || _lastAttemptDay != today))
            {
                var from = _lastReadDay is { } last && last >= oldest && last <= today
                    ? (last < previous ? last : previous)
                    : oldest;
                if (await ReadAsync(from, today, now, fetch, cancellationToken).ConfigureAwait(false))
                    _lastReadDay = today;
                _lastAttemptDay = today;
                _nextRead = now + ReadInterval;
                foreach (var day in _days.Keys.Where(day => day < oldest || day > today).ToArray())
                    _days.Remove(day);
            }

            var window = _days.Where(pair => pair.Key >= oldest && pair.Key <= today).Select(pair => pair.Value).ToArray();
            var total = window.Sum();
            return (_days.TryGetValue(today, out var current) ? current : null,
                _days.TryGetValue(previous, out var yesterday) ? yesterday : null,
                window.Length > 0 && double.IsFinite(total) ? total : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ReadAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        Func<Uri, CancellationToken, Task<AppHttpResponse>> fetch,
        CancellationToken ct)
    {
        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = new UriBuilder(CountsUrl)
        {
            Query = $"start_date={start}&end_date={end}&group_by=day&workspace_user=true"
        }.Uri;
        AppHttpResponse response;
        try
        {
            response = await fetch(url, ct).ConfigureAwait(false);
        }
        catch (ProviderRequestException)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Warn("codex", "token history request failed");
            return false;
        }
        ct.ThrowIfCancellationRequested();
        if (ProviderErrors.IsRateLimited(response))
        {
            var retryAfter = RetryConditionHeaderValue.TryParse(response.Header("Retry-After"), out var retry)
                ? retry.Delta ?? retry.Date - now
                : null;
            _throttledUntil = now + (retryAfter is { } delay && delay > ReadInterval ? delay : ReadInterval);
        }
        if (!response.IsSuccess)
        {
            AppLog.Warn("codex", $"token history HTTP {response.StatusCode}");
            return false;
        }
        if (CodexTokenReport.Parse(response.Json) is not { } parsed)
        {
            AppLog.Warn("codex", "token history response was invalid");
            return false;
        }

        foreach (var day in _days.Keys.Where(day => day >= from && day <= to).ToArray())
            _days.Remove(day);
        foreach (var (day, count) in parsed)
            if (day >= from && day <= to)
                _days[day] = count;
        return true;
    }

    private void Reset((string User, string? Workspace) account)
    {
        _account = account;
        _days.Clear();
        _lastAttemptDay = null;
        _lastReadDay = null;
        _nextRead = default;
        _throttledUntil = default;
    }
}
