# // MULTI-BRIDGE DEBUG MODE — a fleet on one machine

[← Back to the cogitator terminal](../../README.md) · [Multi-node Routing](multi-node.md)

Testing the fleet normally requires two (or more) real machines enrolled under the same soul.
**Multi-bridge debug mode** removes that requirement: `scripts/dev-fleet.sh` launches several
local bridge instances on one machine, each with an **invented hardware profile** (name, OS, form
factor, CPU, RAM, GPU), its own port, its own data directory, and its own soul keypair — all
auto-joined to your running `Aria.Web` server. The result is a fully working `/fleet` dashboard
and real multi-node routing, without leaving your chair.

- [Quick start](#quick-start)
- [What the script does](#what-the-script-does)
- [The fake profiles](#the-fake-profiles)
- [Options & environment](#options--environment)
- [Layout on disk](#layout-on-disk)
- [Safety rails](#safety-rails)
- [Troubleshooting](#troubleshooting)

---

## Quick start

Prerequisites: **.NET 10**, `curl`, `python3`, `sqlite3`, a running `Aria.Web` (Development build)
with at least one user/soul created, and optionally a local LM server (LM Studio) if you want the
debug nodes to answer chat.

```bash
# start two debug nodes joined to the local server, seeded with your LM Studio channel
scripts/dev-fleet.sh --lm-key "sk-your-lm-studio-key"

# more nodes (generic Linux desktop profiles beyond node 2)
scripts/dev-fleet.sh --nodes 4 --lm-key "sk-your-lm-studio-key"

# stop everything
scripts/dev-fleet.sh --stop

# wipe all debug state (fresh soul keypairs, fresh enrollments) and start again
scripts/dev-fleet.sh --stop && rm -rf .local-logs/devfleet
scripts/dev-fleet.sh --lm-key "sk-your-lm-studio-key"
```

When the script finishes, open `http://localhost:5129/fleet`: ARIA CORE at the top, the
`DEBUG-NODE-x` cards below with their fake hardware, live gauges, and model chips. In Chat, the
agent's `fleet_status` tool reports the same muster.

> **Note:** `--reset` alone only wipes state on disk — it does not stop running nodes. If a
> previous fleet is still up, `--stop` first (or the new nodes will collide on ports and fail
> `/soul/join` with a 409 "A soul already exists").

---

## What the script does

For each node `i` (1-based):

1. **Builds** `Aria.Bridge` once (`dotnet build`).
2. **Starts** a bridge process with:
   - `ASPNETCORE_URLS=http://localhost:574<i+1>` — node 1 on **5742**, node 2 on **5743**, etc.
     (the real bridge default 5741 is left free, so debug nodes never shadow it),
   - `ARIA_BRIDGE_DATA_DIR=.local-logs/devfleet/node-<i>` — an isolated vault/soul/log per node,
   - `ARIA_BRIDGE_DEBUG_PROFILE=<json>` — the fake identity/hardware profile (see below),
   - `ASPNETCORE_ENVIRONMENT=Development` — required for the debug profile to be honored.
3. **Waits for health** (`GET /health`, up to 60 s) and records the PID.
4. **Joins the soul**: `POST /soul/join` against the target server, which generates a fresh
   ECDSA keypair on the node and returns its public key.
5. **Pre-enrolls** the node through `POST /api/debug/enroll-node` on `Aria.Web`, registering the
   node's public key under your soul **without the manual pairing-code ceremony**. This endpoint
   is compiled into DEBUG builds only and refuses to run outside the Development environment.
6. **Seeds an LM channel**: `PUT /channels/LM Studio (DEBUG-NODE-<i>)` pointing at the local LM
   URL (`http://localhost:1234/v1` by default). If `--lm-key` is given, the script first fetches
   the real model list from `<lm-url>/models` so the node advertises your actual models, then
   stores the key via `PUT /keys/...` — through the bridge's own loopback HTTP API, never by
   poking the encrypted vault directly. Without a key, a placeholder `local-model` channel is
   created (fine for dashboard testing, not for chat).
7. **Cross-trusts the nodes** (2+ nodes): every node's public key is registered as a trusted
   sibling on every other node via `POST /debug/trust-sibling` (DEBUG + Development only,
   loopback-only). This replaces the production enrollment-certificate ceremony — which cannot
   bootstrap in dev-fleet, because no already-trusted device exists to sign — and is what lets
   context grants replicate between debug nodes, so cross-node tool execution can be authorised
   end-to-end. Idempotent: re-running the script just upserts the same rows.

The target soul is discovered automatically: `ARIA_DEBUG_SOUL_ID` if set, else the first user in
`src/AriaAgent/Aria.Web/aria.db`, else the primary bridge vault's `ServerSoulId`.

---

## The fake profiles

Each node reports invented hardware via `ARIA_BRIDGE_DEBUG_PROFILE` — a JSON record where **null
fields fall through to real probes**, so you can fake only what you care about:

```json
{"Label":"DEBUG-NODE-1","Platform":"Windows","Hostname":"DEBUG-WIN-01","FormFactor":"desktop",
 "CpuModel":"AMD Ryzen 9 7950X","CpuCores":16,"TotalRamMb":32313,
 "GpuName":"NVIDIA GeForce RTX 4090","GpuVramTotalMb":24564,"GpuVramFreeMb":18200}
```

Built-in profiles (`node_profile()` in the script):

| Node | Port | Platform | Form factor | CPU | RAM | GPU |
|---|---|---|---|---|---|---|
| `DEBUG-NODE-1` | 5742 | Windows | desktop | Ryzen 9 7950X, 16 cores | 32 GB | RTX 4090, 24 GB VRAM |
| `DEBUG-NODE-2` | 5743 | Linux | laptop | Core i7-1165G7, 8 cores | 16 GB | **none** |
| `DEBUG-NODE-N` (N≥3) | 5741+N | Linux | desktop | generic, 4 cores | 8 GB | — |

`"GpuName":"none"` (case-insensitive) is the sentinel for a GPU-less machine: GPU name, VRAM
totals, and utilization are suppressed in BOTH `/hardware` and `/metrics` — without it, a missing
`GpuName` falls through to the real probe and leaks the host GPU.

The loader (`Aria.Bridge/Infrastructure/DebugBridgeProfile.cs`) is **gated on
`ASPNETCORE_ENVIRONMENT=Development`** — a production bridge ignores the variable entirely and
can never be spoofed. The fake profile is applied consistently: static inventory (`/hardware`)
reports the fake identity verbatim, and live metrics (`/metrics`) fake total RAM (real usage is
scaled to the fake total so free/used stay plausible) and GPU fields per the profile. Only CPU
utilization remains the host's real value.

---

## Options & environment

```
scripts/dev-fleet.sh [--nodes N] [--reset] [--lm-key KEY] [--lm-url URL]
                     [--server-url URL] [--stop]
```

| Flag / variable | Default | Purpose |
|---|---|---|
| `--nodes N` | `2` | How many debug bridges to launch |
| `--lm-key KEY` / `ARIA_DEBUG_LM_KEY` | — | API key stored on each node for the local LM channel |
| `--lm-url URL` / `ARIA_DEBUG_LM_URL` | `http://localhost:1234/v1` | OpenAI-compatible base URL (LM Studio) |
| `--server-url URL` / `ARIA_SERVER_URL` | `http://localhost:5129` | The `Aria.Web` to join |
| `--reset` | — | Delete `.local-logs/devfleet` before starting (fresh identities) |
| `--stop` | — | Kill all launched debug nodes and exit |
| `ARIA_DEBUG_SOUL_ID` | auto | User/soul id to join; overrides DB discovery |

---

## Layout on disk

Everything lives under `.local-logs/devfleet/` (local-only, never committed):

```
.local-logs/devfleet/
├── node-1.log              # stdout/stderr of the dotnet process
├── node-1/
│   ├── aria-bridge.log     # the bridge's own log (per node, via ARIA_BRIDGE_DATA_DIR)
│   ├── aria-bridge.db      # the node's encrypted vault (soul key, channels, keys)
│   ├── run.pid / bridge.pid
│   └── …
└── node-2/ …
```

Because `ARIA_BRIDGE_DATA_DIR` redirects the whole data directory, each debug node's soul,
channels, keys, cogitations, and `aria-bridge.log` stay in its own folder — nothing leaks into
your real bridge's app-data directory.

---

## Safety rails

- **Debug profile**: honored only with `ASPNETCORE_ENVIRONMENT=Development`; production bridges
  ignore it.
- **Trust endpoint**: `/debug/trust-sibling` is compiled into DEBUG builds only
  (`#if DEBUG`), refuses to run outside Development, and only accepts loopback callers. It writes
  the same `TrustedSiblingKeys` rows the production certificate ceremony would — the production
  trust model (`SiblingRoster` cert verification) is untouched.
- **Enroll endpoint**: `/api/debug/enroll-node` is wrapped in `#if DEBUG` and also refuses to run
  outside Development — it does not exist in Release builds.
- **Loopback only**: nodes bind `localhost`; nothing is exposed to the network.
- **Separate ports & vaults**: the real bridge (5741) and its app-data are untouched.

---

## Troubleshooting

- **`/soul/join` fails / 409 "A soul already exists"** — a previous fleet is still running or its
  state remains. `scripts/dev-fleet.sh --stop && rm -rf .local-logs/devfleet`, then relaunch.
- **Nodes appear unlinked / "MISSING keypair" in the web UI** — enrollment didn't complete; check
  the script output for `WARNING` lines and the node log (`.local-logs/devfleet/node-<i>.log`).
  Usually the server URL or soul id was wrong.
- **Chat on a debug node fails with HTTP 401/200-with-error** — the LM key wasn't stored (script
  ran without `--lm-key`, or the key is wrong). Re-run with `--lm-key`, or add the key on the
  node's own status page (`http://localhost:5742`).
- **Fleet dashboard shows `NO TELEMETRY`** — the node process died; check its log. Note that CPU
  usage is the one metric that stays real (both debug nodes report the host's CPU load) — RAM and
  GPU are faked per the profile.
- **Cross-node calls refuse even after approval** — sibling trust is missing (fleet started with
  an old script/bridge build). Re-run the script so `cross_trust` registers the keys, and check
  the web log for `[GrantSync] … imported 0 grants` warnings.
- **Building fails while nodes run** — `dotnet build` of the bridge can conflict with running
  instances started via `dotnet run`; `--stop` first.
