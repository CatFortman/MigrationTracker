---
paths:
  - "MigrationOps.ConsoleApp/Migrations/**/*.sql"
---

# Migration file rules

- Lives under `Migrations/<Database>/`, where `<Database>` exactly matches a
  key under `Databases` in `MigrationOps.ConsoleApp/Configurations/dbconfig.json`
  (case matters). The folder is the routing — there is no `-- Tags:` comment.
  A subfolder name that doesn't match any configured database is reported as
  a validation error by `validate`/`dry-run` and makes `apply` throw.
- Filename must match `yyyyMMdd-NNN-Description.sql`: date prefix, zero-padded
  three-digit per-day sequence, PascalCase description, no spaces.
- Execution order is lexicographic by filename within that database's own
  subfolder, and therefore chronological per database. Never backdate or
  renumber; new work gets the next sequence number for today's date within
  its database's folder.
- `GO` separators are supported: a line whose only content is `GO` (optionally
  `GO <count>`) ends the batch. Each batch runs as its own SqlCommand, in
  order, inside the file's single transaction. Use one where T-SQL requires a
  new batch (`CREATE PROCEDURE`/`VIEW`/`TRIGGER`, `SET` options), not as a
  general statement separator.
- Applied migrations are immutable. Applied-state is matched on filename AND
  checksum, so editing a file changes its checksum and the runner re-executes
  it. Fixes go in a new migration.
- No `-- Checksum:` header is written or required. The checksum is computed
  from the file's own content at apply/plan time
  (`ScriptParser.ComputeChecksum`); do not add a checksum line yourself.
