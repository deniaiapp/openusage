using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public DensitySetting Density { get; set; } = DensitySetting.Regular;
    public WidgetDisplayMode MeterStyle { get; set; } = WidgetDisplayMode.Remaining;
    public ResetDisplayMode ResetDisplay { get; set; } = ResetDisplayMode.Relative;
    public bool AlwaysShowPacing { get; set; }
    public bool PrivacyMode { get; set; }
    public PriorityLimits PriorityLimits { get; set; } = PriorityLimits.Default;
    public bool LaunchAtLogin { get; set; }
    public string? GlobalHotkey { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 300;
    public bool Seeded { get; set; }
    /// <summary>True from a fresh install until the user leaves the welcome screen.</summary>
    public bool WelcomePending { get; set; }
    public HashSet<string> EnabledProviders { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> EnabledMetrics { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ExpandedMetrics { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> PinnedMetrics { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> OpenCards { get; set; } = new(StringComparer.Ordinal);
    public List<string> ProviderOrder { get; set; } = [];
    public bool CustomizeHintDismissed { get; set; }
    public bool NotifyBurningFast { get; set; }
    public bool NotifyUseItOrLoseIt { get; set; }
    /// <summary>Default metric IDs this layout has already been offered; null on layouts saved before tracking.</summary>
    public HashSet<string>? SeededDefaultMetrics { get; set; }

    /// <summary>Where <see cref="Save"/> writes; defaults to the app's settings file. Tests point it elsewhere.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? AppPaths.SettingsFile;
        try
        {
            if (File.Exists(file))
            {
                using var source = AtomicFile.OpenRead(file);
                var loaded = JsonSerializer.Deserialize<AppSettings>(source, JsonOptions());
                if (loaded is not null)
                {
                    loaded.FilePath = path;
                    loaded.EnabledProviders ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.EnabledMetrics ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.ExpandedMetrics ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.PinnedMetrics ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.OpenCards ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.ProviderOrder ??= [];
                    loaded.PriorityLimits ??= PriorityLimits.Default;
                    if (loaded.RefreshIntervalSeconds < 30) loaded.RefreshIntervalSeconds = 300;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("settings", "could not load settings", ex);
        }
        var fresh = Fresh();
        fresh.FilePath = path;
        return fresh;
    }

    /// <summary>A fresh install starts with no providers on; the welcome screen turns on the user's picks.</summary>
    public static AppSettings Fresh() => new()
    {
        EnabledProviders = new HashSet<string>(StringComparer.Ordinal),
        EnabledMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal),
        ExpandedMetrics = new HashSet<string>(DefaultLayout.ExpandedMetricIds, StringComparer.Ordinal),
        PinnedMetrics = new HashSet<string>(DefaultLayout.PinnedMetricIds, StringComparer.Ordinal),
        SeededDefaultMetrics = new HashSet<string>(DefaultLayout.MetricIds, StringComparer.Ordinal)
    };

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath ?? AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions()));
        }
        catch (Exception ex)
        {
            AppLog.Error("settings", "could not save settings", ex);
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
