# // LOCAL DEV ENVIRONMENT — running Bridge + Web side by side

[← Agent notes](README.md)

Context: this machine already has a long-lived local dev setup — a soul, a linked `Aria.Web`
server, and LM Studio with several models on disk — from many prior sessions. That long-lived
state is exactly what makes it a good place to validate real end-to-end behavior (a real local
model, a real bridge, a real chat), but it also means the setup can drift out of sync between
sessions in ways a fresh `dotnet test` run will never surface. The notes below are about that
drift, not about the app's code.

## `Aria.Console` is off-limits for exercising a real chat

`Aria.Console` looks like the fastest way to script a real LLM conversation (no browser needed),
but don't reach for it:

- It hard-requires interactive `Spectre.Console` prompts for agent/source/model/tool selection
  (`ConsoleHelper.SelectSourceAndModel`, etc.) — there is no CLI-flag or env-var way to skip them,
  so it cannot be driven non-interactively without patching the source.
- It talks to the bridge directly (`Aria.Console.Harness.ConsoleHarnessRuntime` →
  `Aria.Harness.Core.Harness`) and **never sets `HarnessContext.CurrentTurnCheckpoint`** the way
  `Aria.Web`'s `CogitationRunRegistry` does — so anything checkpoint/`\`/rewind`-related is silently
  inert when driven through the console, even though the plumbing (`BridgeMcpTool` →
  `BuiltinTools.InvokeAsync`) is shared and would otherwise work.
- Per explicit product direction, treat `Aria.Console` as a legacy/ignorable surface when asked to
  validate real chat behavior. Use `Aria.Web` instead (see below), even though it requires a
  browser session from the human.

## Starting Bridge + Web for a live manual test

```bash
# Bridge (rebuild first if you changed Aria.Bridge/Aria.Harness/etc — it's a long-lived daemon,
# not something `dotnet test` rebuilds for you)
cd src/AriaAgent && dotnet build Aria.Bridge -m:1 -p:UseSharedCompilation=false -nodeReuse:false
cd Aria.Bridge/bin/Debug/net10.0/osx-arm64 && ./aria-bridge --urls "http://localhost:5741"

# Web
cd src/AriaAgent && dotnet run --project Aria.Web --launch-profile http
```

### Gotcha: `nohup ./aria-bridge &` inside a single shell-tool call does not survive

Backgrounding a process with plain `nohup cmd &` (even with `disown`) inside one shell-tool
invocation gets reaped as soon as that tool call returns — it looked alive for a few seconds
(healthy `curl` response) and then silently died with no error in its own log, because the log
only captures what the process itself writes, not "something outside killed me." If you need a
long-lived local server across multiple tool calls, start it as its own tracked background shell
job (i.e. the equivalent of `block_until_ms: 0` on its own dedicated call, not `&&`-chained after
other commands in the same call) — that is the only form that reliably outlives the call that
started it.

### Even tracked background jobs get SIGKILL'd unpredictably

Even when started correctly (its own tracked background shell job), both `aria-bridge` and
`Aria.Web` have been observed exiting with code `137` (SIGKILL) after anywhere from ~5 to ~35
minutes, with nothing in their own stdout/stderr explaining why — consistent with something
*outside* the process killing it (OS memory pressure, or the surrounding sandbox/session tooling
reclaiming long-lived background jobs), not an application crash. Don't assume "the last thing
I ran must have crashed the server" — check the shell job's actual exit code (`AwaitShell` with
`block_until_ms: 0`) before debugging the app. Practically, once you've confirmed both servers
are reachable, re-check `curl` health before every subsequent action that depends on them being up
rather than trusting an earlier "started successfully" — and be ready to just restart whichever one
died (bridge restart alone is a no-op for chat history/soul state, it's all on disk).

## Gotcha: stale soul-link between `Aria.Bridge` and `Aria.Web` ("Web cannot detect the seal")

Symptom: the browser's local-bridge detection is flaky/half-working — some checks pass (chat
"trusted circuit" discovery), but the bridge's own SignalR node connection to the web server keeps
connecting and immediately disconnecting, and the web log shows repeating:

```
[Bridge/Direct] Challenge issued for userId=<A>
[Bridge/Direct] No public key on record for userId=<A>
```

Root cause: **two independently-persisted local SQLite databases can drift apart.**
`Aria.Web`'s `aria.db` (`Users` table) and `Aria.Bridge`'s `aria-bridge.db` (`Souls` /
`ServerLinks` tables) each remember "the other side's" id independently. If `aria.db` ever gets
reset/recreated (e.g. during earlier testing) while the bridge's `Souls.ServerSoulId` still points
at the old (now nonexistent) `Users.Id`, you get exactly this symptom:

- `Aria.Web.Services.Auth.CircuitAuthService.DiscoverCompleteAsync` matches the soul by
  **public key** (`Users.PublicKey == nodePublicKeyB64`), so basic chat "trusted browser" discovery
  still works even with the stale id — this is why it looks "half broken" rather than fully dead.
- `Aria.Web.Services.ModelBridge.ModelBridgeHub.RegisterDirectBridge` is called by the **bridge's
  own SignalR client** using whatever `userId` it has cached as `Souls.ServerSoulId` — if that's
  stale, `db.Users.FirstOrDefaultAsync(u => u.Id == userId)` comes back null and every connection
  attempt is rejected and immediately disconnects.

Fix (one-time local data reconciliation, not a code change): find the *actual* current web user id
(from the `CircuitAuth ... verified for soul <id>` log lines, which use the id that actually
matched by public key) and point the bridge's stored links at it:

```bash
sqlite3 "$HOME/Library/Application Support/aria-bridge/aria-bridge.db" \
  "UPDATE Souls SET ServerSoulId='<correct-web-user-id>' WHERE ServerUrl='http://localhost:5129';" \
  "UPDATE ServerLinks SET ServerSoulId='<correct-web-user-id>' WHERE ServerUrl='http://localhost:5129';"
```

Then restart the bridge process so it reconnects with the corrected id. Confirm success by tailing
the web log for a `[Bridge/Direct] Challenge issued for userId=<correct-id>` line that is **not**
followed by a `Client disconnected` for that same connection id.

## Local LM Studio setup on this machine

- LM Studio's local server (port `1234`) now requires a bearer token by default
  (`lms server status` / `lms ps` / `lms ls` via the `lms` CLI are the quickest way to check what's
  loaded without hitting the HTTP API's auth wall).
- The bridge already has a configured, working channel for it — check
  `GET http://localhost:5741/channels`: look for `"name":"Mac - LM Studio"` with `"hasKey":true`.
  Don't try to re-derive LM Studio credentials; if that channel exists and has a key, use it as-is.
- There is also a separate `local-lmstudio-proxy` (Node, port `12345`) that forwards to a
  **different, remote** LM Studio instance over the LAN (`appconfig.json` → `targetUrl`). It is
  unrelated to the local bridge's own LM Studio channel above — don't confuse the two when a model
  name doesn't show up where you expect it, and don't wait long on requests to it from a sandboxed
  shell (the remote host may simply be unreachable from there).
