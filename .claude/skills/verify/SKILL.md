---
name: verify
description: Build and run MigrationOps end-to-end against a throwaway SQL Server LocalDB database to verify migration/script behavior at the real CLI surface.
---

# Verifying MigrationOps

## Build
`dotnet build` FAILS on this repo (SSDT targets in MigrationOps.ConsoleApp.csproj require VS MSBuild). Use:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" MigrationOps.sln /p:Configuration=Debug /nologo /v:minimal
```

## Database handle
Use SQL Server LocalDB with a throwaway DB — never a real server:

```powershell
sqllocaldb start MSSQLLocalDB     # auto-creates the automatic instance
# create/drop DBs via System.Data.SqlClient from PowerShell (sqlcmd not guaranteed)
```

Clean up after: drop the DB, then `sqllocaldb stop MSSQLLocalDB; sqllocaldb delete MSSQLLocalDB` (only if you created it).

## Run
Copy `MigrationOps.ConsoleApp\bin\Debug\net8.0` to a scratch dir (so probe files never touch the repo), then run with CWD = that dir — config paths (`Configurations/dbconfig.json`, `Scripts`, `Migrations`) resolve relative to the current directory:

```powershell
Set-Location <scratch-copy>
dotnet MigrationOps.ConsoleApp.dll   # exit 0 = success, 1 = halted
```

Point Db1 at LocalDB via a `Configurations\dbconfig.local.json` overlay (gitignored layer, overrides committed template):

```json
{ "Databases": { "Db1": { "ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=<testdb>;Integrated Security=true;" } } }
```

Env vars (`Databases__Db1__ConnectionString`) override both JSON layers.

## Gotchas
- Every `.sql` file needs a `-- Tags: db1` header or the run halts. No `-- Checksum:` header is needed — checksums are computed from file content at apply/plan time (`MigrationService.ComputeChecksum`).
- Fresh empty DB: object scripts that depend on migration-created tables are deferred ("Deferring X ... will retry after migrations") and retried after migrations — a single run on an empty DB should exit 0. A script still failing at the retry pass halts with exit 1.
- Windows PowerShell sandbox may block commands containing `(localdb)\...` literals alongside Remove-Item — build connection strings via `SqlConnectionStringBuilder` and do deletions in separate calls.
- Query verification state via `__MigrationHistory` and `__ScriptHistory` tables in the test DB.
