#Requires -Version 5.1
# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge version bump + release tag
# ═══════════════════════════════════════════════════════════════════════════════
# Usage: .\bump-bridge.ps1 [major|minor|fix] [-Yes]
#
# Bumps the bridge version in Aria.Bridge.csproj, commits the change, creates
# the matching bridge-v* tag, and pushes both.
# ═══════════════════════════════════════════════════════════════════════════════

param(
    [Parameter(Position = 0)]
    [ValidateSet("major", "minor", "fix")]
    [string]$Bump = "fix",

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

if ($Bump -notin @("major", "minor", "fix")) {
    Write-ErrorLine "Invalid bump type: $Bump"
    Write-Info "Usage: .\bump-bridge.ps1 [major|minor|fix] [-Yes]"
    exit 1
}

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

$diff = git diff --quiet HEAD -- $Csproj 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-ErrorLine "$Csproj has uncommitted changes"
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

[int]$Major = $matches[1]
[int]$Minor = $matches[2]
[int]$Fix = $matches[3]
[string]$Suffix = $matches[4]

Write-Info "Current bridge version: $Current"

# ── Compute new version ──────────────────────────────────────────────────────
switch ($Bump) {
    "major" { $Major++; $Minor = 0; $Fix = 0 }
    "minor" { $Minor++; $Fix = 0 }
    "fix"   { $Fix++ }
}

$NewVersion = "$Major.$Minor.$Fix$Suffix"
$AssemblyVersion = "$Major.$Minor.$Fix.0"
$Tag = "bridge-v$NewVersion"

Write-Info "New bridge version: $NewVersion"
Write-Info "Git tag: $Tag"

# ── Check for existing tag ───────────────────────────────────────────────────
$existing = git rev-parse --verify --quiet "refs/tags/$Tag" 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-ErrorLine "Tag $Tag already exists"
    exit 1
}

# ── Update csproj ────────────────────────────────────────────────────────────
$content = Get-Content $Csproj -Raw
$content = $content -replace '(?<=<Version>)[^<]+', $NewVersion
$content = $content -replace '(?<=<AssemblyVersion>)[^<]+', $AssemblyVersion
$content = $content -replace '(?<=<FileVersion>)[^<]+', $AssemblyVersion
Set-Content $Csproj $content -NoNewline

Write-Success "Updated $Csproj"

# ── Confirm ──────────────────────────────────────────────────────────────────
if (-not $Yes) {
    Write-Host ""
    git diff -- $Csproj
    Write-Host ""
    $reply = Read-Host "Commit and push tag $Tag? [y/N]"
    if ($reply -notmatch '^[Yy]$') {
        Write-WarningLine "Aborted. Reverting changes..."
        git checkout -- $Csproj
        exit 0
    }
}

# ── Commit, tag, push ────────────────────────────────────────────────────────
git add $Csproj
git commit -m "chore: bump bridge to v$NewVersion"
git tag $Tag
git push origin $CurrentBranch
git push origin $Tag

Write-Success "Released $Tag from branch $CurrentBranch"
