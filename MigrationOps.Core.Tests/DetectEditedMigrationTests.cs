using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    // Covers the guard added for #29: editing an already-applied migration must be refused by
    // `apply`, not just flagged by dry-run. DetectEditedMigration is the pure decision behind
    // that guard - given the checksum of the migration's last *successful* apply (or null if
    // it has never successfully applied) and the checksum of the file on disk, it decides
    // whether it's safe to proceed.
    public class DetectEditedMigrationTests
    {
        [Fact]
        public void AllowsAnUnchangedAlreadyAppliedMigration()
        {
            var error = ScriptParser.DetectEditedMigration("20260101-001-Foo.sql", recordedChecksum: "abc", currentChecksum: "abc");

            Assert.Null(error);
        }

        [Fact]
        public void RejectsAMigrationEditedAfterItWasApplied()
        {
            var error = ScriptParser.DetectEditedMigration("20260101-001-Foo.sql", recordedChecksum: "old-checksum", currentChecksum: "new-checksum");

            Assert.NotNull(error);
            Assert.Contains("20260101-001-Foo.sql", error);
            Assert.Contains("immutable", error);
        }

        [Fact]
        public void AllowsAMigrationThatHasNeverSuccessfullyApplied()
        {
            // recordedChecksum is null both for a brand-new file and for one whose only history
            // row is a failed attempt (Success = 0) - GetLatestSuccessfulMigrationChecksum only
            // looks at Success = 1 rows, so a failed-then-fixed migration is allowed to re-run.
            var error = ScriptParser.DetectEditedMigration("20260101-001-Foo.sql", recordedChecksum: null, currentChecksum: "whatever");

            Assert.Null(error);
        }
    }
}
