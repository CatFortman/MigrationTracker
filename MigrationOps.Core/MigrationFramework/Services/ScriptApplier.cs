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
        /// Applies database object scripts (functions, views, stored procedures, triggers) from the
        /// configured script directory. Runs before migrations so that migrations can rely on the
        /// latest object definitions.
        ///
        /// A script whose SQL fails here (e.g. a view referencing a table a pending migration
        /// creates) is deferred rather than fatal — the caller retries the returned files with
        /// <see cref="RetryDeferredScripts"/> after migrations run. Validation failures (missing
        /// tags, no CREATE OR ALTER) still throw immediately, since a retry cannot fix them.
        /// </summary>
        /// <returns>The scripts that failed to apply and should be retried after migrations.</returns>
        public List<string> ApplyDatabaseObjectScripts(string scriptsRootDirectory, string? onlyDatabase = null)
        {
            var files = ScriptCatalog.ListDatabaseObjectFiles(scriptsRootDirectory);

            var deferred = new List<string>();

            foreach (var file in files)
            {
                if (!ApplyScriptFile(file, ScriptKind.DatabaseObject, deferSqlFailures: true, onlyDatabase))
                {
                    deferred.Add(file);
                }
            }

            return deferred;
        }

        /// <summary>
        /// Retries database object scripts deferred by <see cref="ApplyDatabaseObjectScripts"/>.
        /// By this point migrations have run, so any remaining failure is a real error and throws.
        /// </summary>
        public void RetryDeferredScripts(List<string> deferredFiles, string? onlyDatabase = null)
        {
            foreach (var file in deferredFiles)
            {
                ApplyScriptFile(file, ScriptKind.DatabaseObject, deferSqlFailures: false, onlyDatabase);
            }
        }

        public void ApplyMigrations(string directory, string? onlyDatabase = null)
        {
            foreach (var file in ScriptCatalog.ListMigrationFiles(directory))
            {
                ApplyScriptFile(file, ScriptKind.Migration, deferSqlFailures: false, onlyDatabase);
            }
        }

        private bool ApplyScriptFile(string file, ScriptKind kind, bool deferSqlFailures = false, string? onlyDatabase = null)
        {
            string scriptName = Path.GetFileName(file);
            string kindLabel = kind == ScriptKind.Migration ? "migration" : "database object script";

            List<string> tags;
            string checksum;
            string script = File.ReadAllText(file);

            try
            {
                tags = ScriptParser.ParseTagsFromFile(file);
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

            foreach (var tag in tags)
            {
                // A file tagged only for other databases is skipped silently when a --db
                // filter is active; the default (null) keeps every call site's behavior.
                if (onlyDatabase != null && !tag.Equals(onlyDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string currentDb;
                string connectionString;

                try
                {
                    currentDb = ScriptParser.DetermineDatabaseFromTags(new List<string> { tag }, _config.GetDatabaseNames());
                    connectionString = _config.GetConnectionString(currentDb);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to resolve target database for {kindLabel} '{scriptName}' (tag '{tag}'): {ex.Message}", ex);
                }

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
                    Console.WriteLine($"Skipping {scriptName} as it has already been applied to {currentDb}");
                    continue;
                }

                var result = _gateway.ApplyScript(connectionString, script, scriptName, checksum, kind);

                if (result.Succeeded)
                {
                    Console.WriteLine($"Applied {scriptName} to {currentDb} on the specified server");
                    continue;
                }

                if (deferSqlFailures)
                {
                    Console.WriteLine(
                        $"Deferring {scriptName} on {currentDb} (will retry after migrations): {result.ErrorMessage}");
                    return false;
                }

                ReportFailure(connectionString, scriptName, checksum, currentDb, kind, result.ErrorMessage, result.DurationMs);

                throw new InvalidOperationException(
                    $"Failed to apply {kindLabel} '{scriptName}' to database '{currentDb}' (rolled back): {result.ErrorMessage}",
                    result.Error);
            }

            return true;
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
