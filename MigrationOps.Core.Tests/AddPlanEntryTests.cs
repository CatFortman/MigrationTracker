using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    // AddPlanEntry is the single place that turns a MigrationFileStatus diff into the status
    // the dry-run report / --verify gate acts on. These tests exist mainly to lock in the one
    // rule that matters most: a drifted (edited) migration must classify as Changed, while a
    // drifted database object script (proc/view/etc., meant to be re-applied) classifies as
    // WouldApply — mixing those up would either block legitimate object redeploys or let an
    // edited migration slip through as an ordinary pending apply.
    public class AddPlanEntryTests
    {
        private static readonly HashSet<string> NoUnresolvedReported = new(StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void AlreadyAppliedStatusMapsToAlreadyApplied()
        {
            var plan = new DryRunPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                Tags = new List<string> { "Db1" },
                IsApplied = true,
                CurrentChecksum = "abc"
            };

            MigrationService.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", "Foo.sql", NoUnresolvedReported);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(PlanEntryStatus.AlreadyApplied, entry.Status);
        }

        [Fact]
        public void DriftedMigrationMapsToChangedNotWouldApply()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "SELECT 1;");
            var plan = new DryRunPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                Tags = new List<string> { "Db1" },
                HasDrift = true,
                RecordedChecksum = "old",
                CurrentChecksum = "new"
            };

            MigrationService.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", filePath, NoUnresolvedReported);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(PlanEntryStatus.Changed, entry.Status);
            Assert.Contains("recorded", entry.Detail);
            Assert.NotNull(entry.ScriptText);
        }

        [Fact]
        public void DriftedDatabaseObjectScriptMapsToWouldApplyNotChanged()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");
            var plan = new DryRunPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                Tags = new List<string> { "Db1" },
                HasDrift = true,
                RecordedChecksum = "old",
                CurrentChecksum = "new"
            };

            MigrationService.AddPlanEntry(plan, status, ScriptKind.DatabaseObject, "Db1", filePath, NoUnresolvedReported);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(PlanEntryStatus.WouldApply, entry.Status);
            Assert.Contains("updated", entry.Detail);
        }

        [Fact]
        public void NewPendingFileMapsToWouldApply()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "SELECT 1;");
            var plan = new DryRunPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                Tags = new List<string> { "Db1" },
                CurrentChecksum = "new"
            };

            MigrationService.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", filePath, NoUnresolvedReported);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(PlanEntryStatus.WouldApply, entry.Status);
            Assert.Contains("new", entry.Detail);
        }

        [Fact]
        public void TaglessFileIsReportedOnceAcrossMultipleDatabasesAsUnresolved()
        {
            var plan = new DryRunPlan();
            var status = new MigrationFileStatus { FileName = "Foo.sql", ValidationError = "no tags" };
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            MigrationService.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", "Foo.sql", reported);
            MigrationService.AddPlanEntry(plan, status, ScriptKind.Migration, "Db2", "Foo.sql", reported);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(PlanEntryStatus.ValidationError, entry.Status);
            Assert.Equal("(unresolved)", entry.Database);
        }
    }
}
