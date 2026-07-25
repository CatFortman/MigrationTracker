---
paths:
  - "MigrationOps.ConsoleApp/Migrations/**/*.sql"
---

# Migration file rules

- Filename must match `yyyyMMdd-NNN-Description.sql`: date prefix, zero-padded
  three-digit per-day sequence, PascalCase description, no spaces.
- Execution order is lexicographic by filename and therefore chronological.
  Never backdate or renumber; new work gets the next sequence number for
  today's date.
- Every file needs a `-- Tags:` comment listing target databases. Each tag
  must match a key under `Databases` in
  `MigrationOps.ConsoleApp/Configurations/dbconfig.json` (case-insensitive).
- No `GO` separators: each file runs as a single SqlCommand batch.
- Applied migrations are immutable. Applied-state is matched on filename AND
  checksum, so editing a file changes its checksum and the runner re-executes
  it. Fixes go in a new migration.
- No `-- Checksum:` header is written or required. The checksum is computed
  from the file's own content at apply/plan time
  (`MigrationService.ComputeChecksum`); do not add a checksum line yourself.
