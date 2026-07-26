using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Data
{
    /// <summary>
    /// Reads and writes the per-database history tables (__MigrationHistory, __ScriptHistory)
    /// outside of any apply transaction. The success row written *inside* an apply transaction
    /// belongs to <see cref="Execution.IScriptExecutionGateway"/> instead, so that the row and the
    /// script it records commit or roll back together.
    /// </summary>
    public interface IHistoryStore
    {
        /// <summary>Creates (or upgrades) the history table for this script kind if needed.</summary>
        void EnsureHistoryTable(string connectionString, ScriptKind kind);

        /// <summary>True when a row already records this exact (name, checksum) as applied.</summary>
        bool HasBeenApplied(string connectionString, string scriptName, string checksum, ScriptKind kind);

        /// <summary>
        /// Checksum of the migration's most recent *successful* apply, or null if it has never
        /// applied successfully. Matches by name alone, which is what makes the edited-migration
        /// guard possible.
        /// </summary>
        string? GetLatestSuccessfulMigrationChecksum(string connectionString, string scriptName);

        List<MigrationHistoryRecord> GetMigrationHistory(string connectionString);

        List<ScriptHistoryRecord> GetScriptObjectHistory(string connectionString);

        /// <summary>
        /// Records a failed migration attempt (Success = 0) on a fresh connection, after the
        /// apply transaction has already rolled back. Migrations only — __ScriptHistory has no
        /// Success column.
        /// </summary>
        void RecordMigrationFailure(string connectionString, string scriptName, string checksum, string errorMessage, int durationMs);
    }
}
