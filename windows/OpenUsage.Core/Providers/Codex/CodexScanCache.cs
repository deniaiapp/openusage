using System.Security.Cryptography;
using System.Text;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

/// <summary>
/// Remembers each rollout's parsed events between refreshes. An unchanged file is reused as is; a
/// file that only grew is read from its last complete line with the saved parser state (#1383).
/// Anything else (rewritten, truncated, or a prefix that no longer matches) is parsed from the start.
/// </summary>
public sealed class CodexScanCache
{
    /// <summary>Bytes before the resume offset whose hash must still match before tail-reading.</summary>
    public const int FingerprintWindow = 4096;

    private sealed class Entry
    {
        public long Size;
        public DateTime ModifiedUtc;
        public List<CodexLogUsageScanner.Event> Events = [];
        /// <summary>Byte offset just past the last complete line; null when the file can't be resumed.</summary>
        public long? Offset;
        public CodexParseState? State;
        public byte[]? Fingerprint;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReadKind> _lastReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static CodexScanCache Shared { get; } = new();

    /// <summary>How the last <see cref="Events"/> call obtained each file, for diagnostics and tests.</summary>
    public enum ReadKind { Reused, Appended, Full }

    public ReadKind? LastRead(string path)
    {
        lock (_gate) return _lastReads.TryGetValue(path, out var kind) ? kind : null;
    }

    public List<CodexLogUsageScanner.Event> Events(IReadOnlyList<string> files)
    {
        lock (_gate)
        {
            var events = new List<CodexLogUsageScanner.Event>();
            var live = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _entries.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _entries.Remove(stale);
                _lastReads.Remove(stale);
            }
            foreach (var path in files)
            {
                var entry = Read(path);
                if (entry is null) continue;
                events.AddRange(entry.Events);
            }
            return events;
        }
    }

    private Entry? Read(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                _entries.Remove(path);
                return null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("codex", $"could not stat {path}: {ex.Message}");
            return null;
        }

        var size = info.Length;
        var modified = info.LastWriteTimeUtc;
        if (_entries.TryGetValue(path, out var cached))
        {
            if (cached.Size == size && cached.ModifiedUtc == modified)
            {
                _lastReads[path] = ReadKind.Reused;
                return cached;
            }
            if (TryAppend(path, cached, size, modified))
            {
                _lastReads[path] = ReadKind.Appended;
                return cached;
            }
        }

        try
        {
            var entry = FullParse(path, size, modified);
            _entries[path] = entry;
            _lastReads[path] = ReadKind.Full;
            return entry;
        }
        catch (Exception ex)
        {
            AppLog.Warn("codex", $"could not read {path}: {ex.Message}");
            _entries.Remove(path);
            return null;
        }
    }

    private static Entry FullParse(string path, long size, DateTime modified)
    {
        using var stream = Open(path);
        var parser = new CodexLogParser();
        var events = new List<CodexLogUsageScanner.Event>();
        var endsAtLineBoundary = true;
        long consumed = 0;
        foreach (var (line, terminated, end) in Lines(stream, 0, size))
        {
            if (parser.Parse(line) is { } ev) events.Add(ev);
            consumed = end;
            if (!terminated) endsAtLineBoundary = false;
        }
        var entry = new Entry { Size = size, ModifiedUtc = modified, Events = events };
        // A file still being written mid-line is parsed in full again next time rather than resumed.
        if (endsAtLineBoundary && consumed == size)
        {
            entry.Offset = size;
            entry.State = parser.State.Clone();
            entry.Fingerprint = Fingerprint(stream, size);
        }
        return entry;
    }

    private static bool TryAppend(string path, Entry cached, long size, DateTime modified)
    {
        if (cached.Offset is not { } offset || cached.State is null || cached.Fingerprint is null) return false;
        if (size <= offset) return false;
        try
        {
            using var stream = Open(path);
            if (Fingerprint(stream, offset) is not { } prefix || !prefix.AsSpan().SequenceEqual(cached.Fingerprint))
                return false;
            var parser = new CodexLogParser(cached.State.Clone());
            var appended = new List<CodexLogUsageScanner.Event>();
            long consumed = offset;
            foreach (var (line, terminated, end) in Lines(stream, offset, size))
            {
                if (!terminated) return false;
                if (parser.Parse(line) is { } ev) appended.Add(ev);
                consumed = end;
            }
            if (consumed != size) return false;
            cached.Events.AddRange(appended);
            cached.Size = size;
            cached.ModifiedUtc = modified;
            cached.Offset = size;
            cached.State = parser.State.Clone();
            cached.Fingerprint = Fingerprint(stream, size);
            return cached.Fingerprint is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("codex", $"tail read of {path} failed, re-reading it: {ex.Message}");
            return false;
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    private static byte[]? Fingerprint(FileStream stream, long endingAt)
    {
        var start = Math.Max(0, endingAt - FingerprintWindow);
        var length = (int)(endingAt - start);
        var buffer = new byte[length];
        stream.Seek(start, SeekOrigin.Begin);
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(buffer, read, length - read);
            if (n == 0) return null;
            read += n;
        }
        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// Lines between <paramref name="start"/> and <paramref name="limit"/>, with whether each ended in
    /// a newline and the byte offset just past it. Reads in chunks so large rollouts stay out of memory.
    /// </summary>
    private static IEnumerable<(string Line, bool Terminated, long End)> Lines(FileStream stream, long start, long limit)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        var position = start;
        while (position < limit)
        {
            var toRead = (int)Math.Min(buffer.Length, limit - position);
            var n = stream.Read(buffer, 0, toRead);
            if (n == 0) break;
            var lineStart = 0;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, lineStart, i - lineStart);
                yield return (Decode(pending), true, position + i + 1);
                pending.SetLength(0);
                lineStart = i + 1;
            }
            pending.Write(buffer, lineStart, n - lineStart);
            position += n;
        }
        if (pending.Length > 0) yield return (Decode(pending), false, position);
    }

    private static string Decode(MemoryStream bytes)
    {
        var span = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        return Encoding.UTF8.GetString(span).TrimStart('\uFEFF');
    }
}
