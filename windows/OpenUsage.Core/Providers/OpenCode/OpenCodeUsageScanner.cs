using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.OpenCode;

/// <summary>
/// Table probing for OpenCode's channel databases. <see cref="SqliteReader.QueryValue"/> returns null for
/// both "no value" and "couldn't read", so every probe here returns a prefixed marker: a null result is
/// always a read failure, never an absent table or row.
/// </summary>
public static class OpenCodeDatabase
{
    private const string TablesPrefix = "tables:";

    /// <summary>
    /// The OpenCode tables a database holds (<c>message</c>, <c>session_message</c>, <c>migration</c>,
    /// <c>credential</c>), or null when the database couldn't be read.
    /// </summary>
    public static HashSet<string>? Tables(string path)
    {
        var value = SqliteReader.QueryValue(path,
            $"SELECT '{TablesPrefix}' || COALESCE(group_concat(name), '') FROM sqlite_master " +
            "WHERE type='table' AND name IN ('message','session_message','migration','credential');");
        if (value is null || !value.StartsWith(TablesPrefix, StringComparison.Ordinal)) return null;
        return value[TablesPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static bool HasMessageTables(HashSet<string> tables) =>
        tables.Contains("message") || tables.Contains("session_message");
}

public static class OpenCodeUsageScanner
{
    public static readonly string[] HostedProviderIds = ["opencode-go", "opencode"];

    private static readonly string ProviderFilter = "(" + string.Join(",", HostedProviderIds.Select(id => $"'{id}'")) + ")";

    // OpenCode 1 writes `message` rows with flat `role`/`modelID`/`providerID`; OpenCode 2 writes
    // `session_message` rows with a `type` column and a nested `$.model`, and its migrated rows carry
    // token buckets without `$.tokens.total`. Completed compactions carry usage too.
    private const string ModelId = "COALESCE(json_extract(data,'$.model.id'), json_extract(data,'$.modelID'))";
    private const string ProviderId = "COALESCE(json_extract(data,'$.model.providerID'), json_extract(data,'$.providerID'))";
    private const string TotalTokens = """
        COALESCE(json_extract(data,'$.tokens.total'),
                 COALESCE(json_extract(data,'$.tokens.input'),0)
                 + COALESCE(json_extract(data,'$.tokens.output'),0)
                 + COALESCE(json_extract(data,'$.tokens.reasoning'),0)
                 + COALESCE(json_extract(data,'$.tokens.cache.read'),0)
                 + COALESCE(json_extract(data,'$.tokens.cache.write'),0))
        """;
    private const string V1Role = "json_extract(data,'$.role') = 'assistant'";
    private const string V2Role = "(type = 'assistant' OR (type = 'compaction' AND json_extract(data,'$.status') = 'completed'))";
    private const string ProbePrefix = "probe:";

    /// <summary>
    /// Hosted (Go + Zen) spend from every <c>opencode*.db</c>, or null when there is no usable database.
    /// Throws when every database holding message tables failed to read, so broken storage is never
    /// mistaken for "no usage".
    /// </summary>
    public static LogUsageScan? Scan(int daysBack = 30, DateTimeOffset? now = null, string? dataDirectory = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var since = DailyUsageAccumulator.Since(daysBack, current);
        var cutoffMs = since.ToUnixTimeMilliseconds();
        var files = OpenCodePaths.DatabaseFiles(dataDirectory ?? OpenCodePaths.DataDirectory());
        if (files.Count == 0) return null;

        var rows = new List<Row>();
        var checkedCount = 0;
        var failures = 0;
        foreach (var file in files)
        {
            var tables = OpenCodeDatabase.Tables(file);
            if (tables is null)
            {
                checkedCount++;
                failures++;
                continue;
            }
            // A database with no message tables has no usage to read and does not vote on failure.
            if (!OpenCodeDatabase.HasMessageTables(tables)) continue;
            checkedCount++;
            // json_group_array always yields at least "[]", so null here is a read failure.
            var json = SqliteReader.QueryValue(file, DataSql(cutoffMs, tables));
            if (json is null)
            {
                failures++;
                AppLog.Warn("opencode", $"usage query failed for {Path.GetFileName(file)}");
                continue;
            }
            rows.AddRange(ParseRows(json));
        }
        if (checkedCount > 0 && failures == checkedCount)
            throw new InvalidOperationException(OpenCodeErrors.DatabaseUnreadable);
        if (checkedCount == 0) return null;

        var acc = new DailyUsageAccumulator();
        // OpenCode 2 copies legacy `message` rows into `session_message` under their original IDs and
        // keeps both tables, so the union can hold two copies of one message.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Id is { } id && !seenIds.Add(id)) continue;
            var date = DateTimeOffset.FromUnixTimeMilliseconds((long)row.Ms);
            if (date < since) continue;
            acc.Add(DailyUsageAccumulator.DayKey(date), row.Tokens, row.Cost);
        }
        return acc.Build();
    }

    /// <summary>Whether any database holds hosted usage — the local-only first-run signal.</summary>
    public static bool HasHostedUsage(string? dataDirectory = null)
    {
        foreach (var file in OpenCodePaths.DatabaseFiles(dataDirectory ?? OpenCodePaths.DataDirectory()))
        {
            if (OpenCodeDatabase.Tables(file) is not { } tables || !OpenCodeDatabase.HasMessageTables(tables)) continue;
            var value = SqliteReader.QueryValue(file,
                $"SELECT '{ProbePrefix}' || EXISTS(SELECT 1 FROM {Source(tables, null)} LIMIT 1);");
            if (value == ProbePrefix + "1") return true;
        }
        return false;
    }

    public static string DataSql(long cutoffMs, IReadOnlySet<string> tables) => $"""
        SELECT json_group_array(json_array(
                 time_created,
                 json_extract(data,'$.cost'),
                 {TotalTokens},
                 {ModelId},
                 {ProviderId},
                 id))
        FROM {Source(tables, cutoffMs)};
        """;

    private static string Source(IReadOnlySet<string> tables, long? cutoffMs)
    {
        var bodies = new List<string>();
        if (tables.Contains("message")) bodies.Add(RowsSql("message", V1Role, cutoffMs));
        if (tables.Contains("session_message")) bodies.Add(RowsSql("session_message", V2Role, cutoffMs));
        return "(\n" + string.Join("\n        UNION ALL\n", bodies) + "\n)";
    }

    private static string RowsSql(string table, string role, long? cutoffMs) => $"""
        SELECT id, time_created, data FROM {table}
          WHERE {(cutoffMs is { } ms ? $"time_created >= {ms} AND " : "")}json_valid(data)
            AND {role}
            AND {ProviderId} IN {ProviderFilter}
            AND json_type(data,'$.cost') IN ('integer','real')
        """;

    private sealed record Row(double Ms, double Cost, long Tokens, string? Id);

    /// <summary>
    /// Parse <c>[time_created, cost, tokensTotal, modelID, providerID, id]</c> rows. Rows with a missing
    /// timestamp or cost are skipped at this boundary.
    /// </summary>
    private static IEnumerable<Row> ParseRows(string json)
    {
        JsonArray? array;
        try { array = JsonNode.Parse(json) as JsonArray; }
        catch (Exception ex)
        {
            AppLog.Warn("opencode", $"usage rows unreadable: {ex.Message}");
            yield break;
        }
        if (array is null) yield break;
        foreach (var element in array)
        {
            if (element is not JsonArray entry || entry.Count < 5) continue;
            if (JsonUtil.Number(entry[0]) is not { } ms) continue;
            if (JsonUtil.Number(entry[1]) is not { } cost || cost < 0) continue;
            var tokens = (long)Math.Min(Math.Max(JsonUtil.Number(entry[2]) ?? 0, 0), 1e15);
            var id = entry.Count >= 6 ? JsonUtil.String(entry[5]) : null;
            yield return new Row(ms, cost, tokens, id);
        }
    }
}
