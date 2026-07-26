namespace MigrationOps.Core.MigrationFramework.Configuration
{
    /// <summary>
    /// Read-only view of dbconfig.json: which databases exist, how to reach them, and where the
    /// migration/object-script folders live. Everything that used to read <c>IConfiguration</c>
    /// directly inside MigrationService goes through this, so callers can substitute a config
    /// without a file on disk.
    /// </summary>
    public interface IMigrationConfig
    {
        /// <summary>
        /// Connection string for a configured database, or empty when the database is not
        /// configured. Empty behaves exactly as the previous null did: SqlConnection fails with
        /// "The ConnectionString property has not been initialized" at open time.
        /// </summary>
        string GetConnectionString(string databaseName);

        /// <summary>Configured migrations folder, or null when unset.</summary>
        string? MigrationDirectory { get; }

        /// <summary>Configured object-scripts root folder, or null when unset.</summary>
        string? ScriptDirectory { get; }

        /// <summary>The keys under "Databases", in configuration order.</summary>
        List<string> GetDatabaseNames();

        bool AlertsEnabled { get; }

        string? AlertWebhookUrl { get; }
    }
}
