using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public static class LimitsCommand
{
    private const string Schema = "openusage.limits.v2";
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int Run(string[] args, string dataDirectory, TextWriter output, TextWriter error, DateTimeOffset now)
    {
        string? filter = null;
        var force = false;
        foreach (var arg in args)
        {
            if (arg == "--force" && !force)
            {
                force = true;
                continue;
            }
            if (filter is not null || !LimitsProjection.HasProvider(arg))
            {
                error.WriteLine("Usage: openusage [provider] [--force]");
                return 2;
            }
            filter = arg;
        }
        if (force) error.WriteLine("--force is ignored; openusage reads the last fetched usage.");

        var enabled = Read<Settings>(Path.Combine(dataDirectory, "settings.json"), WireOptions, error)?.EnabledProviders
            ?? [];
        if (filter is not null && !enabled.Contains(filter))
        {
            error.WriteLine($"No enabled provider named {filter}.");
            return 2;
        }

        var providers = new SortedDictionary<string, WireProvider>(StringComparer.Ordinal);
        foreach (var provider in enabled.Where(LimitsProjection.HasProvider).Where(id => filter is null || id == filter))
        {
            var snapshot = Read<Snapshot>(Path.Combine(dataDirectory, "cache", provider + ".json"), null, error);
            if (snapshot is not { Limits: { } limits } || snapshot.ProviderId != provider || snapshot.RefreshedAt == default)
                continue;
            var fetchedAt = snapshot.RefreshedAt.ToUniversalTime();
            var expiresAt = fetchedAt.AddMinutes(10);
            providers[provider] = new(snapshot.DisplayName, snapshot.Plan, fetchedAt, expiresAt, now >= expiresAt, limits);
        }

        output.WriteLine(JsonSerializer.Serialize(new { Schema, GeneratedAt = now.ToUniversalTime(), Providers = providers }, WireOptions));
        if (providers.Count > 0) return 0;
        error.WriteLine("No usage has been fetched yet. Open OpenUsage and refresh your providers.");
        return 4;
    }

    private static T? Read<T>(string path, JsonSerializerOptions? options, TextWriter error) where T : class
    {
        try
        {
            using var source = AtomicFile.OpenRead(path);
            return JsonSerializer.Deserialize<T>(source, options);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine($"Could not read {Path.GetFileName(path)}.");
            return null;
        }
    }

    private sealed record Settings(HashSet<string>? EnabledProviders);
    private sealed record Snapshot(string ProviderId, string DisplayName, string? Plan, DateTimeOffset RefreshedAt,
        Dictionary<string, LimitResource>? Limits);
    private sealed record WireProvider(string DisplayName, string? Plan, DateTimeOffset FetchedAt,
        DateTimeOffset ExpiresAt, bool Stale, Dictionary<string, LimitResource> Resources);
}
