using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    public class GetMigrationFileStatusesTests
    {
        private readonly MigrationService _service = TestMigrationService.Create();

        [Fact]
        public void MarksFileAppliedWhenChecksumMatchesSuccessfulHistory()
        {
            using var dir = new TempDirectory();
            const string script = "-- Tags: Db1\nSELECT 1;";
            dir.WriteFile("20260101-001-Foo.sql", script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = MigrationService.ComputeChecksum(script), Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.True(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void MarksFileAsDriftedWhenFileChecksumDiffersFromLastSuccessfulApply()
        {
            using var dir = new TempDirectory();
            const string script = "-- Tags: Db1\nSELECT 1;";
            dir.WriteFile("20260101-001-Foo.sql", script);
            var currentChecksum = MigrationService.ComputeChecksum(script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = "old-checksum", Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.False(status.IsApplied);
            Assert.True(status.HasDrift);
            Assert.Equal("old-checksum", status.RecordedChecksum);
            Assert.Equal(currentChecksum, status.CurrentChecksum);
        }

        [Fact]
        public void MarksFileAsPendingWhenNotYetInHistory()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("20260101-001-Foo.sql", "-- Tags: Db1\nSELECT 1;");

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));

            Assert.False(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void IgnoresFailedHistoryRowsSoRetryIsNotMistakenForDrift()
        {
            using var dir = new TempDirectory();
            const string script = "-- Tags: Db1\nSELECT 1;";
            dir.WriteFile("20260101-001-Foo.sql", script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = MigrationService.ComputeChecksum(script), Success = false, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.False(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void UsesTheMostRecentSuccessfulChecksumWhenHistoryHasMultipleRows()
        {
            using var dir = new TempDirectory();
            const string script = "-- Tags: Db1\nSELECT 1;";
            dir.WriteFile("20260101-001-Foo.sql", script);
            var currentChecksum = MigrationService.ComputeChecksum(script);

            var history = new List<MigrationHistoryRecord>
            {
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = "first", Success = true, AppliedOn = DateTime.UtcNow.AddMinutes(-10) },
                new() { MigrationName = "20260101-001-Foo.sql", Checksum = currentChecksum, Success = true, AppliedOn = DateTime.UtcNow }
            };

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", history));

            Assert.True(status.IsApplied);
            Assert.False(status.HasDrift);
        }

        [Fact]
        public void SkipsFilesNotTaggedForTheRequestedDatabase()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("20260101-001-Foo.sql", "-- Tags: Db2\nSELECT 1;");

            Assert.Empty(_service.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));
        }

        [Fact]
        public void ReportsValidationErrorWhenFileHasNoTagsHeader()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("20260101-001-Foo.sql", "SELECT 1;");

            var status = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));

            Assert.NotNull(status.ValidationError);
        }

        [Fact]
        public void TaglessFileIsReportedRegardlessOfWhichDatabaseIsRequested()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("20260101-001-Foo.sql", "SELECT 1;");

            var statusForDb1 = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db1", new List<MigrationHistoryRecord>()));
            var statusForDb2 = Assert.Single(_service.GetMigrationFileStatuses(dir.Path, "Db2", new List<MigrationHistoryRecord>()));

            Assert.NotNull(statusForDb1.ValidationError);
            Assert.NotNull(statusForDb2.ValidationError);
        }
    }
}
