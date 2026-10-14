namespace OpenUsage.Core.Support;

public static class AppLog
{
    private static readonly object Gate = new();

    public static void Info(string tag, string message) => Write("info", tag, message);
    public static void Warn(string tag, string message) => Write("warn", tag, message);
    public static void Error(string tag, string message, Exception? ex = null) =>
        Write("error", tag, ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string tag, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{tag}] {Redact(message)}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take down the app.
            }
        }
    }

    private static string Redact(string message)
    {
        // Never persist obvious token-shaped blobs in the log file.
        if (message.Contains("sk-", StringComparison.Ordinal) ||
            message.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted]";
        }
        return message;
    }
}
