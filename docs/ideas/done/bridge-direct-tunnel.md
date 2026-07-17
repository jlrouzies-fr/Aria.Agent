# Task: Aria.Bridge Direct Tunnel (Remove Browser Dependency)

## Problem

Current routing requires the browser tab to stay open:

```
Server ──SignalR──► Browser (WASM / BridgeComponent) ──HTTP──► Aria.Bridge (127.0.0.1:5741)
```

If the tab is closed or the browser throttles it, all LLM calls via BRIDGED local sources and
all cloud provider calls (key-custody path) break silently.

## Solution

Aria.Bridge opens an outbound SignalR connection directly to the Aria server on startup —
same "pull agent" pattern as Azure DevOps / GitHub Actions self-hosted runners:

```
Server ◄──SignalR── Aria.Bridge (outbound on startup, no browser involved)
```

Server pushes `HandleRequest` / `LocalRestRequest` down that connection.
Bridge executes locally (LLM proxy, key injection, MCP tools, soul auth) and streams back.

## What Needs to Change

### Aria.Bridge
- Add `Microsoft.AspNetCore.SignalR.Client` NuGet package
- On startup, read server URL + registration token from config (`appsettings.json` or env)
- Connect to `https://<aria-server>/bridge-hub` and register with user identity
- Implement `HandleRequest` handler (already has `/llm/proxy` — just wire it internally)
- Implement `LocalRestRequest` handler (key CRUD, MCP tool calls)
- Reconnect with exponential backoff on disconnect
- Soul auth challenge-response (currently done in WASM BridgeComponent)

### Aria.Web (Server Hub)
- Extend or add a hub that accepts direct bridge connections (not just WASM)
- Route `HandleRequest` to either: direct bridge connection OR WASM fallback (for users without a running bridge daemon)
- Map bridge connections by user ID (not browser `BridgeSessionId`)
- Add a registration endpoint / token issuance for bridge auth

### Aria.Web.Client (WASM)
- BridgeComponent becomes **optional fallback** for users who don't run Aria.Bridge as a daemon
- If a direct bridge connection exists for the user, WASM relay is bypassed entirely

### FormatProber
- Currently invoked from WASM on model/source change → move trigger to bridge-side
  (bridge can probe on its own SignalR connection and report formats back)

## Registration / Auth

Options (pick one):
- **Soul-key signed token**: Bridge signs a timestamp with the user's soul key; server verifies
- **Simple registration token**: Server issues a long-lived token per user; stored in bridge config
- **OAuth device flow**: Cleaner but more work

## Config (Aria.Bridge `appsettings.json`)
```json
{
  "AriaServer": {
    "Url": "https://aria.example.com",
    "UserId": 1,
    "Token": "..."
  }
}
```

## Result
- Browser tab no longer required for LLM calls or cloud key injection
- Bridge can run as a system service / LaunchAgent / Windows Service
- Latency slightly lower (one less hop)
- Foundation for future: bridge can run on a different machine from the browser
