using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    // AddPlanEntry is the single place that turns a MigrationFileStatus diff into the status
    // the validate report / dry-run gate acts on. These tests exist mainly to lock in the one
    // rule that matters most: a drifted (edited) migration must classify as Changed, while a
    // drifted database object script (proc/view/etc., meant to be re-applied) classifies as
    // WouldApply — mixing those up would either block legitimate object redeploys or let an
    // edited migration slip through as an ordinary pending apply.
    public class AddPlanEntryTests
    {
        [Fact]
        public void AlreadyAppliedStatusMapsToAlreadyApplied()
        {
            var plan = new MigrationPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                IsApplied = true,
                CurrentChecksum = "abc"
            };

            PlanBuilder.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", "Foo.sql");

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(EntryStatus.AlreadyApplied, entry.Status);
        }

        [Fact]
        public void DriftedMigrationMapsToChangedNotWouldApply()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "SELECT 1;");
            var plan = new MigrationPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                HasDrift = true,
                RecordedChecksum = "old",
                CurrentChecksum = "new"
            };

            PlanBuilder.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", filePath);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(EntryStatus.Changed, entry.Status);
            Assert.Contains("recorded", entry.Detail);
            Assert.NotNull(entry.ScriptText);
        }

        [Fact]
        public void DriftedDatabaseObjectScriptMapsToWouldApplyNotChanged()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1;");
            var plan = new MigrationPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                HasDrift = true,
                RecordedChecksum = "old",
                CurrentChecksum = "new"
            };

            PlanBuilder.AddPlanEntry(plan, status, ScriptKind.DatabaseObject, "Db1", filePath);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(EntryStatus.WouldApply, entry.Status);
            Assert.Contains("updated", entry.Detail);
        }

        [Fact]
        public void NewPendingFileMapsToWouldApply()
        {
            using var dir = new TempDirectory();
            var filePath = dir.WriteFile("Foo.sql", "SELECT 1;");
            var plan = new MigrationPlan();
            var status = new MigrationFileStatus
            {
                FileName = "Foo.sql",
                CurrentChecksum = "new"
            };

            PlanBuilder.AddPlanEntry(plan, status, ScriptKind.Migration, "Db1", filePath);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(EntryStatus.WouldApply, entry.Status);
            Assert.Contains("new", entry.Detail);
        }

        [Fact]
        public void ObjectScriptValidationErrorMapsToValidationError()
        {
            var plan = new MigrationPlan();
            var status = new MigrationFileStatus { FileName = "Foo.sql", ValidationError = "missing CREATE OR ALTER" };

            PlanBuilder.AddPlanEntry(plan, status, ScriptKind.DatabaseObject, "Db1", "Foo.sql");

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(EntryStatus.ValidationError, entry.Status);
            Assert.Equal("Db1", entry.Database);
        }
    }
}
