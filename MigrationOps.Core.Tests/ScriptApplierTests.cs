using MigrationOps.Core.MigrationFramework.Scripts;
using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    // The apply pipeline used to be reachable only against a live SQL Server. With the execution
    // gateway and history store behind interfaces, these drive it end to end in-process: folder
    // routing, the immutability guard, defer-and-retry for object scripts, and failure telemetry.
    public class ScriptApplierTests
    {
        private readonly TestConfig _config = TestConfig.WithDatabases("Db1", "Db2");
        private readonly FakeHistoryStore _history = new();
        private readonly FakeExecutionGateway _gateway = new();
        private readonly RecordingAlertNotifier _notifier = new();

        private ScriptApplier CreateApplier() => new(_config, _history, _gateway, _notifier);

        [Fact]
        public void AppliesAPendingMigrationWithTheChecksumComputedFromItsContent()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);

            CreateApplier().ApplyMigrations(dir.Path);

            var attempt = Assert.Single(_gateway.Attempts);
            Assert.Equal("20260101-001-Foo.sql", attempt.ScriptName);
            Assert.Equal("conn:Db1", attempt.ConnectionString);
            Assert.Equal(ScriptKind.Migration, attempt.Kind);
            Assert.Equal(ScriptParser.ComputeChecksum(script), attempt.Checksum);
        }

        [Fact]
        public void SkipsAMigrationAlreadyRecordedWithTheSameChecksum()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);
            _history.AppliedRecords.Add(("20260101-001-Foo.sql", ScriptParser.ComputeChecksum(script)));

            CreateApplier().ApplyMigrations(dir.Path);

            Assert.Empty(_gateway.Attempts);
        }

        [Fact]
        public void RefusesToReapplyAMigrationEditedAfterItWasApplied()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 2;");
            _history.LatestSuccessfulChecksums["20260101-001-Foo.sql"] = "checksum-of-the-original";

            var ex = Assert.Throws<InvalidOperationException>(() => CreateApplier().ApplyMigrations(dir.Path));

            Assert.Contains("immutable", ex.Message);
            Assert.Empty(_gateway.Attempts);
        }

        [Fact]
        public void DatabaseFilterAppliesOnlyToTheRequestedDatabase()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 1;");
            dir.WriteFile("Db2/20260101-001-Foo.sql", "SELECT 1;");

            CreateApplier().ApplyMigrations(dir.Path, onlyDatabase: "Db2");

            var attempt = Assert.Single(_gateway.Attempts);
            Assert.Equal("conn:Db2", attempt.ConnectionString);
        }

        [Fact]
        public void MigrationsRunInFilenameOrder()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-002-Second.sql", "SELECT 2;");
            dir.WriteFile("Db1/20260101-001-First.sql", "SELECT 1;");

            CreateApplier().ApplyMigrations(dir.Path);

            Assert.Equal(new[] { "20260101-001-First.sql", "20260101-002-Second.sql" }, _gateway.Committed);
        }

        [Fact]
        public void EnsuresTheHistoryTableBeforeTouchingTheDatabase()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 1;");

            CreateApplier().ApplyMigrations(dir.Path);

            Assert.Equal(("conn:Db1", ScriptKind.Migration), Assert.Single(_history.EnsuredTables));
        }

        [Fact]
        public void ThrowsWhenAFolderDoesNotMatchAnyConfiguredDatabase()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db3/20260101-001-Foo.sql", "SELECT 1;");

            var ex = Assert.Throws<InvalidOperationException>(() => CreateApplier().ApplyMigrations(dir.Path));

            Assert.Contains("Db3", ex.Message);
            Assert.Contains("do not match any configured database", ex.Message);
            Assert.Empty(_gateway.Attempts);
        }

        [Fact]
        public void FailedMigrationRecordsAFailureRowAlertsOnceAndThrows()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 1;");
            _gateway.FailuresRemaining["20260101-001-Foo.sql"] = 1;

            var ex = Assert.Throws<InvalidOperationException>(() => CreateApplier().ApplyMigrations(dir.Path));

            Assert.Contains("rolled back", ex.Message);
            Assert.Contains("SQL error in 20260101-001-Foo.sql", ex.Message);

            var failure = Assert.Single(_history.RecordedFailures);
            Assert.Equal("20260101-001-Foo.sql", failure.Name);
            Assert.Equal(7, failure.DurationMs);

            var alert = Assert.Single(_notifier.Alerts);
            Assert.Equal("Db1", alert.Database);
        }

        [Fact]
        public void TelemetryFailuresDoNotMaskTheOriginalSqlError()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 1;");
            _gateway.FailuresRemaining["20260101-001-Foo.sql"] = 1;
            _history.RecordFailureThrows = new InvalidOperationException("history table unreachable");
            _notifier.Throws = new InvalidOperationException("webhook down");

            var ex = Assert.Throws<InvalidOperationException>(() => CreateApplier().ApplyMigrations(dir.Path));

            Assert.Contains("SQL error in 20260101-001-Foo.sql", ex.Message);
        }

        [Fact]
        public void FailedObjectScriptIsDeferredRatherThanFatal()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Db1/Views/Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");
            _gateway.FailuresRemaining["Foo.sql"] = 1;

            var deferred = CreateApplier().ApplyDatabaseObjectScripts(dir.Path);

            var (file, database) = Assert.Single(deferred);
            Assert.Equal(Path.GetFullPath(filePath), Path.GetFullPath(file));
            Assert.Equal("Db1", database);
            Assert.Empty(_notifier.Alerts);
            Assert.Empty(_history.RecordedFailures);
        }

        [Fact]
        public void DeferredObjectScriptSucceedsOnRetryAfterMigrations()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/Views/Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");
            _gateway.FailuresRemaining["Foo.sql"] = 1;

            var applier = CreateApplier();
            var deferred = applier.ApplyDatabaseObjectScripts(dir.Path);
            applier.RetryDeferredScripts(deferred);

            Assert.Equal("Foo.sql", Assert.Single(_gateway.Committed));
            Assert.Equal(2, _gateway.Attempts.Count);
        }

        [Fact]
        public void ObjectScriptStillFailingOnRetryThrowsAndAlertsWithoutAHistoryRow()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/Views/Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");
            _gateway.FailuresRemaining["Foo.sql"] = 2;

            var applier = CreateApplier();
            var deferred = applier.ApplyDatabaseObjectScripts(dir.Path);

            var ex = Assert.Throws<InvalidOperationException>(() => applier.RetryDeferredScripts(deferred));

            Assert.Contains("Failed to apply database object script", ex.Message);
            Assert.Single(_notifier.Alerts);
            // __ScriptHistory has no Success column, so object scripts never get a failure row.
            Assert.Empty(_history.RecordedFailures);
        }

        [Fact]
        public void ObjectScriptWithoutCreateOrAlterThrowsEvenThoughSqlFailuresAreDeferred()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/Views/Foo.sql", "CREATE VIEW dbo.V AS SELECT 1;");

            var ex = Assert.Throws<InvalidOperationException>(() => CreateApplier().ApplyDatabaseObjectScripts(dir.Path));

            Assert.Contains("Failed to process database object script", ex.Message);
            Assert.Empty(_gateway.Attempts);
        }

        [Fact]
        public void ObjectScriptsRunInFolderOrderFunctionsViewsProceduresTriggers()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/Triggers/T.sql", "CREATE OR ALTER TRIGGER dbo.T ON dbo.X AFTER INSERT AS SELECT 1;");
            dir.WriteFile("Db1/Functions/F.sql", "CREATE OR ALTER FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END;");
            dir.WriteFile("Db1/StoredProcedures/P.sql", "CREATE OR ALTER PROCEDURE dbo.P AS SELECT 1;");
            dir.WriteFile("Db1/Views/V.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");

            CreateApplier().ApplyDatabaseObjectScripts(dir.Path);

            Assert.Equal(new[] { "F.sql", "V.sql", "P.sql", "T.sql" }, _gateway.Committed);
        }

        [Fact]
        public void AnEditedObjectScriptIsReappliedUnlikeAnEditedMigration()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/Views/Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 2;");
            _history.LatestSuccessfulChecksums["Foo.sql"] = "checksum-of-the-original";

            var deferred = CreateApplier().ApplyDatabaseObjectScripts(dir.Path);

            Assert.Empty(deferred);
            Assert.Equal("Foo.sql", Assert.Single(_gateway.Committed));
        }
    }
}
