using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public static class LimitsCommand
{
    private const string Schema = "openusage.limits.v2";
    private const string Help = """
        Usage: openusage [provider-or-account] [options]

        Print the limits OpenUsage last fetched as openusage.limits.v2 JSON.
        The command never refreshes usage or signs in.

        provider-or-account   A provider kind (codex) or an exact account ID (provider@id).
                              Omit it to include all enabled providers and accounts.

        Options:
          -h, --help      Show this help.
          -v, --version   Show the installed CLI build version.
          --force         Accepted for compatibility; does not refresh.

        Exit codes: 0 success, 2 invalid arguments or disabled provider, 4 no saved usage.
        """;
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int Run(string[] args, string dataDirectory, TextWriter output, TextWriter error,
        DateTimeOffset now, string? version = null)
    {
        string? filter = null;
        var force = false;
        var help = false;
        var showVersion = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    continue;
                case "-v" or "--version":
                    showVersion = true;
                    continue;
                case "--force":
                    force = true;
                    continue;
            }
            var normalized = arg.ToLowerInvariant();
            if (filter is not null || !IsSupported(normalized))
            {
                error.WriteLine("Run 'openusage --help' for usage.");
                return 2;
            }
            filter = normalized;
        }
        if (help)
        {
            output.WriteLine(Help);
            return 0;
        }
        if (showVersion)
        {
            output.WriteLine($"openusage {version ?? "(development build)"}");
            return 0;
        }
        if (force) error.WriteLine("--force is ignored; openusage reads the last fetched usage.");

        var settings = Read<Settings>(Path.Combine(dataDirectory, "settings.json"), WireOptions, error);
        var enabled = settings?.EnabledProviders ?? [];
        var freshnessSeconds = 2d * AppSettings.NormalizeRefreshInterval(settings?.RefreshIntervalSeconds);
        var selected = enabled.Where(id => IsSupported(id) && id != OllamaProvider.Id)
            .Where(id => filter is null || id == filter || id.StartsWith(filter + "@", StringComparison.Ordinal))
            .ToArray();
        if (filter is not null && selected.Length == 0)
        {
            error.WriteLine($"No enabled provider or account named {filter}.");
            return 2;
        }

        var providers = new SortedDictionary<string, WireProvider>(StringComparer.Ordinal);
        foreach (var provider in selected)
        {
            var snapshot = Read<Snapshot>(Path.Combine(dataDirectory, "cache", provider + ".json"), null, error);
            if (snapshot is not { Limits: { } limits } || snapshot.ProviderId != provider || snapshot.RefreshedAt == default)
                continue;
            var fetchedAt = snapshot.RefreshedAt.ToUniversalTime();
            var expiresAt = fetchedAt.AddSeconds(freshnessSeconds);
            providers[provider] = new(snapshot.DisplayName, snapshot.Plan, fetchedAt, expiresAt, now >= expiresAt, limits);
        }

        output.WriteLine(JsonSerializer.Serialize(new { Schema, GeneratedAt = now.ToUniversalTime(), Providers = providers }, WireOptions));
        if (providers.Count > 0) return 0;
        error.WriteLine("No usage has been fetched yet. Open OpenUsage and refresh your providers.");
        return 4;
    }

    private static bool IsSupported(string? id)
    {
        if (id is null) return false;
        var scope = id.IndexOf('@');
        return scope < 0 ? LimitsProjection.HasProvider(id)
            : ApiKeyAccounts.Supports(id[..scope]) && Guid.TryParseExact(id[(scope + 1)..], "N", out _);
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

    private sealed record Settings(HashSet<string>? EnabledProviders, int? RefreshIntervalSeconds);
    private sealed record Snapshot(string ProviderId, string DisplayName, string? Plan, DateTimeOffset RefreshedAt,
        Dictionary<string, LimitResource>? Limits);
    private sealed record WireProvider(string DisplayName, string? Plan, DateTimeOffset FetchedAt,
        DateTimeOffset ExpiresAt, bool Stale, Dictionary<string, LimitResource> Resources);
}
