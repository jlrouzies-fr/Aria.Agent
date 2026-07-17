#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════════════
# Aria Bridge installer — retro cogitator node style
# ═══════════════════════════════════════════════════════════════════════════════

REPO="jlrouzies-fr/Aria.Agent"
INSTALL_DIR="${HOME}/.local/lib/aria-agent"
BIN_DIR="${HOME}/.local/bin"
BIN_NAME="aria-bridge"

# ── ANSI palette ─────────────────────────────────────────────────────────────
C_RESET=$'\033[0m'
C_BOLD=$'\033[1m'
C_DIM=$'\033[2m'
C_RED=$'\033[38;2;255;80;48m'      # Phosphor red
C_AMBER=$'\033[38;2;255;140;0m'    # Amber
C_GOLD=$'\033[38;2;212;160;32m'    # Gold
C_BLOOD=$'\033[38;2;204;61;0m'     # Blood red
C_MUTED=$'\033[38;2;204;112;80m'   # Muted
C_DIMRED=$'\033[38;2;139;32;16m'   # Dim red

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

info()    { printf "${C_MUTED}> %s${C_RESET}\n" "$1" >&2; }
success() { printf "${C_AMBER}✓ %s${C_RESET}\n" "$1" >&2; }
warning() { printf "${C_GOLD}! %s${C_RESET}\n" "$1" >&2; }
error()   { printf "${C_BLOOD}✗ %s${C_RESET}\n" "$1" >&2; }

header() {
    local version="$1"
    cat <<EOF
${C_RED}╔══════════════════════════════════════════════════════════════════════════════╗
║${C_RESET}                                                                              ${C_RED}║
║${C_RESET}               ${C_BOLD}${C_RED}▓▓▒▒░░ ARIA // COGITATOR BRIDGE INSTALLER ░░▒▒▓▓${C_RESET}               ${C_RED}║
║${C_RESET}                 ${C_MUTED}version: ${version}  |  loopback: localhost:5741${C_RESET}                 ${C_RED}║
║${C_RESET}                                                                              ${C_RED}║
╚══════════════════════════════════════════════════════════════════════════════╝${C_RESET}
EOF
}

# ── Argument parsing ─────────────────────────────────────────────────────────
# By default the installer stops anything listening on the bridge port so the
# new version can take over. Use --no-kill to leave running processes alone.
KILL_RUNNING=true
VERSION_ARG=""

while [ $# -gt 0 ]; do
    case "$1" in
        --no-kill|-n)
            KILL_RUNNING=false
            shift
            ;;
        --force|-f)
            # Backward compatibility: killing is now the default.
            shift
            ;;
        -*)
            error "Unknown option: $1"
            info "Usage: $0 [--no-kill|-n] [VERSION]"
            exit 1
            ;;
        *)
            if [ -z "$VERSION_ARG" ]; then
                VERSION_ARG="$1"
            else
                error "Unexpected argument: $1"
                info "Usage: $0 [--no-kill|-n] [VERSION]"
                exit 1
            fi
            shift
            ;;
    esac
done

VERSION="${VERSION_ARG:-latest}"

header "$VERSION"

rule "CONFIGURATION"
info "Repository: ${REPO}"
info "Target directory: ${INSTALL_DIR}"
info "Symlink: ${BIN_DIR}/${BIN_NAME}"
if [ "$KILL_RUNNING" = true ]; then
    info "Port 5741 will be cleared before installation if occupied"
else
    warning "No-kill mode — any process on port 5741 will be left running"
fi

# ── Platform detection ───────────────────────────────────────────────────────
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
case "$OS" in
    linux*)     PLATFORM="linux" ;;
    darwin*)    PLATFORM="osx" ;;
    *)          error "Unsupported OS: $OS"; exit 1 ;;
esac

ARCH=$(uname -m)
case "$ARCH" in
    x86_64|amd64)  RID_ARCH="x64" ;;
    arm64|aarch64) RID_ARCH="arm64" ;;
    *)             error "Unsupported architecture: ${ARCH}"; exit 1 ;;
esac

ASSET_NAME="aria-bridge-${PLATFORM}-${RID_ARCH}.zip"
info "Platform: ${PLATFORM}-${RID_ARCH}"
info "Expected asset: ${ASSET_NAME}"

# Find PIDs listening on a given TCP port.
find_pids_on_port() {
    local port="$1"
    local pids=""

    if command -v lsof >/dev/null 2>&1; then
        pids=$(lsof -ti tcp:"$port" -sTCP:LISTEN 2>/dev/null || true)
    fi

    if [ -z "$pids" ] && command -v fuser >/dev/null 2>&1; then
        pids=$(fuser "$port/tcp" 2>/dev/null || true)
    fi

    echo "$pids"
}

# Detect whatever is already bound to the bridge port, and optionally stop
# an installed bridge so the new version can take over.
detect_and_handle_port_conflict() {
    local port=5741
    local pids
    pids=$(find_pids_on_port "$port")

    if [ -z "$pids" ]; then
        return
    fi

    section "PORT CHECK"
    warning "Something is already listening on port ${port}."

    for pid in $pids; do
        local comm args
        comm=$(ps -p "$pid" -o comm= 2>/dev/null || echo "?")
        args=$(ps -p "$pid" -o args= 2>/dev/null || echo "?")
        echo ""
        info "PID ${pid}: ${comm}"
        printf "${C_MUTED}    %s${C_RESET}\n" "$args"

        if [ "$comm" = "$BIN_NAME" ] || echo "$args" | grep -qE "(\.local.{0,1}lib.{0,1}aria-agent|Aria.{0,1}bridge.{0,1}aria-bridge|aria-bridge)"; then
            info "This looks like the installed aria-bridge binary."
            if [ "$KILL_RUNNING" = true ]; then
                warning "Stopping PID ${pid} so the new version can take over..."
                kill "$pid" 2>/dev/null || true
                sleep 1
                if kill -0 "$pid" 2>/dev/null; then
                    kill -9 "$pid" 2>/dev/null || true
                    sleep 1
                fi
                if kill -0 "$pid" 2>/dev/null; then
                    error "PID ${pid} did not stop."
                else
                    success "Stopped."
                fi
            else
                info "Leaving it running because --no-kill was used."
            fi
        elif [ "$comm" = "dotnet" ] || echo "$args" | grep -q "Aria.Bridge"; then
            info "This looks like a 'dotnet run' debug session."
            if [ "$KILL_RUNNING" = true ]; then
                warning "Stopping the debug session so the installed binary can use port ${port}..."
                kill "$pid" 2>/dev/null || true
                sleep 1
                if kill -0 "$pid" 2>/dev/null; then
                    kill -9 "$pid" 2>/dev/null || true
                    sleep 1
                fi
                if kill -0 "$pid" 2>/dev/null; then
                    error "PID ${pid} did not stop."
                else
                    success "Stopped."
                fi
            else
                info "Leaving it running because --no-kill was used."
            fi
        else
            warning "This does not look like aria-bridge."
            if [ "$KILL_RUNNING" = true ]; then
                warning "Stopping unknown PID ${pid} to free port ${port}..."
                kill "$pid" 2>/dev/null || true
                sleep 1
                if kill -0 "$pid" 2>/dev/null; then
                    kill -9 "$pid" 2>/dev/null || true
                    sleep 1
                fi
                if kill -0 "$pid" 2>/dev/null; then
                    error "PID ${pid} did not stop."
                else
                    success "Stopped."
                fi
            else
                info "Leaving it running because --no-kill was used."
            fi
        fi
    done
}

# Resolve a download URL for the given tag ("latest" or "bridge-vX.Y.Z").
# Queries the public GitHub API (no auth needed) so prerelease tags are
# still found — the /releases/latest endpoint excludes prereleases, and
# bridge releases are prerelease until declared stable.
resolve_download_url() {
    local tag="$1"
    local release_url
    local release_json
    local http_status

    if [ "$tag" = "latest" ]; then
        release_url="https://api.github.com/repos/${REPO}/releases"
    else
        release_url="https://api.github.com/repos/${REPO}/releases/tags/${tag}"
    fi

    info "GitHub API URL: ${release_url}"

    http_status=$(curl -s -o "${TMP_DIR:-/tmp}/release.json" -w "%{http_code}" "$release_url" || true)
    info "GitHub API response status: ${http_status}"

    if [ "$http_status" != "200" ]; then
        error "GitHub API request failed"
        if [ -f "${TMP_DIR:-/tmp}/release.json" ]; then
            cat "${TMP_DIR:-/tmp}/release.json" >&2 || true
        fi
        exit 1
    fi

    if [ "$tag" = "latest" ]; then
        release_json=$(python3 -c "
import sys, json
with open('${TMP_DIR:-/tmp}/release.json') as f:
    releases = json.load(f)
if not releases:
    print('No releases found', file=sys.stderr)
    sys.exit(1)
print(json.dumps(releases[0]))
")
        info "Selected latest release (newest by GitHub order)"
    else
        release_json=$(cat "${TMP_DIR:-/tmp}/release.json")
    fi

    info "Release tag: $(echo "$release_json" | python3 -c "import sys, json; print(json.load(sys.stdin).get('tag_name','?'))")"
    info "Release prerelease: $(echo "$release_json" | python3 -c "import sys, json; print(json.load(sys.stdin).get('prerelease','?'))")"
    info "Release published_at: $(echo "$release_json" | python3 -c "import sys, json; print(json.load(sys.stdin).get('published_at','?'))")"

    info "Assets in release:"
    echo "$release_json" | python3 -c "import sys, json; [print('  -', a.get('name','?')) for a in json.load(sys.stdin).get('assets', [])]" >&2

    local download_url
    download_url=$(echo "$release_json" | python3 -c "
import sys, json
assets = json.load(sys.stdin).get('assets', [])
for a in assets:
    if a.get('name') == '${ASSET_NAME}':
        print(a.get('browser_download_url', ''))
        break
" 2>/dev/null || echo "")

    if [ -z "$download_url" ]; then
        error "Could not find asset ${ASSET_NAME} in release ${tag}"
        exit 1
    fi

    echo "$download_url"
}

if [ "$VERSION" = "latest" ]; then
    TAG="latest"
else
    TAG="bridge-v${VERSION}"
fi

# Prepare directories early so we have a place for temp debug files.
mkdir -p "$INSTALL_DIR"
mkdir -p "$BIN_DIR"

# Check for a running bridge before we replace files.
detect_and_handle_port_conflict

# Download to temp.
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

section "RESOLVING DOWNLOAD URL"
DOWNLOAD_URL=$(resolve_download_url "$TAG")

section "DOWNLOADING"
info "Target: ${INSTALL_DIR}"
info "Download URL: ${DOWNLOAD_URL}"

ASSET_FILE="${TMP_DIR}/aria-bridge.zip"
info "Saving to: ${ASSET_FILE}"

http_status=$(curl -s -L -o "$ASSET_FILE" -w "%{http_code}" --max-time 120 --connect-timeout 10 -A "aria-bridge-installer/1.0" "$DOWNLOAD_URL" 2>"$TMP_DIR/curl.err" || true)

info "Download HTTP status: ${http_status}"

if [ "$http_status" != "200" ]; then
    error "Download failed with status ${http_status}"
    if [ -f "$TMP_DIR/curl.err" ]; then
        cat "$TMP_DIR/curl.err" >&2 || true
    fi
    if [ -f "$ASSET_FILE" ]; then
        head -c 2048 "$ASSET_FILE" >&2 || true
    fi
    exit 1
fi

file_size=$(stat -f%z "$ASSET_FILE" 2>/dev/null || stat -c%s "$ASSET_FILE" 2>/dev/null || echo "?")
success "Downloaded archive size: ${file_size} bytes"

section "EXTRACTING"
unzip -q "$ASSET_FILE" -d "$TMP_DIR/extract"

# Some releases zip the publish folder itself, others its contents.
# If the root of the archive contains a single directory, use that.
SOURCE_DIR="$TMP_DIR/extract"
EXTRACT_ROOT_COUNT=$(find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')
EXTRACT_ROOT_DIR_COUNT=$(find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')
info "Archive root entries: ${EXTRACT_ROOT_COUNT} (directories: ${EXTRACT_ROOT_DIR_COUNT})"
if [ "$EXTRACT_ROOT_COUNT" -eq 1 ] && [ "$EXTRACT_ROOT_DIR_COUNT" -eq 1 ]; then
    SOURCE_DIR=$(find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 -type d)
    info "Using nested source directory: ${SOURCE_DIR}"
fi

BIN_PATH=$(find "$SOURCE_DIR" -type f -name "$BIN_NAME" | head -n 1 || true)
if [ -z "$BIN_PATH" ]; then
    error "Could not find ${BIN_NAME} inside the downloaded archive."
    find "$TMP_DIR/extract" >&2
    exit 1
fi

success "Binary found at: ${BIN_PATH}"

section "INSTALLING"
rm -rf "${INSTALL_DIR:?}"/*
cp -R "$SOURCE_DIR/"* "$INSTALL_DIR/"
chmod +x "${INSTALL_DIR}/${BIN_NAME}"

ln -sf "${INSTALL_DIR}/${BIN_NAME}" "${BIN_DIR}/${BIN_NAME}"
success "Symlinked ${BIN_DIR}/${BIN_NAME}"

section "VERIFYING"
INSTALLED_VERSION=$("${BIN_DIR}/${BIN_NAME}" --version)
success "aria-bridge ${INSTALLED_VERSION} installed to ${INSTALL_DIR}/${BIN_NAME}"

echo ""
rule "AUTO-START"
info "aria-bridge will start automatically in 10 seconds..."
for i in $(seq 10 -1 1); do
    printf "\r${C_MUTED}> Starting in %d...${C_RESET}" "$i"
    sleep 1
done
printf "\n"

BRIDGE_EXE="${BIN_DIR}/${BIN_NAME}"
if [ -x "$BRIDGE_EXE" ]; then
    nohup "$BRIDGE_EXE" >/dev/null 2>&1 &
    success "Started aria-bridge."
    info "Open the status page: http://localhost:5741"
else
    error "Could not find ${BRIDGE_EXE}."
    info "Start it manually: aria-bridge"
    info "Open the status page: http://localhost:5741"
fi
