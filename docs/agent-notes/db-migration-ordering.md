# // DB MIGRATION ORDERING — the `CREATE TABLE IF NOT EXISTS` footgun

[← Agent notes](README.md)

`src/AriaAgent/Aria.Bridge/Infrastructure/BridgeDatabaseInitializer.cs` runs a long, hand-written
sequence of `ExecuteSqlRawAsync` statements on every bridge startup instead of using EF Core
migrations. Two different idioms coexist in that file for evolving an existing table:

1. **New table**: `CREATE TABLE IF NOT EXISTS Foo (...)` with the full, final column list. Correct
   and safe — it only ever runs on databases where `Foo` doesn't exist yet.
2. **New column on an existing table**: a `pragma_table_info('Foo')` check + conditional
   `ALTER TABLE Foo ADD COLUMN ...`, safe to re-run every startup.

The footgun: **if a table predates the new column, `CREATE TABLE IF NOT EXISTS` with the column
already added to its literal SQL text is a silent no-op** — SQLite does not retroactively add the
column to the existing table, and does not error either. It just does nothing.

## The concrete bug this caused

When the `Checkpoint` column was added to `FileUndo` for the turn-checkpoints/`/rewind` feature,
the code was written as:

```csharp
// WRONG — index creation runs immediately after a CREATE TABLE that no-ops on existing DBs
await db.Database.ExecuteSqlRawAsync("""
    CREATE TABLE IF NOT EXISTS FileUndo (
        ...
        Checkpoint TEXT,
        ...
    );
    CREATE INDEX IF NOT EXISTS IX_FileUndo_CreatedAt ON FileUndo (CreatedAt);
    CREATE INDEX IF NOT EXISTS IX_FileUndo_Checkpoint ON FileUndo (Checkpoint);
""");
// ... (300 lines of other migrations) ...
// later in the same method, the *correct* safe migration for pre-existing DBs:
foreach (var col in new[] { ("Checkpoint", "TEXT") })
{
    // ALTER TABLE FileUndo ADD COLUMN Checkpoint TEXT; (only if missing)
}
```

On any bridge database created **before** this feature shipped (i.e. every real developer's and
user's actual local bridge), `FileUndo` already existed without `Checkpoint`. The `CREATE TABLE IF
NOT EXISTS` no-op'd as designed, but the `CREATE INDEX ... ON FileUndo (Checkpoint)` right after it
ran unconditionally against the real (old) table shape and crashed the entire bridge on startup
with `SQLite Error 1: 'no such column: Checkpoint'` — before ever reaching the correct, safe
`ALTER TABLE` migration 300 lines further down. This was invisible in `dotnet test` because tests
build fresh databases via `EnsureCreatedAsync`, which uses the *current* EF model (column already
present) rather than replaying this raw-SQL history — the bug only reproduces against a database
that predates the change.

**Fix**: don't create indexes for new columns in the `CREATE TABLE IF NOT EXISTS` block at all.
Create them only after (and near) the `ALTER TABLE ADD COLUMN` block that actually guarantees the
column exists on every DB shape.

## Rule of thumb for future columns added to existing tables here

- Never add a brand-new column to the literal text of an existing `CREATE TABLE IF NOT EXISTS`
  statement in this file expecting it to retrofit old databases — it won't.
- Any index on a newly-added column must be created **after** the `ALTER TABLE ADD COLUMN` guard
  for that column, not colocated with the table's original `CREATE TABLE` statement.
- If you touch this file, the only way to actually catch ordering bugs like this is to test against
  a copy of a **real, pre-existing** bridge database (e.g. `~/Library/Application Support/aria-bridge/aria-bridge.db`
  on macOS), not just a fresh one — `dotnet test` alone will not catch this class of bug.
