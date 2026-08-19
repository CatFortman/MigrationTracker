using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Execution
{
    /// <summary>Outcome of one transactional apply attempt.</summary>
    public sealed class ScriptExecutionResult
    {
        private ScriptExecutionResult(bool succeeded, int durationMs, Exception? error)
        {
            Succeeded = succeeded;
            DurationMs = durationMs;
            Error = error;
        }

        public bool Succeeded { get; }

        public int DurationMs { get; }

        /// <summary>The SQL error that caused the rollback; null when <see cref="Succeeded"/>.</summary>
        public Exception? Error { get; }

        public string ErrorMessage => Error?.Message ?? string.Empty;

        public static ScriptExecutionResult Success(int durationMs) => new(true, durationMs, null);

        public static ScriptExecutionResult Failure(Exception error, int durationMs) => new(false, durationMs, error);
    }

    /// <summary>
    /// One open connection with an uncommitted transaction, used by verify to execute a database's
    /// pending scripts and then throw the work away. Disposing always rolls back.
    /// </summary>
    public interface IVerifySession : IDisposable
    {
        /// <summary>
        /// Executes one script in the session's transaction, split on its GO lines exactly as an
        /// apply would; throws if the SQL fails.
        /// </summary>
        void Execute(string script);

        /// <summary>
        /// True when the transaction can no longer be used (XACT_STATE() = -1), so subsequent
        /// scripts would fail for reasons unrelated to their own SQL.
        /// </summary>
        bool IsTransactionDoomed();
    }

    /// <summary>
    /// The boundary between orchestration and actually running SQL. Apply commits; verify never
    /// does. Substituting this is what makes the apply and verify pipelines testable without a
    /// live SQL Server.
    /// </summary>
    public interface IScriptExecutionGateway
    {
        /// <summary>
        /// Runs a script and its history row in a single transaction: both commit, or neither
        /// does. A script split across GO lines runs batch by batch inside that one transaction,
        /// so it is still all-or-nothing. Returns the outcome rather than throwing, so callers
        /// decide whether a SQL failure is fatal (migrations) or deferrable (object scripts).
        /// </summary>
        ScriptExecutionResult ApplyScript(string connectionString, string script, string scriptName, string checksum, ScriptKind kind);

        IVerifySession BeginVerifySession(string connectionString);
    }
}
