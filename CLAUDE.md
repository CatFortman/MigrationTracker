# MigrationOps

SQL Server schema versioning tool. Migrations are plain .sql files executed in
filename order by the ConsoleApp. Solution has three projects:
`MigrationOps.ConsoleApp` (runner), `MigrationOps.Core` (framework), and
`MigrationOps.Dashboard` (Razor Pages web UI over the same Core logic;
read-only except the dry-run verify action, which executes pending scripts in
a rolled-back transaction).

## Core layout

`MigrationOps.Core/MigrationFramework/` is split by responsibility — there is no
god class, and each piece is constructible on its own:

- `Configuration/` — `IMigrationConfig` / `MigrationConfig`: dbconfig.json
  layering (base → `.local` overlay → environment variables), connection
  strings, folder settings, alert settings.
- `Scripts/` — `ScriptParser` (tags, checksum, CREATE OR ALTER validation,
  edited-migration detection), `SqlBatchSplitter` (`GO` batch separators) and
  `ScriptCatalog` (which .sql files run, in what order). Pure; no database, no
  configuration.
- `Data/` — `IHistoryStore` / `SqlHistoryStore`: reads and writes of
  `__MigrationHistory` / `__ScriptHistory` outside any apply transaction.
- `Execution/` — `IScriptExecutionGateway` / `SqlScriptExecutionGateway`: the
  only place that opens connections and transactions. Apply runs a script and
  its history row in one transaction; `IVerifySession` is the verify
  counterpart, and disposing it always rolls back. Both paths send SQL through
  the same `ExecuteBatches` helper, so `GO` splitting is identical for apply
  and verify.
- `Services/` — orchestration with no ADO.NET of its own: `ScriptApplier`
  (apply pipeline), `DryRunPlanner` (plan building/classification),
  `PlanVerifier` (`--verify`).
- `MigrationOpsServices` — composition root; `CreateDefault(path?)` wires the
  SQL Server implementations. Tests substitute fakes for the interfaces above
  instead of needing a live server.

## Build and run

Requires the .NET 8 SDK.

- Build (repo root): `dotnet build MigrationOps.sln`
- Run: `cd MigrationOps.ConsoleApp && dotnet run`

Console app CLI:

- `dotnet run -- apply [--db <name>]` — apply object scripts + migrations
  (optionally to one configured database only).
- `dotnet run -- dry-run [--db <name>] [--verify]` — read-only preview: per
  file, reports already applied / would apply / CHANGED (edited applied
  migration) / validation errors, never halting early. `--verify` additionally
  executes pending scripts in one transaction per database and always rolls
  back. Exit code 0 only with no CHANGED, validation-error, or verify-failed
  entries.
- No args: interactive menu (choose action + target DB). If stdin is
  redirected (CI), it performs a full apply exactly like the old behavior.

Always run from `MigrationOps.ConsoleApp/`. `Configurations/dbconfig.json` and
the `Migrations` folder are resolved relative to the working directory, so
`dotnet run --project` from the repo root silently loads no configuration.

- Dashboard: `cd MigrationOps.Dashboard && dotnet run` (listens on
  `http://localhost:5280`). Also working-directory-sensitive: its
  `appsettings.json` points at the ConsoleApp's `dbconfig.json` and
  `Migrations` folder via relative paths. Requires a pre-created dashboard
  database (`DashboardStore:ConnectionString`) for login accounts; the app
  creates its `__DashboardUsers` table but never the database itself. First
  account is bootstrapped at `/Register`, which closes once any user exists.
  `/DryRun` is the web equivalent of the console `dry-run` command (same
  `BuildDryRunPlan`/`VerifyPlan` Core calls); the object-scripts root defaults
  to the `Scripts` folder next to `MigrationsRoot`, overridable via a
  `ScriptsRoot` setting in `appsettings.json`.

## Migration files

- Live in `MigrationOps.ConsoleApp/Migrations/`, named
  `yyyyMMdd-NNN-Description.sql` (e.g. `20240807-001-CreateUserTable.sql`).
  `NNN` is a zero-padded per-day sequence starting at 001; `Description` is
  PascalCase, no spaces.
- Every .sql file needs a `-- Tags:` comment. Tags are DATABASE TARGETS, not
  labels: each tag must match a key under `Databases` in
  `Configurations/dbconfig.json` (case-insensitive, e.g. `db1`, `db2`). The
  runner routes the script to each tagged database and throws if no tag
  matches a configured database.
- `GO` batch separators are supported. A line whose only content is `GO`
  (optionally `GO <count>`, optionally with a trailing `--` comment) ends the
  batch; the runner sends each batch as its own SqlCommand, in order, inside
  the file's single transaction, so the file is still all-or-nothing. `GO`
  inside a string, quoted identifier or comment is left alone. Use it where
  T-SQL requires it — `CREATE PROCEDURE`/`VIEW`/`TRIGGER` must start a batch,
  as must `SET` options and DDL that later statements reference.
- Never edit a migration that has already been applied. The checksum used to
  match applied-state is computed from the file's own content at apply/plan
  time (`ScriptParser.ComputeChecksum`), so editing the file changes its
  checksum and the runner re-executes it against the database. Fixes go in a
  new migration. `dry-run` reports such files as CHANGED and exits 1.
- Reusable objects (procs, views, functions, triggers) go under `Scripts/`
  using `CREATE OR ALTER`, not in `Migrations/`.
- To create a new migration, use the `/new-migration` skill.

## Checksums are computed, not stored

Nothing writes a `-- Checksum:` header anymore. `ComputeChecksum` hashes each
script's own content (SHA-256) at apply/plan time; a leading `-- Checksum:`
line is stripped if present (for files committed before this changed) but is
never required or written. The pre-commit hook only rejects commits of .sql
files missing the `-- Tags:` comment.
