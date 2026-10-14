using System.Text;

namespace OpenUsage.Core.Providers.Claude;

/// In-memory parse cache for Claude Code's session logs. An unchanged file reuses its parsed entries;
/// a file that only grew is resumed from its last complete line, so a long-running session costs only
/// the appended bytes on each refresh. A changed prefix (checked by fingerprinting the bytes just before
/// the resume point), a truncated file, or an incomplete appended line falls back to a full re-read.
public sealed class ClaudeLogScanCache
{
    public const int FingerprintWindow = 4096;
    private const string UsageMarker = "\"usage\":{";

    private sealed class FileRecord
    {
        public long Size;
        public DateTime Mtime;
        /// Byte offset just past the last complete line, when the file ended on a line boundary.
        public long? Offset;
        public long Fingerprint;
        public List<ClaudeLogUsageScanner.Entry> Entries = [];
    }

    private readonly Dictionary<string, FileRecord> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int FullParses { get; private set; }
    public int TailParses { get; private set; }

    /// Parsed entries for `paths`, in the given order (file order, then line order). Files not listed
    /// are forgotten, so the cache follows the scan window.
    public List<ClaudeLogUsageScanner.Entry> Entries(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            var result = new List<ClaudeLogUsageScanner.Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!seen.Add(path)) continue;
                if (Load(path) is { } record) result.AddRange(record.Entries);
            }
            foreach (var stale in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(stale);
            return result;
        }
    }

    private FileRecord? Load(string path)
    {
        long size;
        DateTime mtime;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { _files.Remove(path); return null; }
            size = info.Length;
            mtime = info.LastWriteTimeUtc;
        }
        catch
        {
            _files.Remove(path);
            return null;
        }

        if (_files.TryGetValue(path, out var record))
        {
            if (record.Size == size && record.Mtime == mtime) return record;
            if (TryAppend(path, record, size, mtime)) return record;
        }

        var fresh = FullParse(path, size, mtime);
        if (fresh is null) _files.Remove(path);
        else _files[path] = fresh;
        return fresh;
    }

    private bool TryAppend(string path, FileRecord record, long size, DateTime mtime)
    {
        if (record.Offset is not { } offset || size <= offset) return false;
        try
        {
            if (Fingerprint(path, offset) != record.Fingerprint) return false;
            if (ReadRange(path, offset, size) is not { EndsAtLineBoundary: true } appended) return false;
            var fingerprint = Fingerprint(path, size);
            record.Entries.AddRange(appended.Entries);
            record.Size = size;
            record.Mtime = mtime;
            record.Offset = size;
            record.Fingerprint = fingerprint;
            TailParses++;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private FileRecord? FullParse(string path, long size, DateTime mtime)
    {
        try
        {
            if (ReadRange(path, 0, size) is not { } parsed) return null;
            FullParses++;
            var record = new FileRecord { Size = size, Mtime = mtime, Entries = parsed.Entries };
            if (parsed.EndsAtLineBoundary)
            {
                record.Offset = size;
                record.Fingerprint = Fingerprint(path, size);
            }
            return record;
        }
        catch
        {
            return null;
        }
    }

    /// Parses every line in `[start, end)`. A trailing line without a newline is still parsed (a full
    /// read keeps it), but reported so a resumed read can fall back instead of caching half a record.
    private static (List<ClaudeLogUsageScanner.Entry> Entries, bool EndsAtLineBoundary)? ReadRange(string path, long start, long end)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(start, SeekOrigin.Begin);
        var entries = new List<ClaudeLogUsageScanner.Entry>();
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var remaining = end - start;
        var atFileStart = start == 0;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) return null; // the file shrank under us
            remaining -= read;
            var span = buffer.AsSpan(0, read);
            int newline;
            while ((newline = span.IndexOf((byte)'\n')) >= 0)
            {
                line.Write(span[..newline]);
                Emit(line, entries, ref atFileStart);
                span = span[(newline + 1)..];
            }
            line.Write(span);
        }
        var endsAtBoundary = line.Length == 0;
        if (!endsAtBoundary) Emit(line, entries, ref atFileStart);
        return (entries, endsAtBoundary);
    }

    private static void Emit(MemoryStream line, List<ClaudeLogUsageScanner.Entry> entries, ref bool atFileStart)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        if (atFileStart && bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[3..];
        atFileStart = false;
        var text = Encoding.UTF8.GetString(bytes);
        line.SetLength(0);
        if (text.Length == 0) return;
        if (!text.Contains(UsageMarker, StringComparison.Ordinal)) return;
        entries.AddRange(ClaudeLogUsageScanner.ParseLine(text.TrimEnd('\r')));
    }

    /// FNV-1a over the `FingerprintWindow` bytes ending at `offset`.
    private static long Fingerprint(string path, long offset)
    {
        var start = Math.Max(0, offset - FingerprintWindow);
        var length = (int)(offset - start);
        var bytes = new byte[length];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(bytes);
        }
        var hash = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return unchecked((long)hash);
    }
}
