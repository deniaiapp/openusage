using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers;

public sealed record DailyUsageEntry(string Date, long TotalTokens, double? CostUsd);

public sealed class DailyUsageSeries
{
    public List<DailyUsageEntry> Daily { get; init; } = [];
}

public sealed class LogUsageScan
{
    public DailyUsageSeries Series { get; init; } = new();
    public Dictionary<string, HashSet<string>> UnknownModelsByDay { get; init; } = new(StringComparer.Ordinal);
}

public sealed class DailyUsageAccumulator
{
    private readonly Dictionary<string, long> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _cost = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _unknown = new(StringComparer.Ordinal);

    public static string DayKey(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}";
    }

    public static DateTimeOffset Since(int daysBack, DateTimeOffset now)
    {
        var start = now.ToLocalTime().Date.AddDays(-daysBack);
        return new DateTimeOffset(start, now.ToLocalTime().Offset);
    }

    public void Add(string day, long tokens, double cost)
    {
        _tokens[day] = _tokens.GetValueOrDefault(day) + tokens;
        _cost[day] = _cost.GetValueOrDefault(day) + cost;
    }

    public void AddUnknown(string day, string model)
    {
        if (!_unknown.TryGetValue(day, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _unknown[day] = set;
        }
        set.Add(model);
    }

    public LogUsageScan Build()
    {
        var days = _tokens.Keys.Union(_cost.Keys).Distinct().OrderByDescending(k => k).ToList();
        return new LogUsageScan
        {
            Series = new DailyUsageSeries
            {
                Daily = days.Select(day => new DailyUsageEntry(day, _tokens.GetValueOrDefault(day), _cost.GetValueOrDefault(day))).ToList()
            },
            UnknownModelsByDay = _unknown
        };
    }
}

public static class SpendTileMapper
{
    public static IReadOnlyList<WidgetDescriptor> HistoryWidgets(Provider provider, string? spendNote = null) =>
        new[] { WidgetDescriptor.UsageTrend(provider) }
            .Concat(WidgetDescriptor.SpendTiles(provider, spendNote))
            .ToList();

    public static async Task AppendScanAsync(
        List<MetricLine> lines,
        Func<LogUsageScan?> scan,
        string trendNote,
        string tag,
        CancellationToken cancellationToken = default)
    {
        LogUsageScan? result = null;
        try
        {
            result = await Task.Run(scan, cancellationToken);
            AppendAll(result, lines, DateTimeOffset.Now, trendNote);
        }
        catch (Exception ex)
        {
            AppLog.Warn(tag, $"usage history scan failed: {ex.Message}");
        }
    }

    public const int PreviousDays = 30;

    public static void AppendAll(
        LogUsageScan? scan,
        List<MetricLine> lines,
        DateTimeOffset now,
        string trendNote,
        bool estimated = true)
    {
        if (scan is null) return;
        AppendTokenUsage(scan, lines, now, estimated);
        AppendUsageTrend(scan.Series, lines, now, trendNote);
    }

    public static void AppendTokenUsage(LogUsageScan scan, List<MetricLine> lines, DateTimeOffset now, bool estimated)
    {
        var today = DailyUsageAccumulator.DayKey(now);
        var yesterday = DailyUsageAccumulator.DayKey(now.AddDays(-1));
        AppendDay("Today", today);
        AppendDay("Yesterday", yesterday);

        var window = UsageWindow(now);
        var inWindow = scan.Series.Daily.Where(d => window.Contains(d.Date)).ToList();
        long totalTokens = 0;
        foreach (var day in inWindow) totalTokens += day.TotalTokens;
        var totalCost = inWindow.Sum(d => d.CostUsd ?? 0);
        if (totalTokens > 0 || totalCost > 0)
        {
            var unknown = scan.UnknownModelsByDay
                .Where(kv => window.Contains(kv.Key))
                .SelectMany(kv => kv.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            lines.Add(MetricLine.Values("Last 30 Days", SpendValues(totalTokens, totalCost, estimated), unknownModels: unknown));
        }

        void AppendDay(string label, string key)
        {
            var entry = scan.Series.Daily.FirstOrDefault(d => d.Date == key);
            if (entry is null || (entry.TotalTokens <= 0 && (entry.CostUsd ?? 0) <= 0)) return;
            var unknown = scan.UnknownModelsByDay.GetValueOrDefault(key)?.OrderBy(x => x).ToList();
            lines.Add(MetricLine.Values(label, SpendValues(entry.TotalTokens, entry.CostUsd, estimated), unknownModels: unknown));
        }
    }

    public static void AppendUsageTrend(DailyUsageSeries usage, List<MetricLine> lines, DateTimeOffset now, string note)
    {
        var tokensByDay = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var day in usage.Daily)
            tokensByDay[day.Date] = tokensByDay.GetValueOrDefault(day.Date) + day.TotalTokens;
        if (!tokensByDay.Values.Any(v => v > 0)) return;

        var points = new List<MetricChartPoint>();
        var today = now.ToLocalTime().Date;
        for (var offset = PreviousDays; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var key = $"{day.Year:D4}-{day.Month:D2}-{day.Day:D2}";
            var tokens = tokensByDay.GetValueOrDefault(key);
            points.Add(new MetricChartPoint(
                tokens,
                Formatters.MonthDayLabel(new DateTimeOffset(day, now.ToLocalTime().Offset)),
                MetricFormatter.Number(tokens, MetricKind.Count, MetricFormatter.Style.Row) + " tokens"));
        }
        if (points.All(p => p.Value <= 0)) return;
        lines.Add(MetricLine.Chart("Usage Trend", points, note));
    }

    private static HashSet<string> UsageWindow(DateTimeOffset now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var today = now.ToLocalTime().Date;
        for (var offset = 0; offset <= PreviousDays; offset++)
        {
            var day = today.AddDays(-offset);
            set.Add($"{day.Year:D4}-{day.Month:D2}-{day.Day:D2}");
        }
        return set;
    }

    private static IReadOnlyList<MetricValue> SpendValues(long tokens, double? costUsd, bool estimated)
    {
        var values = new List<MetricValue>();
        if (costUsd is { } cost)
            values.Add(new MetricValue(cost, MetricKind.Dollars, estimated ? null : null));
        values.Add(new MetricValue(tokens, MetricKind.Count, "tokens"));
        return values;
    }
}

public static class JsonlScan
{
    public static IEnumerable<string> FilesUnder(string directory, string? fileName = null, DateTimeOffset? since = null)
    {
        if (!Directory.Exists(directory)) yield break;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }
        foreach (var path in files)
        {
            if (fileName is not null && !string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (since is { } cutoff)
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff.LocalDateTime.AddDays(-1))
                        continue;
                }
                catch { continue; }
            }
            yield return path;
        }
    }

    public static IEnumerable<string> ReadLines(string path)
    {
        StreamReader reader;
        try { reader = new StreamReader(path); }
        catch { yield break; }
        using (reader)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
                yield return line;
        }
    }
}
