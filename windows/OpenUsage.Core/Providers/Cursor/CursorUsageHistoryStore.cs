using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

/// <summary>
/// Per-account cache of finished Cursor usage days, so a refresh only refetches today, yesterday,
/// and days that were cached before they ended. One file per account (hashed user id); a cache from
/// another account, schema, or time zone is ignored.
/// </summary>
public sealed class CursorUsageHistoryStore
{
    private const int SchemaVersion = 3;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _directory;

    public CursorUsageHistoryStore(string? directory = null) => _directory = directory;

    private string CacheDirectory => _directory ?? Path.Combine(AppPaths.OpenUsageDir, "cursor-usage-history");

    public Dictionary<string, CursorUsageDay> Load(string userId, string timeZone)
    {
        var path = FilePath(userId);
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        try
        {
            var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllBytes(path), Options);
            if (file is null || file.SchemaVersion != SchemaVersion || file.UserId != userId || file.TimeZone != timeZone)
                return new(StringComparer.Ordinal);
            return file.Days is { } days ? new(days, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Warn("cursor", $"usage history cache could not be read: {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, CursorUsageDay> days, string userId, string timeZone)
    {
        var file = new HistoryFile
        {
            SchemaVersion = SchemaVersion,
            UserId = userId,
            TimeZone = timeZone,
            Days = days
        };
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var path = FilePath(userId);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("cursor", $"usage history cache could not be saved: {ex.Message}");
        }
    }

    private string FilePath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{hash[..32]}.json");
    }

    private sealed class HistoryFile
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("userID")] public string? UserId { get; set; }
        [JsonPropertyName("timeZone")] public string? TimeZone { get; set; }
        [JsonPropertyName("days")] public IReadOnlyDictionary<string, CursorUsageDay>? Days { get; set; }
    }
}
