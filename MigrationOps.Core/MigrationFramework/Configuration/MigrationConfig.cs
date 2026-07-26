using Microsoft.Extensions.Configuration;

namespace MigrationOps.Core.MigrationFramework.Configuration
{
    /// <summary>
    /// <see cref="IMigrationConfig"/> backed by an <see cref="IConfiguration"/>. Use
    /// <see cref="FromJsonFile"/> for the normal dbconfig.json layering, or the constructor
    /// directly (e.g. with an in-memory configuration) in tests.
    /// </summary>
    public class MigrationConfig : IMigrationConfig
    {
        private readonly IConfiguration _configuration;

        public MigrationConfig(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// The ConsoleApp's convention: Configurations/dbconfig.json relative to the working
        /// directory. Callers whose working directory isn't the ConsoleApp's (e.g. the
        /// Dashboard) should use <see cref="FromJsonFile"/> with an explicit path instead.
        /// </summary>
        public static MigrationConfig FromDefaultLocation()
        {
            return FromJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "Configurations", "dbconfig.json"));
        }

        public static MigrationConfig FromJsonFile(string dbConfigFilePath)
        {
            var fullPath = Path.GetFullPath(dbConfigFilePath);
            var basePath = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var fileName = Path.GetFileName(fullPath);
            var localFileName = Path.GetFileNameWithoutExtension(fileName) + ".local" + Path.GetExtension(fileName);

            // Layering, lowest to highest precedence:
            //   1. dbconfig.json          - committed template, no real secrets
            //   2. dbconfig.local.json    - gitignored, per-developer local overrides
            //   3. environment variables  - e.g. Databases__Db1__ConnectionString, used in CI/CD
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(fileName, optional: true, reloadOnChange: true)
                .AddJsonFile(localFileName, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            return new MigrationConfig(builder.Build());
        }

        public string GetConnectionString(string databaseName)
        {
            return _configuration[$"Databases:{databaseName}:ConnectionString"] ?? string.Empty;
        }

        public string? MigrationDirectory => _configuration["MigrationSettings:MigrationDirectory"];

        public string? ScriptDirectory => _configuration["MigrationSettings:ScriptDirectory"];

        public List<string> GetDatabaseNames()
        {
            return _configuration.GetSection("Databases").GetChildren().Select(db => db.Key).ToList();
        }

        public bool AlertsEnabled => bool.TryParse(_configuration["AlertSettings:Enabled"], out var enabled) && enabled;

        public string? AlertWebhookUrl => _configuration["AlertSettings:WebhookUrl"];
    }
}
