#!/usr/bin/env bash
#
# dev-fleet.sh — Launch multiple local Aria bridge instances for fleet debugging.
#
# Usage:
#   scripts/dev-fleet.sh [--nodes N] [--reset] [--lm-key KEY] [--lm-url URL]
#                        [--server-url URL] [--stop]
#
# Defaults:
#   --nodes 2
#   --lm-url  http://localhost:1234/v1   (LM Studio OpenAI-compatible base)
#   --server-url http://localhost:5129
#
# Each instance runs on its own port/data dir with a fake debug profile:
#   DEBUG-NODE-1 : port 5742, Windows Desktop, RTX 4090, 32 GB RAM
#   DEBUG-NODE-2 : port 5743, Linux  Laptop,  no GPU,    16 GB RAM
#
# Environment overrides:
#   ARIA_DEBUG_SOUL_ID   - Aria.Web user id to join the fake nodes to.
#                          If unset, the script tries to read it from the web DB
#                          (src/AriaAgent/Aria.Web/aria.db) first, then falls back
#                          to the primary bridge vault.
#   ARIA_DEBUG_LM_KEY    - API key for the local LM channel.
#   ARIA_DEBUG_LM_URL    - Local LM OpenAI-compatible base URL (default http://localhost:1234/v1).
#   ARIA_SERVER_URL      - Aria.Web URL (default http://localhost:5129).
#
# The script auto-joins every node via /soul/join and then pre-enrolls it through
# /api/debug/enroll-node, so no manual pairing-code approval is required. A local
# LM channel is also seeded through the bridge's own localhost HTTP API (not by
# poking at the encrypted vault directly). Finally, every node's public key is
# cross-registered as a trusted sibling on every other node (POST /debug/trust-sibling,
# DEBUG+Development only) so context grants replicate between fleet nodes — this
# replaces the production enrollment-certificate ceremony, which cannot bootstrap
# in dev-fleet. Node profiles live in node_profile(); GpuName:"none" fakes a
# GPU-less machine.
#
# Stop all launched instances:
#   scripts/dev-fleet.sh --stop
#
# Wipe state and start fresh identities:
#   scripts/dev-fleet.sh --reset
#

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FLEET_DIR="$ROOT/.local-logs/devfleet"
NODES=2
RESET=0
STOP=0
LM_KEY="${ARIA_DEBUG_LM_KEY:-}"
LM_URL="${ARIA_DEBUG_LM_URL:-http://localhost:1234/v1}"
SERVER_URL="${ARIA_SERVER_URL:-http://localhost:5129}"

usage() {
    sed -n '2,35p' "$0" | sed 's/^# //'
    exit 1
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --nodes) NODES="$2"; shift 2 ;;
        --reset) RESET=1; shift ;;
        --stop)  STOP=1; shift ;;
        --lm-key) LM_KEY="$2"; shift 2 ;;
        --lm-url) LM_URL="$2"; shift 2 ;;
        --server-url) SERVER_URL="$2"; shift 2 ;;
        -h|--help) usage ;;
        *) echo "Unknown option: $1"; usage ;;
    esac
done

ensure_tools() {
    if ! command -v curl >/dev/null 2>&1; then
        echo "ERROR: curl is required." >&2
        exit 1
    fi
}

discover_soul_id() {
    local soul_id="${ARIA_DEBUG_SOUL_ID:-}"
    if [[ -n "$soul_id" ]]; then
        echo "$soul_id"
        return
    fi

    if ! command -v sqlite3 >/dev/null 2>&1; then
        echo "ERROR: sqlite3 not found and ARIA_DEBUG_SOUL_ID is unset." >&2
        exit 1
    fi

    # Prefer the web DB of the server we're enrolling into; only fall back to the primary bridge vault.
    local web_db="$ROOT/src/AriaAgent/Aria.Web/aria.db"
    if [[ -f "$web_db" ]]; then
        soul_id="$(sqlite3 "$web_db" "SELECT Id FROM Users LIMIT 1;" 2>/dev/null || true)"
        if [[ -n "$soul_id" ]]; then
            echo "$soul_id"
            return
        fi
    fi

    local vault
    case "$(uname -s)" in
        Darwin) vault="$HOME/Library/Application Support/aria-bridge/aria-bridge.db" ;;
        *)      vault="$HOME/.config/aria-bridge/aria-bridge.db" ;;
    esac

    if [[ ! -f "$vault" ]]; then
        echo "ERROR: primary vault not found at $vault and ARIA_DEBUG_SOUL_ID is unset." >&2
        exit 1
    fi

    soul_id="$(sqlite3 "$vault" "SELECT ServerSoulId FROM Souls LIMIT 1;" 2>/dev/null || true)"
    if [[ -z "$soul_id" ]]; then
        echo "ERROR: could not discover ServerSoulId from $vault. Set ARIA_DEBUG_SOUL_ID." >&2
        exit 1
    fi
    echo "$soul_id"
}

build_bridge() {
    echo "[dev-fleet] Building Aria.Bridge..."
    dotnet build "$ROOT/src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj" --nologo -v q
}

wait_for_health() {
    local port="$1"
    local deadline=$((SECONDS + 60))
    while ((SECONDS < deadline)); do
        if curl -fsS "http://localhost:$port/health" >/dev/null 2>&1; then
            return 0
        fi
        sleep 0.5
    done
    echo "ERROR: bridge on port $port did not become healthy within 60s." >&2
    return 1
}

capture_bridge_pid() {
    local port="$1"
    local data_dir="$2"
    # Prefer the process actually bound to the port; fall back to the dotnet run PID.
    local pid=""
    if command -v lsof >/dev/null 2>&1; then
        pid="$(lsof -ti "tcp:$port" 2>/dev/null | head -n1 || true)"
    fi
    if [[ -z "$pid" ]]; then
        pid="$(cat "$data_dir/run.pid" 2>/dev/null || true)"
    fi
    if [[ -n "$pid" ]]; then
        echo "$pid" > "$data_dir/bridge.pid"
    fi
}

node_profile() {
    local i="$1"
    case "$i" in
        1) cat <<'JSON'
{"Label":"DEBUG-NODE-1","Platform":"Windows","Hostname":"DEBUG-WIN-01","FormFactor":"desktop","CpuModel":"AMD Ryzen 9 7950X","CpuCores":16,"TotalRamMb":32313,"GpuName":"NVIDIA GeForce RTX 4090","GpuVramTotalMb":24564,"GpuVramFreeMb":18200}
JSON
            ;;
        2) cat <<'JSON'
{"Label":"DEBUG-NODE-2","Platform":"Linux","Hostname":"DEBUG-LIN-02","FormFactor":"laptop","CpuModel":"Intel Core i7-1165G7","CpuCores":8,"TotalRamMb":16384,"GpuName":"none"}
JSON
            ;;
        *) cat <<JSON
{"Label":"DEBUG-NODE-$i","Platform":"Linux","Hostname":"DEBUG-NODE-$i","FormFactor":"desktop","CpuCores":4,"TotalRamMb":8192}
JSON
            ;;
    esac
}

start_node() {
    local i="$1"
    local port=$((5741 + i))
    local label="DEBUG-NODE-$i"
    local data_dir="$FLEET_DIR/node-$i"
    local log_file="$FLEET_DIR/node-$i.log"
    local profile
    profile="$(node_profile "$i" | tr -d '\n')"

    mkdir -p "$data_dir"

    echo "[dev-fleet] Starting $label on port $port (data: $data_dir)"

    # Run the built DLL directly instead of `dotnet run`: a later `dotnet build`/`dotnet test`
    # of the bridge project kills `dotnet run` children, but leaves a plain DLL process alone.
    # The csproj builds RID-specific (PublishSingleFile), so resolve the runtime dir dynamically.
    local bridge_dll
    bridge_dll="$(find "$ROOT/src/AriaAgent/Aria.Bridge/bin/Debug/net10.0" -name aria-bridge.dll -maxdepth 2 | head -n1)"
    if [[ -z "$bridge_dll" ]]; then
        echo "ERROR: aria-bridge.dll not found under bin/Debug/net10.0 — build failed?" >&2
        return 1
    fi

    ARIA_BRIDGE_DATA_DIR="$data_dir" \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://localhost:$port" \
    ARIA_BRIDGE_DEBUG_PROFILE="$profile" \
        dotnet "$bridge_dll" \
            > "$log_file" 2>&1 &

    echo $! > "$data_dir/run.pid"
}

join_node() {
    local i="$1"
    local port=$((5741 + i))
    local label="DEBUG-NODE-$i"
    local soul_id="$2"

    echo "[dev-fleet] Joining $label to $SERVER_URL (user $soul_id)..."

    local join_payload
    join_payload="$(python3 -c 'import json,sys; print(json.dumps({"serverUrl":sys.argv[1],"serverSoulId":sys.argv[2],"name":sys.argv[3],"label":sys.argv[4]}))' \
        "$SERVER_URL" "$soul_id" "$label Soul" "$label")"

    local join_resp
    join_resp="$(curl -fsS -X POST "http://localhost:$port/soul/join" \
        -H 'Content-Type: application/json' \
        -d "$join_payload" 2>/dev/null || true)"

    if [[ -z "$join_resp" ]]; then
        echo "WARNING: /soul/join failed for $label. Skipping enrollment." >&2
        return 1
    fi

    local node_pub
    node_pub="$(echo "$join_resp" | python3 -c 'import sys,json; print(json.load(sys.stdin)["nodePublicKey"])' 2>/dev/null || true)"
    if [[ -z "$node_pub" ]]; then
        echo "WARNING: could not extract nodePublicKey from join response for $label." >&2
        return 1
    fi

    # Remember the key so cross_trust can register it on every sibling afterwards.
    NODE_PUBS[$i]="$node_pub"

    local platform
    platform="$(echo "$join_resp" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("platform",""))' 2>/dev/null || true)"

    local enroll_payload
    enroll_payload="$(python3 -c 'import json,sys; print(json.dumps({"userId":sys.argv[1],"nodePublicKey":sys.argv[2],"label":sys.argv[3],"platform":sys.argv[4]}))' \
        "$soul_id" "$node_pub" "$label" "$platform")"

    local enroll_resp
    enroll_resp="$(curl -fsS -X POST "$SERVER_URL/api/debug/enroll-node" \
        -H 'Content-Type: application/json' \
        -d "$enroll_payload" 2>/dev/null || true)"

    if [[ -z "$enroll_resp" ]] || ! echo "$enroll_resp" | python3 -c 'import sys,json; sys.exit(0 if json.load(sys.stdin).get("ok") else 1)' 2>/dev/null; then
        echo "WARNING: /api/debug/enroll-node failed for $label: $enroll_resp" >&2
        return 1
    fi

    echo "[dev-fleet] $label enrolled."
}

seed_lm_channel() {
    local i="$1"
    local port=$((5741 + i))
    local label="DEBUG-NODE-$i"
    local channel_name="LM Studio ($label)"
    local url_encoded
    url_encoded="$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$channel_name")"

    echo "[dev-fleet] Seeding local LM channel for $label..."

    # If LM Studio is already running and a key is provided, fetch its real model list.
    local models='["local-model"]'
    if [[ -n "$LM_KEY" ]]; then
        local lm_models
        lm_models="$(curl -fsS -H "Authorization: Bearer $LM_KEY" "$LM_URL/models" 2>/dev/null \
            | python3 -c 'import sys,json; d=json.load(sys.stdin); print(json.dumps([m["id"] for m in d.get("data",[])]))' 2>/dev/null || true)"
        if [[ -n "$lm_models" ]]; then
            models="$lm_models"
            echo "[dev-fleet] Fetched $(echo "$models" | python3 -c 'import sys,json; print(len(json.load(sys.stdin)))') models from LM Studio."
        else
            echo "[dev-fleet] LM Studio not reachable or returned no models; using placeholder model."
        fi
    fi

    local channel_payload
    channel_payload="$(python3 -c 'import json,sys; print(json.dumps({"url":sys.argv[1],"models":json.loads(sys.argv[2]),"isBridged":True}))' "$LM_URL" "$models")"

    curl -fsS -X PUT "http://localhost:$port/channels/$url_encoded" \
        -H 'Content-Type: application/json' \
        -d "$channel_payload" >/dev/null 2>&1 || {
        echo "WARNING: could not seed channel for $label." >&2
        return 1
    }

    if [[ -n "$LM_KEY" ]]; then
        local key_payload
        key_payload="$(python3 -c 'import json,sys; print(json.dumps({"key":sys.argv[1]}))' "$LM_KEY")"

        local key_url_encoded
        key_url_encoded="$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$channel_name")"
        curl -fsS -X PUT "http://localhost:$port/keys/$key_url_encoded" \
            -H 'Content-Type: application/json' \
            -d "$key_payload" >/dev/null 2>&1 || {
            echo "WARNING: could not store LM key for $label." >&2
            return 1
        }
    else
        echo "[dev-fleet] No LM key provided; channel created without authentication."
    fi
}

# Cross-register every node's public key as a trusted sibling on every OTHER node, via the
# bridge's DEBUG-only /debug/trust-sibling endpoint. This replaces the production enrollment-
# certificate ceremony (impossible in dev-fleet: no already-trusted device exists to sign).
# Idempotent — re-running the script without --reset just upserts the same rows.
cross_trust() {
    local soul_id="$1"
    local i j port_i payload
    for ((i = 1; i <= NODES; i++)); do
        port_i=$((5741 + i))
        for ((j = 1; j <= NODES; j++)); do
            [[ "$i" == "$j" ]] && continue
            [[ -n "${NODE_PUBS[$j]:-}" ]] || continue
            payload="$(python3 -c 'import json,sys; print(json.dumps({"userId":sys.argv[1],"nodePublicKey":sys.argv[2]}))' \
                "$soul_id" "${NODE_PUBS[$j]}")"
            if curl -fsS -X POST "http://localhost:$port_i/debug/trust-sibling" \
                    -H 'Content-Type: application/json' -d "$payload" >/dev/null 2>&1; then
                echo "[dev-fleet] node-$i now trusts node-$j's key."
            else
                echo "WARNING: /debug/trust-sibling failed for node-$j on node-$i (old bridge build?)." >&2
            fi
        done
    done
}

stop_fleet() {
    local killed=0
    if [[ -d "$FLEET_DIR" ]]; then
        for pid_file in "$FLEET_DIR"/node-*/bridge.pid "$FLEET_DIR"/node-*/run.pid; do
            [[ -f "$pid_file" ]] || continue
            local pid
            pid="$(cat "$pid_file" 2>/dev/null || true)"
            if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
                echo "[dev-fleet] Stopping pid $pid ($pid_file)..."
                kill "$pid" 2>/dev/null || true
                local waited=0
                while kill -0 "$pid" 2>/dev/null && ((waited < 10)); do
                    sleep 1
                    ((waited++)) || true
                done
                if kill -0 "$pid" 2>/dev/null; then
                    kill -9 "$pid" 2>/dev/null || true
                fi
                killed=1
            fi
            rm -f "$pid_file"
        done
    fi
    if ((killed)); then
        echo "[dev-fleet] Fleet stopped."
    else
        echo "[dev-fleet] No running fleet processes found."
    fi
}

main() {
    ensure_tools

    NODE_PUBS=()

    if ((STOP)); then
        stop_fleet
        exit 0
    fi

    if ((RESET)); then
        echo "[dev-fleet] Resetting fleet state in $FLEET_DIR..."
        rm -rf "$FLEET_DIR"
    fi

    mkdir -p "$FLEET_DIR"

    build_bridge

    local soul_id
    soul_id="$(discover_soul_id)"
    echo "[dev-fleet] Target server: $SERVER_URL, soul: $soul_id"

    local i
    for ((i = 1; i <= NODES; i++)); do
        start_node "$i"
    done

    for ((i = 1; i <= NODES; i++)); do
        local port=$((5741 + i))
        local data_dir="$FLEET_DIR/node-$i"
        wait_for_health "$port"
        capture_bridge_pid "$port" "$data_dir"
    done

    for ((i = 1; i <= NODES; i++)); do
        join_node "$i" "$soul_id" && seed_lm_channel "$i"
    done

    if ((NODES > 1)); then
        cross_trust "$soul_id"
    fi

    echo
    echo "[dev-fleet] Fleet ready. Nodes:"
    for ((i = 1; i <= NODES; i++)); do
        echo "  DEBUG-NODE-$i -> http://localhost:$((5741 + i))  (log: $FLEET_DIR/node-$i.log)"
    done
    echo "[dev-fleet] Stop with: scripts/dev-fleet.sh --stop"
}

main
