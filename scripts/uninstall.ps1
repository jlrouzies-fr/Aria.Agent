#Requires -Version 5.1

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge uninstaller — retro cogitator node style
# ═══════════════════════════════════════════════════════════════════════════════

$InstallDir = Join-Path $env:LOCALAPPDATA "Aria\bridge"
$BinName = "aria-bridge.exe"

# ── ANSI palette ─────────────────────────────────────────────────────────────
$ESC = [char]0x1b
$C_RESET = "${ESC}[0m"
$C_BOLD = "${ESC}[1m"
$C_DIM = "${ESC}[2m"
$C_RED = "${ESC}[38;2;255;80;48m"
$C_AMBER = "${ESC}[38;2;255;140;0m"
$C_GOLD = "${ESC}[38;2;212;160;32m"
$C_BLOOD = "${ESC}[38;2;204;61;0m"
$C_MUTED = "${ESC}[38;2;204;112;80m"
$C_DIMRED = "${ESC}[38;2;139;32;16m"

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
    Write-Host "${C_RED}╔══════════════════════════════════════════════════════════════════════════════╗${C_RESET}"
    Write-Host "${C_RED}║${C_RESET}                                                                              ${C_RED}║${C_RESET}"
    Write-Host "${C_RED}║${C_RESET}   ${C_BOLD}${C_RED}▓▓▒▒░░ ARIA // COGITATOR NODE UNINSTALLER ░░▒▒▓▓${C_RESET}                            ${C_RED}║${C_RESET}"
    Write-Host "${C_RED}║${C_RESET}   ${C_MUTED}loopback: 127.0.0.1:5741${C_RESET}                                                   ${C_RED}║${C_RESET}"
    Write-Host "${C_RED}║${C_RESET}                                                                              ${C_RED}║${C_RESET}"
    Write-Host "${C_RED}╚══════════════════════════════════════════════════════════════════════════════╝${C_RESET}"
}

Write-Header

# ── Detect running bridge ────────────────────────────────────────────────────
Write-Section -Title "PROCESS CHECK"
$Processes = Get-Process -Name "aria-bridge" -ErrorAction SilentlyContinue
if ($Processes) {
    Write-Warning "aria-bridge appears to be running."
    Write-Info "Stop it before uninstalling to avoid file-in-use errors."
    Write-Info "The uninstaller will not kill a running process."
} else {
    Write-Success "No running aria-bridge process detected."
}

# ── Remove installed files ───────────────────────────────────────────────────
Write-Section -Title "REMOVING FILES"
if (Test-Path $InstallDir) {
    Write-Info "Removing ${InstallDir}"
    Remove-Item -Recurse -Force $InstallDir
    Write-Success "Install directory removed."
} else {
    Write-Warning "Install directory not found: ${InstallDir}"
}

# ── Remove from user PATH ────────────────────────────────────────────────────
Write-Section -Title "REMOVING PATH ENTRY"
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -like "*${InstallDir}*") {
    Write-Info "Removing ${InstallDir} from user PATH"
    $NewPath = ($UserPath -split ';' | Where-Object { $_ -ne $InstallDir }) -join ';'
    [Environment]::SetEnvironmentVariable("Path", $NewPath, "User")
    Write-Success "PATH entry removed."
} else {
    Write-Info "Install directory not in user PATH"
}

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Rule -Label "COMPLETE"
Write-Success "aria-bridge has been uninstalled."
