using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Execution;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Services
{
    /// <summary>
    /// Executes a dry-run plan's pending entries against the real databases and throws the work
    /// away, recording per-entry verify results. Nothing here commits.
    /// </summary>
    public class PlanDryRunner
    {
        private readonly IMigrationConfig _config;
        private readonly IScriptExecutionGateway _gateway;

        public PlanDryRunner(IMigrationConfig config, IScriptExecutionGateway gateway)
        {
            _config = config;
            _gateway = gateway;
        }

        /// <summary>
        /// Executes each database's pending entries (WouldApply + Changed) inside one transaction
        /// per database — so later scripts can see earlier scripts' schema — and always rolls it
        /// back. Proves the SQL works without committing anything; history inserts are not replayed.
        /// Results land in each entry's DryRunStatus/DryRunDetail.
        /// </summary>
        public void RunDryRun(MigrationPlan plan)
        {
            foreach (var database in plan.TargetDatabases)
            {
                var pending = plan.Entries
                    .Where(e => e.Database.Equals(database, StringComparison.OrdinalIgnoreCase)
                             && (e.Status == EntryStatus.WouldApply || e.Status == EntryStatus.Changed))
                    .ToList();

                if (pending.Count == 0)
                {
                    continue;
                }

                try
                {
                    ValidatePlan(_config.GetConnectionString(database), pending);
                }
                catch (Exception ex)
                {
                    // Couldn't even get a connection/transaction: first pending entry carries
                    // the error, the rest are unverified.
                    pending[0].DryRunStatus = EntryStatus.DryRunFailed;
                    pending[0].DryRunDetail = ex.Message;
                    foreach (var entry in pending.Skip(1))
                    {
                        entry.DryRunStatus = EntryStatus.NotRun;
                    }
                }
            }
        }

        private void ValidatePlan(string connectionString, List<Entry> pending)
        {
            // Disposing the session rolls the transaction back, whatever happened inside it.
            using (var session = _gateway.BeginValidateSession(connectionString))
            {
                var objectEntries = pending.Where(e => e.Kind == ScriptKind.DatabaseObject).ToList();
                var migrationEntries = pending.Where(e => e.Kind == ScriptKind.Migration).ToList();
                var deferred = new List<Entry>();
                var stopped = false;

                // Phase 1: object scripts, mirroring the real run's defer-on-failure.
                foreach (var entry in objectEntries)
                {
                    if (stopped)
                    {
                        MarkNotRun(entry);
                        continue;
                    }

                    try
                    {
                        ExecuteEntry(session, entry);
                        entry.DryRunStatus = EntryStatus.DryRunPassed;
                    }
                    catch (Exception ex)
                    {
                        if (session.IsTransactionDoomed())
                        {
                            entry.DryRunStatus = EntryStatus.DryRunFailed;
                            entry.DryRunDetail = ex.Message;
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
                        MarkNotRun(entry);
                        continue;
                    }

                    try
                    {
                        ExecuteEntry(session, entry);
                        entry.DryRunStatus = EntryStatus.DryRunPassed;
                    }
                    catch (Exception ex)
                    {
                        entry.DryRunStatus = EntryStatus.DryRunFailed;
                        entry.DryRunDetail = ex.Message;
                        stopped = true;
                    }
                }

                // Phase 3: retry deferred object scripts now that migrations ran.
                foreach (var entry in deferred)
                {
                    if (stopped)
                    {
                        MarkNotRun(entry);
                        continue;
                    }

                    try
                    {
                        ExecuteEntry(session, entry);
                        entry.DryRunStatus = EntryStatus.DryRunPassed;
                    }
                    catch (Exception ex)
                    {
                        entry.DryRunStatus = EntryStatus.DryRunFailed;
                        entry.DryRunDetail = ex.Message;
                        stopped = true;
                    }
                }
            }
        }

        private static void MarkNotRun(Entry entry)
        {
            entry.DryRunStatus = EntryStatus.NotRun;
            entry.DryRunDetail = "not run - earlier failure";
        }

        private static void ExecuteEntry(IValidateSession session, Entry entry)
        {
            session.Execute(entry.ScriptText ?? File.ReadAllText(entry.FilePath));
        }
    }
}
