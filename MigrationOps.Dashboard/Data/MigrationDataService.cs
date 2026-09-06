using MigrationOps.Core.MigrationFramework;
using MigrationOps.Core.MigrationFramework.Services;
using MigrationOps.Core.Models;

namespace MigrationOps.Dashboard.Data
{
    public class DatabaseOverview
    {
        public string Name { get; set; } = string.Empty;
        public List<MigrationHistoryRecord> History { get; set; } = new();
        public List<MigrationFileStatus> FileStatuses { get; set; } = new();
    }

    // Thin wrapper around the MigrationOps.Core framework, reused as-is so the dashboard shares
    // the exact tag/checksum/drift logic the ConsoleApp runner uses. DryRunMigrationPlan (the
    // dashboard's "dry-run" action) executes pending scripts inside a transaction that is
    // always rolled back; RunApply (the "Run Migrations" action) is the one non-read-only path —
    // it writes real, permanent changes, identical to the console `apply` command.
    public class MigrationDataService
    {
        private readonly MigrationOpsServices _services;
        private readonly string _migrationsRoot;
        private readonly string _scriptsRoot;

        public MigrationDataService(IConfiguration configuration)
        {
            var dbConfigPath = configuration["DbConfigPath"]
                ?? throw new InvalidOperationException("DbConfigPath is not configured.");
            _migrationsRoot = Path.GetFullPath(configuration["MigrationsRoot"]
                ?? throw new InvalidOperationException("MigrationsRoot is not configured."));

            // Optional override; by default the object scripts live in the Scripts folder next
            // to the Migrations folder (the ConsoleApp layout).
            _scriptsRoot = Path.GetFullPath(configuration["ScriptsRoot"]
                ?? Path.Combine(Path.GetDirectoryName(_migrationsRoot)!, "Scripts"));

            _services = MigrationOpsServices.CreateDefault(dbConfigPath);
        }

        public List<string> GetDatabaseNames() => _services.Config.GetDatabaseNames();

        public DatabaseOverview GetDatabaseOverview(string databaseName)
        {
            var connectionString = _services.Config.GetConnectionString(databaseName);
            var history = _services.HistoryStore.GetMigrationHistory(connectionString);
            var fileStatuses = PlanBuilder.GetMigrationFileStatuses(_migrationsRoot, databaseName, history);

            return new DatabaseOverview
            {
                Name = databaseName,
                History = history,
                FileStatuses = fileStatuses
            };
        }

        public List<DatabaseOverview> GetAllDatabaseOverviews()
        {
            return GetDatabaseNames().Select(GetDatabaseOverview).ToList();
        }

        // Read-only report: diffs files against history and classifies each one. Mirrors the
        // console `validate` command; nothing beyond reading history touches the database.
        public MigrationPlan VerifyMigrationPlan(string? databaseName)
        {
            var targets = databaseName != null
                ? new List<string> { databaseName }
                : GetDatabaseNames();

            return _services.Planner.BuildPlan(_scriptsRoot, _migrationsRoot, targets);
        }

        // Same plan as VerifyMigrationPlan, plus executes each pending entry in one transaction
        // per database and always rolls back. Mirrors the console `dry-run` command.
        public MigrationPlan DryRunMigrationPlan(string? databaseName)
        {
            var plan = VerifyMigrationPlan(databaseName);
            _services.DryRunner.RunDryRun(plan);
            return plan;
        }

        // Mirrors the console `apply` command exactly (same ScriptApplier pipeline, same
        // object-scripts-before-migrations-then-retry-deferred order). Writes real, permanent
        // changes to the target database(s); the applier throws on a real SQL failure, same as
        // the console app, so callers should expect and handle that. Returns the plan rebuilt
        // after the apply so the caller can show the resulting state.
        public MigrationPlan RunApply(string? databaseName)
        {
            var targets = databaseName != null
                ? new List<string> { databaseName }
                : GetDatabaseNames();

            var deferred = _services.Applier.ApplyDatabaseObjectScripts(_scriptsRoot, databaseName);
            _services.Applier.ApplyMigrations(_migrationsRoot, databaseName);
            _services.Applier.RetryDeferredScripts(deferred);

            return _services.Planner.BuildPlan(_scriptsRoot, _migrationsRoot, targets);
        }
    }
}
