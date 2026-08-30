using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Execution;
using MigrationOps.Core.Models;

namespace MigrationOps.Core.MigrationFramework.Services
{
    /// <summary>
    /// Executes a dry-run plan's pending entries against the real databases and throws the work
    /// away, recording per-entry verify results. Nothing here commits.
    /// </summary>
    public class PlanVerifier
    {
        private readonly IMigrationConfig _config;
        private readonly IScriptExecutionGateway _gateway;

        public PlanVerifier(IMigrationConfig config, IScriptExecutionGateway gateway)
        {
            _config = config;
            _gateway = gateway;
        }

        /// <summary>
        /// Executes each database's pending entries (WouldApply + Changed) inside one transaction
        /// per database — so later scripts can see earlier scripts' schema — and always rolls it
        /// back. Proves the SQL works without committing anything; history inserts are not replayed.
        /// Results land in each entry's VerifyStatus/VerifyDetail.
        /// </summary>
        public void VerifyPlan(MigrationPlan plan)
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
                    VerifyDatabase(_config.GetConnectionString(database), pending);
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
            // Disposing the session rolls the transaction back, whatever happened inside it.
            using (var session = _gateway.BeginVerifySession(connectionString))
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
                        ExecuteVerify(session, entry);
                        entry.VerifyStatus = PlanEntryStatus.VerifyPassed;
                    }
                    catch (Exception ex)
                    {
                        if (session.IsTransactionDoomed())
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
                        ExecuteVerify(session, entry);
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
                        ExecuteVerify(session, entry);
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
        }

        private static void MarkNotVerified(PlanEntry entry)
        {
            entry.VerifyStatus = PlanEntryStatus.NotVerified;
            entry.VerifyDetail = "not verified - earlier failure";
        }

        private static void ExecuteVerify(IVerifySession session, PlanEntry entry)
        {
            session.Execute(entry.ScriptText ?? File.ReadAllText(entry.FilePath));
        }
    }
}
