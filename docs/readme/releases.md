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

## Docker image

> **⚠️ Docker is a convenience option, not the intended experience.** The bridge is designed to run directly on your machine so (if configured) it can access your files, shell, and OS-specific key storage. Running inside a Linux container limits or might breaks several of those features.

A pre-built image is published to GitHub Container Registry. Mount the same folder that the direct install uses so your vault, soul, and history persist:

### macOS

```bash
docker run -d --name aria-bridge \
  -p 5741:5741 \
  -v "~/Library/Application Support/aria-bridge:/home/app/.config/aria-bridge" \
  ghcr.io/jlrouzies-fr/aria.agent/aria-bridge:latest
```

### Linux

```bash
docker run -d --name aria-bridge \
  -p 5741:5741 \
  -v ~/.config/aria-bridge:/home/app/.config/aria-bridge \
  ghcr.io/jlrouzies-fr/aria.agent/aria-bridge:latest
```

### Windows (PowerShell)

```powershell
docker run -d --name aria-bridge `
  -p 5741:5741 `
  -v "C:\Users\$env:username\AppData\Roaming\aria-bridge:/home/app/.config/aria-bridge" `
  ghcr.io/jlrouzies-fr/aria.agent/aria-bridge:latest
```

The image:
- Exposes port `5741`.
- Stores the SQLite vault in `/home/app/.config/aria-bridge`.
- Is available for `linux/amd64` and `linux/arm64`.
- Is published manually via `.github/workflows/docker-bridge.yml`. The default run pushes `:latest`; enter a version (e.g. `1.25.8-beta`) to also tag that version and update `:latest`.

### Limitations

- **Always Linux inside the container.** **Docker** runs the bridge as a Linux process regardless of your host OS. Terminal commands, file tools, and Agent Projects are only accessible if you manually bind-mount the relevant host folders.
- **Purely Windows-dependent projects will not work.** For example, projects targeting .NET Framework or relying on Windows-only tooling cannot run in the Linux ecosystem inside the container.
- **OS-specific vault encryption differs.** The Windows build encrypts sensitive values with Windows DPAPI; the Linux container uses a file-based key. This means a vault created by the Windows direct install cannot be read inside the Docker container, and vice versa. Use one or the other for a given soul.
- **Recommended:** use the direct install (`install.sh` / `install.ps1`) for full integration. Use Docker only when a headless, isolated bridge is acceptable.

To pull a specific version:

```bash
docker pull ghcr.io/jlrouzies-fr/aria.agent/aria-bridge:1.25.8-beta
```

The direct build (`dotnet run --project Aria.Bridge`) and the single-file executables continue to bind to `localhost:5741` by default; only the Docker image binds broadly so that the host port mapping works.

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

The Docker image is published separately and manually via `.github/workflows/docker-bridge.yml`.

To cut a release, bump `<Version>` in `src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj` and push the matching tag:

```bash
git tag bridge-v$(grep -oP '(?<=<Version>)[^<]+' src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj)
git push origin bridge-v<version>
```