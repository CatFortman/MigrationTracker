using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Data;
using MigrationOps.Core.MigrationFramework.Execution;
using MigrationOps.Core.MigrationFramework.Scripts;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Services
{
    /// <summary>
    /// The apply pipeline: decides which files run against which databases, enforces the
    /// validation and immutability rules, and hands the SQL to an
    /// <see cref="IScriptExecutionGateway"/>. Contains no ADO.NET of its own.
    /// </summary>
    public class ScriptApplier
    {
        private readonly IMigrationConfig _config;
        private readonly IHistoryStore _historyStore;
        private readonly IScriptExecutionGateway _gateway;
        private readonly IMigrationAlertNotifier _alertNotifier;

        public ScriptApplier(
            IMigrationConfig config,
            IHistoryStore historyStore,
            IScriptExecutionGateway gateway,
            IMigrationAlertNotifier alertNotifier)
        {
            _config = config;
            _historyStore = historyStore;
            _gateway = gateway;
            _alertNotifier = alertNotifier;
        }

        /// <summary>
        /// Applies database object scripts (functions, views, stored procedures, triggers) from
        /// each target database's own subfolder under the configured script directory. Runs before
        /// migrations so that migrations can rely on the latest object definitions.
        ///
        /// A script whose SQL fails here (e.g. a view referencing a table a pending migration
        /// creates) is deferred rather than fatal — the caller retries the returned entries with
        /// <see cref="RetryDeferredScripts"/> after migrations run. Validation failures (no
        /// CREATE OR ALTER) still throw immediately, since a retry cannot fix them.
        /// </summary>
        /// <returns>The (file, database) pairs that failed to apply and should be retried after migrations.</returns>
        public List<(string File, string Database)> ApplyDatabaseObjectScripts(string scriptsRootDirectory, string? onlyDatabase = null)
        {
            EnsureNoUnrecognizedDatabaseFolders(scriptsRootDirectory);

            var deferred = new List<(string File, string Database)>();

            foreach (var database in TargetDatabases(onlyDatabase))
            {
                foreach (var file in ScriptCatalog.ListDatabaseObjectFiles(scriptsRootDirectory, database))
                {
                    if (!ApplyScriptFile(file, ScriptKind.DatabaseObject, database, deferSqlFailures: true))
                    {
                        deferred.Add((file, database));
                    }
                }
            }

            return deferred;
        }

        /// <summary>
        /// Retries database object scripts deferred by <see cref="ApplyDatabaseObjectScripts"/>.
        /// By this point migrations have run, so any remaining failure is a real error and throws.
        /// </summary>
        public void RetryDeferredScripts(List<(string File, string Database)> deferredFiles)
        {
            foreach (var (file, database) in deferredFiles)
            {
                ApplyScriptFile(file, ScriptKind.DatabaseObject, database, deferSqlFailures: false);
            }
        }

        public void ApplyMigrations(string directory, string? onlyDatabase = null)
        {
            EnsureNoUnrecognizedDatabaseFolders(directory);

            foreach (var database in TargetDatabases(onlyDatabase))
            {
                foreach (var file in ScriptCatalog.ListMigrationFiles(directory, database))
                {
                    ApplyScriptFile(file, ScriptKind.Migration, database, deferSqlFailures: false);
                }
            }
        }

        // Every configured database, or just onlyDatabase when a --db filter is active.
        private IEnumerable<string> TargetDatabases(string? onlyDatabase)
        {
            return onlyDatabase != null ? new[] { onlyDatabase } : _config.GetDatabaseNames();
        }

        // A folder that doesn't match any configured database would otherwise be silently never
        // discovered, since routing is now purely by folder location. Checked against every
        // configured database regardless of an active --db filter.
        private void EnsureNoUnrecognizedDatabaseFolders(string rootDirectory)
        {
            var stray = ScriptCatalog.FindUnrecognizedDatabaseFolders(rootDirectory, _config.GetDatabaseNames());

            if (stray.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Folder(s) {string.Join(", ", stray)} under '{rootDirectory}' do not match any configured database.");
            }
        }

        private bool ApplyScriptFile(string file, ScriptKind kind, string database, bool deferSqlFailures = false)
        {
            string scriptName = Path.GetFileName(file);
            string kindLabel = kind == ScriptKind.Migration ? "migration" : "database object script";
            string script = File.ReadAllText(file);
            string checksum;

            try
            {
                checksum = ScriptParser.ComputeChecksum(script);

                if (kind == ScriptKind.DatabaseObject)
                {
                    ScriptParser.EnsureCreateOrAlterStatement(script, scriptName);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to process {kindLabel} '{scriptName}': {ex.Message}", ex);
            }

            var connectionString = _config.GetConnectionString(database);

            _historyStore.EnsureHistoryTable(connectionString, kind);

            // Migrations are immutable once applied: HasBeenApplied only matches on
            // (name, checksum), so an edited file would otherwise look "never applied"
            // and get re-executed. Object scripts are exempt - re-applying an edited
            // proc/view is the designed workflow.
            if (kind == ScriptKind.Migration)
            {
                var recordedChecksum = _historyStore.GetLatestSuccessfulMigrationChecksum(connectionString, scriptName);
                var editedError = ScriptParser.DetectEditedMigration(scriptName, recordedChecksum, checksum);

                if (editedError != null)
                {
                    throw new InvalidOperationException(editedError);
                }
            }

            if (_historyStore.HasBeenApplied(connectionString, scriptName, checksum, kind))
            {
                Console.WriteLine($"Skipping {scriptName} as it has already been applied to {database}");
                return true;
            }

            var result = _gateway.ApplyScript(connectionString, script, scriptName, checksum, kind);

            if (result.Succeeded)
            {
                Console.WriteLine($"Applied {scriptName} to {database} on the specified server");
                return true;
            }

            if (deferSqlFailures)
            {
                Console.WriteLine(
                    $"Deferring {scriptName} on {database} (will retry after migrations): {result.ErrorMessage}");
                return false;
            }

            ReportFailure(connectionString, scriptName, checksum, database, kind, result.ErrorMessage, result.DurationMs);

            throw new InvalidOperationException(
                $"Failed to apply {kindLabel} '{scriptName}' to database '{database}' (rolled back): {result.ErrorMessage}",
                result.Error);
        }

        /// <summary>
        /// Best-effort failure telemetry for a non-deferred apply failure: records a Success = 0
        /// history row (migrations only — __ScriptHistory has no Success column) and fires the
        /// alert webhook. Runs after rollback on a fresh connection; never throws, so telemetry
        /// problems cannot mask the original failure.
        /// </summary>
        private void ReportFailure(string connectionString, string scriptName, string checksum, string currentDb, ScriptKind kind, string errorMessage, int durationMs)
        {
            if (kind == ScriptKind.Migration)
            {
                try
                {
                    _historyStore.RecordMigrationFailure(connectionString, scriptName, checksum, errorMessage, durationMs);
                }
                catch (Exception recordEx)
                {
                    Console.WriteLine($"Failed to record failure of {scriptName} in __MigrationHistory: {recordEx.Message}");
                }
            }

            try
            {
                _alertNotifier.NotifyFailureAsync(scriptName, currentDb, errorMessage).GetAwaiter().GetResult();
            }
            catch (Exception alertEx)
            {
                Console.WriteLine($"Failed to send failure alert for {scriptName}: {alertEx.Message}");
            }
        }
    }
}
