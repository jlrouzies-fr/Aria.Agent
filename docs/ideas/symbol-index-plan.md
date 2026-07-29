# // IDEA — Bridge-local symbol index (`find_symbol` / `find_references`, powers `#sym:`)

**Status: planned.** Code navigation today is grep/glob/read only — the agent burns its read budget
("grep, read 3 files, grep again") to locate one definition. The `#sym:` reference is already
listed Planned in `ChatCatalog.cs` and was explicitly deferred as "needs LSP/ctags". LSP is heavy;
a ctags-style index living in the bridge's SQLite vault is not. The bridge already intercepts every
file mutation (for undo metadata), which gives incremental index maintenance almost for free.

## Current state

- Navigation tools: `grep` (regex, 200-match cap), `glob` (500-path cap), `read_file` —
  `Aria.Bridge/BuiltinTools/BuiltinTools.Grep.cs`, `BuiltinTools.File.cs`.
- `#sym:<Name>` and `#diag` are Planned entries in `Aria.Web/Services/Chat/ChatCatalog.cs`
  (~:83-84); `#` reference resolution is wired for other refs already (file paths, git state).
- Every bridge file mutation funnels through `BuiltinTools.File.cs` handlers that already write
  undo rows — a natural invalidation/update hook.
- The bridge owns a SQLite vault (Noosphere + undo + config live there already).
- Scope: tools are constrained to Allowed Paths + `/scope` grants; the index must be too.

## Design

### Index (v1: ctags-style, no LSP)

- New tables in the bridge vault:
  `symbols(path, name, kind, line, signature, file_hash, indexed_at)` and
  `index_files(path, hash, indexed_at)` for incremental refresh.
- Per-language definition patterns (regex + indentation heuristics) for: C#, Python, JS/TS, Go,
  Rust, Java, C/C++, Ruby, PHP. Kinds: class/struct/interface/enum/record, function/method,
  constant. Test fixtures per language prove the patterns.
- Population: lazy per-directory — first `find_symbol` under a path prefix indexes that subtree
  (skipping `.git`, `node_modules`, `bin`, `obj`, like grep); results may carry
  `index: partial` while a directory is stale (>60 s since scan). Explicit `symbol_index`
  {action: build|refresh|status, path?} tool for warming and diagnostics.
- Maintenance: bridge file-mutation handlers (`write_file`, `edit_file`, `multi_edit`,
  `move_path`, `delete_*`) re-index the touched file synchronously (single-file parse is
  millisecond-cheap). Stale-hash check on query as a safety net.

### Tools

- `find_symbol {name, kind?, path?}` → top ~20 matches: `Kind Name — path:line — signature`,
  ranked (exact > prefix > substring; definitions over references).
- `find_references {name, path?}` → v1 is *index-assisted grep*: word-boundary match, comments and
  the definition line itself deprioritised, results grouped by file with the existing grep caps.
  Honest about being heuristic — no false "0 references" claims; the text says "grep-based".
- Both are read-class tools: counted in read budgets, never approval-gated, scope-enforced.

### `#sym:` reference

- `ChatCatalog.cs`: flip `#sym:` to READY; resolution calls `find_symbol` on the owning bridge and
  injects the top matches (with file:line) as context, same shape as existing `#` file refs.

### Explicitly not v1

- Full LSP (per-language servers on the bridge) — possible v2 for exact references and `#diag`
  (compiler diagnostics), which is a separate Planned entry.
- tree-sitter grammars — better accuracy than regex; upgrade path behind the same index interface.

## Implementation steps

1. Schema + migration in the bridge DB initializer; `SymbolIndex` service (scan, per-file refresh,
   query) with per-language pattern table.
2. Fixtures + tests per language (a tiny class/func file per language → expected symbols).
3. Mutation hooks in `BuiltinTools.File.cs` handlers (after undo write).
4. `find_symbol` / `find_references` / `symbol_index` builtins: manifest, dispatch, read-class
   governance mapping, scope checks.
5. `#sym:` wiring in `Aria.Web` (catalog flip + resolution).
6. Prompt addendum (Projects tools registered): prefer `find_symbol` over exploratory grep.
   Docs: README bullet + architecture.md.

## Open questions

- Repo-size policy: cap indexed files per subtree (by recency) or index everything and let SQLite
  cope? Start unbounded, measure on this repo (~large .NET solution).
- Multi-root Allowed Paths: one index keyed by absolute path handles it naturally — verify scope
  filtering on shared parent paths.
- Signature capture depth (full param lists vs first line)? First line v1; parsers can enrich later.
