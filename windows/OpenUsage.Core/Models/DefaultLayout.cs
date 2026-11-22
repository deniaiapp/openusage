namespace OpenUsage.Core.Models;

public static class DefaultLayout
{
    public static readonly string[] MetricIds =
    [
        "antigravity.geminiPro", "antigravity.geminiWeekly", "antigravity.claude", "antigravity.claudeWeekly",
        "antigravity.trend", "antigravity.today", "antigravity.yesterday", "antigravity.last30",

        "claude.session", "claude.weekly", "claude.fable",
        "claude.extra", "claude.rateLimitResets",

        "codex.session", "codex.weekly", "codex.spark", "codex.sparkWeekly",
        "codex.credits", "codex.rateLimitResets",

        "cursor.usage", "cursor.auto", "cursor.api", "cursor.grokBot", "cursor.trend",
        "cursor.onDemand", "cursor.today", "cursor.yesterday", "cursor.last30",

        "copilot.premium", "copilot.extra", "copilot.orgCredits", "copilot.orgSpend",
        "copilot.chat", "copilot.completions",

        "devin.daily", "devin.weekly", "devin.extra",

        "grok.weekly", "grok.payAsYouGo",

        "muse.session", "muse.weekly",

        "ollama.session", "ollama.weekly", "ollama.monthly", "ollama.purchasedCredits",

        "opencode.session", "opencode.weekly", "opencode.monthly", "opencode.trend",
        "opencode.today", "opencode.yesterday", "opencode.last30",

        "openrouter.credits", "openrouter.balance",
        "openrouter.today", "openrouter.week", "openrouter.month", "openrouter.keyLimit",

        "zai.session", "zai.weekly", "zai.webSearches"
    ];

    public static readonly string[] PinnedMetricIds =
    [
        "antigravity.geminiPro", "antigravity.geminiWeekly",
        "claude.session", "claude.weekly",
        "codex.session", "codex.weekly",
        "cursor.auto", "cursor.api",
        "copilot.premium",
        "muse.session",
        "ollama.monthly",
        "openrouter.credits",
        "zai.session", "zai.weekly"
    ];

    public static readonly string[] ExpandedMetricIds =
    [
        "antigravity.claude", "antigravity.claudeWeekly",
        "antigravity.today", "antigravity.yesterday", "antigravity.last30",
        "claude.sonnet", "claude.rateLimitResets",
        "codex.spark", "codex.sparkWeekly",
        "codex.credits", "codex.rateLimitResets",
        "cursor.grokBot", "cursor.onDemand", "cursor.requests", "cursor.credits",
        "cursor.today", "cursor.yesterday", "cursor.last30",
        "copilot.orgCredits", "copilot.orgSpend", "copilot.chat", "copilot.completions",
        "devin.extra",
        "grok.payAsYouGo",
        "ollama.purchasedCredits",
        "opencode.today", "opencode.yesterday", "opencode.last30",
        "openrouter.today", "openrouter.week", "openrouter.month", "openrouter.keyLimit",
        "zai.webSearches"
    ];

    public static readonly string[] StarterProviders = ["claude", "codex", "cursor"];

    /// <summary>
    /// Defaults that layouts saved before seeding was tracked already had. Anything in
    /// <see cref="MetricIds"/> beyond this list is new to those users and gets added once.
    /// </summary>
    public static readonly string[] MigrationBaselineMetricIds = MetricIds
        .Except(["claude.rateLimitResets", "ollama.monthly", "ollama.purchasedCredits", "muse.session", "muse.weekly"])
        .ToArray();
}
