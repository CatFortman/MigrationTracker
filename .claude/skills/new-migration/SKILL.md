---
name: new-migration
description: Scaffold a new MigrationOps migration file with the correct yyyyMMdd-NNN-Description.sql name under the target database's own subfolder. Use whenever a new schema migration file needs to be created.
argument-hint: "[Description] [database]"
disable-model-invocation: true
---

Create a new migration file under `MigrationOps.ConsoleApp/Migrations/<Database>/` for: $ARGUMENTS

The first token of $ARGUMENTS is the Description. The optional second token is
the target database — a file's database is its subfolder, not a comment, so
there is exactly one target. It must match a key under `Databases` in
`MigrationOps.ConsoleApp/Configurations/dbconfig.json` (case-insensitive when
matching, but create the folder using the config key's exact casing). If no
database is given, use the `DefaultDatabase` from `MigrationSettings` in that
file. If a given database matches no configured key, stop and ask rather than
guessing.

## Steps

1. Normalize the Description to PascalCase, no spaces or hyphens (e.g. "add customer email index" becomes `AddCustomerEmailIndex`).
2. Compute today's date as `yyyyMMdd`.
3. List `MigrationOps.ConsoleApp/Migrations/<Database>/` for files starting with today's date; next `NNN` is the highest found plus one, zero-padded to three digits, or `001` if none exist (or the folder doesn't exist yet).
4. Create the file with exactly this content:

```sql
-- TODO: migration body
```

5. Confirm the filename and target database back to the user and stop. Only write the migration body if the user asked for specific SQL in the same request.

## Constraints

- Never reuse or renumber an existing `NNN` for the same date within that database's folder; never backdate.
- `GO` batch separators are supported: a line whose only content is `GO` ends the batch, and the runner executes each batch in order inside the file's single transaction. Add one only where T-SQL requires a new batch (`CREATE PROCEDURE`/`VIEW`/`TRIGGER`, `SET` options), not as a general statement separator.
- Do not add a `-- Checksum:` line. Checksums are computed from file content at apply/plan time; no header is written or required.
- Never modify an existing migration file. Applied-state matches on filename AND checksum, so an edit causes the runner to re-execute the file.
- A single migration can only target one database. If the same change is genuinely needed in more than one database, create a separate migration file in each database's folder.
