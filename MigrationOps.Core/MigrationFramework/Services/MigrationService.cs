using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using MigrationOps.Core.MigrationFramework.AppConstants;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Services
{
    public class MigrationService
    {
        private static readonly string[] DatabaseObjectFolders = { "Functions", "Views", "StoredProcedures", "Triggers" };

        private readonly string _connectionStringTemplate;
        private readonly IConfiguration _configuration;
        private readonly IMigrationAlertNotifier _alertNotifier;

        public MigrationService()
            : this(Path.Combine(Directory.GetCurrentDirectory(), "Configurations", "dbconfig.json"))
        {
        }

        // Lets callers whose working directory isn't the ConsoleApp's (e.g. the Dashboard) point
        // directly at the shared dbconfig.json instead of resolving it via the current directory.
        public MigrationService(string dbConfigFilePath)
        {
            var fullPath = Path.GetFullPath(dbConfigFilePath);
            var basePath = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var fileName = Path.GetFileName(fullPath);
            var localFileName = Path.GetFileNameWithoutExtension(fileName) + ".local" + Path.GetExtension(fileName);

            // Layering, lowest to highest precedence:
            //   1. dbconfig.json          - committed template, no real secrets
            //   2. dbconfig.local.json    - gitignored, per-developer local overrides
            //   3. environment variables  - e.g. Databases__Db1__ConnectionString, used in CI/CD
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(fileName, optional: true, reloadOnChange: true)
                .AddJsonFile(localFileName, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            _configuration = builder.Build();

            _connectionStringTemplate = _configuration["DatabaseSettings:ConnectionStringTemplate"];

            var alertsEnabled = bool.TryParse(_configuration["AlertSettings:Enabled"], out var enabled) && enabled;
            _alertNotifier = new WebhookAlertNotifier(_configuration["AlertSettings:WebhookUrl"], alertsEnabled);
        }

        public string GetConnectionString(string databaseName)
        {
            return _configuration[$"Databases:{databaseName}:ConnectionString"];
        }

        public string GetMigrationDirectory()
        {
            return _configuration["MigrationSettings:MigrationDirectory"];
        }

        public string GetScriptDirectory()
        {
            return _configuration["MigrationSettings:ScriptDirectory"];
        }

        public List<string> GetDatabaseNames()
        {
            return _configuration.GetSection("Databases").GetChildren().Select(db => db.Key).ToList();
        }

        public static List<string> ParseTagsFromFile(string filePath)
        {
            var tags = new List<string>();
            var lines = File.ReadLines(filePath);

            foreach (var line in lines)
            {
                if (line.StartsWith("-- Tags:"))
                {
                    var tagsLine = line.Split(':')[1].Trim();
                    tags = tagsLine.Split(',').Select(tag => tag.Trim()).ToList();
                    break;
                }
            }

            if (tags.Count == 0)
            {
                throw new InvalidOperationException($"The script {Path.GetFileName(filePath)} does not contain a 'Tags' comment.");
            }

            return tags;
        }

        public static bool ShouldApplyScript(List<string> tags, string currentDb)
        {
            return tags.Contains(currentDb, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies database object scripts (functions, views, stored procedures, triggers) from the
        /// configured script directory. Runs before migrations so that migrations can rely on the
        /// latest object definitions.
        ///
        /// A script whose SQL fails here (e.g. a view referencing a table a pending migration
        /// creates) is deferred rather than fatal — the caller retries the returned files with
        /// <see cref="RetryDeferredScripts"/> after migrations run. Validation failures (missing
        /// checksum/tags, no CREATE OR ALTER) still throw immediately, since a retry cannot fix them.
        /// </summary>
        /// <returns>The scripts that failed to apply and should be retried after migrations.</returns>
        public List<string> ApplyDatabaseObjectScripts(string scriptsRootDirectory, string? onlyDatabase = null)
        {
            var files = ListDatabaseObjectFiles(scriptsRootDirectory);

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
            var files = Directory.GetFiles(directory, "*.sql")
                                 .OrderBy(f => Path.GetFileName(f))
                                 .ToList();

            foreach (var file in files)
            {
                ApplyScriptFile(file, ScriptKind.Migration, deferSqlFailures: false, onlyDatabase);
            }
        }

        // The four object folders, flattened in the order the apply pipeline runs them.
        private static List<string> ListDatabaseObjectFiles(string scriptsRootDirectory)
        {
            return DatabaseObjectFolders
                .Select(folder => Path.Combine(scriptsRootDirectory, folder))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.sql").OrderBy(f => Path.GetFileName(f)))
                .ToList();
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
                tags = ParseTagsFromFile(file);
                checksum = ComputeChecksum(script);

                if (kind == ScriptKind.DatabaseObject)
                {
                    EnsureCreateOrAlterStatement(script, scriptName);
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
                    currentDb = DetermineDatabaseFromTags(new List<string> { tag });
                    connectionString = GetConnectionString(currentDb);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to resolve target database for {kindLabel} '{scriptName}' (tag '{tag}'): {ex.Message}", ex);
                }

                EnsureHistoryTable(connectionString, kind);

                // Migrations are immutable once applied: HasBeenApplied only matches on
                // (name, checksum), so an edited file would otherwise look "never applied"
                // and get re-executed. Object scripts are exempt - re-applying an edited
                // proc/view is the designed workflow.
                if (kind == ScriptKind.Migration)
                {
                    var recordedChecksum = GetLatestSuccessfulMigrationChecksum(connectionString, scriptName);
                    var editedError = DetectEditedMigration(scriptName, recordedChecksum, checksum);

                    if (editedError != null)
                    {
                        throw new InvalidOperationException(editedError);
                    }
                }

                if (HasBeenApplied(connectionString, scriptName, checksum, kind))
                {
                    Console.WriteLine($"Skipping {scriptName} as it has already been applied to {currentDb}");
                    continue;
                }

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (var transaction = connection.BeginTransaction())
                    {
                        var stopwatch = Stopwatch.StartNew();

                        try
                        {
                            using (var command = new SqlCommand(script, connection, transaction))
                            {
                                command.ExecuteNonQuery();
                            }

                            stopwatch.Stop();
                            RecordApplied(connection, transaction, scriptName, checksum, kind, (int)stopwatch.ElapsedMilliseconds);

                            transaction.Commit();
                            Console.WriteLine($"Applied {scriptName} to {currentDb} on the specified server");
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            transaction.Rollback();

                            if (deferSqlFailures)
                            {
                                Console.WriteLine(
                                    $"Deferring {scriptName} on {currentDb} (will retry after migrations): {ex.Message}");
                                return false;
                            }

                            ReportFailure(connectionString, scriptName, checksum, currentDb, kind, ex.Message, (int)stopwatch.ElapsedMilliseconds);

                            throw new InvalidOperationException(
                                $"Failed to apply {kindLabel} '{scriptName}' to database '{currentDb}' (rolled back): {ex.Message}", ex);
                        }
                    }
                }
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
                    using (var connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        using (var command = new SqlCommand(SqlStatements.InsertMigrationRecord, connection))
                        {
                            command.Parameters.AddWithValue("@MigrationName", scriptName);
                            command.Parameters.AddWithValue("@Checksum", checksum);
                            command.Parameters.AddWithValue("@Success", false);
                            command.Parameters.AddWithValue("@ErrorMessage", errorMessage);
                            command.Parameters.AddWithValue("@DurationMs", durationMs);
                            command.ExecuteNonQuery();
                        }
                    }
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

        public string DetermineDatabaseFromTags(List<string> tags)
        {
            // Get list of known databases.
            var knownDatabases = _configuration.GetSection("Databases").GetChildren().Select(db => db.Key).ToList();

            // Check each tag to see if it matches a known database.
            foreach (var tag in tags)
            {
                if (knownDatabases.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    return tag;
                }
            }

            throw new InvalidOperationException("No valid database found in tags.");
        }

        public void EnsureMigrationHistoryTable(string connectionString)
        {
            EnsureHistoryTable(connectionString, ScriptKind.Migration);
        }

        private void EnsureHistoryTable(string connectionString, ScriptKind kind)
        {
            var sql = kind == ScriptKind.Migration
                ? SqlStatements.CreateMigrationHistoryTable
                : SqlStatements.CreateScriptHistoryTable;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                var command = new SqlCommand(sql, connection);
                command.ExecuteNonQuery();
            }
        }

        private string? GetLatestSuccessfulMigrationChecksum(string connectionString, string scriptName)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                var command = new SqlCommand(SqlStatements.SelectLatestSuccessfulMigrationChecksum, connection);
                command.Parameters.AddWithValue("@MigrationName", scriptName);
                var result = command.ExecuteScalar();
                return result == null || result == DBNull.Value ? null : (string)result;
            }
        }

        /// <summary>
        /// Pure decision function behind the "editing an applied migration is forbidden" guard:
        /// given the checksum of the migration's last successful apply (null if it has never
        /// successfully applied) and the checksum of the file on disk now, returns a descriptive
        /// error if they've diverged, or null if it's safe to proceed (never applied, or applied
        /// with this exact checksum already).
        /// </summary>
        internal static string? DetectEditedMigration(string scriptName, string? recordedChecksum, string currentChecksum)
        {
            if (recordedChecksum == null || recordedChecksum == currentChecksum)
            {
                return null;
            }

            return $"Migration '{scriptName}' was already applied with checksum {ShortChecksum(recordedChecksum)} " +
                   $"but the file now has checksum {ShortChecksum(currentChecksum)}. Migrations are immutable once " +
                   "applied - create a new migration instead of editing this one.";
        }

        private bool HasBeenApplied(string connectionString, string scriptName, string checksum, ScriptKind kind)
        {
            var sql = kind == ScriptKind.Migration ? SqlStatements.CheckMigrationApplied : SqlStatements.CheckScriptApplied;
            var paramName = kind == ScriptKind.Migration ? "@MigrationName" : "@ScriptName";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue(paramName, scriptName);
                command.Parameters.AddWithValue("@Checksum", checksum);
                return (int)command.ExecuteScalar() > 0;
            }
        }

        private void RecordApplied(SqlConnection connection, SqlTransaction transaction, string scriptName, string checksum, ScriptKind kind, int durationMs)
        {
            var sql = kind == ScriptKind.Migration ? SqlStatements.InsertMigrationRecord : SqlStatements.InsertScriptRecord;
            var paramName = kind == ScriptKind.Migration ? "@MigrationName" : "@ScriptName";

            using (var command = new SqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue(paramName, scriptName);
                command.Parameters.AddWithValue("@Checksum", checksum);

                if (kind == ScriptKind.Migration)
                {
                    command.Parameters.AddWithValue("@Success", true);
                    command.Parameters.AddWithValue("@ErrorMessage", DBNull.Value);
                    command.Parameters.AddWithValue("@DurationMs", durationMs);
                }

                command.ExecuteNonQuery();
            }
        }

        public List<MigrationHistoryRecord> GetMigrationHistory(string connectionString)
        {
            EnsureMigrationHistoryTable(connectionString);

            var records = new List<MigrationHistoryRecord>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(SqlStatements.SelectMigrationHistory, connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        records.Add(new MigrationHistoryRecord
                        {
                            MigrationId = reader.GetInt32(reader.GetOrdinal("MigrationId")),
                            MigrationName = reader.GetString(reader.GetOrdinal("MigrationName")),
                            AppliedOn = reader.GetDateTime(reader.GetOrdinal("AppliedOn")),
                            Checksum = reader.IsDBNull(reader.GetOrdinal("Checksum")) ? null : reader.GetString(reader.GetOrdinal("Checksum")),
                            Success = reader.GetBoolean(reader.GetOrdinal("Success")),
                            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                            DurationMs = reader.IsDBNull(reader.GetOrdinal("DurationMs")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("DurationMs"))
                        });
                    }
                }
            }

            return records;
        }

        // Diffs the migration files targeting `database` against its history to report what's
        // pending and whether an already-applied file's contents have drifted from what was recorded.
        public List<MigrationFileStatus> GetMigrationFileStatuses(string migrationsDirectory, string database, List<MigrationHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestSuccessChecksum = history
                .Where(h => h.Success)
                .GroupBy(h => h.MigrationName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            var files = Directory.GetFiles(migrationsDirectory, "*.sql").OrderBy(f => Path.GetFileName(f));

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);

                List<string> tags;
                try
                {
                    tags = ParseTagsFromFile(file);
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

                if (!ShouldApplyScript(tags, database))
                {
                    continue;
                }

                var currentChecksum = ComputeChecksum(File.ReadAllText(file));

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

        public List<ScriptHistoryRecord> GetScriptObjectHistory(string connectionString)
        {
            EnsureHistoryTable(connectionString, ScriptKind.DatabaseObject);

            var records = new List<ScriptHistoryRecord>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(SqlStatements.SelectScriptHistory, connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        records.Add(new ScriptHistoryRecord
                        {
                            ScriptId = reader.GetInt32(reader.GetOrdinal("ScriptId")),
                            ScriptName = reader.GetString(reader.GetOrdinal("ScriptName")),
                            AppliedOn = reader.GetDateTime(reader.GetOrdinal("AppliedOn")),
                            Checksum = reader.IsDBNull(reader.GetOrdinal("Checksum")) ? null : reader.GetString(reader.GetOrdinal("Checksum"))
                        });
                    }
                }
            }

            return records;
        }

        // Object-script counterpart of GetMigrationFileStatuses: same shape, but enumerates the
        // four object folders in run order and additionally validates CREATE OR ALTER.
        public List<MigrationFileStatus> GetScriptObjectFileStatuses(string scriptsRootDirectory, string database, List<ScriptHistoryRecord> history)
        {
            var statuses = new List<MigrationFileStatus>();

            var latestChecksum = history
                .GroupBy(h => h.ScriptName)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.AppliedOn).First().Checksum);

            foreach (var file in ListDatabaseObjectFiles(scriptsRootDirectory))
            {
                var fileName = Path.GetFileName(file);

                List<string> tags;
                try
                {
                    tags = ParseTagsFromFile(file);
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

                if (!ShouldApplyScript(tags, database))
                {
                    continue;
                }

                var script = File.ReadAllText(file);
                var currentChecksum = ComputeChecksum(script);

                try
                {
                    EnsureCreateOrAlterStatement(script, fileName);
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

        /// <summary>
        /// Builds a read-only preview of what a real run would do against each target database:
        /// object scripts first, then migrations, classified per file. Never halts on a bad file
        /// or an unreachable database — problems become entries in the plan.
        /// </summary>
        public DryRunPlan BuildDryRunPlan(string scriptsRootDirectory, string migrationsDirectory, IReadOnlyList<string> targetDatabases)
        {
            var plan = new DryRunPlan { TargetDatabases = targetDatabases.ToList() };

            // Tagless files surface once per classifier call; report each only once.
            var unresolvedReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var database in targetDatabases)
            {
                List<MigrationHistoryRecord> migrationHistory;
                List<ScriptHistoryRecord> scriptHistory;

                try
                {
                    var connectionString = GetConnectionString(database);
                    migrationHistory = GetMigrationHistory(connectionString);
                    scriptHistory = GetScriptObjectHistory(connectionString);
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
                        FindDatabaseObjectFilePath(scriptsRootDirectory, status.FileName), unresolvedReported);
                }

                foreach (var status in GetMigrationFileStatuses(migrationsDirectory, database, migrationHistory))
                {
                    AddPlanEntry(plan, status, ScriptKind.Migration, database,
                        Path.Combine(migrationsDirectory, status.FileName), unresolvedReported);
                }
            }

            return plan;
        }

        // internal (not private) so MigrationOps.Core.Tests can exercise the classification
        // logic directly, without needing a live database connection.
        internal static void AddPlanEntry(DryRunPlan plan, MigrationFileStatus status, ScriptKind kind, string database, string filePath, HashSet<string> unresolvedReported)
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
                entry.Detail = $"recorded {ShortChecksum(status.RecordedChecksum)} but file is {ShortChecksum(status.CurrentChecksum)}";
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

        private static string ShortChecksum(string? checksum)
        {
            return string.IsNullOrEmpty(checksum) ? "(none)" : checksum.Length <= 8 ? checksum : checksum.Substring(0, 8) + "...";
        }

        private static string FindDatabaseObjectFilePath(string scriptsRootDirectory, string fileName)
        {
            return DatabaseObjectFolders
                .Select(folder => Path.Combine(scriptsRootDirectory, folder, fileName))
                .FirstOrDefault(File.Exists) ?? fileName;
        }

        /// <summary>
        /// Executes each database's pending entries (WouldApply + Changed) inside one transaction
        /// per database — so later scripts can see earlier scripts' schema — and always rolls it
        /// back. Proves the SQL works without committing anything; history inserts are not replayed.
        /// Results land in each entry's VerifyStatus/VerifyDetail.
        /// </summary>
        public void VerifyPlan(DryRunPlan plan)
        {
            foreach (var database in plan.TargetDatabases)
            {
                var pending = plan.Entries
                    .Where(e => e.Database.Equals(database, StringComparison.OrdinalIgnoreCase)
                             && (e.Status == PlanEntryStatus.WouldApply || e.Status == PlanEntryStatus.Changed))
                    .ToList();

                if (pending.Count == 0)
                {
                    continue;
                }

                try
                {
                    VerifyDatabase(GetConnectionString(database), pending);
                }
                catch (Exception ex)
                {
                    // Couldn't even get a connection/transaction: first pending entry carries
                    // the error, the rest are unverified.
                    pending[0].VerifyStatus = PlanEntryStatus.VerifyFailed;
                    pending[0].VerifyDetail = ex.Message;
                    foreach (var entry in pending.Skip(1))
                    {
                        entry.VerifyStatus = PlanEntryStatus.NotVerified;
                    }
                }
            }
        }

        private void VerifyDatabase(string connectionString, List<PlanEntry> pending)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var objectEntries = pending.Where(e => e.Kind == ScriptKind.DatabaseObject).ToList();
                        var migrationEntries = pending.Where(e => e.Kind == ScriptKind.Migration).ToList();
                        var deferred = new List<PlanEntry>();
                        var stopped = false;

                        // Phase 1: object scripts, mirroring the real run's defer-on-failure.
                        foreach (var entry in objectEntries)
                        {
                            if (stopped)
                            {
                                MarkNotVerified(entry);
                                continue;
                            }

                            try
                            {
                                ExecuteVerify(entry, connection, transaction);
                                entry.VerifyStatus = PlanEntryStatus.VerifyPassed;
                            }
                            catch (Exception ex)
                            {
                                if (IsTransactionDoomed(connection, transaction))
                                {
                                    entry.VerifyStatus = PlanEntryStatus.VerifyFailed;
                                    entry.VerifyDetail = ex.Message;
                                    stopped = true;
                                }
                                else
                                {
                                    deferred.Add(entry);
                                }
                            }
                        }

                        // Phase 2: migrations — the real run is fail-fast here, and the
                        // transaction may be doomed, so stop on the first failure.
                        foreach (var entry in migrationEntries)
                        {
                            if (stopped)
                            {
                                MarkNotVerified(entry);
                                continue;
                            }

                            try
                            {
                                ExecuteVerify(entry, connection, transaction);
                                entry.VerifyStatus = PlanEntryStatus.VerifyPassed;
                            }
                            catch (Exception ex)
                            {
                                entry.VerifyStatus = PlanEntryStatus.VerifyFailed;
                                entry.VerifyDetail = ex.Message;
                                stopped = true;
                            }
                        }

                        // Phase 3: retry deferred object scripts now that migrations ran.
                        foreach (var entry in deferred)
                        {
                            if (stopped)
                            {
                                MarkNotVerified(entry);
                                continue;
                            }

                            try
                            {
                                ExecuteVerify(entry, connection, transaction);
                                entry.VerifyStatus = PlanEntryStatus.VerifyPassed;
                            }
                            catch (Exception ex)
                            {
                                entry.VerifyStatus = PlanEntryStatus.VerifyFailed;
                                entry.VerifyDetail = ex.Message;
                                stopped = true;
                            }
                        }
                    }
                    finally
                    {
                        // Rolling back a doomed transaction throws but the work is already
                        // undone server-side — never let that mask the collected results.
                        try
                        {
                            transaction.Rollback();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void MarkNotVerified(PlanEntry entry)
        {
            entry.VerifyStatus = PlanEntryStatus.NotVerified;
            entry.VerifyDetail = "not verified - earlier failure";
        }

        private static void ExecuteVerify(PlanEntry entry, SqlConnection connection, SqlTransaction transaction)
        {
            var script = entry.ScriptText ?? File.ReadAllText(entry.FilePath);

            using (var command = new SqlCommand(script, connection, transaction))
            {
                command.ExecuteNonQuery();
            }
        }

        private static bool IsTransactionDoomed(SqlConnection connection, SqlTransaction transaction)
        {
            try
            {
                using (var command = new SqlCommand("SELECT XACT_STATE()", connection, transaction))
                {
                    return Convert.ToInt32(command.ExecuteScalar()) == -1;
                }
            }
            catch
            {
                // Can't even query the transaction state — treat it as unusable.
                return true;
            }
        }

        /// <summary>
        /// Computes the script's integrity checksum from its own content instead of trusting a
        /// header written by something else. A leading "-- Checksum:" line (left over from files
        /// committed before this change, or a stray hand-edit) is stripped before hashing, so its
        /// presence or removal never changes the result. Line endings are hashed as-is rather than
        /// normalized: this reproduces the SHA-256 the pre-commit hook used to compute for a file's
        /// first commit (verified against the real headers already checked into this repo).
        /// </summary>
        public static string ComputeChecksum(string script)
        {
            var newlineIndex = script.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                var firstLine = script.Substring(0, newlineIndex).TrimEnd('\r');
                if (firstLine.StartsWith("-- Checksum:"))
                {
                    script = script.Substring(newlineIndex + 1);
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(script);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Database object scripts must be idempotent, since they are re-run on every deploy.
        /// This enforces that the first executable statement (after the checksum/tags header
        /// comments) is a CREATE OR ALTER, rather than a plain CREATE that fails on redeploy.
        /// </summary>
        // internal (not private) so MigrationOps.Core.Tests can exercise it directly.
        internal static void EnsureCreateOrAlterStatement(string script, string scriptName)
        {
            var lines = script.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith("--"))
                {
                    continue;
                }

                if (!trimmed.StartsWith("CREATE OR ALTER", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Database object script '{scriptName}' must begin with a 'CREATE OR ALTER' statement.");
                }

                return;
            }

            throw new InvalidOperationException($"Database object script '{scriptName}' is empty or contains no executable statement.");
        }
    }
}
