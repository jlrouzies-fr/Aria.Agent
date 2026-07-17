# Reference: WASM browser-direct bridge

**This folder is reference-only. It is not part of the build** (it lives outside every `.csproj`).

It preserves the version of `BridgeComponent.razor` from when the **browser itself** made the
outbound HTTP calls — the WASM component fetched the local model / Hindsight (`localhost:8888`)
**directly from the browser**, and only routed cloud providers through the local node.

## Why it's kept

It's a clean, self-contained implementation of a useful pattern: *let a remotely-hosted Blazor
Server app reach resources on the visitor's own machine through their browser, with no VPN and no
local agent* — as long as those resources send CORS headers and are reachable from the browser
(`localhost`, or LAN over HTTPS-exempt origins).

## Why the live app moved on

The live `Aria.Web.Client/BridgeComponent.razor` now routes **all** model + memory calls through the
local cogitator node (`aria-mcp-bridge`, `127.0.0.1:5741`) instead of fetching them from the browser.
The node (a local process, not a browser) makes the outbound call, which:

- avoids browser **CORS** entirely (the local model / Hindsight no longer need CORS headers),
- fixes **mixed content** (an HTTPS-hosted app can't `fetch()` a `http://192.168.x.x` LAN model from
  the browser; the node can),
- keeps cloud API keys on the node, and gives a single egress for everything.

If you ever want the browser-direct topology (no local node required for local/LAN models, at the
cost of CORS + mixed-content constraints), this file is the starting point.
