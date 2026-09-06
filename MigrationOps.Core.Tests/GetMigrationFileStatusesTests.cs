using MigrationOps.Core.MigrationFramework.Scripts;
using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    public class GetMigrationFileStatusesTests
    {
        [Fact]
        public void MarksFileAppliedWhenChecksumMatchesSuccessfulHistory()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = ScriptParser.ComputeChecksum(script), Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.True(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void MarksFileAsDriftedWhenFileChecksumDiffersFromLastSuccessfulApply()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);
            var currentChecksum = ScriptParser.ComputeChecksum(script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = "old-checksum", Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.False(status.IsApplied);
            Assert.True(status.HasDrift);
            Assert.Equal("old-checksum", status.RecordedChecksum);
            Assert.Equal(currentChecksum, status.CurrentChecksum);
        }

        [Fact]
        public void MarksFileAsPendingWhenNotYetInHistory()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db1/20260101-001-Foo.sql", "SELECT 1;");

            var status = Assert.Single(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));

            Assert.False(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void IgnoresFailedHistoryRowsSoRetryIsNotMistakenForDrift()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = ScriptParser.ComputeChecksum(script), Success = false, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.False(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void UsesTheMostRecentSuccessfulChecksumWhenHistoryHasMultipleRows()
        {
            using var dir = new TempDirectory();
            const string script = "SELECT 1;";
            dir.WriteFile("Db1/20260101-001-Foo.sql", script);
            var currentChecksum = ScriptParser.ComputeChecksum(script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = "first", Success = true, AppliedOn = DateTime.UtcNow.AddMinutes(-10) },
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = currentChecksum, Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.True(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void OnlyListsFilesUnderTheRequestedDatabasesFolder()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("Db2/20260101-001-Foo.sql", "SELECT 1;");

            Assert.Empty(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));
        }

        [Fact]
        public void ReturnsEmptyWhenTheDatabasesFolderDoesNotExistYet()
        {
            using var dir = new TempDirectory();

            Assert.Empty(PlanBuilder.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));
        }
    }
}
