# Claude Code Prompt — Split Oversized Blazor Files

> Paste everything below into Claude Code.

---

## Task

Split oversized files in this Blazor project into smaller, focused files. Work **one file at a time** and follow the rules below strictly to **minimize token usage**.

**Target size:** aim for **200–300 lines per file**. This is a goal, not a hard rule — if a file genuinely can't be split below ~300 lines without hurting readability or breaking cohesion, **say so and leave it** rather than forcing an awkward split. Never break working code just to hit a line count.

**Scope:** both `.razor` markup files and `.cs` / `.razor.cs` C# files.

**Scripts:*** if you have to run Python, first make it as a script file, then execute it

---

## Step 1 and 2 — Discovery already done


| Lines | File | One-line split guess |
|-------|------|----------------------|
| ~~1827~~ | ~~`Aria.Web/Services/CollectiveOrchestrator.cs`~~ | ✅ Done — split into 6 partials: main, Conditions, Cogitation, Loop, Phases, DbHelpers |
| ~~1817~~ | ~~`Aria.Web/Components/Layout/NavMenu.razor.cs`~~ | ✅ Done — split into 6 partials: main, Bridge, Agents, Tools, Channels, Contacts |
| ~~1579~~ | ~~`Aria.Web/Components/Layout/NavMenu.razor`~~ | ✅ Done — extracted 8 flyout panels into child components |
| ~~1092~~ | ~~`Aria.Web/Components/Layout/NavMenu.razor`~~ | ✅ Done — modals moved into related panels; shell now 227 lines |
| ~~1546~~ | ~~`Aria.Web/Components/Pages/Chat.razor.cs`~~ | ✅ Done — split into 6 partials: base, Session, Messaging, Rendering, HiveGate, Vox |
| ~~1220~~ | ~~`Aria.Web/Services/AgentService.cs`~~ | ✅ Done — split into 6 partials in `Services/AgentService/`: main, FormatCache, ThinkingDetection, ToolCallDetection, Session, BridgeTools |
| ~~1194~~ | ~~`Aria.Bridge/Program.cs`~~ | ✅ Done — endpoint groups extracted to `Endpoints/`; `Program.cs` now ~16 lines |
| ~~1065~~ | ~~`Aria.Web/Services/WargameService.cs`~~ | ✅ Done — split into 3 partials in `Services/WargameService/`: State, Economy, Ai |
| ~~993~~ | ~~`Aria.Agent/UniversalReasoningHandler.cs`~~ | ✅ Done — split into `UniversalReasoningHandler.cs` + 5 `UniversalSSEStream` partials: core, Filtering, Thinking, ToolCalls, Rewriters |
| ~~854~~ | ~~`Aria.Web/Components/Pages/Hive.razor`~~ | ✅ Done — extracted `HiveSidebar`, `HiveCanvas`, `HiveTimeline`, `HiveOvermindDrawer`, `HiveDroneDrawer`, `HiveCogitateModal` |
| ~~845~~ | ~~`Aria.Web/Components/Pages/Hive.razor.cs`~~ | ✅ Done — split into 5 partials: main, Canvas, Config, Members, Cogitation |
| ~~687~~ | ~~`Aria.Bridge/BuiltinTools.cs`~~ | ✅ Done — split into `BuiltinTools/` partials: main, Shell, File, CommandsIndex; `terminal_help` renamed to `commands_index` |
| ~~661~~ | ~~`Aria.Web/Program.cs`~~ | ✅ Done — extracted `AddAriaServices` + `UseAriaPipeline`/`MapAriaEndpoints` into `ServiceCollectionExtensions.cs`/`WebApplicationExtensions.cs`; endpoints split into `Endpoints/` (BridgeNode, Soul, OAuth, Vox, Debug) |
| ~~652~~ | ~~`Aria.Web/Components/Shared/CronSchedulerPanel.razor`~~ | ✅ Done — split into shell + `CronScheduleView` + `CronVigilsView` |
| ~~418~~ | ~~`Aria.Web/Helpers/AgentSprites.cs`~~ | ✅ Done — sprite pixel data moved to `AgentSprites.Sprites.cs`; logic kept in `AgentSprites.cs` |
| ~~411~~ | ~~`Aria.Web/Components/Pages/Wargame.razor`~~ | ✅ Done — split into shell + `WargameMap` + `WargameFactionPanel` + `WargameLog` |
| ~~357~~ | ~~`Aria.Tools/GraphTools.cs`~~ | ✅ Done — split into `GraphTools.Core.cs`, `GraphTools.Email.cs`, `GraphTools.Calendar.cs` |
| ~~349~~ | ~~`Aria.Web/Components/Pages/Chat.razor`~~ | ✅ Done — left as-is (349 lines, cohesive markup, splitting would add complexity) |
| ~~332~~ | ~~`Aria.Web/Services/ModelBridgeRegistry.cs`~~ | ✅ Done — split into main registry + `ModelBridgeRegistry.Routing.cs` |
| 332 | `Aria.Web/Services/CollectiveService.cs` | Split CRUD from execution helpers |
| 326 | `Aria.Bridge/Endpoints/SoulEndpoints.cs` | Possibly split keypair management from challenge-response |

---

## Step 3 — Work the approved file (one only)

For the single approved file:

- Read **only** that file, plus its paired file if relevant (a `.razor` and its `.razor.cs` code-behind go together).
- If a dependency genuinely requires reading another file, **name the file and the reason first**, then read it. Don't read unrelated files.
- Propose a concrete split before editing. Good Blazor splitting strategies:
  - **Extract code-behind:** move C# logic out of a `.razor` file into a `.razor.cs` partial class.
  - **Extract child components:** pull self-contained chunks of markup into their own smaller `.razor` components.
  - **Split large C# classes** using `partial class` across multiple files, grouped by responsibility.
  - **Extract services / helpers / models** into their own files when logic doesn't belong in the component.
- **Preserve all existing behavior, namespaces, and public signatures.** Prefer mechanical extraction over redesign. Don't rename things or change APIs unless I ask.

**File placement & IDE nesting rules (apply after split only):**

- **Razor code-behind partials** stay in the same folder as the parent `.razor` file. Add `<DependentUpon>ParentName.razor</DependentUpon>` to each new `<Compile Update="…">` entry in `Aria.Web.csproj` so Rider/VS nests them visually under the parent.
- **Large C# class split into partials**: move all partial files into a dedicated subfolder named after the class/
- **Namespace stays unchanged** regardless of folder move. Always declare the namespace explicitly — do not let the folder path change the namespace.

---

## Step 4 — Apply, then verify the build

Make the edits, then confirm nothing broke:

```bash
dotnet build
```

Report the build result. If it fails, fix it before continuing.

---

## Step 5 — Summarize and STOP before the next file

After the file builds clean:

- Summarize what changed in **2–3 lines** (new file names + what moved where).
- **Ask for my validation before moving to the next file.**
- Never batch multiple files together without my explicit go-ahead.

---

## Token-saving constraints (apply throughout)

- Don't re-read files already in context.
- Never read `bin/`, `obj/`, or generated files.
- Don't echo full file contents back to me — filenames and short diffs are enough.
- Keep all summaries short.
- One file per cycle. Discovery → plan → **(wait)** → split → build → summarize → **(wait)**.


# Post action

- Update architecture.md table files tree with splited files
- Update this file to tag the files done