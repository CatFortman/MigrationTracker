using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MigrationOps.Core.MigrationFramework.AppConstants;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Execution
{
    /// <summary>SQL Server implementation of <see cref="IScriptExecutionGateway"/>.</summary>
    public class SqlScriptExecutionGateway : IScriptExecutionGateway
    {
        public ScriptExecutionResult ApplyScript(string connectionString, string script, string scriptName, string checksum, ScriptKind kind)
        {
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

                        return ScriptExecutionResult.Success((int)stopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        transaction.Rollback();

                        return ScriptExecutionResult.Failure(ex, (int)stopwatch.ElapsedMilliseconds);
                    }
                }
            }
        }

        public IVerifySession BeginVerifySession(string connectionString)
        {
            return new SqlVerifySession(connectionString);
        }

        // Written inside the caller's transaction so the history row and the script it records
        // share one fate.
        private static void RecordApplied(SqlConnection connection, SqlTransaction transaction, string scriptName, string checksum, ScriptKind kind, int durationMs)
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

        private sealed class SqlVerifySession : IVerifySession
        {
            private readonly SqlConnection _connection;
            private readonly SqlTransaction _transaction;

            public SqlVerifySession(string connectionString)
            {
                _connection = new SqlConnection(connectionString);

                try
                {
                    _connection.Open();
                    _transaction = _connection.BeginTransaction();
                }
                catch
                {
                    _connection.Dispose();
                    throw;
                }
            }

            public void Execute(string script)
            {
                using (var command = new SqlCommand(script, _connection, _transaction))
                {
                    command.ExecuteNonQuery();
                }
            }

            public bool IsTransactionDoomed()
            {
                try
                {
                    using (var command = new SqlCommand("SELECT XACT_STATE()", _connection, _transaction))
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

            public void Dispose()
            {
                // Rolling back a doomed transaction throws but the work is already undone
                // server-side — never let that mask the collected results.
                try
                {
                    _transaction.Rollback();
                }
                catch
                {
                }

                _transaction.Dispose();
                _connection.Dispose();
            }
        }
    }
}
