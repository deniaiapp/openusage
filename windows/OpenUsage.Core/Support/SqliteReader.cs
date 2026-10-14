using Microsoft.Data.Sqlite;

namespace OpenUsage.Core.Support;

public static class SqliteReader
{
    public static string? QueryValue(string path, string sql)
    {
        var expanded = AppPaths.Expand(path);
        if (!File.Exists(expanded)) return null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = expanded,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = command.ExecuteScalar();
            var text = result?.ToString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex)
        {
            AppLog.Warn("sqlite", $"query failed for {Path.GetFileName(expanded)}: {ex.Message}");
            return null;
        }
    }

    public static void Execute(string path, string sql)
    {
        var expanded = AppPaths.Expand(path);
        if (!File.Exists(expanded)) throw new FileNotFoundException(expanded);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = expanded,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string Escape(string value) => value.Replace("'", "''");
}
