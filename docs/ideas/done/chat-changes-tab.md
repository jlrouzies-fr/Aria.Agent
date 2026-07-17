# // IDEA — "Changes" tab in the chat explorer (git status, diffs, commit)

**Status: planned. Builds on `chat-diff-cards.md`** (shares the diff renderer). A tab next to the
file tree showing the project's git working state: changed files with badges, click-through diffs
in the existing viewer, and a guarded stage/commit flow with an agent-drafted message.

## Current state

- The explorer (`Chat.Explorer.razor.cs`) shows a tree + read-only viewer per project
  (`SessionState.AllowedProjectPaths`, per-project `NodeId`).
- Git already runs on the bridge, read-only: `POST /project-git/run` with
  `mode ∈ { "diff" | "status" | "log" }`, called via `ProjectFilesClient.RunGitAsync`
  (`Services/ModelBridge/ProjectFilesClient.cs:79`) — currently only used by the file picker
  (`Chat.FilePicker.razor.cs:449`).

## Design

### 1. Bridge: extend `/project-git/run`

New modes, still allowlisted (never a raw arg passthrough):

| mode | command | notes |
|---|---|---|
| `status-porcelain` | `git status --porcelain=v1 -z` | machine-parseable; also return branch from `git rev-parse --abbrev-ref HEAD` |
| `diff-file` | `git diff -- <path>` / `git diff --cached -- <path>` | `path` validated against `allowedPaths` |
| `stage` / `unstage` | `git add -- <paths>` / `git restore --staged -- <paths>` | first write modes; paths validated |
| `commit` | `git commit -m <message>` | message passed as a single argv element, never shell-interpolated |
| `discard` | `git checkout -- <path>` | destructive → UI requires typed confirmation |

Write modes are a bridge **minor** version bump and should log to the timeline/bridge log.

### 2. Web: the tab

- Explorer header becomes two tabs: `FILES` / `CHANGES` (badge = dirty count, polled on tab open +
  after every agent turn that ran file tools — no background polling).
- Changes list: `M/A/D/R/??` badges, staged section above unstaged, click → diff rendered in the
  existing viewer panel via the `DiffCard` hunk renderer (read-only mode).
- Dirty markers in the FILES tree too: small dot on modified files (from the same porcelain parse,
  matched by rel path).
- Commit box at the bottom: message textarea, `[DRAFT]` button, `[COMMIT n STAGED]` button.
- `[DRAFT]` sends the staged diff (capped) to the **currently selected channel/model** via the
  existing headless-call path and fills the textarea — user edits before committing. Prompt:
  conventional-commit style, subject ≤ 72 chars, no body unless multi-concern.
- Branch name + ahead/behind shown in the tab header (from `status-porcelain` mode payload).

### 3. Scope guards

- Everything is user-initiated from the UI, so agent governance doesn't apply; the bridge's
  `allowedPaths` check is the trust boundary, same as file read today.
- No push/pull/rebase — this is a review-and-commit loop, not a git client. Anything beyond it
  belongs in the shared terminal (`chat-shared-terminal.md`).

## Implementation steps

1. Bridge: porcelain/branch/diff-file modes (read-only) + parsing DTOs.
2. Web: CHANGES tab UI + list + viewer diff rendering + tree dirty dots.
3. Bridge: stage/unstage/commit/discard modes.
4. Web: staging interactions + commit box + typed-confirm discard.
5. `[DRAFT]` commit-message generation.

## Open questions

- Multi-project explorer: the tab operates on the active project root; per-project state cached so
  switching projects doesn't refetch constantly.
- Rename detection (`R`) and untracked directories are porcelain edge cases — parse defensively,
  render unknown codes as `?`.
