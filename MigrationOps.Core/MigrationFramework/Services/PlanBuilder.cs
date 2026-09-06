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
            var knownDatabases = _config.GetDatabaseNames();

            // Checked against every configured database, not just this run's targets, so a stray
            // folder is flagged even under a --db-filtered run.
            foreach (var stray in ScriptCatalog.FindUnrecognizedDatabaseFolders(scriptsRootDirectory, knownDatabases)
                         .Concat(ScriptCatalog.FindUnrecognizedDatabaseFolders(migrationsDirectory, knownDatabases))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                plan.Entries.Add(new Entry
                {
                    FileName = $"({stray})",
                    Database = "(unresolved)",
                    Status = EntryStatus.ValidationError,
                    Detail = $"folder '{stray}' does not match any configured database"
                });
            }

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
                    plan.Entries.Add(new Entry
                    {
                        FileName = "(connection)",
                        Database = database,
                        Status = EntryStatus.ValidationError,
                        Detail = $"cannot read history: {ex.Message}"
                    });
                    continue;
                }

                foreach (var status in GetScriptObjectFileStatuses(scriptsRootDirectory, database, scriptHistory))
                {
                    AddPlanEntry(plan, status, ScriptKind.DatabaseObject, database,
                        ScriptCatalog.FindDatabaseObjectFilePath(scriptsRootDirectory, database, status.FileName));
                }

                foreach (var status in GetMigrationFileStatuses(migrationsDirectory, database, migrationHistory))
                {
                    AddPlanEntry(plan, status, ScriptKind.Migration, database,
                        Path.Combine(migrationsDirectory, database, status.FileName));
                }
            }

            return plan;
        }

        // Diffs the migration files under `database`'s own subfolder against its history to report
        // what's pending and whether an already-applied file's contents have drifted.
        public static List<MigrationFileStatus> GetMigrationFileStatuses(string migrationsDirectory, string database, List<MigrationHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestSuccessChecksum = history
                .Where(h => h.Success)
                .GroupBy(h => h.MigrationName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            foreach (var file in ScriptCatalog.ListMigrationFiles(migrationsDirectory, database))
            {
                var fileName = Path.GetFileName(file);
                var currentChecksum = ScriptParser.ComputeChecksum(File.ReadAllText(file));

                var isApplied = history.Any(h => h.Success && h.MigrationName == fileName && h.Checksum == currentChecksum);
                var hasRecordedChecksum = latestSuccessChecksum.TryGetValue(fileName, out var recordedChecksum);

                statuses.Add(new MigrationFileStatus
                {
                    FileName = fileName,
                    IsApplied = isApplied,
                    HasDrift = hasRecordedChecksum && recordedChecksum != currentChecksum,
                    RecordedChecksum = hasRecordedChecksum ? recordedChecksum : null,
                    CurrentChecksum = currentChecksum
                });
            }

            return statuses;
        }

        // Object-script counterpart of GetMigrationFileStatuses: same shape, but enumerates the
        // four object folders (under `database`'s own subfolder) in run order and additionally
        // validates CREATE OR ALTER.
        public static List<MigrationFileStatus> GetScriptObjectFileStatuses(string scriptsRootDirectory, string database, List<ScriptHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestChecksum = history
                .GroupBy(h => h.ScriptName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            foreach (var file in ScriptCatalog.ListDatabaseObjectFiles(scriptsRootDirectory, database))
            {
                var fileName = Path.GetFileName(file);
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
        internal static void AddPlanEntry(MigrationPlan plan, MigrationFileStatus status, ScriptKind kind, string database, string filePath)
        {
            var entry = new Entry
            {
                FileName = status.FileName,
                FilePath = filePath,
                Kind = kind,
                Database = database,
                RecordedChecksum = status.RecordedChecksum,
                CurrentChecksum = string.IsNullOrEmpty(status.CurrentChecksum) ? null : status.CurrentChecksum
            };

            if (status.ValidationError != null)
            {
                entry.Status = EntryStatus.ValidationError;
                entry.Detail = status.ValidationError;
            }
            else if (status.HasDrift && kind == ScriptKind.Migration)
            {
                entry.Status = EntryStatus.Changed;
                entry.Detail = $"recorded {ScriptParser.ShortChecksum(status.RecordedChecksum)} but file is {ScriptParser.ShortChecksum(status.CurrentChecksum)}";
            }
            else if (status.IsApplied)
            {
                entry.Status = EntryStatus.AlreadyApplied;
            }
            else
            {
                // Includes drifted object scripts: editing a proc/view so it re-applies is the
                // designed workflow, unlike editing an applied migration.
                entry.Status = EntryStatus.WouldApply;
                entry.Detail = status.HasDrift ? "would apply (updated)" : "would apply (new)";
            }

            if (entry.Status == EntryStatus.WouldApply || entry.Status == EntryStatus.Changed)
            {
                entry.ScriptText = File.ReadAllText(filePath);
            }

            plan.Entries.Add(entry);
        }
    }
}
