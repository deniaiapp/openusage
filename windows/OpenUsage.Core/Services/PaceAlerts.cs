using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public enum PaceAlertKind { BurningFast, UseItOrLoseIt }

public sealed record PaceAlertMetric(string Id, string Label, WidgetData? Data);
public sealed record PaceAlert(PaceAlertKind Kind, string Label);

public sealed class PaceAlerts
{
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public void Clear() => _windows.Clear();

    public IReadOnlyList<PaceAlert> Observe(IEnumerable<PaceAlertMetric> metrics, DateTimeOffset now)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var alerts = new List<PaceAlert>();
        foreach (var metric in metrics)
        {
            var data = metric.Data;
            visible.Add(metric.Id);
            if (data is not { HasData: true, Limit: > 0 }) continue;
            if (data.ResetsAt is not { } reset
                || reset <= now || data.PeriodDurationMs is not > 0) continue;

            var limit = data.Limit.Value;
            var period = TimeSpan.FromMilliseconds(data.PeriodDurationMs.Value);
            var pace = Pace.Evaluate(data.Used, limit, reset, period, now);
            var burning = pace?.Status == Pace.Status.Behind && data.Used < limit;
            var unused = data.Kind != MetricKind.Dollars && data.PeriodDurationMs > MetricPeriod.DayMs
                && reset - now <= TimeSpan.FromDays(1)
                && Math.Round((limit - data.Used) / limit * 100, MidpointRounding.AwayFromZero) > 50;
            if (!_windows.TryGetValue(metric.Id, out var previous))
            {
                _windows[metric.Id] = new(reset, burning, unused);
                continue;
            }

            var newWindow = previous.Reset <= now && reset - previous.Reset > TimeSpan.FromSeconds(1);
            var burningNotified = !newWindow && previous.BurningNotified
                && pace?.Status != Pace.Status.Ahead && data.Used > 0;
            var unusedNotified = !newWindow && previous.UnusedNotified;
            if (burning && !burningNotified) alerts.Add(new(PaceAlertKind.BurningFast, metric.Label));
            if (unused && !unusedNotified) alerts.Add(new(PaceAlertKind.UseItOrLoseIt, metric.Label));
            _windows[metric.Id] = new(reset, burning || burningNotified, unused || unusedNotified);
        }
        foreach (var id in _windows.Keys.Where(id => !visible.Contains(id)).ToArray())
            _windows.Remove(id);
        return alerts;
    }

    private readonly record struct Window(DateTimeOffset Reset, bool BurningNotified, bool UnusedNotified);
}
