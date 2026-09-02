using MigrationOps.Core.MigrationFramework.Configuration;
using MigrationOps.Core.MigrationFramework.Data;
using MigrationOps.Core.MigrationFramework.Execution;
using MigrationOps.Core.MigrationFramework.Services;

namespace MigrationOps.Core.MigrationFramework
{
    /// <summary>
    /// Composition root for the framework: wires config, history store, execution gateway and
    /// alert notifier into the three orchestrators. Holds no logic of its own — anything that
    /// wants different pieces (a fake gateway in a test, a different notifier) constructs the
    /// orchestrators directly instead of going through <see cref="CreateDefault"/>.
    /// </summary>
    public class MigrationOpsServices
    {
        public MigrationOpsServices(
            IMigrationConfig config,
            IHistoryStore historyStore,
            IScriptExecutionGateway gateway,
            IMigrationAlertNotifier alertNotifier)
        {
            Config = config;
            HistoryStore = historyStore;
            Applier = new ScriptApplier(config, historyStore, gateway, alertNotifier);
            Planner = new PlanBuilder(config, historyStore);
            DryRunner = new PlanDryRunner(config, gateway);
        }

        public IMigrationConfig Config { get; }

        public IHistoryStore HistoryStore { get; }

        public ScriptApplier Applier { get; }

        public PlanBuilder Planner { get; }

        public PlanDryRunner DryRunner { get; }

        /// <summary>
        /// The production wiring against SQL Server. Pass a dbconfig.json path when the working
        /// directory isn't the ConsoleApp's (e.g. the Dashboard); omit it to use the ConsoleApp's
        /// Configurations/dbconfig.json convention.
        /// </summary>
        public static MigrationOpsServices CreateDefault(string? dbConfigFilePath = null)
        {
            var config = dbConfigFilePath == null
                ? MigrationConfig.FromDefaultLocation()
                : MigrationConfig.FromJsonFile(dbConfigFilePath);

            return new MigrationOpsServices(
                config,
                new SqlHistoryStore(),
                new SqlScriptExecutionGateway(),
                new WebhookAlertNotifier(config.AlertWebhookUrl, config.AlertsEnabled));
        }
    }
}
