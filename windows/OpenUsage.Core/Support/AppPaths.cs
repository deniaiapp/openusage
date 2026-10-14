namespace OpenUsage.Core.Support;

public static class AppPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string OpenUsageDir
    {
        get
        {
            var dir = Path.Combine(AppData, "OpenUsage");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(OpenUsageDir, "settings.json");
    public static string CacheDir
    {
        get
        {
            var dir = Path.Combine(OpenUsageDir, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string LogFile => Path.Combine(OpenUsageDir, "openusage.log");
    public static string KeysFile => Path.Combine(OpenUsageDir, "keys.bin");

    public static string Expand(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("~/") || trimmed.StartsWith("~\\"))
            trimmed = Path.Combine(Home, trimmed[2..]);
        else if (trimmed == "~")
            trimmed = Home;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
    }

    public static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static IEnumerable<string> Existing(params string[] paths) =>
        paths.Select(Expand).Where(File.Exists);

    public static IEnumerable<string> ExistingDirs(params string[] paths) =>
        paths.Select(Expand).Where(Directory.Exists);
}
