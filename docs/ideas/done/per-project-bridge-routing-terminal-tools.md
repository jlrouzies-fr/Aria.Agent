# Plan: Per-Project Bridge Routing for Terminal Tools

## Objective

Allow a single agent session to route its **LLM calls to one bridge node** (e.g., PC A) while routing **Terminal tool calls and the `#` file picker to another bridge node** (e.g., PC B), based on the bridge node bound to each Terminal project. Also make the Terminal system prompt platform-aware using the target bridge node's reported OS.

This intentionally departs from the original "one session = one node" v1 scope in `docs/Idea/bridge-remote-nodes-feature-plan.md`, but reuses the already-implemented multi-node registry.

## In Scope

- Per Terminal project: bind to a bridge node + cache its platform.
- LLM routed to the active channel's `BridgeNodeId`.
- Terminal builtins (`bash_exec`, `read_file`, `write_file`, `edit_file`, `list_dir`, `glob`, `commands_index`) routed per project node.
- `#` file picker lists/reads files on the active project's node.
- Terminal addendum adapted to the target node's platform (Windows / macOS / Linux).
- Backward compatibility: existing `AllowedPaths` JSON without `nodeId`/`platform` defaults to the LLM/default node and unknown platform.

## Out of Scope (for this plan)

- Per-bridge-node routing for other tools (web search, date/time, MCP servers). They stay on the LLM/default node.
- Offline-node auto-fallback. A project bound to an offline node will error/skip rather than silently run elsewhere.
- Data replication / custody changes from §11 of the remote-nodes plan.

## Key Files

- `src/AriaAgent/Aria.Web/Services/ModelBridge/ProjectFilesClient.cs`
- `src/AriaAgent/Aria.Web/Components/Layout/NavMenu.Tools.razor.cs`
- `src/AriaAgent/Aria.Web/Components/Layout/NavMenuToolsPanel.razor`
- `src/AriaAgent/Aria.Web/Services/Llm/WebHarnessRuntime.cs`
- `src/AriaAgent/Aria.Harness/Core/IHarnessRuntime.cs`
- `src/AriaAgent/Aria.Harness/Models/BridgeHttpHandler.cs`
- `src/AriaAgent/Aria.Harness/Bridge/BridgeMcpTool.cs`
- `src/AriaAgent/Aria.Harness/Core/Harness.cs`
- `src/AriaAgent/Aria.Web/Services/AgentServices/AgentService.cs`
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.FilePicker.razor.cs`
- `src/AriaAgent/Aria.Web/Services/Chat/UserSessionState.cs`

## Implementation Steps

### 1. Data model: TerminalProject gains NodeId + Platform

- Update `TerminalProject` in `ProjectFilesClient.cs`:
  ```csharp
  public record TerminalProject(
      string Name,
      string Path,
      string Description,
      string? NodeId = null,
      string? Platform = null);
  ```
- Update `ParseProjects` to read optional `nodeId` and `platform` JSON properties.

### 2. Tool config UI: per-project bridge node picker

- Change `NavMenu.Tools.razor.cs` `_pathListEntries` from `(string Name, string Path, string Description)` to `(string Name, string Path, string Description, string? NodeId, string? Platform)`.
- Add helper `UpdatePathEntryNodeId(int idx, string? value)` and `UpdatePathEntryPlatform(int idx, string? value)`.
- In `SyncPathList`, serialize `nodeId` and `platform`.
- In `OpenModalAsync`, populate NodeId/Platform from `ParseProjects`.
- In `NavMenuToolsPanel.razor`, inside each path-entry group, add a `ThemedSelect` bound to the project's `NodeId`. Options come from a new helper `Menu.BridgeNodeOptions()` (already exists from the channel panel work) plus a `""` option meaning "use LLM/default node".
- When a node is selected, also capture its `Platform` into the entry so it can be serialized and displayed.

### 3. Runtime: thread `nodeId` through bridge calls

- `IHarnessRuntime`:
  - Add optional `string? nodeId = null` to `BridgePostAsync` and `BridgeStreamAsync`.
- `WebHarnessRuntime`:
  - Pass `nodeId` to `_bridge.SendRequestAsync(..., nodeId)` and `_bridge.SendLocalRestAsync(..., nodeId)`.
- `BridgeHttpHandler`:
  - Add `string? nodeId` constructor parameter.
  - Pass it to `_runtime.BridgeStreamAsync(..., nodeId)`.
- `BridgeMcpTool`:
  - Add `string? nodeId` constructor parameter.
  - Pass it to `_runtime.BridgePostAsync(..., nodeId)`.

### 4. Harness: split LLM node from Terminal tool nodes

- `Harness.BuildChatClient`:
  - Accept `string? bridgeNodeId`.
  - Pass it to `new BridgeHttpHandler(..., bridgeNodeId)`.
- `Harness.CreateSessionAsync`:
  - Resolve the LLM node from `options.SelectedSourceName` → source's `BridgeNodeId` (or `options.BridgeNodeId` if already set).
  - Pass that node into `BuildChatClient(..., llmNodeId)`.
  - In the `terminal` case:
    - Parse projects and group by `NodeId` (null/empty groups to the LLM node).
    - For each distinct target node, call `LoadBridgeToolsAsync(builtinSrv, context, ..., nodeId)` and create `BridgeMcpTool(..., nodeId)` for each returned tool.
    - Aggregate all per-node tools into the main tool list.
    - Pass a dictionary of `nodeId → platform` to `BuildTerminalAddendum`.
- `AgentService.BuildTerminalAddendum`:
  - Accept `IReadOnlyDictionary<string?, string> nodePlatforms` (or similar).
  - Emit OS-specific shell/path guidance per project based on its node's platform. If platform unknown, keep today's Windows-centric default.

### 5. File picker: list/read on the project's node

- `ProjectFilesClient.ListFilesAsync` / `ReadFileAsync`:
  - Add optional `string? nodeId = null` parameter.
  - Pass it to `registry.SendLocalRestAsync(..., nodeId)`.
- `Chat.FilePicker.razor.cs`:
  - In `OpenFilePickerAsync`, pass `project.NodeId` to `ProjectFilesClient.ListFilesAsync`.
- `UserSessionState`:
  - No schema change needed; `Projects` already parses from the Terminal config JSON.
  - `EnsureActiveProject` should continue defaulting to the first project; the file picker will now hit that project's node.

### 6. Backward compatibility & error handling

- Missing `nodeId` in JSON ⇒ null ⇒ route to LLM/default node (today's behavior).
- Missing `platform` ⇒ null ⇒ fall back to generic guidance.
- If a project's bound node is offline at session creation:
  - Skip Terminal tools for that node and log a warning, **or**
  - Throw a clear error so the user knows the remote machine is unavailable.
  - Recommendation: throw for now (`InvalidOperationException($"Project '{name}' is bound to node '{nodeId}' which is offline")`) — silent fallback to another machine would violate the user's intent.

### 7. Verification

1. Two bridges (A and B) enrolled for the same soul.
2. Create a local channel bound to Bridge A.
3. In Tools → Terminal, add a project with path `C:\src\proj` (or `/home/user/proj`) and bind it to Bridge B.
4. Start a cogitation with that channel.
5. Ask the agent to list files in the project; confirm the `#` picker shows files from Bridge B.
6. Ask the agent to read a file; confirm the tool call is served by Bridge B.
7. Confirm LLM responses stream through Bridge A (e.g., by checking Bridge A logs or stopping Bridge B and seeing LLM still work while file tools fail).
8. Verify existing single-node users see no change.

## Rollout Notes

- No DB migration is required: Terminal project metadata lives in the existing `UserToolConfig.ConfigJson` blob.
- No bridge version bump is required unless bridge behavior changes (it doesn't in this plan — the bridge still serves its own `localhost:5741` tools).
- Build and restart both apps after changes per `AGENTS.md`.
