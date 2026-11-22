# OpenUsage on Windows

Windows builds live in [`windows/`](../windows/README.md) as a .NET 8 system-tray app. They are a port of the Swift menu-bar app, not a replacement for it.

The Windows tray app has its own dashboard, Customize, Settings, five-minute refresh, first-launch welcome screen, local spend tiles and usage trend, and global shortcut. Providers still work with existing local sign-ins. OpenRouter and Z.ai can also have multiple accounts.

The dashboard opens on **All Usage**. Switch to **Priority** for pinned metrics and limits that are running out or used up. Both tabs respect which providers and metrics you have turned on.

**Running Out** puts the shortest projected time to exhaustion first. Limits without a usable pace follow, with the lowest remaining percentage first.

To add one, open **Customize**, choose **OpenRouter** or **Z.ai**, and enter a name and API key under **Additional Accounts**. OpenUsage checks the key before saving it with Windows user encryption. New cards go after the provider's existing additional accounts or its visible main card. If neither exists, the new card goes at the bottom. Rename or remove the account in the same section. Existing API keys and provider settings stay in place.

Ollama Cloud connects with API keys only. Get a key from [Ollama settings](https://ollama.com/settings/keys), then add it under **Customize → Ollama → Accounts**. The first new Ollama account gets the default Monthly tray pin. Existing API-key accounts keep their settings. If you used the old local Ollama sign-in, add a key to resume tracking cloud usage.

Choose **Replace API Key** next to an additional account to change its key without changing its name or dashboard settings. If the new key doesn't pass validation, OpenUsage keeps the old key.

In **Customize**, use the up and down arrows to move a provider and its accounts together on the dashboard. The order is saved; **Undo Reorder** restores the previous order during the current session.

Right-click a dashboard metric or usage chart and choose **Hide** to remove it from the dashboard. Turn it back on from the provider's page in **Customize**.

Reset countdowns show calendar months and years for long waits and omit zero units, such as `17d`, `3mo 17d`, or `1y 2mo`.

Your metric choices are saved even if you clear an entire group of settings, such as unpinning every metric. New metrics can still appear with their defaults after an update.

If Windows reports that there is no network path, OpenUsage retries that refresh for about a minute before showing an error. Other refresh failures keep the usual backoff.

Existing credentials are read from Windows paths (AppData, `%USERPROFILE%` config files, and Credential Manager) instead of the macOS Keychain.

Enable **Usage Notifications** in Settings to get a Windows notification before a limit runs out at its current pace, or before a multi-day limit resets with more than half left. Notifications are off by default and don't fire on the first reading of a limit.

Click the banner or the saved notification in Notification Center to open the dashboard. If Notification Center stays in front, close it to see the dashboard. OpenUsage registers a Start menu shortcut and a dashboard link for the current Windows user while usage notifications are enabled. If Windows can't show an OpenUsage toast, or if OpenUsage runs as administrator but the Windows shell does not, the app falls back to a tray balloon. Click that banner while it's visible, or use the tray icon after it moves to Notification Center.

OpenRouter and Z.ai only use API keys saved in OpenUsage. If you previously relied on `OPENROUTER_API_KEY` or `ZAI_API_KEY`, enter the key for that provider in **Customize**. Already saved keys keep working.

OpenRouter shows the remaining **Balance** from purchased credits only when it reports both the purchase and usage totals. Only a limit set on the key appears as **Key Limit**; the old purchased-credits meter and its tray pin no longer appear.

A fresh install starts with no providers on and opens a welcome screen. It lists the providers already signed in on this PC (checked locally, nothing is sent anywhere) and preselects them; **Show All** lists the rest. **Connect Selected** turns them on one at a time; one that can't connect stays off, shows why, and offers **Retry**. **Skip for Now** opens an empty dashboard with a **Choose Providers** button. Closing the welcome window quits OpenUsage, and the welcome screen comes back on the next launch. Scheduled updates start once you leave it.

Antigravity reads the `gemini:antigravity` Windows credential (or a cached Google token) and uses the active Cloud Code quota counters, falling back for missing quotas. A session with any usage shows its reset time.

When Copilot or Antigravity cannot load usage because the provider is rate-limiting requests, OpenUsage reports that error rather than asking you to sign in or replacing cached usage with an empty card.

OpenUsage encrypts its cached Antigravity sign-in for your Windows user. On refresh, it removes the old unencrypted cache only after saving a protected copy.


### Amp on Windows

Use **Customize → Amp → Sign In with Amp** to authorize with a device code. Turn on Amp in Customize to show its usage. You do not need the Amp CLI installed. The collapsed card shows Agent Usage and Amp Free. Expand it for Agent Usage, Orb Hours, Amp Free, and Individual Credits in that order. Amp Free shows the remaining allowance without a countdown because Amp does not report an exact reset time.

### Muse Code on Windows

In **Customize → Muse Code**, choose **Sign In with Meta** and confirm the displayed code in your browser. OpenUsage checks that the account has a Muse Code subscription before saving its sign-in with Windows user encryption. Turn Muse Code on in Customize to see **Session** and **Weekly** limits; **Session** is pinned to the tray by default. Use **Sign In Again** to replace the connection without losing your layout.

### Provider details that match macOS

- **Claude:** Rate Limit Resets and a plan badge from the live profile. Resets are checked hourly; if the first check can't connect, the next refresh tries again. Usage limits still show if the resets check fails. Enterprise plans show **Spend** instead of **Extra Usage**. Max 5x shows as Max, and Max 20x shows as Ultra.
- **Codex:** Pro 100 / 200 / 500 plan names. The Credits row shows a reported balance, including zero. It is hidden if ChatGPT says the account has no credits or does not report a balance.
- **Cursor:** Unified Cursor/X/Grok plans show the dashboard's credits and additional usage. For other plans, personal budgets show **Extra Usage** and team pools show **Team Spend**. Spend history is fetched day by day and finished days are cached per account.
- **Grok:** The pay-as-you-go limit is shown in dollars.
- **Ollama:** Monthly and Purchased Credits come from the balance endpoint. Free accounts can show purchased credits without a monthly allowance.
- **OpenCode:** Add a Go API key in **Customize → OpenCode → Additional Accounts** to see live Session, Weekly, and Monthly limits without a local OpenCode install. Existing local connections still work.
- **OpenRouter:** Key Limit shows the next reset if OpenRouter reports a daily, weekly, or monthly interval. Limits reset at midnight UTC; weeks start on Monday.
- **Z.ai:** Expand an account card to see available Limit Resets and their expiries. OpenUsage checks the balance hourly and does not spend resets. A rejected API key prompts you to replace it in Customize.

Z.ai shows token windows of a day or longer on the **Weekly** row, with the reset time it reports.

For Copilot, open **Customize → Copilot**. Leave the address as `github.com`, or enter your organization's GitHub Enterprise Cloud address (for example, `octocorp.ghe.com`). Choose **Sign In with GitHub** and enter the code on the matching site. Turn on Copilot in Customize if it is off. OpenUsage saves the verified sign-in with Windows user encryption. **Sign In Again** replaces the connection without changing your card settings. Existing editor and GitHub CLI sign-ins still work if you haven't connected Copilot in OpenUsage.

For Codex, enable device code authorization in ChatGPT's security settings, then open **Customize → Codex → Sign In with ChatGPT**. Enter the code on ChatGPT's device sign-in page. OpenUsage saves the verified connection with Windows user encryption. Use **Sign In Again** to reconnect without changing your card settings. Existing Codex CLI logins still work if you haven't connected ChatGPT in OpenUsage.

Claude and Codex logins stored in Credential Manager aren't refreshed by OpenUsage. If a CLI-based session expires, run the corresponding CLI once.

Antigravity, Claude, Codex, and Grok show live limits without reading local session logs. Estimated spend and usage trends from those logs are no longer shown; Cursor's provider-reported history is unchanged.

See the [Windows README](../windows/README.md) for how to build it and which providers are wired up. The
[command-line helper](../windows/README.md#command-line) replaces the legacy loopback HTTP API and reads saved limits even when the tray app is closed.

For ChatGPT, **Rate Limit Resets** uses the latest usage response's count when available, including zero.
The CLI lists expiries from the usage response if the separate reset list disagrees.

## Priority Limits

In **Settings → Priority Limits**, choose when the dashboard and tray icon warn about low limits. The defaults are 30% left for orange and 10% left for red. A limit that's on pace to run out can still turn red earlier.

**Show Use It or Lose It** is on by default. It lists unpinned limits with more than half left that reset within 24 hours, soonest first. Sessions and spending caps are excluded. This controls only the Priority list.
