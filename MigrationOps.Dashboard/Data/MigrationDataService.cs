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
    // the exact tag/checksum/drift logic the ConsoleApp runner uses. Everything here is
    // read-only except RunDryRun with verify, which executes pending scripts inside a
    // transaction that is always rolled back.
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
            var fileStatuses = DryRunPlanner.GetMigrationFileStatuses(_migrationsRoot, databaseName, history);

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

        // Same plan the console `dry-run` command builds; verify mirrors `--verify` (executes
        // pending scripts in one transaction per database, always rolled back).
        public DryRunPlan RunDryRun(string? databaseName, bool verify)
        {
            var targets = databaseName != null
                ? new List<string> { databaseName }
                : GetDatabaseNames();

            var plan = _services.Planner.BuildDryRunPlan(_scriptsRoot, _migrationsRoot, targets);

            if (verify)
            {
                _services.Verifier.VerifyPlan(plan);
            }

            return plan;
        }
    }
}
