using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    // The `dry-run` command executes a plan's pending scripts in one always-rolled-back transaction per
    // database. With the verify session behind an interface these cover the parts that used to
    // need a live server: phase ordering, defer-and-retry, doomed transactions, and what happens
    // to the entries after the first hard failure.
    public class PlanDryRunnerTests
    {
        private readonly TestConfig _config = TestConfig.WithDatabases("Db1", "Db2");
        private readonly FakeExecutionGateway _gateway = new();

        private PlanDryRunner CreateDryRunner() => new(_config, _gateway);

        private static Entry Entry(string fileName, ScriptKind kind, string database = "Db1",
            EntryStatus status = EntryStatus.WouldApply, string? scriptText = null)
        {
            return new Entry
            {
                FileName = fileName,
                FilePath = fileName,
                Kind = kind,
                Database = database,
                Status = status,
                ScriptText = scriptText ?? $"-- {fileName}"
            };
        }

        private static MigrationPlan Plan(params Entry[] entries)
        {
            var databases = entries.Select(e => e.Database).Distinct().ToList();
            return new MigrationPlan { TargetDatabases = databases, Entries = entries.ToList() };
        }

        [Fact]
        public void PendingEntriesPassAndTheTransactionIsAlwaysRolledBack()
        {
            var entry = Entry("20260101-001-Foo.sql", ScriptKind.Migration);

            CreateDryRunner().RunDryRun(Plan(entry));

            Assert.Equal(EntryStatus.DryRunPassed, entry.DryRunStatus);
            Assert.True(_gateway.LastSession!.Disposed);
        }

        [Fact]
        public void EntriesThatWouldNotRunAreNeitherExecutedNorMarked()
        {
            var applied = Entry("Applied.sql", ScriptKind.Migration, status: EntryStatus.AlreadyApplied);
            var invalid = Entry("Invalid.sql", ScriptKind.Migration, status: EntryStatus.ValidationError);

            CreateDryRunner().RunDryRun(Plan(applied, invalid));

            Assert.Null(applied.DryRunStatus);
            Assert.Null(invalid.DryRunStatus);
            Assert.Empty(_gateway.Sessions);
        }

        [Fact]
        public void ChangedEntriesAreVerifiedButKeepTheirChangedClassification()
        {
            var changed = Entry("20260101-001-Foo.sql", ScriptKind.Migration, status: EntryStatus.Changed);

            CreateDryRunner().RunDryRun(Plan(changed));

            Assert.Equal(EntryStatus.Changed, changed.Status);
            Assert.Equal(EntryStatus.DryRunPassed, changed.DryRunStatus);
        }

        [Fact]
        public void ObjectScriptsRunBeforeMigrationsWithinTheTransaction()
        {
            var migration = Entry("20260101-001-Foo.sql", ScriptKind.Migration);
            var view = Entry("V.sql", ScriptKind.DatabaseObject);

            // Plan order deliberately puts the migration first; the verifier must still phase it.
            CreateDryRunner().RunDryRun(Plan(migration, view));

            Assert.Equal(new[] { "-- V.sql", "-- 20260101-001-Foo.sql" }, _gateway.LastSession!.Executed);
        }

        [Fact]
        public void AnObjectScriptThatFailsFirstIsRetriedAfterMigrationsAndCanPass()
        {
            var view = Entry("V.sql", ScriptKind.DatabaseObject);
            var migration = Entry("20260101-001-Foo.sql", ScriptKind.Migration);
            _gateway.ConfigureSession = session => session.FailuresRemaining["-- V.sql"] = 1;

            CreateDryRunner().RunDryRun(Plan(view, migration));

            Assert.Equal(EntryStatus.DryRunPassed, view.DryRunStatus);
            Assert.Equal(EntryStatus.DryRunPassed, migration.DryRunStatus);
            Assert.Equal(new[] { "-- V.sql", "-- 20260101-001-Foo.sql", "-- V.sql" }, _gateway.LastSession!.Executed);
        }

        [Fact]
        public void AFailingObjectScriptThatDoomsTheTransactionStopsTheRun()
        {
            var view = Entry("V.sql", ScriptKind.DatabaseObject);
            var migration = Entry("20260101-001-Foo.sql", ScriptKind.Migration);
            _gateway.ConfigureSession = session =>
            {
                session.FailuresRemaining["-- V.sql"] = 1;
                session.Doomed = true;
            };

            CreateDryRunner().RunDryRun(Plan(view, migration));

            Assert.Equal(EntryStatus.DryRunFailed, view.DryRunStatus);
            Assert.Contains("verify failed", view.DryRunDetail);
            Assert.Equal(EntryStatus.NotRun, migration.DryRunStatus);
            Assert.Equal("-- V.sql", Assert.Single(_gateway.LastSession!.Executed));
        }

        [Fact]
        public void AFailingMigrationStopsEverythingAfterIt()
        {
            var first = Entry("20260101-001-First.sql", ScriptKind.Migration);
            var second = Entry("20260101-002-Second.sql", ScriptKind.Migration);
            _gateway.ConfigureSession = session => session.FailuresRemaining["-- 20260101-001-First.sql"] = 1;

            CreateDryRunner().RunDryRun(Plan(first, second));

            Assert.Equal(EntryStatus.DryRunFailed, first.DryRunStatus);
            Assert.Equal(EntryStatus.NotRun, second.DryRunStatus);
            Assert.Equal("not run - earlier failure", second.DryRunDetail);
        }

        [Fact]
        public void DeferredObjectScriptsAreNotRetriedOnceAMigrationHasFailed()
        {
            var view = Entry("V.sql", ScriptKind.DatabaseObject);
            var migration = Entry("20260101-001-Foo.sql", ScriptKind.Migration);
            _gateway.ConfigureSession = session =>
            {
                session.FailuresRemaining["-- V.sql"] = 1;
                session.FailuresRemaining["-- 20260101-001-Foo.sql"] = 1;
            };

            CreateDryRunner().RunDryRun(Plan(view, migration));

            Assert.Equal(EntryStatus.DryRunFailed, migration.DryRunStatus);
            Assert.Equal(EntryStatus.NotRun, view.DryRunStatus);
        }

        [Fact]
        public void AConnectionFailureFailsTheFirstEntryAndLeavesTheRestUnverified()
        {
            var first = Entry("20260101-001-First.sql", ScriptKind.Migration);
            var second = Entry("20260101-002-Second.sql", ScriptKind.Migration);
            _gateway.BeginSessionThrows = new InvalidOperationException("login failed");

            CreateDryRunner().RunDryRun(Plan(first, second));

            Assert.Equal(EntryStatus.DryRunFailed, first.DryRunStatus);
            Assert.Equal("login failed", first.DryRunDetail);
            Assert.Equal(EntryStatus.NotRun, second.DryRunStatus);
        }

        [Fact]
        public void EachTargetDatabaseGetsItsOwnSession()
        {
            var db1 = Entry("20260101-001-Foo.sql", ScriptKind.Migration, "Db1");
            var db2 = Entry("20260101-001-Foo.sql", ScriptKind.Migration, "Db2");

            CreateDryRunner().RunDryRun(Plan(db1, db2));

            Assert.Equal(new[] { "conn:Db1", "conn:Db2" }, _gateway.Sessions.Select(s => s.ConnectionString));
        }

        [Fact]
        public void FallsBackToReadingTheFileWhenThePlanCarriesNoScriptText()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("20260101-001-Foo.sql", "-- Tags: Db1\nSELECT 1;");
            var entry = Entry("20260101-001-Foo.sql", ScriptKind.Migration);
            entry.FilePath = filePath;
            entry.ScriptText = null;

            CreateDryRunner().RunDryRun(Plan(entry));

            Assert.Equal("-- Tags: Db1\nSELECT 1;", Assert.Single(_gateway.LastSession!.Executed));
        }
    }
}
