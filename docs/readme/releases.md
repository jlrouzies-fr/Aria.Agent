# Bridge Releases

The Aria Bridge is distributed as self-contained single-file executables via GitHub Releases.

## Install the bridge

Pick the one-liner for your platform. The installer detects your OS/architecture, downloads the latest release, and adds `aria-bridge` to your PATH.

### macOS / Linux

```bash
curl -fsSL https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/install.sh | bash
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/install.ps1 | iex
```

## Update the bridge

Run the same installer command again. It overwrites the local binary while preserving your soul data and sessions.

If something is already listening on port `5741`, the installer **stops it by default** so the new version can take over. It tells you which process it is (installed binary, `dotnet run` debug session, or something else) before stopping it.

To leave a running process alone, pass `--no-kill` (bash) or `-NoKill` (PowerShell):

```bash
# macOS / Linux — leave whatever is on port 5741 running
curl -fsSL https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/install.sh | bash -s -- --no-kill

# Windows (PowerShell) — download first so you can pass -NoKill
irm https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/install.ps1 -OutFile install.ps1
.\install.ps1 -NoKill
```

The web terminal also shows your running bridge version in the header. If a newer release exists, the version badge glows and opens an update modal when clicked.

## Uninstall the bridge

### macOS / Linux

Download and run the uninstall script:

```bash
curl -fsSL https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/uninstall.sh | bash
```

Or manually:

```bash
pkill -f "aria-bridge"
rm -rf ~/.local/lib/aria-agent
rm -f ~/.local/bin/aria-bridge
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/jlrouzies-fr/Aria.Agent/main/scripts/uninstall.ps1 | iex
```

Or manually:

```powershell
$InstallDir = Join-Path $env:LOCALAPPDATA "Aria\bridge"
Get-Process aria-bridge -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force $InstallDir
```

> **Note:** the uninstaller removes the bridge binary and its working directory, which includes your local SQLite database (soul, sessions, history). Back it up first if you want to keep it.

## Manual build

If you prefer to build from source:

```bash
cd src/AriaAgent
dotnet run --project Aria.Bridge
```

The bridge binds to [http://localhost:5741](http://localhost:5741).

## How releases work

Pushing a tag matching `bridge-v*` triggers `.github/workflows/release-bridge.yml`. The workflow:

1. Verifies the tag matches `<Version>` in `Aria.Bridge.csproj`.
2. Builds self-contained single-file executables for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`.
3. Creates a GitHub Release and uploads the platform zip files plus `install.sh`, `install.ps1`, `uninstall.sh`, and `uninstall.ps1`.
4. Updates the public install gist so the one-liner commands always point to the latest scripts.

To cut a release, bump `<Version>` in `src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj` and push the matching tag:

```bash
git tag bridge-v$(grep -oP '(?<=<Version>)[^<]+' src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj)
git push origin bridge-v<version>
```