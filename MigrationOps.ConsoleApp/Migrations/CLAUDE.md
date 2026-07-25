# Migrations/ conventions

> Lab 3.3 comparison artifact: the directory-CLAUDE.md form of
> `.claude/rules/migrations.md`. Compare loading behavior with `/memory`
> while editing a migration versus an unrelated C# file, then DELETE ONE of
> the two files. Keeping both duplicates these instructions in context.

- Filename: `yyyyMMdd-NNN-Description.sql` (zero-padded per-day sequence,
  PascalCase description).
- Execution order is lexicographic; never backdate or renumber.
- `-- Tags:` lists target databases; each tag must match a key under
  `Databases` in `Configurations/dbconfig.json`.
- No `GO` separators (single SqlCommand batch).
- Applied migrations are immutable; edits change the checksum and cause
  re-execution. Fixes go in a new migration.
- No `-- Checksum:` header is written or required; the checksum is computed
  from file content at apply/plan time.
