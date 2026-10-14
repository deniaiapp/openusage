using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Pricing;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed record CursorSession(string UserId, string SessionToken)
{
    /// <summary>Cursor's web session cookie value is <c>userId::accessToken</c> (URL-encoded colons).</summary>
    public static CursorSession? From(string accessToken)
    {
        var subject = Jwt.Subject(accessToken);
        if (subject is null) return null;
        var parts = subject.Split('|');
        var userId = parts.Length > 1 ? parts[1] : parts[0];
        if (string.IsNullOrEmpty(userId)) return null;
        return new CursorSession(userId, $"{userId}%3A%3A{accessToken}");
    }
}

/// <summary>
/// Fetches the last 30 local days of Cursor usage events one day at a time (paged), a few days in
/// parallel, under an overall deadline. Finished days are cached per account, so later refreshes only
/// refetch today, yesterday, and days cached before they ended. Returns null unless every day in the
/// window is known — partial history would understate Last 30 Days.
/// </summary>
public sealed class CursorSpendHistoryFetcher
{
    public static readonly Uri UsageEventsUrl = new("https://cursor.com/api/dashboard/get-filtered-usage-events");
    public const int WindowDays = 30;

    private readonly AppHttp _http;
    private readonly CursorUsageHistoryStore _store;
    private readonly TimeSpan _budget;
    private readonly int _maxConcurrentDays;
    private readonly int _pageSize;

    public CursorSpendHistoryFetcher(
        AppHttp http, CursorUsageHistoryStore store, TimeSpan budget, int maxConcurrentDays = 4, int pageSize = 1000)
    {
        _http = http;
        _store = store;
        _budget = budget;
        _maxConcurrentDays = Math.Max(1, maxConcurrentDays);
        _pageSize = pageSize;
    }

    private sealed record WindowDay(string Key, DateTimeOffset Start, DateTimeOffset End, bool IsComplete);

    public async Task<IReadOnlyList<(DateTimeOffset DayStart, CursorUsageDay Day)>?> RefreshAsync(
        CursorSession session, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var window = Window(now);
        var timeZone = TimeZoneInfo.Local.Id;
        var windowKeys = window.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var cached = _store.Load(session.UserId, timeZone)
            .Where(kv => windowKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var daysToFetch = window
            .Where((day, index) => index < 2 || !cached.TryGetValue(day.Key, out var known) || !known.IsComplete)
            .ToList();

        var gate = new object();
        var closed = false;
        var successfulDays = 0;
        var failures = 0;
        string? firstFailure = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var slots = new SemaphoreSlim(_maxConcurrentDays);

        var tasks = daysToFetch.Select(async day =>
        {
            try
            {
                await slots.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                var (result, failure) = await FetchDayAsync(session, day, deadline.Token);
                lock (gate)
                {
                    if (closed) return;
                    if (result is not null)
                    {
                        cached[day.Key] = result;
                        successfulDays++;
                    }
                    else
                    {
                        failures++;
                        firstFailure ??= failure;
                    }
                }
            }
            finally
            {
                slots.Release();
            }
        }).ToList();

        var all = Task.WhenAll(tasks);
        var deadlineHit = await Task.WhenAny(all, Task.Delay(_budget, deadline.Token)) != all;
        // Stops the deadline timer once every day finished, or abandons in-flight days once it fired.
        deadline.Cancel();

        Dictionary<string, CursorUsageDay> final;
        lock (gate)
        {
            closed = true;
            final = new Dictionary<string, CursorUsageDay>(cached, StringComparer.Ordinal);
        }

        if (failures > 0)
            AppLog.Warn("cursor", $"usage events: {failures} day{(failures == 1 ? "" : "s")} failed ({firstFailure})");
        if (deadlineHit)
        {
            var missing = window.Count(d => !final.ContainsKey(d.Key));
            AppLog.Warn("cursor", $"usage events: deadline hit with {missing} of {window.Count} days still missing");
        }
        if (successfulDays > 0)
            _store.Save(final, session.UserId, timeZone);
        if (!window.All(d => final.ContainsKey(d.Key))) return null;
        return window.Select(d => (d.Start, final[d.Key])).ToList();
    }

    private static List<WindowDay> Window(DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        var days = new List<WindowDay>(WindowDays);
        for (var offset = 0; offset < WindowDays; offset++)
        {
            var start = LocalMidnight(today.AddDays(-offset));
            var next = LocalMidnight(today.AddDays(-offset + 1));
            var end = next < now ? next : now;
            days.Add(new WindowDay(DailyUsageAccumulator.DayKey(start), start, end, end == next));
        }
        return days;
    }

    private static DateTimeOffset LocalMidnight(DateTime date) =>
        new(date, TimeZoneInfo.Local.GetUtcOffset(date));

    private async Task<(CursorUsageDay? Day, string? Failure)> FetchDayAsync(
        CursorSession session, WindowDay window, CancellationToken ct)
    {
        var endMs = window.End.ToUnixTimeMilliseconds() - 1;
        var page = 1;
        int? expectedTotal = null;
        var received = 0;
        var day = new CursorUsageDay { IsComplete = window.IsComplete };
        while (true)
        {
            AppHttpResponse response;
            try
            {
                response = await _http.SendAsync(PageRequest(session, window.Start.ToUnixTimeMilliseconds(), endMs, page), ct);
            }
            catch (Exception)
            {
                return (null, "request failed");
            }
            if (!response.IsSuccess) return (null, $"HTTP {response.StatusCode}");
            CursorUsageEventsPage parsed;
            try { parsed = CursorUsageEvents.ParsePage(response.Body); }
            catch (Exception) { return (null, "invalid response"); }

            if (expectedTotal is { } total && parsed.TotalCount != total) return (null, "incomplete pages");
            expectedTotal = parsed.TotalCount;
            var pageEvents = parsed.Events.Count + parsed.RejectedEventCount;
            received += pageEvents;
            if (parsed.RejectedEventCount > 0)
                AppLog.Warn("cursor", $"usage events: {parsed.RejectedEventCount} rejected event{(parsed.RejectedEventCount == 1 ? "" : "s")} on one page");
            if (parsed.TotalCount == 0)
                return pageEvents == 0 ? (day, null) : (null, "incomplete pages");
            if (pageEvents == 0 && received < parsed.TotalCount) return (null, "incomplete pages");
            foreach (var item in parsed.Events)
            {
                if (DailyUsageAccumulator.DayKey(item.Date) != window.Key) return (null, "invalid response");
                day.Add(item.Tokens, item.Model);
            }
            if (received == parsed.TotalCount) return (day, null);
            if (received > parsed.TotalCount) return (null, "incomplete pages");
            page++;
        }
    }

    private AppHttpRequest PageRequest(CursorSession session, long startMs, long endMs, int page)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["startDate"] = startMs.ToString(CultureInfo.InvariantCulture),
            ["endDate"] = endMs.ToString(CultureInfo.InvariantCulture),
            ["page"] = page,
            ["pageSize"] = _pageSize
        });
        return new AppHttpRequest(
            "POST", UsageEventsUrl,
            new Dictionary<string, string>
            {
                ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}",
                ["Content-Type"] = "application/json",
                ["Origin"] = "https://cursor.com"
            },
            Encoding.UTF8.GetBytes(body),
            15);
    }
}

/// <summary>Turns cached Cursor usage days into the shared Today / Yesterday / Last 30 Days tiles and trend.</summary>
public static class CursorSpendMapper
{
    public const string HistoryNote = "From your Cursor usage history";

    public static List<CursorUsageRow> Rows(
        IEnumerable<(DateTimeOffset DayStart, CursorUsageDay Day)> days, ModelPricing pricing) =>
        days.SelectMany(entry => entry.Day.Models.Select(model => new CursorUsageRow(
                entry.DayStart,
                model.Key,
                model.Value,
                // A day row sums many requests; its size must not add a long-context surcharge.
                pricing.EstimatedCost(model.Key, model.Value, applyLongContext: false))))
            .ToList();

    /// <summary>
    /// Rows no pricing source can price are left out of every total (mixing their tokens with priced
    /// dollars makes the figures incoherent) and only surface as unknown models on their day.
    /// Each day's dollars are summed raw, then rounded to cents once.
    /// </summary>
    public static LogUsageScan BuildScan(IEnumerable<CursorUsageRow> rows)
    {
        var cost = new Dictionary<string, double>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, long>(StringComparer.Ordinal);
        var unknown = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var day = DailyUsageAccumulator.DayKey(row.Date);
            var model = row.Model.Trim();
            if (row.ImputedCostDollars is not { } dollars)
            {
                if (row.Tokens.TotalTokens > 0 && model.Length > 0)
                {
                    if (!unknown.TryGetValue(day, out var set))
                        unknown[day] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(model);
                }
                continue;
            }
            cost[day] = cost.GetValueOrDefault(day) + dollars;
            tokens[day] = tokens.GetValueOrDefault(day) + row.Tokens.TotalTokens;
        }
        var daily = tokens.Keys
            .OrderByDescending(k => k, StringComparer.Ordinal)
            .Select(day => new DailyUsageEntry(
                day,
                tokens[day],
                Math.Round(cost.GetValueOrDefault(day) * 100, MidpointRounding.AwayFromZero) / 100))
            .ToList();
        return new LogUsageScan { Series = new DailyUsageSeries { Daily = daily }, UnknownModelsByDay = unknown };
    }

    public static void AppendSpendLines(IEnumerable<CursorUsageRow> rows, DateTimeOffset now, List<MetricLine> lines) =>
        SpendTileMapper.AppendAll(BuildScan(rows), lines, now, HistoryNote);
}
