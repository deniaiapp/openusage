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

On first launch the welcome screen checks for OpenUsage sign-ins already saved on this PC. Pick the ones to follow and click **Connect Selected**, or **Skip for Now** and connect providers later in **Customize**. Closing the welcome window quits the app; it shows again next time.

If metrics are all hidden for the providers that are on, the dashboard links to **Customize** so you can turn them back on without reconnecting.

## What it reads

Connect providers in Customize. For Ollama and OpenCode Go, open **Accounts** to add API keys. For OpenRouter and Z.ai, use **Additional Accounts**.

| Provider | Windows credential sources |
|---|---|
| Amp | Device-code sign-in in Customize (Windows user encryption); does not read the Amp CLI credentials |
| Claude | **Customize → Claude → Sign In with Claude**; CLI files, Credential Manager items, and environment tokens are not read |
| Codex | **Customize → Codex → Sign In with ChatGPT** (requires device code authorization enabled in ChatGPT); CLI files and Credential Manager items are not read |
| Cursor | **Customize → Cursor → Sign In with Cursor**; the Cursor app's SQLite database and Credential Manager tokens are not read or changed |
| Copilot | **Customize → Copilot → Sign In with GitHub** (GitHub.com) |
| Grok | **Customize → Grok → Sign In with Grok**; Grok CLI credentials are not read |
| Muse Code | **Customize → Muse Code → Sign In with Meta** |
| OpenCode | Go API key in **Customize → OpenCode → Accounts**. If you relied on an OpenCode CLI sign-in, add a key here. OpenUsage no longer reads the CLI's database or auth file |
| Antigravity | **Customize → Antigravity → Sign In with Google**, or an existing OpenUsage sign-in cache. Windows Credential Manager is no longer read |
| Ollama | Cloud API key in Customize. Reads `/api/balance` for Monthly and Purchased Credits, or Session / Weekly on older plans |
| Devin | Browser sign-in from **Customize → Devin**; the session is stored by OpenUsage |
| OpenRouter / Z.ai | API key in Customize (DPAPI-protected under `%APPDATA%\OpenUsage`) |

OpenRouter management keys show Yesterday and Prior 30 Days spend when you expand the account card. These periods count only completed days in UTC.

Settings, cache, and logs live in `%APPDATA%\OpenUsage`.

Z.ai shows token windows of a day or longer on the Weekly row, with the reported reset time.


Expand Antigravity to see Argon Weekly immediately after Weekly when the account has a separate Argon quota. The CLI reports it as `argonWeekly`.

If a manual and scheduled refresh overlap for one provider, OpenUsage shares the same request
instead of renewing that sign-in twice.

If a refresh fails, OpenUsage keeps that account's last successful data and waits before automatically trying again.
Other accounts update normally. You can still retry manually.

Cursor keeps using the last known billing plan for the current sign-in if the plan lookup fails.

OpenUsage renews its own Claude and ChatGPT sign-ins without touching the CLI login. Existing CLI-only Claude and Codex connections need to sign in once in Customize.

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
The CLI does not open the app, refresh usage, or read credentials. Data becomes stale after two configured refresh intervals (ten minutes by default).
Use `--help` for usage or `--version` for the CLI build version; neither reads saved limits.
The legacy `--force` flag is accepted but ignored. Exit codes are 0 for saved data, 2 for invalid
arguments or a disabled provider or account, and 4 when there's no saved data yet.

To run the helper directly, run `.\build.ps1` and use the path printed after `CLI:`. Keep it with its
companion files and on a separate path from the tray app.

## Self-contained build

From this folder on Windows x64 with the .NET 8 SDK, publish to a directory that does not exist yet:

```powershell
.\package.ps1 -OutputDirectory ..\dist\windows-x64
..\dist\windows-x64\app\OpenUsage.exe
..\dist\windows-x64\cli\openusage.exe --version
```

Keep each executable with the files in its own folder. The target PC does not need the .NET runtime.
This is an unsigned local build, not an installer or an automatic update channel.

## Prepare the Windows release ZIP

Run [Windows CI](https://github.com/deniaiapp/openusage/actions/workflows/windows-ci.yml) manually on
the reviewed `main` commit you intend to tag. Download the `windows-x64-unsigned` artifact from the
successful run for that exact commit, and keep the ZIP inside it. The workflow does not publish a
release.

In PowerShell, set the approved tag and path to the ZIP (without the extension):

```powershell
$tag = "<approved-release-tag>"
$output = ".\dist\windows-x64-unsigned"
```

Alternatively, build from a clean checkout of the tag on Windows x64 with the .NET 8 SDK:

```powershell
$source = git rev-parse --verify "refs/tags/$tag^{commit}"
if ($LASTEXITCODE -ne 0 -or (git rev-parse HEAD) -ne $source -or (git status --porcelain)) {
    throw "Check out the exact release tag in a clean working tree."
}
.\windows\package.ps1 -OutputDirectory $output -Zip
```

With either build, check the extracted CLI's version and launch the extracted tray app:

```powershell
Expand-Archive -LiteralPath "$output.zip" -DestinationPath "$output-smoke"
& "$output-smoke\cli\openusage.exe" --version
if ($LASTEXITCODE -ne 0) { throw "The extracted CLI did not start." }
Start-Process "$output-smoke\app\OpenUsage.exe"
```

The CLI version should match the release tag, ignoring the leading `v` and the CLI's `+commit` suffix.

For a new **stable Windows release**, use the approved tag on the reviewed commit and include
non-empty Windows release notes. Verify the tag's commit before publishing. This does not run the
macOS release workflow or update Sparkle. Carry forward the upstream v0.6.28 update manifest unchanged
so legacy Tauri clients can still use `releases/latest/download/latest.json`.

```powershell
$legacy = Join-Path (Split-Path $output) 'latest.json'
gh release download v0.6.28 --pattern latest.json --output $legacy --repo robinebers/openusage
if ($LASTEXITCODE -ne 0) { throw "Could not download the legacy update manifest." }
gh release create $tag "${output}.zip#Windows x64 (unsigned ZIP)" $legacy --verify-tag --latest --title "OpenUsage for Windows $tag" --notes-file .\release-notes.md --repo deniaiapp/openusage
```

If the matching [GitHub Release](https://github.com/deniaiapp/openusage/releases) is already published,
attach the ZIP instead. Do not replace an existing asset:

```powershell
$isDraft = gh release view $tag --repo deniaiapp/openusage --json isDraft --jq '.isDraft'
if ($LASTEXITCODE -ne 0 -or $isDraft -ne 'false') { throw "No published release for $tag." }
gh release upload $tag "${output}.zip#Windows x64 (unsigned ZIP)" --repo deniaiapp/openusage
```

## Not in this port yet

macOS-only pieces left out on purpose:

- iCloud spend sync
- Sparkle auto-update
- Share-screenshot cards

You can [connect and disconnect CLIProxyAPI hubs](../docs/windows.md#cliproxyapi-hubs) in Customize.
The app keeps supported hub logins in sync at startup, on demand, and on scheduled refreshes.
Failed checks appear next to the hub in Customize. The core can read usage through the hub, but provider refreshes and the dashboard do not use it yet.
Management requests use HTTPS, or HTTP for loopback, local network, and Tailscale addresses, without following redirects.

Proxy support still honors `~\.openusage\config.json` (or `%APPDATA%\OpenUsage\config.json`) with a `proxy` URL.
