#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge release tag (no version bump)
# ═══════════════════════════════════════════════════════════════════════════════
# Usage: tag-bridge.sh [-y|--yes]
#
# Creates and pushes a bridge-v* tag for the CURRENT version already committed
# in Aria.Bridge.csproj. Use this when the version was bumped separately (or
# already matches what you want released) and you just need the tag.
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
AUTO_YES=false

while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes)
            AUTO_YES=true
            shift
            ;;
        -h|--help)
            cat <<EOF
Usage: $(basename "$0") [-y|--yes]

  Tags and pushes the CURRENT bridge version from Aria.Bridge.csproj —
  does not bump or commit anything.

  -y      skip the confirmation prompt
EOF
            exit 0
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

CURRENT_BRANCH=$(git branch --show-current)
info "Current branch: ${CURRENT_BRANCH}"

# ── Parse current version ────────────────────────────────────────────────────
CURRENT=$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$CSPROJ")
if [[ ! "$CURRENT" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-.*)?$ ]]; then
    error "Could not parse current version: ${CURRENT}"
    exit 1
fi

info "Current bridge version: ${CURRENT}"

TAG="bridge-v${CURRENT}"
info "Git tag: ${TAG}"

# ── Check for existing tag ───────────────────────────────────────────────────
if git rev-parse --verify --quiet "refs/tags/${TAG}" >/dev/null; then
    error "Tag ${TAG} already exists"
    exit 1
fi

# ── Confirm ──────────────────────────────────────────────────────────────────
if [ "$AUTO_YES" = false ]; then
    echo ""
    read -r -p "Create and push tag ${TAG} at $(git rev-parse --short HEAD)? [y/N] " REPLY
    if [[ ! "$REPLY" =~ ^[Yy]$ ]]; then
        warning "Aborted."
        exit 0
    fi
fi

# ── Tag, push ────────────────────────────────────────────────────────────────
git tag "$TAG"
git push origin "$TAG"

success "Released ${TAG} from branch ${CURRENT_BRANCH}"
