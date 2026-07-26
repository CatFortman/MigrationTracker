using Microsoft.Data.SqlClient;
using MigrationOps.Core.MigrationFramework.AppConstants;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Data
{
    /// <summary>SQL Server implementation of <see cref="IHistoryStore"/>.</summary>
    public class SqlHistoryStore : IHistoryStore
    {
        public void EnsureHistoryTable(string connectionString, ScriptKind kind)
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

        public bool HasBeenApplied(string connectionString, string scriptName, string checksum, ScriptKind kind)
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

        public string? GetLatestSuccessfulMigrationChecksum(string connectionString, string scriptName)
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

        public List<MigrationHistoryRecord> GetMigrationHistory(string connectionString)
        {
            EnsureHistoryTable(connectionString, ScriptKind.Migration);

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

        public void RecordMigrationFailure(string connectionString, string scriptName, string checksum, string errorMessage, int durationMs)
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
    }
}
