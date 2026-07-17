#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge uninstaller — retro cogitator node style
# ═══════════════════════════════════════════════════════════════════════════════

INSTALL_DIR="${HOME}/.local/lib/aria-agent"
BIN_DIR="${HOME}/.local/bin"
BIN_NAME="aria-bridge"

# ── ANSI palette ─────────────────────────────────────────────────────────────
C_RESET=$'\033[0m'
C_BOLD=$'\033[1m'
C_DIM=$'\033[2m'
C_RED=$'\033[38;2;255;80;48m'
C_AMBER=$'\033[38;2;255;140;0m'
C_GOLD=$'\033[38;2;212;160;32m'
C_BLOOD=$'\033[38;2;204;61;0m'
C_MUTED=$'\033[38;2;204;112;80m'
C_DIMRED=$'\033[38;2;139;32;16m'

# ── Render helpers ───────────────────────────────────────────────────────────
section() {
    local title="$1"
    local width=78
    printf "${C_RED}┌"
    printf '%*s' $((width - 2)) '' | tr ' ' '─'
    printf "┐${C_RESET}\n"
    printf "${C_RED}│ ${C_BOLD}${C_AMBER}%-*s${C_RESET}${C_RED} │${C_RESET}\n" $((width - 4)) "$title"
    printf "${C_RED}└"
    printf '%*s' $((width - 2)) '' | tr ' ' '─'
    printf "┘${C_RESET}\n"
}

rule() {
    local label="${1:-}"
    local width=78
    if [ -n "$label" ]; then
        local pad=$((width - ${#label} - 4))
        printf "${C_DIMRED}── ${C_BOLD}${C_RED}%s${C_RESET}${C_DIMRED} %*s${C_RESET}\n" "$label" "$pad" '' | tr ' ' '─'
    else
        printf "${C_DIMRED}%*s${C_RESET}\n" "$width" '' | tr ' ' '─'
    fi
}

info()    { printf "${C_MUTED}> %s${C_RESET}\n" "$1"; }
success() { printf "${C_AMBER}✓ %s${C_RESET}\n" "$1"; }
warning() { printf "${C_GOLD}! %s${C_RESET}\n" "$1"; }
error()   { printf "${C_BLOOD}✗ %s${C_RESET}\n" "$1" >&2; }

header() {
    cat <<EOF
${C_RED}╔══════════════════════════════════════════════════════════════════════════════╗
║${C_RESET}                                                                              ${C_RED}║
║${C_RESET}   ${C_BOLD}${C_RED}▓▓▒▒░░ ARIA // COGITATOR NODE UNINSTALLER ░░▒▒▓▓${C_RESET}                            ${C_RED}║
║${C_RESET}   ${C_MUTED}loopback: 127.0.0.1:5741${C_RESET}                                                   ${C_RED}║
║${C_RESET}                                                                              ${C_RED}║
╚══════════════════════════════════════════════════════════════════════════════╝${C_RESET}
EOF
}

header

# ── Detect running bridge ────────────────────────────────────────────────────
section "PROCESS CHECK"
if pgrep -x "$BIN_NAME" > /dev/null 2>&1; then
    warning "aria-bridge appears to be running."
    info "Stop it before uninstalling to avoid file-in-use errors."
    info "The uninstaller will not kill a running process."
else
    success "No running aria-bridge process detected."
fi

# ── Remove installed files ───────────────────────────────────────────────────
section "REMOVING FILES"
if [ -d "$INSTALL_DIR" ]; then
    info "Removing ${INSTALL_DIR}"
    rm -rf "$INSTALL_DIR"
    success "Install directory removed."
else
    warning "Install directory not found: ${INSTALL_DIR}"
fi

# ── Remove symlink ───────────────────────────────────────────────────────────
section "REMOVING SYMLINK"
if [ -L "${BIN_DIR}/${BIN_NAME}" ]; then
    info "Removing symlink ${BIN_DIR}/${BIN_NAME}"
    rm -f "${BIN_DIR}/${BIN_NAME}"
    success "Symlink removed."
elif [ -e "${BIN_DIR}/${BIN_NAME}" ]; then
    warning "${BIN_DIR}/${BIN_NAME} exists but is not a symlink; leaving it alone."
else
    info "Symlink not found: ${BIN_DIR}/${BIN_NAME}"
fi

# ── Done ─────────────────────────────────────────────────────────────────────
echo ""
rule "COMPLETE"
success "aria-bridge has been uninstalled."
info "Your shell PATH may still contain ${BIN_DIR}; remove it from your profile if you no longer need it."
