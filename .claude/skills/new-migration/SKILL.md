---
name: new-migration
description: Scaffold a new MigrationOps migration file with the correct yyyyMMdd-NNN-Description.sql name and required -- Tags header. Use whenever a new schema migration file needs to be created.
argument-hint: "[Description] [database-tags...]"
disable-model-invocation: true
---

Create a new migration file in `MigrationOps.ConsoleApp/Migrations/` for: $ARGUMENTS

The first token of $ARGUMENTS is the Description. Any remaining tokens are
database tags. Tags are ROUTING TARGETS: each must match a key under
`Databases` in `MigrationOps.ConsoleApp/Configurations/dbconfig.json`
(case-insensitive). If no tags are given, use the `DefaultDatabase` from
`MigrationSettings` in that file (currently `db1`). If a given tag matches no
configured database, stop and ask rather than guessing.

## Steps

1. Normalize the Description to PascalCase, no spaces or hyphens (e.g. "add customer email index" becomes `AddCustomerEmailIndex`).
2. Compute today's date as `yyyyMMdd`.
3. List `MigrationOps.ConsoleApp/Migrations/` for files starting with today's date; next `NNN` is the highest found plus one, zero-padded to three digits, or `001` if none exist.
4. Create the file with exactly this header, then a TODO body:

```sql
-- Tags: db1

-- TODO: migration body
```

5. Confirm the filename and target databases back to the user and stop. Only write the migration body if the user asked for specific SQL in the same request.

## Constraints

- Never reuse or renumber an existing `NNN` for the same date; never backdate.
- No `GO` batch separators: the runner executes each file as one SqlCommand batch.
- Do not add a `-- Checksum:` line. Checksums are computed from file content at apply/plan time; no header is written or required.
- Never modify an existing migration file. Applied-state matches on filename AND checksum, so an edit causes the runner to re-execute the file.
