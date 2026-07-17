#Requires -Version 5.1
param(
    [string]$RepoDir = (Join-Path $env:LOCALAPPDATA "Aria\src\Aria.Agent"),
    [string]$Branch  = "auto",
    [switch]$SkipPull,
    [switch]$NoKill,
    [switch]$NoStart
)

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge — build-from-source deployer (run & forget)
# ═══════════════════════════════════════════════════════════════════════════════
# Drop-in alternative to install.ps1. Instead of downloading a GitHub Release,
# this clones/pulls the repo, builds the bridge from source with the SAME publish
# command CI uses, then installs + starts it exactly like install.ps1:
#
#   * install dir : %LOCALAPPDATA%\Aria\bridge   (same as install.ps1)
#   * source dir  : %LOCALAPPDATA%\Aria\src\Aria.Agent   (-RepoDir to change)
#   * port 5741 cleared before start (unless -NoKill)
#   * runs hidden on success (unless -NoStart)
#
# Re-run it any time to pull latest and redeploy — that is the whole workflow.
#
# Branch selection: -Branch defaults to "auto", which fetches every branch and
# builds the one whose tip commit is the most recent (no need to name it). Pass
# -Branch <name> to pin a specific branch instead.
#
# Requires:  git  and  the .NET 10 SDK  on PATH.
#
# This is a public repository — no authentication is required. git clones and
# fetches anonymously over HTTPS.
# ═══════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"

$Repo       = "jlrouzies-fr/Aria.Agent"
$RepoUrl    = "https://github.com/${Repo}.git"
$InstallDir = Join-Path $env:LOCALAPPDATA "Aria\bridge"
$BinName    = "aria-bridge.exe"
$ProjectRel = "src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj"

# ── ANSI palette (matches install.ps1) ───────────────────────────────────────
$ESC = [char]0x1b
$C_RESET = "${ESC}[0m"
$C_BOLD = "${ESC}[1m"
$C_RED = "${ESC}[38;2;255;80;48m"
$C_AMBER = "${ESC}[38;2;255;140;0m"
$C_GOLD = "${ESC}[38;2;212;160;32m"
$C_BLOOD = "${ESC}[38;2;204;61;0m"
$C_MUTED = "${ESC}[38;2;204;112;80m"
$C_DIMRED = "${ESC}[38;2;139;32;16m"

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
Write-Host "${C_RED}╔══════════════════════════════════════════════════════════════════════════════╗"
Write-Host "${C_RED}║${C_RESET}                                                                              ${C_RED}║"
Write-Host "${C_RED}║${C_RESET}              ${C_BOLD}${C_RED}▓▓▒▒░░ ARIA // COGITATOR BRIDGE — SOURCE DEPLOY ░░▒▒▓▓${C_RESET}              ${C_RED}║"
Write-Host "${C_RED}║${C_RESET}                 ${C_MUTED}build from source  |  loopback: localhost:5741${C_RESET}                 ${C_RED}║"
Write-Host "${C_RED}║${C_RESET}                                                                              ${C_RED}║"
Write-Host "${C_RED}╚══════════════════════════════════════════════════════════════════════════════╝${C_RESET}"
}

# ── Port helpers (same behaviour as install.ps1) ──────────────────────────────
function Get-PortOccupant {
    param([int]$Port = 5741)
    try {
        $tcp = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($tcp -and $tcp.OwningProcess -and $tcp.OwningProcess -ne 0) {
            $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($tcp.OwningProcess)" -ErrorAction SilentlyContinue
            if ($proc) {
                return [PSCustomObject]@{
                    Pid = $proc.ProcessId; Name = $proc.Name
                    Path = $proc.ExecutablePath; CommandLine = $proc.CommandLine
                }
            }
        }
    } catch {}
    return $null
}
function Stop-Occupant {
    param($Occupant)
    try {
        Stop-Process -Id $Occupant.Pid -Force
        Start-Sleep -Seconds 1
        if (Get-Process -Id $Occupant.Pid -ErrorAction SilentlyContinue) {
            Write-ErrorLine "PID $($Occupant.Pid) did not stop."
        } else {
            Write-Success "Stopped."
        }
    } catch {
        Write-ErrorLine "Could not stop PID $($Occupant.Pid): $_"
    }
}
function Test-PortConflict {
    param([int]$Port = 5741)
    $occupant = Get-PortOccupant -Port $Port
    if (-not $occupant) { return }

    Write-Section -Title "PORT CHECK"
    Write-Warning "Something is already listening on port $Port."
    Write-Info "PID $($occupant.Pid): $($occupant.Name)"
    if ($occupant.Path) { Write-Info "  $($occupant.Path)" }
    if ($occupant.CommandLine) { Write-Info "  $($occupant.CommandLine)" }

    if ($NoKill) { Write-Info "Leaving it running because -NoKill was used."; return }
    Write-Warning "Stopping PID $($occupant.Pid) so the fresh build can take over..."
    Stop-Occupant -Occupant $occupant
}

# ── Prerequisites ─────────────────────────────────────────────────────────────
function Assert-Command {
    param([string]$Name, [string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-ErrorLine "'$Name' was not found on PATH."
        Write-Info $Hint
        exit 1
    }
}

# ── Git wrapper (public repo — anonymous HTTPS, no auth needed) ───────────────
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    & git @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

# ══════════════════════════════════════════════════════════════════════════════
Write-Header

Write-Rule -Label "CONFIGURATION"
$branchLabel = if ($Branch -eq "auto") { "auto (most recently updated)" } else { $Branch }
Write-Info "Repository   : $Repo (branch: $branchLabel)"
Write-Info "Source dir   : $RepoDir"
Write-Info "Install dir  : $InstallDir"
Write-Info "Binary       : $(Join-Path $InstallDir $BinName)"

Write-Section -Title "PREREQUISITES"
Assert-Command -Name "git" -Hint "Install Git for Windows: https://git-scm.com/download/win"
Assert-Command -Name "dotnet" -Hint "Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
$DotnetVersion = (& dotnet --version).Trim()
Write-Info "git    : $((& git --version))"
Write-Info "dotnet : $DotnetVersion"
if ($DotnetVersion -notmatch '^(1[0-9]|[2-9][0-9])\.') {
    Write-Warning "This project targets net10.0 — a .NET 10+ SDK is required. Found $DotnetVersion."
    Write-Warning "The build will likely fail. Install the .NET 10 SDK and re-run."
}

# ── Platform / RID ────────────────────────────────────────────────────────────
$Rid = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "win-x64" }
    "ARM64" { "win-arm64" }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}
Write-Info "Runtime ID : $Rid"

# ── Pick the most recently updated remote branch (auto mode) ──────────────────
function Resolve-TargetBranch {
    # Reads local remote-tracking refs (populated by the fetch/clone above),
    # sorts by tip commit date, returns the newest branch name (no origin/ prefix).
    $lines = & git for-each-ref --sort=-committerdate refs/remotes/origin `
        --format='%(refname:short)|%(committerdate:iso8601)'
    foreach ($line in $lines) {
        if (-not $line) { continue }
        $parts = $line -split '\|', 2
        $name  = $parts[0]
        if ($name -eq 'origin/HEAD' -or $name -eq 'origin') { continue }
        $short = $name -replace '^origin/', ''
        return [PSCustomObject]@{ Name = $short; Date = $parts[1] }
    }
    return $null
}

# ── Clone or update ───────────────────────────────────────────────────────────
Write-Section -Title "SOURCE"
$GitDir = Join-Path $RepoDir ".git"

# Full-branch refspec so 'auto' can compare every branch (not just one).
$AllBranches = '+refs/heads/*:refs/remotes/origin/*'

if (-not (Test-Path $GitDir)) {
    Write-Info "Cloning $Repo into $RepoDir (all branches) ..."
    $parent = Split-Path -Parent $RepoDir
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Invoke-Git clone $RepoUrl $RepoDir
} elseif (-not $SkipPull) {
    Write-Info "Fetching all branches from origin ..."
    Push-Location $RepoDir
    try {
        Invoke-Git remote set-url origin $RepoUrl
        Invoke-Git fetch --prune origin $AllBranches
    } finally { Pop-Location }
} else {
    Write-Info "Existing checkout found; -SkipPull set — skipping fetch."
}

Push-Location $RepoDir
try {
    if ($SkipPull) {
        # Build whatever is currently checked out, untouched.
        $Target = (& git rev-parse --abbrev-ref HEAD).Trim()
        Write-Info "Building current branch: $Target"
    } else {
        if ($Branch -eq "auto") {
            $picked = Resolve-TargetBranch
            if (-not $picked) { throw "Could not enumerate remote branches to auto-select one." }
            $Target = $picked.Name
            Write-Success "Most recently updated branch: $Target  ($($picked.Date))"
        } else {
            $Target = $Branch
        }
        Write-Info "Checking out origin/$Target (hard reset, discards local edits)..."
        Invoke-Git checkout -B $Target "origin/$Target" --quiet
        Invoke-Git reset --hard "origin/$Target"
    }
    Write-Success "At $((& git rev-parse --short HEAD)) — $((& git log -1 --pretty=%s))"
} finally { Pop-Location }

# ── Build (mirrors .github/workflows/release-bridge.yml) ───────────────────────
Write-Section -Title "BUILDING"
$ProjectPath = Join-Path $RepoDir $ProjectRel
if (-not (Test-Path $ProjectPath)) { throw "Project not found: $ProjectPath" }

$PublishDir = Join-Path $env:TEMP "aria-bridge-publish\$Rid"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

Write-Info "dotnet publish -c Release -r $Rid --self-contained true -p:PublishSingleFile=true"
Write-Info "(first build downloads NuGet packages and can take a few minutes)"
& dotnet publish $ProjectPath `
    -c Release `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { Write-ErrorLine "dotnet publish failed (exit $LASTEXITCODE)."; exit 1 }

$BuiltBin = Join-Path $PublishDir $BinName
if (-not (Test-Path $BuiltBin)) {
    Write-ErrorLine "Build succeeded but $BinName was not found in $PublishDir."
    exit 1
}
Write-Success "Built $((& $BuiltBin --version 2>$null))"

# ── Stop running instance, install, start ─────────────────────────────────────
Test-PortConflict

Write-Section -Title "INSTALLING"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if (Test-Path "$InstallDir\*") { Remove-Item -Recurse -Force "$InstallDir\*" }
Copy-Item -Path "$PublishDir\*" -Destination $InstallDir -Recurse -Force
Write-Success "Installed to $InstallDir"

$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*${InstallDir}*") {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$InstallDir", "User")
    Write-Success "Added $InstallDir to your user PATH. Restart your terminal to use 'aria-bridge'."
} else {
    Write-Info "Install directory already in user PATH."
}

$BridgeExe = Join-Path $InstallDir $BinName
if ($NoStart) {
    Write-Rule -Label "DONE"
    Write-Info "Start it later with: aria-bridge"
    Write-Info "Status page: http://localhost:5741"
    return
}

Write-Section -Title "STARTING"
try {
    Start-Process -FilePath $BridgeExe -WorkingDirectory $InstallDir -WindowStyle Hidden
    Write-Success "Started aria-bridge."
    Write-Info "Status page: http://localhost:5741"
} catch {
    Write-ErrorLine "Could not start aria-bridge: $_"
    Write-Info "Start it manually: aria-bridge"
}
