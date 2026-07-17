#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge version bump + release tag
# ═══════════════════════════════════════════════════════════════════════════════
# Usage: bump-bridge.sh [major|minor|fix] [-y|--yes]
#
# Bumps the bridge version in Aria.Bridge.csproj, commits the change, creates
# the matching bridge-v* tag, and pushes both.
# ═══════════════════════════════════════════════════════════════════════════════

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CSPROJ="${REPO_ROOT}/src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj"

cd "$REPO_ROOT"

# ── ANSI palette ─────────────────────────────────────────────────────────────
C_RESET=$'\033[0m'
C_BOLD=$'\033[1m'
C_RED=$'\033[38;2;255;80;48m'
C_AMBER=$'\033[38;2;255;140;0m'
C_GREEN=$'\033[38;2;128;255;128m'
C_MUTED=$'\033[38;2;204;112;80m'

info()    { printf "${C_MUTED}> %s${C_RESET}\n" "$1"; }
success() { printf "${C_GREEN}✓ %s${C_RESET}\n" "$1"; }
warning() { printf "${C_AMBER}! %s${C_RESET}\n" "$1"; }
error()   { printf "${C_RED}✗ %s${C_RESET}\n" "$1" >&2; }

# ── Argument parsing ─────────────────────────────────────────────────────────
BUMP_TYPE="fix"
AUTO_YES=false

while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes)
            AUTO_YES=true
            shift
            ;;
        -h|--help)
            cat <<EOF
Usage: $(basename "$0") [major|minor|fix] [-y|--yes]

  major   bump the major component (resets minor and fix to 0)
  minor   bump the minor component (resets fix to 0)
  fix     bump the fix component (default)
  -y      skip the confirmation prompt
EOF
            exit 0
            ;;
        major|minor|fix)
            BUMP_TYPE="$1"
            shift
            ;;
        -*)
            error "Unknown option: $1"
            info "Run '$(basename "$0") --help' for usage."
            exit 1
            ;;
        *)
            error "Unexpected argument: $1"
            info "Run '$(basename "$0") --help' for usage."
            exit 1
            ;;
    esac
done

# ── Preconditions ────────────────────────────────────────────────────────────
if [ ! -f "$CSPROJ" ]; then
    error "Could not find ${CSPROJ}"
    exit 1
fi

if ! git rev-parse --git-dir >/dev/null 2>&1; then
    error "Not inside a git repository"
    exit 1
fi

if ! git diff --quiet HEAD -- "$CSPROJ"; then
    error "${CSPROJ} has uncommitted changes"
    exit 1
fi

CURRENT_BRANCH=$(git branch --show-current)
info "Current branch: ${CURRENT_BRANCH}"

# ── Parse current version ────────────────────────────────────────────────────
CURRENT=$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$CSPROJ")
if [[ ! "$CURRENT" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-.*)?$ ]]; then
    error "Could not parse current version: ${CURRENT}"
    exit 1
fi

MAJOR="${BASH_REMATCH[1]}"
MINOR="${BASH_REMATCH[2]}"
FIX="${BASH_REMATCH[3]}"
SUFFIX="${BASH_REMATCH[4]:-}"

info "Current bridge version: ${CURRENT}"

# ── Compute new version ──────────────────────────────────────────────────────
case "$BUMP_TYPE" in
    major)
        MAJOR=$((MAJOR + 1))
        MINOR=0
        FIX=0
        ;;
    minor)
        MINOR=$((MINOR + 1))
        FIX=0
        ;;
    fix)
        FIX=$((FIX + 1))
        ;;
esac

NEW_VERSION="${MAJOR}.${MINOR}.${FIX}${SUFFIX}"
ASSEMBLY_VERSION="${MAJOR}.${MINOR}.${FIX}.0"
TAG="bridge-v${NEW_VERSION}"

info "New bridge version: ${NEW_VERSION}"
info "Git tag: ${TAG}"

# ── Check for existing tag ───────────────────────────────────────────────────
if git rev-parse --verify --quiet "refs/tags/${TAG}" >/dev/null; then
    error "Tag ${TAG} already exists"
    exit 1
fi

# ── Update csproj ────────────────────────────────────────────────────────────
sed -i.bak -E "s|<Version>[^<]+</Version>|<Version>${NEW_VERSION}</Version>|" "$CSPROJ"
sed -i.bak -E "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>${ASSEMBLY_VERSION}</AssemblyVersion>|" "$CSPROJ"
sed -i.bak -E "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>${ASSEMBLY_VERSION}</FileVersion>|" "$CSPROJ"
rm -f "${CSPROJ}.bak"

success "Updated ${CSPROJ}"

# ── Confirm ──────────────────────────────────────────────────────────────────
if [ "$AUTO_YES" = false ]; then
    echo ""
    git diff -- "$CSPROJ"
    echo ""
    read -r -p "Commit and push tag ${TAG}? [y/N] " REPLY
    if [[ ! "$REPLY" =~ ^[Yy]$ ]]; then
        warning "Aborted. Reverting changes..."
        git checkout -- "$CSPROJ"
        exit 0
    fi
fi

# ── Commit, tag, push ────────────────────────────────────────────────────────
git add "$CSPROJ"
git commit -m "chore: bump bridge to v${NEW_VERSION}"
git tag "$TAG"
git push origin "${CURRENT_BRANCH}"
git push origin "$TAG"

success "Released ${TAG} from branch ${CURRENT_BRANCH}"
