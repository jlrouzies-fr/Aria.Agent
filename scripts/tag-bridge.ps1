#Requires -Version 5.1
# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge release tag (no version bump)
# ═══════════════════════════════════════════════════════════════════════════════
# Usage: .\tag-bridge.ps1 [-Yes]
#
# Creates and pushes a bridge-v* tag for the CURRENT version already committed
# in Aria.Bridge.csproj. Use this when the version was bumped separately (or
# already matches what you want released) and you just need the tag.
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [switch]$Yes
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\.."
$Csproj = Join-Path $RepoRoot "src\AriaAgent\Aria.Bridge\Aria.Bridge.csproj"

Set-Location $RepoRoot

# ── ANSI palette ─────────────────────────────────────────────────────────────
$ESC = [char]0x1b
$C_RESET = "${ESC}[0m"
$C_RED = "${ESC}[38;2;255;80;48m"
$C_AMBER = "${ESC}[38;2;255;140;0m"
$C_GREEN = "${ESC}[38;2;128;255;128m"
$C_MUTED = "${ESC}[38;2;204;112;80m"

function Write-Info { param([string]$Text) Write-Host "${C_MUTED}> ${Text}${C_RESET}" }
function Write-Success { param([string]$Text) Write-Host "${C_GREEN}✓ ${Text}${C_RESET}" }
function Write-WarningLine { param([string]$Text) Write-Host "${C_AMBER}! ${Text}${C_RESET}" }
function Write-ErrorLine { param([string]$Text) Write-Host "${C_RED}✗ ${Text}${C_RESET}" }

# ── Preconditions ────────────────────────────────────────────────────────────
if (-not (Test-Path $Csproj)) {
    Write-ErrorLine "Could not find $Csproj"
    exit 1
}

$null = git rev-parse --git-dir 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-ErrorLine "Not inside a git repository"
    exit 1
}

$CurrentBranch = git branch --show-current
Write-Info "Current branch: $CurrentBranch"

# ── Parse current version ────────────────────────────────────────────────────
[xml]$Xml = Get-Content $Csproj
$Current = $Xml.Project.PropertyGroup.Version

if ($Current -notmatch '^([0-9]+)\.([0-9]+)\.([0-9]+)(-.*)?$') {
    Write-ErrorLine "Could not parse current version: $Current"
    exit 1
}

Write-Info "Current bridge version: $Current"

$Tag = "bridge-v$Current"
Write-Info "Git tag: $Tag"

# ── Check for existing tag ───────────────────────────────────────────────────
$existing = git rev-parse --verify --quiet "refs/tags/$Tag" 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-ErrorLine "Tag $Tag already exists"
    exit 1
}

# ── Confirm ──────────────────────────────────────────────────────────────────
if (-not $Yes) {
    Write-Host ""
    $shortSha = git rev-parse --short HEAD
    $reply = Read-Host "Create and push tag $Tag at ${shortSha}? [y/N]"
    if ($reply -notmatch '^[Yy]$') {
        Write-WarningLine "Aborted."
        exit 0
    }
}

# ── Tag, push ────────────────────────────────────────────────────────────────
git tag $Tag
git push origin $Tag

Write-Success "Released $Tag from branch $CurrentBranch"
