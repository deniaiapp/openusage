# OpenUsage on Windows

Windows builds live in [`windows/`](../windows/README.md) as a .NET 8 system-tray app. They are a port of the Swift menu-bar app, not a replacement for it.

The Windows tray app has its own dashboard, Customize, Settings, five-minute refresh, first-launch welcome screen, local spend tiles and usage trend, and global shortcut. Providers still work with existing local sign-ins. OpenRouter and Z.ai can also have multiple accounts.

To add one, open **Customize**, choose **OpenRouter** or **Z.ai**, and enter a name and API key under **Additional Accounts**. OpenUsage checks the key before saving it with Windows user encryption. New cards go after the provider's existing additional accounts or its visible main card. If neither exists, the new card goes at the bottom. Rename or remove the account in the same section. Existing API keys and provider settings stay in place.

Choose **Replace API Key** next to an additional account to change its key without changing its name or dashboard settings. If the new key doesn't pass validation, OpenUsage keeps the old key.

Existing credentials are read from Windows paths (AppData, `%USERPROFILE%` config files, and Credential Manager) instead of the macOS Keychain.

A fresh install starts with no providers on and opens a welcome screen. It lists the providers already signed in on this PC (checked locally, nothing is sent anywhere) and preselects them; **Show All** lists the rest. **Connect Selected** turns them on one at a time; one that can't connect stays off, shows why, and offers **Retry**. **Skip for Now** opens an empty dashboard with a **Choose Providers** button. Closing the welcome window quits OpenUsage, and the welcome screen comes back on the next launch. Scheduled updates start once you leave it.

Ollama stays off until you turn it on in Customize — the local signing key exists even when you only run models on-device. Antigravity reads the `gemini:antigravity` Windows credential (or a cached Google token) and uses the active Cloud Code quota counters, falling back for missing quotas. A session with any usage shows its reset time.

Provider details that match macOS:

- **Claude:** Rate Limit Resets row (checked at most once an hour), plan badge from the live profile.
- **Codex:** Pro 100 / 200 / 500 plan names, auto-review usage free from October 6, Ultrafast priced at 6x on GPT-6 Astra. A slow history scan shows a warning instead of holding back live limits.
- **Cursor:** Unified Cursor/X/Grok plans show the dashboard's credits and additional usage. For other plans, extra usage is measured against the matching personal or pooled budget. Spend history is fetched day by day and finished days are cached per account.
- **Grok:** The pay-as-you-go limit is shown in dollars.
- **Ollama:** Monthly and Purchased Credits come from the balance endpoint. Free accounts can show purchased credits without a monthly allowance.
- **OpenCode:** reads OpenCode 2's database (usage and Go key) and falls back to `auth.json` on OpenCode 1.
- **Z.ai:** A rejected API key prompts you to replace it in Customize.

Claude and Codex logins stored in Credential Manager aren't refreshed by OpenUsage, so it never signs the CLI out. If the card says the session expired, run the CLI once.

See the [Windows README](../windows/README.md) for how to build it and which providers are wired up. The
[command-line helper](../windows/README.md#command-line) replaces the legacy loopback HTTP API and reads saved limits even when the tray app is closed.

## Usage Notifications

Enable these in Settings to get a Windows notification before a limit runs out at its current pace, or before a weekly or longer limit resets with more than half left. Notifications are off by default and don't fire on the first reading of a limit. Click one to open the dashboard.
