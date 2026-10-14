using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

/// <summary>
/// A local history scan can outlive a quota refresh (#1338). Never start overlapping scans: wait a
/// short budget for the running one, and if it misses, let the next refresh collect its result.
/// The last completed history is retained and shown while a newer scan is still running.
/// </summary>
public sealed class CodexHistoryRefresh
{
    public const string PendingWarning = "Local token history is still updating.";

    private readonly object _gate = new();
    private Task<LogUsageScan?>? _running;
    private LogUsageScan? _retained;
    private bool _hasRetained;

    /// <summary>The scan to show and whether it is still pending (stale or absent).</summary>
    public readonly record struct Result(LogUsageScan? Scan, bool Pending, bool HasHistory);

    public async Task<Result> ValueAsync(TimeSpan wait, Func<LogUsageScan?> scan, CancellationToken cancellationToken)
    {
        Task<LogUsageScan?> running;
        lock (_gate)
        {
            _running ??= Task.Run(scan, CancellationToken.None);
            running = _running;
        }

        if (!running.IsCompleted)
        {
            try { await Task.WhenAny(running, Task.Delay(wait, cancellationToken)); }
            catch (OperationCanceledException) { }
        }

        lock (_gate)
        {
            if (running.IsCompleted)
            {
                if (ReferenceEquals(_running, running)) _running = null;
                if (running.IsCompletedSuccessfully)
                {
                    _retained = running.Result;
                    _hasRetained = true;
                    return new Result(_retained, false, true);
                }
                // A failed scan keeps the last-good history rather than wiping spend.
                AppLog.Error("codex", "usage history scan failed", running.Exception?.GetBaseException());
                return new Result(_retained, false, _hasRetained);
            }
            AppLog.Warn("codex", "local history scan deferred; publishing live quota");
            return new Result(_retained, true, _hasRetained);
        }
    }
}
