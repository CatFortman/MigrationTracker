using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Data;
using MigrationOps.Core.MigrationFramework.Execution;
using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.Tests
{
    // Stand-ins for the four dependencies the orchestrators (ScriptApplier, PlanBuilder,
    // PlanDryRunner) take, so the apply and verify pipelines can be driven end to end without a
    // SQL Server.

    internal sealed class TestConfig : IMigrationConfig
    {
        public Dictionary<string, string> ConnectionStrings { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Databases { get; } = new();

        public string? MigrationDirectory { get; set; }

        public string? ScriptDirectory { get; set; }

        public bool AlertsEnabled { get; set; }

        public string? AlertWebhookUrl { get; set; }

        public static TestConfig WithDatabases(params string[] databaseNames)
        {
            var config = new TestConfig();

            foreach (var name in databaseNames)
            {
                config.Databases.Add(name);
                config.ConnectionStrings[name] = $"conn:{name}";
            }

            return config;
        }

        public string GetConnectionString(string databaseName)
        {
            return ConnectionStrings.TryGetValue(databaseName, out var connectionString) ? connectionString : string.Empty;
        }

        public List<string> GetDatabaseNames() => Databases;
    }

    internal sealed class FakeHistoryStore : IHistoryStore
    {
        public List<(string ConnectionString, ScriptKind Kind)> EnsuredTables { get; } = new();

        // (scriptName, checksum) pairs already recorded as applied.
        public HashSet<(string Name, string Checksum)> AppliedRecords { get; } = new();

        public Dictionary<string, string> LatestSuccessfulChecksums { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string ConnectionString, string Name, string Checksum, string ErrorMessage, int DurationMs)> RecordedFailures { get; } = new();

        public Exception? RecordFailureThrows { get; set; }

        public List<MigrationHistoryRecord> MigrationHistory { get; set; } = new();

        public List<ScriptHistoryRecord> ScriptHistory { get; set; } = new();

        public void EnsureHistoryTable(string connectionString, ScriptKind kind)
        {
            EnsuredTables.Add((connectionString, kind));
        }

        public bool HasBeenApplied(string connectionString, string scriptName, string checksum, ScriptKind kind)
        {
            return AppliedRecords.Contains((scriptName, checksum));
        }

        public string? GetLatestSuccessfulMigrationChecksum(string connectionString, string scriptName)
        {
            return LatestSuccessfulChecksums.TryGetValue(scriptName, out var checksum) ? checksum : null;
        }

        public List<MigrationHistoryRecord> GetMigrationHistory(string connectionString) => MigrationHistory;

        public List<ScriptHistoryRecord> GetScriptObjectHistory(string connectionString) => ScriptHistory;

        public void RecordMigrationFailure(string connectionString, string scriptName, string checksum, string errorMessage, int durationMs)
        {
            if (RecordFailureThrows != null)
            {
                throw RecordFailureThrows;
            }

            RecordedFailures.Add((connectionString, scriptName, checksum, errorMessage, durationMs));
        }
    }

    internal sealed class FakeExecutionGateway : IScriptExecutionGateway
    {
        // scriptName -> how many further attempts should fail before it starts succeeding.
        public Dictionary<string, int> FailuresRemaining { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string ConnectionString, string ScriptName, string Script, string Checksum, ScriptKind Kind)> Attempts { get; } = new();

        public List<string> Committed { get; } = new();

        public FakeVerifySession? LastSession { get; private set; }

        public List<FakeVerifySession> Sessions { get; } = new();

        public Exception? BeginSessionThrows { get; set; }

        // Applied to each session as it is created, so a test can pre-arm which scripts fail.
        public Action<FakeVerifySession>? ConfigureSession { get; set; }

        public ScriptExecutionResult ApplyScript(string connectionString, string script, string scriptName, string checksum, ScriptKind kind)
        {
            Attempts.Add((connectionString, scriptName, script, checksum, kind));

            if (FailuresRemaining.TryGetValue(scriptName, out var remaining) && remaining > 0)
            {
                FailuresRemaining[scriptName] = remaining - 1;
                return ScriptExecutionResult.Failure(new InvalidOperationException($"SQL error in {scriptName}"), 7);
            }

            Committed.Add(scriptName);
            return ScriptExecutionResult.Success(3);
        }

        public IVerifySession BeginVerifySession(string connectionString)
        {
            if (BeginSessionThrows != null)
            {
                throw BeginSessionThrows;
            }

            var session = new FakeVerifySession(connectionString);
            ConfigureSession?.Invoke(session);
            Sessions.Add(session);
            LastSession = session;
            return session;
        }
    }

    internal sealed class FakeVerifySession : IVerifySession
    {
        public FakeVerifySession(string connectionString)
        {
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public List<string> Executed { get; } = new();

        // Script text -> how many further executions should throw before it starts passing.
        public Dictionary<string, int> FailuresRemaining { get; } = new();

        public bool Doomed { get; set; }

        public bool Disposed { get; private set; }

        public void Execute(string script)
        {
            Executed.Add(script);

            if (FailuresRemaining.TryGetValue(script, out var remaining) && remaining > 0)
            {
                FailuresRemaining[script] = remaining - 1;
                throw new InvalidOperationException($"verify failed: {script}");
            }
        }

        public bool IsTransactionDoomed() => Doomed;

        public void Dispose() => Disposed = true;
    }

    internal sealed class RecordingAlertNotifier : IMigrationAlertNotifier
    {
        public List<(string ScriptName, string Database, string ErrorMessage)> Alerts { get; } = new();

        public Exception? Throws { get; set; }

        public Task NotifyFailureAsync(string migrationName, string database, string errorMessage)
        {
            if (Throws != null)
            {
                throw Throws;
            }

            Alerts.Add((migrationName, database, errorMessage));
            return Task.CompletedTask;
        }
    }
}
