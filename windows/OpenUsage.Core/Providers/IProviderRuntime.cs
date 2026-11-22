using OpenUsage.Core.Models;

namespace OpenUsage.Core.Providers;

public interface IProviderRuntime
{
    Provider Provider { get; }
    bool IsAccountTemplate => false;
    IReadOnlyList<WidgetDescriptor> WidgetDescriptors { get; }
    Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default);
}

public interface IApiKeyManaging
{
    string ProviderId { get; }
    string? LoadKey();
    void SaveKey(string? key);
}

public static class BrandColors
{
    public static string Hex(string providerId, bool dark) => providerId switch
    {
        "claude" => "#DE7356",
        "codex" => "#10A37F",
        "cursor" => dark ? "#F5F5F7" : "#13120A",
        "grok" => dark ? "#98989D" : "#8E8E93",
        "opencode" => dark ? "#AEAEB2" : "#6E6E73",
        "openrouter" => "#6467F2",
        "antigravity" => "#4285F4",
        "copilot" => "#A855F7",
        "devin" => "#F34E3F",
        "zai" => dark ? "#D1D1D6" : "#2D2D2D",
        "ollama" => "#34C759",
        _ => "#5856D6"
    };
}
