# // IDEA — Cogitation folders ("Dossiers": organize chats by project / topic)

**Status: exploratory — approach recommended, shape open.** Today the cogitations panel is one
flat list, newest first, capped at 50. Once you use Aria for real work (several code projects,
email triage, recipes, the wargame) the list becomes archaeology. This plan adds user-created
containers — and argues they should be more than visual grouping.

## Current state

- `Cogitation` (`Aria.Web/Data/Cogitations/Cogitation.cs`) has no grouping field: `Title`,
  `SubAgentId`, `CollectiveId`, `OriginNodeId`, timestamps.
- The panel (`NavMenuCogitationsPanel.razor`) renders `Menu._cogitations` flat, from
  `CogitationService.GetListAsync(userId, limit: 50)` ordered by `UpdatedAt` desc.
- "Projects" already exist but are a different animal: `TerminalProject` entries (name/path/nodeId)
  parsed from the terminal tool config (`ProjectFilesClient.ParseProjects`,
  `UserSessionState.Projects`) — they scope the file explorer and governance, and are not entities.
- Cogitation **content** may live on a bridge node (`OriginNodeId`); the **metadata row** always
  lives in the server DB. Folder assignment is metadata → server-side only, no bridge changes.

## Approaches considered

**A. Tags/topics (many-to-many).** Most flexible, worst fit: heavier UI (tag pickers, filter
combinators), and nobody tags chats in practice. Rejected.

**B. Pure auto-grouping (by sub-agent / active terminal project / date).** Zero effort, but wrong
boundaries: topics like "email triage" have no project, and one project can host unrelated
threads. Worth keeping as *secondary* sort/filter, not as the organizing primitive.

**C. Folders — user-created, flat, with optional context defaults. ← Recommended.**
One `CogitationFolder` entity; a cogitation belongs to at most one. The twist that makes it worth
building: a folder is a **context container**, not just a bucket. This is what makes it a
"Project/Topic" in the ChatGPT/Claude-Projects sense rather than a filing cabinet.

## Design (approach C)

### Data model

```csharp
public class CogitationFolder
{
    public int      Id        { get; set; }
    public string   UserId    { get; set; } = "";
    public string   Name      { get; set; } = "";
    public string?  Color     { get; set; }        // accent for the section header / chip
    public int      SortOrder { get; set; }

    // Context defaults — applied to NEW cogitations created inside the folder:
    public int?     DefaultSubAgentId  { get; set; }  // start with this persona
    public string?  DefaultProjectPath { get; set; }  // explorer opens here; must match a TerminalProject
    public string?  StandingDirective  { get; set; }  // system-note injected into every session in the folder
}
```

`Cogitation` gains `FolderId` (nullable FK, null = Unfiled). Incremental SQLite migration in
`DatabaseInitializer`; all existing rows are Unfiled. Flat only — no nesting (revisit if real
demand appears; nesting doubles UI cost for marginal value at personal scale).

### Filing rules (keep friction near zero)

1. **Explicit**: context menu on a cogitation row → "File under…" (+ "New dossier…"); same menu
   in the chat header.
2. **Sticky context**: the panel has an *active folder* (click a header to focus it). "New
   cogitation" while a folder is focused files it there and applies the folder's defaults.
   Focus persists per circuit (`UserSessionState`), shown as a small chip next to the + button.
3. **Suggested**: if the chat's explorer project matches a folder's `DefaultProjectPath`, a subtle
   "file under X?" affordance appears on the first reply — one click, never automatic.

### Context defaults — the actual payoff

When a cogitation is created in a folder:
- `DefaultSubAgentId` preselects the persona (overridable per chat as today);
- `DefaultProjectPath` sets the explorer's active project → file tree, `#` picker, governance
  scope-lock and the future Changes tab all point at the right repo immediately;
- `StandingDirective` is appended to the system prompt at session build (same injection point the
  sub-agent persona uses) — e.g. "Answers in French. This dossier concerns the Aria refactor;
  prefer terse diffs." Applied per-session, never stored into message history.

This turns "organize my chats" into "stop re-explaining context in every new chat" — the reason
to bother filing at all.

### Panel UI

Collapsible sections in the existing flyout (fits the narrow panel better than drill-in pages):

```
◈ COGITATIONS                                [ + ]
▾ ARIA REFACTOR            ● 3            [focus]
    Bridge versioning bump          2h
    Harness extraction              1d
▾ EMAIL TRIAGE                              ○ 1
    Monday sweep                    3d
▸ WARGAME
─ UNFILED ──────────────────────────────────────
    New Cogitation                  5m
```

- Section header: color dot, name, running-spinner / unseen-completion dot **aggregated** from the
  rows inside (the per-row indicators already exist — bubble them up so a collapsed folder still
  shows activity).
- Unfiled stays at the bottom, always expanded, newest first — the current behaviour survives
  untouched for users who never create a folder.
- Collapse state per folder in localStorage; folder CRUD (rename inline, delete → contents become
  Unfiled, never cascade-delete chats) via the same modal patterns as agents/skills panels.
- `GetListAsync` grows a folder include + per-folder ordering; the 50-cap becomes per-folder-aware
  (e.g. 20 most recent per folder + all of the focused one).

## User experience — worked examples

Concrete walkthroughs. UI copy is proposal-grade, not final. Cast: a solo dev using Aria for the
`aria-agent` repo, weekly email triage, and meal planning.

### Case 0 — user who never creates a folder

Nothing changes. The panel is the flat list it is today; no "Unfiled" header, no empty folder
sections, no new affordances beyond one extra `+ DOSSIER` entry in the panel's `[ + ]` menu.
Folders must be invisible until opted into.

### Case 1 — creating the first dossier

1. Panel header `[ + ]` now opens a two-item menu: `+ COGITATION` / `+ DOSSIER`.
2. `+ DOSSIER` → modal (same pattern as the agent-edit modal):

   ```
   ◈ NEW DOSSIER
   NAME        [ Aria Refactor            ]
   COLOR       [●red ●gold ●teal ●violet …]
   ── CONTEXT DEFAULTS (optional) ─────────────
   PERSONA     [ Magos Codewright       ▾ ]   ← sub-agent picker, "— none —" default
   PROJECT     [ ~/Development/AI/aria-agent ▾ ]   ← from Terminal › Allowed Projects
   DIRECTIVE   [ Answers terse. Prefer diffs over
                 prose. Solution structure is in
                 CLAUDE.md; never suggest adding
                 Bootstrap.                    ]
                            [ CANCEL ] [ FORGE ]
   ```

3. On `FORGE`, the panel re-renders with the section at top, empty, expanded, **focused**
   (creating a dossier focuses it — the user's next act is almost always "start working in it"):

   ```
   ◈ COGITATIONS                         [ + ]
   ▾ ● ARIA REFACTOR          — no cogitations —
   ─────────────────────────────────────────────
     Bridge versioning bump                2h
     Monday email sweep                    3d
     Roast chicken variations              6d
   ```

### Case 2 — filing the backlog

Hovering a row shows the existing row affordances plus `⋮` → `FILE UNDER ▸` → list of dossiers +
`+ NEW DOSSIER…`. The user files "Bridge versioning bump" under **Aria Refactor**; the row
animates out of the flat list into the section. Filing never touches `UpdatedAt` — no
reordering surprises. The same `⋮ → FILE UNDER` menu exists in the chat header for the open
cogitation.

Bulk pass: filing five chats is five two-click operations. Acceptable for v1; multi-select is
deliberately out of scope until someone actually hits the wall.

### Case 3 — the payoff: new chat inside a focused dossier

Focus state: clicking a section header row focuses that dossier (header gets a `◂ FOCUSED` tick;
clicking again unfocuses). A chip appears next to the input-bar/new-chat affordance:
`NEW COGITATION → ● ARIA REFACTOR ✕`.

User presses `+ COGITATION` with **Aria Refactor** focused:

- chat opens with **Magos Codewright** already active (agent pill in the header, exactly as if
  hand-picked — still swappable per chat);
- the explorer's active project is already `aria-agent`; the file tree is populated; `#` picker
  and governance scope-lock point at the repo;
- the standing directive rode in at session build. First exchange:

  > **User:** where do I bump the bridge version?
  >
  > **Aria:** `Aria.Bridge/Services/BridgeLogger.cs` — `BridgeLogger.Version`. Minor bump for new
  > endpoints, fix for iterations. Keep `-beta`.

  Terse, no preamble, no "you could also consider…" — because the directive said so. The user
  typed nothing but the question. **That's the feature.**

### Case 4 — the mis-file trap and the correction

Still focused on **Aria Refactor**, the user gets hungry and hits `+` to ask about dinner. The
chat files under Aria Refactor and greets as Magos Codewright — wrong on both counts.

- The focus chip (`→ ● ARIA REFACTOR ✕`) was visible next to `+` before the click — the design's
  first defence is making the destination legible *at the moment of creation*, not after.
- Recovery is one gesture: chat header `⋮ → FILE UNDER ▸ MEAL PLANNING` (or `UNFILED`). Moving a
  cogitation between dossiers never rewrites its history — defaults apply only at creation;
  re-filing changes shelf location, not persona or directives of the existing session.
- Deliberate non-feature: focus does **not** auto-expire on navigation or timeout. Sticky focus
  is the whole value for a work session ("this afternoon is Aria work"); the chip + one-click `✕`
  is the escape hatch.

### Case 5 — suggested filing (project match)

The user starts an **unfiled** chat, opens the explorer, switches the active project to
`aria-agent`, and sends "review Chat.Explorer for dead code". One reply later, a single quiet
line appears under the chat header:

```
▸ file this under ● ARIA REFACTOR ?   [ FILE ]  [ ✕ ]
```

`FILE` files it (defaults NOT retroactively applied — the session already exists). `✕` dismisses
permanently for this cogitation (flag on the row, not localStorage — survives refresh). The
suggestion only ever fires once per cogitation, only when exactly one dossier matches the active
project path, and never for dossiers without a `DefaultProjectPath`.

### Case 6 — a topic dossier with no project: email triage

Dossier **EMAIL TRIAGE**: no project, persona "Adept Scriptor", directive: *"When triaging,
group by sender domain, flag anything mentioning invoices, and end with a bullet list of
suggested archive actions. Never draft replies unless asked."*

Monday morning: focus the dossier, `+`, type "triage the inbox". The agent calls the Gmail tools
and answers in the house format immediately. Every Monday chat lands in the same section; last
week's sweep is one click away for "did I already handle the Fastmail invoice thing?" — the
grouping *is* the recall tool, no Hindsight query needed.

### Case 7 — activity through a collapsed section

Two cogitations are streaming in **Aria Refactor** (background runs via
`CogitationRunRegistry`); the section is collapsed while the user reads email:

```
▸ ● ARIA REFACTOR   ⟳ 2        ← spinner + running count, aggregated from rows
▾ ● EMAIL TRIAGE
    Monday sweep                    ⟳ now
```

One run finishes unseen → the collapsed header swaps to the unseen-completion dot (`▸ ● ARIA
REFACTOR ●1`), exactly the semantics the per-row indicators have today, bubbled up. Expanding the
section shows which row it was; opening the row clears it — unchanged behaviour.

### Case 8 — dossier lifecycle

- **Rename**: inline, pencil-on-hover on the header, like collective renaming (live update).
- **Edit defaults**: `⋮` on the header → `EDIT DOSSIER` reopens the Case-1 modal. Changing the
  standing directive affects **new sessions only** (it's injected at session build); an in-flight
  chat keeps its briefing. The modal says so: *"applies to new cogitations"*.
- **Delete**: `⋮ → DELETE` → confirm modal:

  ```
  ◈ DISBAND DOSSIER — ARIA REFACTOR
  Its 7 cogitations return to the unfiled list.
  No cogitation is deleted.
                      [ CANCEL ] [ DISBAND ]
  ```

  Never cascade-deletes chats. If the deleted dossier was focused, focus clears.

### Case 9 — Hive runs

The user runs the "Docs Cohort" collective twice. Its cogitations are already machine-labelled
(`CollectiveId`), so they render under an implicit, non-editable group:

```
▸ ◇ HIVE — DOCS COHORT                    2
```

Implicit groups sit between the user's dossiers and Unfiled, appear only when non-empty, and
offer no defaults editing (the collective itself is the context). A hive cogitation can still be
manually filed into a real dossier, which removes it from the implicit group.

## Implementation steps

1. Entity + FK + migration; `CogitationFolderService` CRUD (mirror `CollectiveService` shape).
2. Panel sections + collapse + focus chip + aggregated activity dots.
3. "File under…" menus + create-into-focused-folder.
4. Context defaults: persona preselect + explorer project preselect + `StandingDirective`
   injection at session build.
5. Suggested-filing affordance (last — needs 2-4 in place to be meaningful).

## Open questions

- **Console**: `Aria.Console` lists cogitations via the bridge mirror — add `FolderId`+folder
  names to the sync snapshot (`BridgeSyncService`) in a later pass; console renders section
  headers. Not a blocker for the web-first version.
- **Hive runs**: collectives already brand their cogitations (`CollectiveId`) — auto-file each
  collective's runs into an implicit per-collective group, or leave them to manual filing?
  Leaning: implicit group shown under the folder list, since they're already machine-labelled.
- **Folder-scoped memory**: should `Recall`/`Reflect` (Hindsight) bias toward memories retained
  from chats in the same folder? Powerful but needs Hindsight-side namespace support — park it,
  note it as the natural next step after `StandingDirective`.
- Naming: "Dossiers" reads well next to Cogitations ("Archivum" also fits); code says
  `CogitationFolder` regardless.
