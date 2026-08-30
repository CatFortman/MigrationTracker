using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Data;
using MigrationOps.Core.MigrationFramework.Scripts;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Services
{
    /// <summary>
    /// Diffs what's on disk against what each database's history records, and classifies every
    /// file into a read-only plan. The per-file classifiers are static and take history as a
    /// parameter, so they can be exercised without a database.
    /// </summary>
    public class PlanBuilder
    {
        private readonly IMigrationConfig _config;
        private readonly IHistoryStore _historyStore;

        public PlanBuilder(IMigrationConfig config, IHistoryStore historyStore)
        {
            _config = config;
            _historyStore = historyStore;
        }

        /// <summary>
        /// Builds a read-only preview of what a real run would do against each target database:
        /// object scripts first, then migrations, classified per file. Never halts on a bad file
        /// or an unreachable database — problems become entries in the plan.
        /// </summary>
        public MigrationPlan BuildPlan(string scriptsRootDirectory, string migrationsDirectory, IReadOnlyList<string> targetDatabases)
        {
            var plan = new MigrationPlan { TargetDatabases = targetDatabases.ToList() };

            // Tagless files surface once per classifier call; report each only once.
            var unresolvedReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var database in targetDatabases)
            {
                List<MigrationHistoryRecord> migrationHistory;
                List<ScriptHistoryRecord> scriptHistory;

                try
                {
                    var connectionString = _config.GetConnectionString(database);
                    migrationHistory = _historyStore.GetMigrationHistory(connectionString);
                    scriptHistory = _historyStore.GetScriptObjectHistory(connectionString);
                }
                catch (Exception ex)
                {
                    plan.Entries.Add(new PlanEntry
                    {
                        FileName = "(connection)",
                        Database = database,
                        Status = PlanEntryStatus.ValidationError,
                        Detail = $"cannot read history: {ex.Message}"
                    });
                    continue;
                }

                foreach (var status in GetScriptObjectFileStatuses(scriptsRootDirectory, database, scriptHistory))
                {
                    AddPlanEntry(plan, status, ScriptKind.DatabaseObject, database,
                        ScriptCatalog.FindDatabaseObjectFilePath(scriptsRootDirectory, status.FileName), unresolvedReported);
                }

                foreach (var status in GetMigrationFileStatuses(migrationsDirectory, database, migrationHistory))
                {
                    AddPlanEntry(plan, status, ScriptKind.Migration, database,
                        Path.Combine(migrationsDirectory, status.FileName), unresolvedReported);
                }
            }

            return plan;
        }

        // Diffs the migration files targeting `database` against its history to report what's
        // pending and whether an already-applied file's contents have drifted from what was recorded.
        public static List<MigrationFileStatus> GetMigrationFileStatuses(string migrationsDirectory, string database, List<MigrationHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestSuccessChecksum = history
                .Where(h => h.Success)
                .GroupBy(h => h.MigrationName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            foreach (var file in ScriptCatalog.ListMigrationFiles(migrationsDirectory))
            {
                var fileName = Path.GetFileName(file);

                List<string> tags;
                try
                {
                    tags = ScriptParser.ParseTagsFromFile(file);
                }
                catch (InvalidOperationException ex)
                {
                    // A tagless file can't be matched to any database, so it is reported
                    // regardless of the filter instead of sinking the whole listing;
                    // callers running per-database dedupe by filename.
                    statuses.Add(new MigrationFileStatus
                    {
                        FileName = fileName,
                        ValidationError = ex.Message
                    });
                    continue;
                }

                if (!ScriptParser.ShouldApplyScript(tags, database))
                {
                    continue;
                }

                var currentChecksum = ScriptParser.ComputeChecksum(File.ReadAllText(file));

                var isApplied = history.Any(h => h.Success && h.MigrationName == fileName && h.Checksum == currentChecksum);
                var hasRecordedChecksum = latestSuccessChecksum.TryGetValue(fileName, out var recordedChecksum);

                statuses.Add(new MigrationFileStatus
                {
                    FileName = fileName,
                    Tags = tags,
                    IsApplied = isApplied,
                    HasDrift = hasRecordedChecksum && recordedChecksum != currentChecksum,
                    RecordedChecksum = hasRecordedChecksum ? recordedChecksum : null,
                    CurrentChecksum = currentChecksum
                });
            }

            return statuses;
        }

        // Object-script counterpart of GetMigrationFileStatuses: same shape, but enumerates the
        // four object folders in run order and additionally validates CREATE OR ALTER.
        public static List<MigrationFileStatus> GetScriptObjectFileStatuses(string scriptsRootDirectory, string database, List<ScriptHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestChecksum = history
                .GroupBy(h => h.ScriptName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            foreach (var file in ScriptCatalog.ListDatabaseObjectFiles(scriptsRootDirectory))
            {
                var fileName = Path.GetFileName(file);

                List<string> tags;
                try
                {
                    tags = ScriptParser.ParseTagsFromFile(file);
                }
                catch (InvalidOperationException ex)
                {
                    statuses.Add(new MigrationFileStatus
                    {
                        FileName = fileName,
                        ValidationError = ex.Message
                    });
                    continue;
                }

                if (!ScriptParser.ShouldApplyScript(tags, database))
                {
                    continue;
                }

                var script = File.ReadAllText(file);
                var currentChecksum = ScriptParser.ComputeChecksum(script);

                try
                {
                    ScriptParser.EnsureCreateOrAlterStatement(script, fileName);
                }
                catch (InvalidOperationException ex)
                {
                    statuses.Add(new MigrationFileStatus
                    {
                        FileName = fileName,
                        Tags = tags,
                        CurrentChecksum = currentChecksum,
                        ValidationError = ex.Message
                    });
                    continue;
                }

                var isApplied = history.Any(h => h.ScriptName == fileName && h.Checksum == currentChecksum);
                var hasRecordedChecksum = latestChecksum.TryGetValue(fileName, out var recordedChecksum);

                statuses.Add(new MigrationFileStatus
                {
                    FileName = fileName,
                    Tags = tags,
                    IsApplied = isApplied,
                    HasDrift = hasRecordedChecksum && recordedChecksum != currentChecksum,
                    RecordedChecksum = hasRecordedChecksum ? recordedChecksum : null,
                    CurrentChecksum = currentChecksum
                });
            }

            return statuses;
        }

        // internal (not public) so MigrationOps.Core.Tests can exercise the classification logic
        // directly without widening the plan-building API surface.
        internal static void AddPlanEntry(MigrationPlan plan, MigrationFileStatus status, ScriptKind kind, string database, string filePath, HashSet<string> unresolvedReported)
        {
            var unresolved = status.ValidationError != null && status.Tags.Count == 0;
            if (unresolved && !unresolvedReported.Add(status.FileName))
            {
                return;
            }

            var entry = new PlanEntry
            {
                FileName = status.FileName,
                FilePath = filePath,
                Kind = kind,
                Database = unresolved ? "(unresolved)" : database,
                RecordedChecksum = status.RecordedChecksum,
                CurrentChecksum = string.IsNullOrEmpty(status.CurrentChecksum) ? null : status.CurrentChecksum
            };

            if (status.ValidationError != null)
            {
                entry.Status = PlanEntryStatus.ValidationError;
                entry.Detail = status.ValidationError;
            }
            else if (status.HasDrift && kind == ScriptKind.Migration)
            {
                entry.Status = PlanEntryStatus.Changed;
                entry.Detail = $"recorded {ScriptParser.ShortChecksum(status.RecordedChecksum)} but file is {ScriptParser.ShortChecksum(status.CurrentChecksum)}";
            }
            else if (status.IsApplied)
            {
                entry.Status = PlanEntryStatus.AlreadyApplied;
            }
            else
            {
                // Includes drifted object scripts: editing a proc/view so it re-applies is the
                // designed workflow, unlike editing an applied migration.
                entry.Status = PlanEntryStatus.WouldApply;
                entry.Detail = status.HasDrift ? "would apply (updated)" : "would apply (new)";
            }

            if (entry.Status == PlanEntryStatus.WouldApply || entry.Status == PlanEntryStatus.Changed)
            {
                entry.ScriptText = File.ReadAllText(filePath);
            }

            plan.Entries.Add(entry);
        }
    }
}
