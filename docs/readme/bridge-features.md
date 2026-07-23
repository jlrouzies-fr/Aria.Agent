# Cogitator Node Features (`Aria.Bridge`)

[← Back to the cogitator terminal](../../README.md)

The **cogitator node** (`aria-bridge`) is the local daemon that runs on your own machine. It holds your identity, your secrets, and your local execution context. The hosted `Aria.Web` server orchestrates chat sessions, but the node performs every sensitive action.

- [Local-first binding](#local-first-binding)
- [Soul identity](#soul-identity)
- [Direct tunnel](#direct-tunnel)
- [OAuth (Microsoft / Google)](#oauth-microsoft--google)
- [Web search](#web-search)
- [Built-in tools](#built-in-tools)
- [MCP server spawning](#mcp-server-spawning)
- [LLM proxy & cloud keys](#llm-proxy--cloud-keys)
- [Voice transcription](#voice-transcription)
- [Telemetry](#telemetry)
- [Console sync](#console-sync)

---

## Local-first binding

The node binds to [http://localhost:5741](http://localhost:5741) (loopback only). Nothing on the network can reach it; only software running on your own machine can connect. This is the trust anchor for every security guarantee in the architecture.

```bash
# Run from source
dotnet run --project src/AriaAgent/Aria.Bridge
```

The status page opens automatically at [http://localhost:5741](http://localhost:5741) unless you disable it:

```json
{
  "Bridge": { "OpenBrowserOnStart": false }
}
```

## Soul identity

A **soul** is an ECDSA P-256 keypair generated and stored only by the node. The private key never leaves your machine. The server stores only the public key, and the node proves possession by signing server challenges over the direct tunnel. Until the node proves it, the web terminal stays locked.

Use the status page to create a soul, link it to a server, import/export it, or rotate keys.

### Vault location (0.8.0+)

The local vault (soul keys, cogitations, provider keys, OAuth tokens) lives in per-user app data —
`%APPDATA%\aria-bridge\aria-bridge.db` on Windows, `~/Library/Application Support/aria-bridge/` on
macOS — **not** next to the executable. Earlier versions kept it beside the binary, so reinstalling
the bridge wiped the soul. On first run, a legacy vault found next to the executable is migrated
automatically (`BridgeDatabaseInitializer.ResolveDbPath`).

## Direct tunnel

The node opens a persistent outbound SignalR connection to `Aria.Web`. Because WebSockets are bidirectional, the server can push requests to the node even though the node is behind NAT. The tunnel carries:

- LLM requests and streaming SSE responses
- Local REST calls (keys, MCP, memory, cogitations)
- Chunked transcription audio
- Soul authentication challenges

## OAuth (Microsoft / Google)

Microsoft and Google OAuth credentials live in the node's `appsettings.json`, not the server DB. The OAuth flow runs entirely on the node:

1. You register redirect URIs pointing to `http://localhost:5741/oauth/{provider}/callback`.
2. Credentials go in `Aria.Bridge/appsettings.json` under `Auth:Microsoft` or `Auth:Google`.
3. In the web UI, **LOGIN WITH MICROSOFT** / **LOGIN WITH GOOGLE** opens a popup to `http://localhost:5741/oauth/{provider}/connect`.
4. The node completes the authorization-code exchange and stores the refresh/access token in its local SQLite vault.
5. The web UI fetches tokens from the node at call time; the server never sees client secrets or tokens.

If credentials are missing, the connect endpoint returns a clear configuration error instead of a 404.

## Web search

Web search is also node-local. The node calls Ollama's `/api/web_search` endpoint using a key file on your machine:

```json
{
  "OllamaWebSearch": {
    "ApiKeyFile": "~/.config/aria-agent/ollama.key",
    "Enabled": true
  }
}
```

The base URL defaults to `https://ollama.com`. The server UI only enables/disables the tool; it does not store the key file path or URL.

## Built-in tools

The node exposes native shell, file, command-index, datetime, and web-search tools without spawning external processes:

| Tool | What it does |
|---|---|
| `bash_exec` | Run a shell command (governed by allowed/blocked lists) |
| `read_file` / `write_file` / `edit_file` | Read, write, or diff-based edit local files |
| `list_dir` / `glob` | Directory listing and file globbing |
| `grep` | Regex/substring content search (caps results, skips binaries and dependency dirs) |
| `git_status` / `git_diff` / `git_log` | Read-only repository inspection |
| `git_stage` / `git_commit` / `git_discard` | Repository mutations (discard requires explicit paths; high-stakes) |
| `commands_index` | Recall build/run commands for common stacks |
| `install_software` | Install a package via an allowlisted manager (brew/npm/pip/pipx/dotnet/cargo/go); approval-gated in every governed mode |
| `system_info` | Read-only environment recon: OS/arch, shell, CPU/RAM, disk free, available package managers and runtimes |
| `multi_edit` | Batch of exact-string edits to one file in a single call (unique-at-apply-time, atomic, one undo entry) |
| `undo_file` | Restore the most recent undo snapshot for a file (stack semantics; itself undoable) |
| `process_list` / `process_output` / `process_kill` | Manage background jobs started by `bash_exec background:true` (kill only works on registry-tracked pids) |
| `run_background` | Start a long-running process (dev server, watcher) — returns immediately with pid/log; same command gate as `bash_exec` |
| `wait_for` | Wait for a TCP port, a URL to respond, or a background job's log to match a pattern (readiness probe) |
| `http_request` | Structured HTTP client (methods/headers/body, status + raw response, no auto-redirect); Sensitive — it can reach localhost/LAN from the node |
| `read_image` | Feed a local image (png/jpeg/gif/webp, magic-byte sniffed, ≤10MB) to vision-capable models |
| `GetCurrentDateTime` | Current local date/time |
| `SearchWeb` | Ollama web search |

These are governed by the same `GovernanceMode` rules as MCP tools and can escalate to an **Inquisitorial Seal** in Paranoid mode.

### Terminal capability gate (0.25.0+)

The shell tool and the web terminal are **opt-in per node and off by default**. Even if the Terminal tool is enabled in the web UI, the node refuses `bash_exec`, `/terminal/exec`, and PTY requests until a human turns on **Terminal Capability** on the bridge status page ([http://localhost:5741](http://localhost:5741), Telemetry tab). PTY mode still requires its own time-limited Inquisitorial Seal grant on top of the master toggle.

In the web UI, the chat **TERMINAL** button is disabled when the bridge reports Terminal Capability off, and the Terminal tool-options modal is split into **Projects** (file-picker paths, always editable) and **Real Terminal** (blocked commands + bridge enablement status) so the two concerns don't collide.

## MCP server spawning

The node spawns stdio MCP servers on demand and keeps them alive for 10 minutes of idle time. Servers configured in the web UI are pushed to the node; the node manages the child processes and exposes their tools through `/tools/list` and `/tools/call`.

## LLM proxy & cloud keys

Cloud-provider API keys are stored on the node. When the server routes a model call through the tunnel, the node injects the key at the last moment and forwards the request. The server builds requests without keys; the node adds them. Keys never reach `Aria.Web` at rest or in transit.

Local-model calls also route through the node, so a LAN HTTP model works even when `Aria.Web` is hosted over HTTPS.

### Cross-node key sync (0.9.0+)

With several bridges on one soul, the **executing** bridge needs the key in **its own** vault.
Keys replicate between nodes as blobs encrypted with the soul's shared sync data key (§11 DEK):
`GET /keys/sync-export` produces the ciphertext, `POST /keys/sync-import` decrypts and merges
(upsert per provider, never deletes). The server only relays ciphertext. The mesh runs after every
key save and node connect; see [Multi-node routing](multi-node.md) for the full picture.

### Egress log (0.9.1+)

`GET /debug/llm-log` returns the last 25 outbound LLM calls — URL, whether an auth header was
attached, status, content-type, and the first 600 bytes of the response. Readable locally or from
the server over the tunnel (`/api/maintenance/node-llm-log`), it turns "the chat shows nothing"
into "LM Studio said `invalid_api_key`" without shell access to the machine.

## Voice transcription

The chat's **Vox** button (🎙) turns speech into text through one of three channels, chosen per soul in **Tools → Voice Input**. In every case the audio is posted **straight from the browser to the node** (`http://localhost:5741/transcribe…`), never the server.

- **Browser (default, no setup)** — the browser's own Web Speech API. Zero config, but it relies on the browser vendor's cloud (Google for Chrome/Chromium) and is unavailable in some browsers (Edge-on-macOS, Brave/Arc), which surfaces as a "speech engine unreachable" fault.
- **On-device Whisper (1.9.0+)** — fully offline `whisper.cpp` (via `Whisper.net`) running on the node. No API key, no cloud, works in any browser. Pick a model size (Tiny ≈77 MB · Base ≈148 MB · Small ≈488 MB · Medium ≈1.5 GB) and download it once from **Tools → Voice Input**; models cache under the node's app-data dir (`whisper-models/`) and are reused offline forever. whisper.cpp's non-speech tokens (`[BLANK_AUDIO]`, etc.) are stripped so silence yields empty text.
- **Cloud Whisper (OpenAI / Groq)** — highest accuracy. The node injects your stored key and calls the provider; the key never reaches the server.

**Split trust model.** The privacy-sensitive audio always goes browser→node direct (`POST /transcribe/local` or `/transcribe`), allow-listed in the node's local-origin guard so voice never crosses the server. The harmless **control plane** (model status/download) instead routes through the authenticated server→node tunnel (`/transcribe/local/status`, `/transcribe/local/download`) — the node performs the actual model download from Hugging Face. A cross-origin page therefore cannot trigger a multi-gigabyte download on your node.

An optional **Fixing Channel** runs the raw transcript through any configured LLM to repair punctuation, filler words, and mishearings; reasoning/preamble from chatty models is stripped before the text lands in the input box.

## Telemetry

The status page shows live CPU, RAM, and best-effort GPU metrics. On macOS you can grant sudo once to enable GPU power readings via `powermetrics`; the password is piped once and never stored. The same metrics appear in the web chat's **Bridge Telemetry** rail.

## Console sync

`Aria.Console` does not keep its own copy of agents, tools, sources, or MCP servers. `Aria.Web` pushes a snapshot over the direct tunnel to the node (`POST /sync/apply`), and the console reads the mirrored tables through local-only endpoints (`/console/*`). Edit your setup in the web UI; use it immediately in the terminal.
