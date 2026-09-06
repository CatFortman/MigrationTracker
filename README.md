# MigrationOps

**MigrationOps** is a database schema versioning and source control tool designed to bring structure and traceability to SQL changes.

It combines concepts from Entity Framework migrations and SQL source control tools (such as Redgate SQL Source Control), enabling teams to manage database updates through versioned scripts, checksum validation, and automated execution ordering.

## Features

- **SQL Source Control**: Organize your SQL scripts (stored procedures, views, functions, and triggers) in dedicated folders.
- **Content-based Checksums**: Each script's integrity checksum is computed from its own content at apply/plan time — no header to maintain or trust.
- **Migration Management**: Easily handle database migrations with a structured approach, using timestamped filenames to ensure proper execution order.
- **`GO` Batch Separators**: Scripts are split on `GO` lines the way SSMS does, so statements that must start their own batch work — while the whole file still applies in one transaction.
- **Folder-per-database routing**: A script's target database is its own subfolder (`Migrations/Db1/`, `Scripts/Db1/...`) — no tagging comment to keep in sync, and a stray folder name that doesn't match a configured database is caught as a validation error instead of silently never running.

## Installation

### Prerequisites

- **Git**: Ensure Git is installed on your machine. You can download it from [Git for Windows](https://gitforwindows.org/).

### Cloning the Repository

To get started, clone the project repository to your local machine:

```bash
git clone <repository-url>
```

Checksums are computed, not stored: `ScriptParser.ComputeChecksum` computes each script's SHA-256 from its own content at apply/plan time, so there's nothing to insert, update, or trust from a header.

## Usage

### Configuration Setup

**dbconfig.json** is used to configure the database connections and migration settings for MigrationOps. It is committed to source control, so it should never contain real credentials — only a template/example shape.

#### Example Structure:

```json
{
  "Databases": {
    "Db1": {
      "ConnectionString": "Server=myServerAddress;Database=db1;User Id=myUsername;Password=myPassword;"
    },
    "Db2": {
      "ConnectionString": "Server=myServerAddress;Database=db2;User Id=myUsername;Password=myPassword;"
    }
  },
  "MigrationSettings": {
    "MigrationDirectory": "Migrations",
    "ScriptDirectory": "Scripts",
    "DefaultDatabase": "Db1"
  }
}
```

#### Supplying real connection strings

Real connection strings should never be committed. Configuration is layered, from lowest to highest precedence:

1. **`Configurations/dbconfig.json`** — the committed template above.
2. **`Configurations/dbconfig.local.json`** — an optional, git-ignored file with the same shape, for a developer's own local secrets. Only include the keys you want to override:
   ```json
   {
     "Databases": {
       "Db1": { "ConnectionString": "Server=.;Database=db1;Trusted_Connection=True;TrustServerCertificate=True;" }
     }
   }
   ```
3. **Environment variables** — recommended for CI/CD pipelines and shared environments. .NET's double-underscore convention maps to the same nested keys, e.g.:
   ```bash
   Databases__Db1__ConnectionString="Server=...;Database=db1;User Id=...;Password=...;"
   ```

Each layer overrides the one before it, so a value set as an environment variable always wins over the checked-in template.

### Organizing SQL Scripts
Place your SQL scripts into the appropriate folders:

* StoredProcedures/
* Views/
* Functions/
* Triggers/

Ensure that all scripts are written using the CREATE OR ALTER statement to simplify deployment.

### Organizing Migration Scripts
Each configured database gets its own subfolder under `Migrations/`, named exactly like its key in `dbconfig.json`'s `Databases` section. Within a database's folder, scripts are applied in order determined by their filenames — by datetime, then script number.

Example (a `Db1` and a `Db2` database configured in `dbconfig.json`):
`
Migrations/Db1/20240805-001-CreateNewTestSchema.sql
Migrations/Db1/20240805-002-CreateEntityTable.sql
Migrations/Db1/20240806-001-CreateHappyCustomerTable.sql
Migrations/Db2/20240806-001-CreateCustomerTestTable.sql
`

### Handling Migrations
Add your migration scripts to the target database's subfolder under Migrations, with a filename format that includes a timestamp and a brief description:

Example:
`
Migrations/Db1/20240805-001-CreateEntityTable.sql
`

### Running Migrations
When you deploy the project, a script's target database is determined entirely by which subfolder it lives in — there is no tagging comment to keep in sync. A subfolder name that doesn't match any configured database is reported as a validation error (or, for `apply`, makes the run throw and halt) rather than being silently skipped.

Database object scripts are applied before migrations. An object script that fails because it depends on schema a pending migration creates (for example, a view over a table that doesn't exist yet) is deferred and retried automatically after migrations run — so a single deploy works even against a brand-new database. If a script still fails on the retry, the run halts with the script name and nothing further is applied.

Example (`Scripts/Db1/StoredProcedures/MyProcedure.sql`):
```SQL
CREATE OR ALTER PROCEDURE dbo.MyProcedure
AS
BEGIN
    -- Procedure logic here
END

```

#### Batch separators (`GO`)

Scripts may use `GO` batch separators, the same way SSMS and `sqlcmd` do. This is what makes statements that have to start their own batch usable in a migration — `CREATE PROCEDURE`, `CREATE VIEW` and `CREATE TRIGGER`, `SET` options, and DDL that later statements in the same file need to reference:

```SQL
CREATE TABLE dbo.Widget (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
GO
-- A new batch, so the CREATE VIEW is legal and can see the table above.
CREATE VIEW dbo.vw_Widget AS SELECT Id, Name FROM dbo.Widget;
GO
GRANT SELECT ON dbo.vw_Widget TO [public];
```

- A line counts as a separator only when `GO` is the whole line, ignoring indentation and case. An optional repeat count (`GO 3`, which runs the batch three times) and an optional trailing `--` comment are allowed.
- `GO` inside a string literal, a quoted or bracketed identifier, or a comment (including a multi-line one) is left alone. Anything else that merely looks like a separator — `GOTO`, `GO;`, `GO 0`, `GO SELECT 1` — stays in the batch, so SQL Server reports it instead of it being silently swallowed.
- Every batch in a file runs in that file's **single transaction**, in order. A failure in any batch rolls the whole file back, so a multi-batch migration is still all-or-nothing, and the error names the batch that failed (SQL Server's own line numbers are relative to the batch, not the file).
- `dry-run` splits scripts exactly the same way, so a real dry-run proves the real apply.
- For object scripts, the `CREATE OR ALTER` rule applies to the **first** batch. Batches after the first may be anything — grants, extended properties, and so on.

#### Build and Run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

Build the solution from the repository root:

```
dotnet build MigrationOps.sln
```

Run the console app from the `MigrationOps.ConsoleApp` directory, since `Configurations/dbconfig.json` and the `Migrations` folder are resolved relative to the working directory:

```
cd MigrationOps.ConsoleApp
dotnet run
```

Running with no arguments opens an interactive menu (choose validate / dry-run / apply, then a target database). When stdin is redirected — e.g. from CI — a bare `dotnet run` performs a full apply, preserving the original behavior.

#### CLI commands

```
dotnet run -- apply    [--db <name>]
dotnet run -- validate [--db <name>]
dotnet run -- dry-run  [--db <name>]
```

- **`apply`** runs the deploy pipeline (object scripts, then migrations, then deferred retries). `--db` limits it to one configured database.
- **`validate`** previews what a deploy would do without changing anything: each file is reported as already applied, would apply, **CHANGED** (an applied migration whose file was edited — a real run would re-execute it), or a validation error (a stray folder that doesn't match any configured database, or an object script without `CREATE OR ALTER`). Validate never halts early; it collects every problem, prints a per-database summary, and ends with `VALIDATE SUCCEEDED`/`VALIDATE FAILED`.
- **`dry-run`** runs the same report but additionally executes the pending scripts against the target database in one transaction per database — so later scripts can rely on earlier scripts' schema — and always rolls back. It needs a reachable database but never commits anything, including history rows. It ends with `DRY-RUN SUCCEEDED`/`DRY-RUN FAILED`.
- The exit code is the success flag: `0` only when there are no CHANGED, validation-error, or dry-run-failed entries, making both `validate` and `dry-run` suitable as CI gates.

## Dashboard

**MigrationOps.Dashboard** is a Razor Pages web app that provides a read-only view of migration state. It reuses the same `MigrationOps.Core` logic as the console runner, so it shows the exact same per-database picture: applied migration history, pending files, and checksum drift.

### Setup

1. **Create the dashboard's database.** The dashboard stores its login accounts in a dedicated database, separate from the migration-target databases. Create an empty database on your SQL Server instance (e.g. `MigrationOpsDashboard`):

   ```sql
   CREATE DATABASE MigrationOpsDashboard;
   ```

   On first use the app creates its `__DashboardUsers` table automatically, but the database itself must already exist.

2. **Configure the connection string.** `MigrationOps.Dashboard/appsettings.json` is committed with a placeholder `DashboardStore:ConnectionString`. Don't put real credentials in it — override it with a git-ignored `appsettings.Development.json` or an environment variable:

   ```bash
   DashboardStore__ConnectionString="Server=.;Database=MigrationOpsDashboard;Trusted_Connection=True;TrustServerCertificate=True;"
   ```

3. **Check the shared config paths.** `appsettings.json` points at the console app's files via relative paths (`DbConfigPath`, `MigrationsRoot`), which resolve correctly when you run from the `MigrationOps.Dashboard` directory. The dashboard reads `dbconfig.json` through the same layering as the console app, so connection strings in `dbconfig.local.json` or environment variables are picked up here too.

### Running

```
cd MigrationOps.Dashboard
dotnet run
```

The app listens on `http://localhost:5280`.

### First-run account setup

Every page requires login. On a fresh install:

1. Visit `/Register` to create the first account (minimum 8-character password, stored BCrypt-hashed).
2. Log in at `/Login`.

Registration is a one-time bootstrap, not open signup: once any account exists, `/Register` permanently redirects to `/Login`. To add another user later, insert a row into `__DashboardUsers` manually, or clear the table to re-open registration.

## Contributing
I welcome contributions! Please fork the repository and submit a pull request for any enhancements or bug fixes.

## License
This project is licensed under the MIT License - see the LICENSE file for details.

## Contact
For any questions or suggestions, please open an issue or reach out to Cat Fortman.
