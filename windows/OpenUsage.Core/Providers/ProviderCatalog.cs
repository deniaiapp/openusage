using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Antigravity;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Providers.Ollama;
using OpenUsage.Core.Providers.OpenCode;
using OpenUsage.Core.Providers.OpenRouter;
using OpenUsage.Core.Providers.Zai;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers;

public static class ProviderCatalog
{
    public static IReadOnlyList<IProviderRuntime> Make(AppHttp http, ApiKeyStore keys) =>
    [
        new ClaudeProvider(http),
        new CodexProvider(http),
        new CursorProvider(http),
        new AntigravityProvider(http),
        new CopilotProvider(http, keys),
        new DevinProvider(http, keys),
        new GrokProvider(http),
        new OllamaProvider(http),
        new OpenCodeProvider(http),
        new OpenRouterProvider(http, keys),
        new ZaiProvider(http, keys)
    ];

    public static IReadOnlyList<WidgetDescriptor> AllDescriptors(IEnumerable<IProviderRuntime> providers) =>
        providers.SelectMany(p => p.WidgetDescriptors).ToList();
}
