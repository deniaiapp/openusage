namespace OpenUsage.Core.Support;

public static class Pace
{
    public enum Status { Ahead, OnTrack, Behind }

    public sealed record Result(Status Status, double ProjectedUsage);

    public static TimeSpan MinimumElapsed(TimeSpan periodDuration) =>
        TimeSpan.FromSeconds(Math.Max(60, periodDuration.TotalSeconds * 0.01));

    public static Result? Evaluate(
        double used,
        double limit,
        DateTimeOffset resetsAt,
        TimeSpan periodDuration,
        DateTimeOffset now)
    {
        if (limit <= 0 || periodDuration <= TimeSpan.Zero || used <= 0) return null;
        var elapsed = now - (resetsAt - periodDuration);
        if (elapsed < MinimumElapsed(periodDuration) || now >= resetsAt) return null;

        var projected = used / elapsed.TotalSeconds * periodDuration.TotalSeconds;
        if (used >= limit) return new Result(Status.Behind, projected);

        var status = projected <= limit * 0.9 ? Status.Ahead
            : projected <= limit ? Status.OnTrack
            : Status.Behind;
        return new Result(status, projected);
    }

    public static TimeSpan? SecondsToRunOut(
        double used,
        double limit,
        DateTimeOffset resetsAt,
        TimeSpan periodDuration,
        DateTimeOffset now)
    {
        var result = Evaluate(used, limit, resetsAt, periodDuration, now);
        if (result is null || result.Status != Status.Behind) return null;
        var rate = result.ProjectedUsage / periodDuration.TotalSeconds;
        if (rate <= 0) return null;
        var eta = TimeSpan.FromSeconds((limit - used) / rate);
        var remaining = resetsAt - now;
        if (eta <= TimeSpan.Zero || eta >= remaining) return null;
        return eta;
    }
}
