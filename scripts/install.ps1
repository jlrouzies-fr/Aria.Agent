#Requires -Version 5.1
param(
    [string]$Version = "latest",
    [switch]$NoKill
)

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge installer — retro cogitator node style
# ═══════════════════════════════════════════════════════════════════════════════

$Repo = "jlrouzies-fr/Aria.Agent"
$InstallDir = Join-Path $env:LOCALAPPDATA "Aria\bridge"
$BinName = "aria-bridge.exe"

# ── ANSI palette ─────────────────────────────────────────────────────────────
$ESC = [char]0x1b
$C_RESET = "${ESC}[0m"
$C_BOLD = "${ESC}[1m"
$C_DIM = "${ESC}[2m"
$C_RED = "${ESC}[38;2;255;80;48m"      # Phosphor red
$C_AMBER = "${ESC}[38;2;255;140;0m"    # Amber
$C_GOLD = "${ESC}[38;2;212;160;32m"    # Gold
$C_BLOOD = "${ESC}[38;2;204;61;0m"     # Blood red
$C_MUTED = "${ESC}[38;2;204;112;80m"   # Muted
$C_DIMRED = "${ESC}[38;2;139;32;16m"   # Dim red

# ── Render helpers ───────────────────────────────────────────────────────────
function Write-Section {
    param([string]$Title)
    $width = 78
    $line = '─' * ($width - 2)
    $padded = $Title.PadRight($width - 4)
    Write-Host "${C_RED}┌${line}┐${C_RESET}"
    Write-Host "${C_RED}│ ${C_BOLD}${C_AMBER}${padded}${C_RESET}${C_RED} │${C_RESET}"
    Write-Host "${C_RED}└${line}┘${C_RESET}"
}

function Write-Rule {
    param([string]$Label = "")
    $width = 78
    if ($Label) {
        $pad = $width - $Label.Length - 4
        $right = '─' * $pad
        Write-Host "${C_DIMRED}── ${C_BOLD}${C_RED}${Label}${C_RESET}${C_DIMRED} ${right}${C_RESET}"
    } else {
        Write-Host "${C_DIMRED}$('─' * $width)${C_RESET}"
    }
}

function Write-Info { param([string]$Text) Write-Host "${C_MUTED}> ${Text}${C_RESET}" }
function Write-Success { param([string]$Text) Write-Host "${C_AMBER}✓ ${Text}${C_RESET}" }
function Write-Warning { param([string]$Text) Write-Host "${C_GOLD}! ${Text}${C_RESET}" }
function Write-ErrorLine { param([string]$Text) Write-Host "${C_BLOOD}✗ ${Text}${C_RESET}" }

function Write-Header {
    param([string]$Version)
write-host "${C_RED}╔══════════════════════════════════════════════════════════════════════════════╗"
write-host "${C_RED}║${C_RESET}                                                                              ${C_RED}║"
write-host "${C_RED}║${C_RESET}               ${C_BOLD}${C_RED}▓▓▒▒░░ ARIA // COGITATOR BRIDGE INSTALLER ░░▒▒▓▓${C_RESET}               ${C_RED}║"
write-host "${C_RED}║${C_RESET}                 ${C_MUTED}version: ${version}  |  loopback: localhost:5741${C_RESET}                 ${C_RED}║"
write-host "${C_RED}║${C_RESET}                                                                              ${C_RED}║"
write-host "${C_RED}╚══════════════════════════════════════════════════════════════════════════════╝${C_RESET}"
}

# ── Argument handling is done via param() block above ─────────────────────────

Write-Header -Version $Version

Write-Rule -Label "CONFIGURATION"
Write-Info "Repository: $Repo"
Write-Info "Target directory: $InstallDir"
Write-Info "Binary: $(Join-Path $InstallDir $BinName)"
if ($NoKill) {
    Write-Warning "No-kill mode — any process on port 5741 will be left running"
} else {
    Write-Info "Port 5741 will be cleared before installation if occupied"
}

# ── Platform detection ───────────────────────────────────────────────────────
$Arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "x64" }
    "ARM64" { "arm64" }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}

$AssetName = "aria-bridge-win-${Arch}.zip"
Write-Info "Platform: win-$Arch"
Write-Info "Expected asset: $AssetName"

function Get-PortOccupant {
    param([int]$Port = 5741)

    try {
        $tcp = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($tcp -and $tcp.OwningProcess -and $tcp.OwningProcess -ne 0) {
            $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($tcp.OwningProcess)" -ErrorAction SilentlyContinue
            if ($proc) {
                return [PSCustomObject]@{
                    Pid = $proc.ProcessId
                    Name = $proc.Name
                    Path = $proc.ExecutablePath
                    CommandLine = $proc.CommandLine
                }
            }
        }
    } catch {}
    return $null
}

function Test-PortConflict {
    param([int]$Port = 5741)

    $occupant = Get-PortOccupant -Port $Port
    if (-not $occupant) { return }

    Write-Section -Title "PORT CHECK"
    Write-Warning "Something is already listening on port $Port."
    Write-Info "PID $($occupant.Pid): $($occupant.Name)"
    if ($occupant.Path) {
        Write-Info "  $($occupant.Path)"
    }
    if ($occupant.CommandLine) {
        Write-Info "  $($occupant.CommandLine)"
    }

    $isInstalled = ($occupant.Name -eq "aria-bridge.exe") -or
                   ($occupant.Path -like "*\Aria\bridge\aria-bridge.exe") -or
                   ($occupant.CommandLine -like "*aria-bridge.exe*")

    $isDotnet = ($occupant.Name -eq "dotnet.exe") -or
                ($occupant.CommandLine -like "*Aria.Bridge*")

    if ($isInstalled) {
        Write-Info "This looks like the installed aria-bridge binary."
        if (-not $NoKill) {
            Write-Warning "Stopping PID $($occupant.Pid) so the new version can take over..."
            try {
                Stop-Process -Id $occupant.Pid -Force
                Start-Sleep -Seconds 1
                $still = Get-Process -Id $occupant.Pid -ErrorAction SilentlyContinue
                if ($still) {
                    Write-ErrorLine "PID $($occupant.Pid) did not stop."
                } else {
                    Write-Success "Stopped."
                }
            } catch {
                Write-ErrorLine "Could not stop PID $($occupant.Pid): $_"
            }
        } else {
            Write-Info "Leaving it running because -NoKill was used."
        }
    } elseif ($isDotnet) {
        Write-Info "This looks like a 'dotnet run' debug session."
        if (-not $NoKill) {
            Write-Warning "Stopping the debug session so the installed binary can use port $Port..."
            try {
                Stop-Process -Id $occupant.Pid -Force
                Start-Sleep -Seconds 1
                $still = Get-Process -Id $occupant.Pid -ErrorAction SilentlyContinue
                if ($still) {
                    Write-ErrorLine "PID $($occupant.Pid) did not stop."
                } else {
                    Write-Success "Stopped."
                }
            } catch {
                Write-ErrorLine "Could not stop PID $($occupant.Pid): $_"
            }
        } else {
            Write-Info "Leaving it running because -NoKill was used."
        }
    } else {
        Write-Warning "This does not look like aria-bridge."
        if (-not $NoKill) {
            Write-Warning "Stopping unknown PID $($occupant.Pid) to free port $Port..."
            try {
                Stop-Process -Id $occupant.Pid -Force
                Start-Sleep -Seconds 1
                $still = Get-Process -Id $occupant.Pid -ErrorAction SilentlyContinue
                if ($still) {
                    Write-ErrorLine "PID $($occupant.Pid) did not stop."
                } else {
                    Write-Success "Stopped."
                }
            } catch {
                Write-ErrorLine "Could not stop PID $($occupant.Pid): $_"
            }
        } else {
            Write-Info "Leaving it running because -NoKill was used."
        }
    }
}

# Resolve a download URL for the given tag ("latest" or "bridge-vX.Y.Z").
# Queries the public GitHub API (no auth needed) so prerelease tags are
# still found — the /releases/latest endpoint excludes prereleases, and
# bridge releases are prerelease until declared stable.
function Resolve-DownloadUrl {
    param([string]$Tag)

    if ($Tag -eq "latest") {
        $ReleaseUrl = "https://api.github.com/repos/${Repo}/releases"
    } else {
        $ReleaseUrl = "https://api.github.com/repos/${Repo}/releases/tags/${Tag}"
    }

    Write-Info "GitHub API URL: $ReleaseUrl"

    try {
        $Response = Invoke-WebRequest -Uri $ReleaseUrl -UseBasicParsing -MaximumRedirection 10
    } catch {
        Write-ErrorLine "GitHub API request failed"
        Write-Info "Status: $($_.Exception.Response.StatusCode.value__)"
        Write-Info "Exception: $_"
        throw
    }

    Write-Info "GitHub API response status: $($Response.StatusCode)"

    if ($Tag -eq "latest") {
        $Releases = $Response.Content | ConvertFrom-Json
        if (-not $Releases) {
            throw "No releases found"
        }
        $Release = $Releases | Sort-Object published_at -Descending | Select-Object -First 1
        Write-Info "Selected latest release (newest by published_at)"
    } else {
        $Release = $Response.Content | ConvertFrom-Json
    }

    Write-Info "Release tag: $($Release.tag_name)"
    Write-Info "Release prerelease: $($Release.prerelease)"
    Write-Info "Release published_at: $($Release.published_at)"

    Write-Info "Assets in release:"
    $Release.assets | ForEach-Object { Write-Info "  - $($_.name)" }

    $Asset = $Release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $Asset) {
        throw "Could not find asset ${AssetName} in release ${Tag}"
    }

    Write-Info "Asset download URL: $($Asset.browser_download_url)"
    return $Asset.browser_download_url
}

$Tag = if ($Version -eq "latest") { "latest" } else { "bridge-v${Version}" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Test-PortConflict

Write-Section -Title "RESOLVING DOWNLOAD URL"
$DownloadUrl = Resolve-DownloadUrl -Tag $Tag

Write-Section -Title "DOWNLOADING"
Write-Info "Target: $InstallDir"
Write-Info "Download URL: $DownloadUrl"

$TempZip = Join-Path $env:TEMP $AssetName
Write-Info "Saving to: $TempZip"

# Ensure modern TLS is negotiated.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

Write-Info "Downloading..."
Invoke-WebRequest -Uri $DownloadUrl -OutFile $TempZip -UseBasicParsing -TimeoutSec 120 -MaximumRedirection 10

$FileSize = (Get-Item $TempZip).Length
Write-Success "Downloaded archive size: $FileSize bytes"

Write-Section -Title "EXTRACTING"
$TempExtract = Join-Path $env:TEMP "aria-bridge-extract"
if (Test-Path $TempExtract) {
    Remove-Item -Recurse -Force $TempExtract
}
Expand-Archive -Path $TempZip -DestinationPath $TempExtract -Force
Remove-Item $TempZip

$SourceDir = $TempExtract
$RootEntries = Get-ChildItem -Path $TempExtract -Force
Write-Info "Archive root entries: $($RootEntries.Count) (directories: $($RootEntries.Where({$_.PSIsContainer}).Count))"
if ($RootEntries.Count -eq 1 -and $RootEntries[0].PSIsContainer) {
    $SourceDir = $RootEntries[0].FullName
    Write-Info "Using nested source directory: $SourceDir"
}

$BinPath = Get-ChildItem -Path $SourceDir -Recurse -Filter $BinName -File | Select-Object -First 1
if (-not $BinPath) {
    Write-ErrorLine "Could not find ${BinName} inside the downloaded archive."
    Get-ChildItem -Path $TempExtract -Recurse | ForEach-Object { Write-Info "  $_" }
    throw "Could not find ${BinName} inside the downloaded archive."
}

Write-Success "Binary found at: $($BinPath.FullName)"

Write-Section -Title "INSTALLING"
Remove-Item -Recurse -Force $InstallDir\*
Copy-Item -Path "$SourceDir\*" -Destination $InstallDir -Recurse -Force

$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*${InstallDir}*") {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$InstallDir", "User")
    Write-Success "Added ${InstallDir} to your user PATH. Restart your terminal to use 'aria-bridge'."
} else {
    Write-Info "Install directory already in user PATH"
}

Write-Section -Title "VERIFYING"
$InstalledVersion = & (Join-Path $InstallDir $BinName) --version
Write-Success "aria-bridge ${InstalledVersion} installed to $(Join-Path $InstallDir $BinName)"

Write-Rule -Label "AUTO-START"
Write-Info "aria-bridge will start automatically in 10 seconds..."
for ($i = 10; $i -gt 0; $i--) {
    Write-Host -NoNewline "${C_MUTED}> Starting in ${i}...${C_RESET}`r"
    Start-Sleep -Seconds 1
}
Write-Host ""

$BridgeExe = Join-Path $InstallDir $BinName
try {
    Start-Process -FilePath $BridgeExe -WorkingDirectory $InstallDir -WindowStyle Hidden
    Write-Success "Started aria-bridge."
    Write-Info "Open the status page: http://localhost:5741"
} catch {
    Write-ErrorLine "Could not start aria-bridge: $_"
    Write-Info "Start it manually: aria-bridge"
    Write-Info "Open the status page: http://localhost:5741"
}
