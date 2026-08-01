# // RITES OF INITIALISATION — Setup & Configuration

[← Back to the cogitator terminal](../../README.md)

Everything you need to awaken Aria. The lore is flavour; the commands underneath are real.

- [Prerequisites](#prerequisites)
- [The Cogitator Node (aria-mcp-bridge)](#the-cogitator-node-aria-mcp-bridge)
- [Running Aria.Web](#running-ariaweb)
- [LLM channels (machine spirits)](#llm-channels-machine-spirits)
- [Memory (Noosphere)](#memory-noosphere)
- [Tool rites (per-soul configuration)](#tool-rites-per-soul-configuration)
  - [Microsoft Graph](#microsoft-graph)
  - [Google Gmail / Calendar](#google-gmail--calendar)
  - [Web Search](#web-search)
  - [WAR.PLANNER](#warplanner)
  - [MCP servers](#mcp-servers)
  - [Voice Input (Vox)](#voice-input-vox)

---

## Prerequisites

- .NET 10.0 SDK or runtime
- A web browser (for Microsoft Graph / Google authentication on first run)
- An LLM source — either a local OpenAI-compatible endpoint (LM Studio, Ollama, etc.) **or** an API key for one of the built-in public cloud providers (OpenAI, Anthropic, Google Gemini, Mistral, Groq)

---

## The Cogitator Node (`aria-bridge`)

The **cogitator node** is a small local process that runs on your own machine. It holds your soul's keypair, your conversation history, and your API keys — none of which ever touch the Aria server. The web terminal stays locked until the node connects and proves your soul. (Full detail: [Architecture → Soul Identity](architecture.md#soul-identity--bridge-authentication).)

Run it:

```bash
./aria-bridge       # macOS / Linux
aria-bridge.exe     # Windows
# …or from source:
dotnet run --project src/AriaAgent/Aria.Bridge
```

It binds to [http://localhost:5741](http://localhost:5741) (loopback only — not reachable from the network). Keep it running while using Aria.

Once a soul is linked, the node opens a **direct tunnel** — a persistent outbound SignalR connection to the Aria server. Model calls, key injection, MCP tool spawning, and soul verification all route through the node with no browser tab required.

**Status page & telemetry.** The node's status page (`/`) shows uptime, sessions, and live CPU/RAM/GPU metrics. In the web chat, the thin **Bridge Telemetry** rail on the right edge surfaces the same metrics; on macOS you can grant sudo once from the status page to add GPU power readings.

**Browser auto-open.** The node opens a status page in your default browser on first start. To disable this (e.g. when running headlessly or as a background service), set in `appsettings.json` next to the binary:

```json
{
  "Bridge": { "OpenBrowserOnStart": false }
}
```

---

## Running Aria.Web

```bash
cd src/AriaAgent
dotnet run --project Aria.Web
```

Open [http://localhost:5129](http://localhost:5129).

1. Start your cogitator node (above) and create/link a **soul** in it.
2. Back in the terminal, the soul binds automatically once verified (green light by the soul name).
3. Enable tools via the `// TOOLS` section — each is configured via the ⚙ gear icon.
4. Past cogitations (conversations) appear in `// COGITATIONS` and can be reopened.

> **Database:** the web server's `aria.db` (next to the binary) holds non-secret per-soul settings; your cogitations and keys live on the cogitator node. Delete `aria.db` to reset server-side state.

---

## LLM channels (machine spirits)

### Authored on the bridge, mirrored read-only in `Aria.Web`

All channel authoring — URLs, models, and keys, for both local and cloud providers — happens on the **bridge status page** ([http://localhost:5741](http://localhost:5741) → **Channels** tab), never in `Aria.Web` itself. The web UI's `// CHANNEL` panel is a read-only mirror: it lets you pick which channel/model a soul uses and shows key status (`● key stored` / `○ no key`), with a **CONFIGURE CHANNELS ON YOUR BRIDGE ↗** link straight to the bridge tab. There is no in-app key editor in `Aria.Web` — clicking a provider with no key stored is a no-op there by design.

On the bridge's Channels tab:

- **Cloud providers** — OpenAI, Anthropic, Google Gemini, Mistral, Groq. Paste your API key into the provider's field and click **SAVE**; it's encrypted at rest in the node's local vault.
- **Custom / self-hosted endpoints** — LM Studio, Ollama, llama.cpp, or a remote machine on your LAN. Add a name, URL (`http://127.0.0.1:1234/v1` for LM Studio, `http://localhost:11434/v1` for Ollama), model list, and an optional API key — pasted directly, not a file path.

The cogitator node makes every LLM call (not the browser, not the server), so your model server does **not** need CORS, and a LAN model over plain HTTP works even when Aria is hosted over HTTPS. Keys are stored in the node's SQLite vault and injected at the last moment; they never reach `Aria.Web` at rest or in transit.

### Public cloud providers

Five providers are built in, with a fixed canonical endpoint each — no `appsettings.json` changes required:

| Provider | Free tier | Models |
|---|---|---|
| **OpenAI** | No | gpt-4o, gpt-4o-mini, o3, o4-mini |
| **Anthropic** | No | claude-opus-4-8, claude-sonnet-4-6, claude-haiku-4-5-20251001 |
| **Google Gemini** | Yes — [aistudio.google.com](https://aistudio.google.com) | gemini-2.5-pro, gemini-2.5-flash |
| **Mistral** | No | mistral-large-latest, mistral-small-latest, codestral-latest |
| **Groq** | Yes — [console.groq.com](https://console.groq.com) | llama-3.3-70b-versatile, meta-llama/llama-4-scout-17b-16e-instruct, qwen/qwen3-32b, llama-3.1-8b-instant |

**To configure a provider:**

1. Bind a soul (your cogitator node must be running).
2. Open the bridge status page ([http://localhost:5741](http://localhost:5741)) → **Channels** tab → **Cloud Providers**.
3. Paste your API key next to the provider name and click **SAVE**.
4. Back in `Aria.Web`'s `// CHANNEL` panel, the provider now shows `● key stored` and can be selected.

The key is stored **on your cogitator node**, never on the Aria server. A stored key is only ever sent to that provider's fixed official endpoint — the server can neither read it nor redirect it. When you chat, the server builds the request with no key and routes it through the bridge daemon to the node's `/llm/proxy`, which injects your key and calls the provider — so the API key never touches the Aria server, at rest or in transit. Thinking-format and tool-call probing are skipped for cloud providers (they use native OpenAI streaming).

---

## Memory (Noosphere)

Aria uses **Noosphere**, a native bridge-local memory system. There is no external memory service to install and no Docker container to run.

How it works:

- The agent can call memory tools (`Inscribe`, `Probe`, `Contemplate`) to save facts, recall them, and synthesise answers from stored engrams.
- Engrams, entities, and their relationships are stored in the cogitator node's SQLite vault.
- Extraction (turning chat text into structured engrams) and embeddings (vector recall) run on the node — either from **built-in models** or from a channel you configure (LM Studio, Ollama, cloud). You can also disable embeddings and rely on full-text/graph recall.

**Setup — built-in models (recommended if you do not want a third-party inference engine for memory):**

1. Open the bridge status page ([http://localhost:5741](http://localhost:5741)) → **Memory** tab.
2. Under **// Built-in models**, accept the LFM Open License, pick an extract GGUF (LFM2.5-1.2B or LFM2-2.6B, Q4/Q5/Q6 — recommended **2.6B Q5**), download it plus MiniLM embeddings, enable built-in, and **APPLY**.
3. Expected RAM while both models are loaded: roughly **~1.2–1.7&nbsp;GB** (~1.0–1.5&nbsp;GB extract + ~100–200&nbsp;MB embed). Channel pickers are hidden while built-in is on.
4. Enable **Memory (Noosphere)** in `Aria.Web`'s `// TOOLS` section.

**Setup — external channels (LM Studio / Ollama / cloud):**

1. Configure at least one channel on the bridge (see [LLM channels](#llm-channels-machine-spirits)).
2. Open the bridge **Memory** tab with built-in **off** — this is the only place extraction/embeddings channels are configured; it is not exposed in `Aria.Web`.
3. Pick an **extraction channel** and an optional **embeddings channel** → **SAVE**.
4. Enable **Memory (Noosphere)** in `Aria.Web`'s `// TOOLS` section.

In `Aria.Web`, the `// NOOSPHERE` sidebar item opens a **browse/query page** for stored engrams — it shows an "AUGUR ARRAY OFFLINE" banner if embeddings are unavailable, but has no settings of its own. A red warning tip on that nav item appears when the node's last extraction failed (e.g. LM Studio down). No API keys leave your machine; the node talks to built-in models or your local/cloud endpoint directly.

> **Note:** Aria previously used the external [Hindsight](https://github.com/whoiskatrin/hindsight) service. That integration has been removed; the old setup steps are preserved in [archived/hindsight.md](archived/hindsight.md) for reference only.

---

## Tool rites (per-soul configuration)

In `Aria.Web`, tools are enabled per soul through the UI, but most of the actual configuration — credentials, MCP servers, channels — is authored on the bridge:

- Enable/disable tools in `// TOOLS`.
- Open the ⚙ gear icon on a tool for its settings.
- Bridge-local credentials (Microsoft, Google, Web Search) and MCP servers are entered on the cogitator node's status page ([http://localhost:5741](http://localhost:5741)); the `Aria.Web` UI only shows connection status, availability, and links out to the bridge.

### Microsoft Graph

**App Registration (one-time):**

1. [Azure Portal](https://portal.azure.com) → **Microsoft Entra ID** → **App registrations** → **New registration**.
2. Name it (e.g. `aria-agent`). Under **Supported account types** choose *Personal Microsoft accounts only* (personal Outlook/Hotmail) or *Accounts in any org directory and personal Microsoft accounts* (both).
3. **Register**, then copy the **Application (client) ID**.
4. **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated** → add `Mail.Read` and `Calendars.Read`.
5. **For personal accounts only:** **Manifest** → set `"accessTokenAcceptedVersion"` to `2` → save.

**OAuth2 Authorization Code flow via the cogitator node (needs client secret):**

The redirect and token exchange happen on the bridge, not the server, so the server never sees the client secret or the token.

6. App Registration → **Authentication** → **Add a platform** → **Web** → redirect URI `http://localhost:5741/oauth/microsoft/callback`.
7. Leave **Implicit grant** unchecked.
8. **Certificates & secrets** → **New client secret** → copy the value immediately.
9. On the bridge status page ([http://localhost:5741](http://localhost:5741)) → **OAuth** tab → **App Credentials** → enter the **Tenant ID**, **Application (client) ID**, and paste the **client secret** → **SAVE**. It's encrypted at rest in the node's local vault and overrides any `Auth:Microsoft` value in `appsettings.json`; leave it unset to keep using the appsettings.json value (if any), or click **RESET TO APPSETTINGS** to drop the override.

Then open the ⚙ on **Microsoft Email** / **Microsoft Calendar** → **OPEN BRIDGE LOGIN**. The popup goes to `http://localhost:5741/oauth/microsoft/connect`; after consent the bridge stores the token locally and the tool shows **● Connected as your@email.com**.

### Google Gmail / Calendar

A personal Gmail account is enough — no Workspace required.

**Google Cloud setup (one-time):**

1. [console.cloud.google.com](https://console.cloud.google.com) → create a project (personal Gmail).
2. **APIs & Services → Library** — enable **Gmail API** and **Google Calendar API**.
3. **OAuth consent screen** — **External**, fill app name + your email, add your account as a **Test user**.
4. **Credentials → Create Credentials → OAuth client ID** → **Desktop app** → **Create**.
5. **Download JSON** and save it:
   ```bash
   mv ~/Downloads/client_secret_*.json ~/.aria-agent/google-credentials.json
   ```

> **Is the JSON "client secret" sensitive?** For Desktop-app credentials Google states it is not confidential — it can't access user data without explicit browser consent. Safe to store on disk outside version control.

**OAuth2 Authorization Code flow via the cogitator node:**

The redirect and token exchange happen on the bridge, not the server.

6. Google Cloud Console → **Credentials** → your Desktop App credential → **Authorized redirect URIs** → add `http://localhost:5741/oauth/google/callback` → save.
7. On the bridge status page ([http://localhost:5741](http://localhost:5741)) → **OAuth** tab → **App Credentials** → paste the whole downloaded JSON into the Google field → **SAVE**. The client id/secret are extracted from it automatically, encrypted at rest, and override any `Auth:Google` value in `appsettings.json`; **RESET TO APPSETTINGS** drops the override.

Then open the ⚙ on **Gmail** / **Google Calendar** → **OPEN BRIDGE LOGIN**. The popup goes to `http://localhost:5741/oauth/google/connect`; the bridge stores the token locally.

### Web Search

Uses Ollama's `/api/web_search` endpoint. The call is made by the cogitator node, so the API key stays on your machine and the server never sees it.

1. Get an API key from [ollama.com](https://ollama.com) → your account → **API Keys**.
2. On the bridge status page ([http://localhost:5741](http://localhost:5741)) → **Tools / MCP** → **Web Search (Ollama)** → paste the key → **SAVE**. It's encrypted at rest in the node's local vault, the same as cloud LLM keys (see [LLM channels](#llm-channels-machine-spirits)) — never in a config file.
3. In **Aria.Web**, enable **Web Search** in the `// TOOLS` section. The base URL defaults to `https://ollama.com`; there's nothing else to configure.

### WAR.PLANNER

The wargame tool needs no external credentials. Enable **⚔️ WAR.PLANNER** in `// TOOLS`; the agent can then call `GetWarSituationReport` for a strategic briefing (turn, faction resources, unit positions, buildings, recent combat).

The game itself lives at `/wargame`. Generate a map by specifying faction names, races (`Empire`, `Greenskins`, `Chaos`, `Undead`), model sources, and colours. Each AI faction runs its own model; the game loop runs as a background `IHostedService` and renders in real time on the canvas.

### MCP servers

MCP servers are authored exclusively on the bridge status page ([http://localhost:5741](http://localhost:5741) → **Tools / MCP** tab) — never in `Aria.Web`. The `Aria.Web` MCP tool modal is read-only: "Servers are configured on the bridge at [http://localhost:5741](http://localhost:5741). This panel only shows which ones are available." Two transports:

| Transport | When to use |
|---|---|
| **LOCAL BRIDGE** | A local process (`npx`, `dotnet run`, `python`, …), spawned and kept alive by your cogitator node. This is the default for stdio-style servers, whether `Aria.Web` is local or hosted remotely. |
| **SSE** | A remote HTTP endpoint (publicly hosted MCP server). Enter the URL — no local process needed. |

For LOCAL BRIDGE: name, command, arguments (one per line), optional env vars (`KEY=VALUE`, one per line). For SSE: name + endpoint URL. The node spawns the process on first call and keeps it alive for 10 minutes of idle time — no config file needed on your machine.

### Voice Input (Vox)

Dictate directives with the 🎙 **Vox** button beside the chat input. Configure it in **Tools → Voice Input (⚙)** — the choice is stored per soul. Whichever channel you pick, the recorded audio goes **straight from your browser to your node**, never the server.

| Transcription channel | Setup | Notes |
|---|---|---|
| **Browser (built-in)** | None | Default. Uses the browser's Web Speech API. No download, but it depends on the browser vendor's cloud and is unavailable in some browsers (Edge-on-macOS, Brave/Arc) — you'll see a "speech engine unreachable" fault there. Try Chrome or Safari, or switch to Local Whisper. |
| **Local Whisper (offline)** | One-time model download | Fully on-device `whisper.cpp` on your node — offline, no API key, works in **any** browser. Pick a size (Tiny ≈77 MB · Base ≈148 MB · Small ≈488 MB · Medium ≈1.5 GB) and click **Download**; it caches on the node and is reused offline forever. **Base** is the recommended balance; Tiny is noticeably less accurate. |
| **OpenAI / Groq (Whisper)** | A cloud key on your node | Highest accuracy. Save the provider key on the bridge's **Channels** tab first (see [LLM channels](#llm-channels-machine-spirits)); the node injects it and the key never reaches the server. |

Optionally set a **Fixing Channel** — any configured LLM that repairs punctuation, filler words, and mishearings on the raw transcript before it lands in the input box. Leave it as **None** to use Whisper's output verbatim (Base/Small are usually clean enough). A plain instruct model works best here; a reasoning model wastes latency on chain-of-thought you don't see.

The models are on-device, so the **first** transcription after a node restart has a ~1–2 s cold start while the model loads into RAM; instant after that. See [Bridge Features → Voice transcription](bridge-features.md#voice-transcription) for the trust model (audio direct, control-plane via tunnel).
