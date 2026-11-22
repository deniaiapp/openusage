# OpenUsage for Windows

Native Windows port of [OpenUsage](https://github.com/robinebers/openusage): a system-tray app that shows AI provider usage (Claude, Codex, Cursor, Copilot, Grok, OpenRouter, Z.ai, OpenCode, Devin).

The macOS app stays on Swift / SwiftUI. This folder is a separate .NET 8 WPF tray app that reuses the same provider APIs, metric vocabulary, and layout defaults.

## Run

Requires the .NET 8 SDK (the .NET 10 SDK can build it too).

```powershell
cd windows
dotnet test
dotnet run --project OpenUsage.App
```

The app lives in the notification area (system tray). Left-click the icon to open the dashboard popover. Right-click for Options.

In **Priority → Running Out**, limits with a known usage pace are ordered by time to exhaustion. Limits without a pace follow, with the lowest remaining percentage first.

In **Priority**, **Use It or Lose It** shows unpinned multi-day limits with most of their quota left and a reset within 24 hours. Turn it off in **Settings → Priority Limits**.

On first launch a welcome screen lists the AI tools already signed in on this PC. Pick the ones to follow and click **Connect Selected**, or **Skip for Now** and turn providers on later in **Customize**. Closing the welcome window quits the app; it shows again next time.

## What it reads

Unlike the macOS app, the Windows port can use local sign-ins already on the machine. For Ollama, OpenCode Go, OpenRouter, and Z.ai, use **Additional Accounts** in Customize to connect API keys.

| Provider | Windows credential sources |
|---|---|
| Amp | Device-code sign-in in Customize (Windows user encryption); does not read the Amp CLI credentials |
| Claude | `%USERPROFILE%\.claude\.credentials.json`, Windows Credential Manager (`Claude Code-credentials`), `CLAUDE_CODE_OAUTH_TOKEN` |
| Codex | **Customize → Codex → Sign In with ChatGPT** (requires device code authorization enabled in ChatGPT), or an existing Codex CLI sign-in |
| Cursor | `%APPDATA%\Cursor\User\globalStorage\state.vscdb`, Credential Manager (`cursor-access-token`) |
| Copilot | **Customize → Copilot → Sign In with GitHub** (GitHub.com), or existing editor and GitHub CLI credentials |
| Grok | `%USERPROFILE%\.grok\auth.json` |
| Muse Code | **Customize → Muse Code → Sign In with Meta** |
| OpenCode | Go API key in Customize, or `%USERPROFILE%\.local\share\opencode` (also `%LOCALAPPDATA%\opencode`): the `opencode*.db` credential table on OpenCode 2, `auth.json` on OpenCode 1 |
| Antigravity | **Customize → Antigravity → Sign In with Google**, or an existing Windows sign-in with a DPAPI-protected cache when not connected in OpenUsage |
| Ollama | Cloud API key in Customize. Reads `/api/balance` for Monthly and Purchased Credits, or Session / Weekly on older plans |
| Devin | Browser sign-in from **Customize → Devin**; the session is stored by OpenUsage |
| OpenRouter / Z.ai | API key in Customize (DPAPI-protected under `%APPDATA%\OpenUsage`) |

Settings, cache, and logs live in `%APPDATA%\OpenUsage`.

Z.ai shows token windows of a day or longer on the Weekly row, with the reported reset time.

If a manual and scheduled refresh overlap for one provider, OpenUsage shares the same request
instead of renewing that sign-in twice.

If a refresh fails, OpenUsage keeps that account's last successful data and waits before automatically trying again.
Other accounts update normally. You can still retry manually.

OpenUsage renews its own ChatGPT sign-in without touching the CLI login. For CLI-based Claude or Codex logins, OpenUsage only refreshes tokens that came from a file, keeping the rest of that file intact. Tokens from Credential Manager are left for the CLI to refresh.

When an update adds a new default metric, it's turned on once for existing layouts too. If you turn it off, it stays off.

## Command line

Windows no longer serves the legacy loopback HTTP API. Update scripts that called `/v1/limits`
or `/v1/usage` to run the CLI and read the JSON from standard output.

After refreshing a provider with this build of the tray app, read its saved limits:

```powershell
dotnet run --project OpenUsage.Cli --
dotnet run --project OpenUsage.Cli -- codex
```

The CLI emits `openusage.limits.v2` JSON for enabled providers. Selecting a provider such as `openrouter`
includes all its enabled accounts; use an exact `provider@id` from the output to select one account.
The CLI does not open the app, refresh usage, or read credentials. Data becomes stale after ten minutes.
Use `--help` for usage or `--version` for the CLI build version; neither reads saved limits.
The legacy `--force` flag is accepted but ignored. Exit codes are 0 for saved data, 2 for invalid
arguments or a disabled provider or account, and 4 when there's no saved data yet.

To run the helper directly, run `.\build.ps1` and use the path printed after `CLI:`. Keep it with its
companion files and on a separate path from the tray app.

## Not in this port yet

macOS-only pieces left out on purpose:

- iCloud spend sync
- Sparkle auto-update
- Share-screenshot cards

Proxy support still honors `~\.openusage\config.json` (or `%APPDATA%\OpenUsage\config.json`) with a `proxy` URL.
