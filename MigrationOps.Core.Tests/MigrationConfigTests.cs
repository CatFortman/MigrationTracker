using Microsoft.Extensions.Configuration;
using MigrationOps.Core.MigrationFramework.Configuration;

namespace MigrationOps.Core.Tests
{
    public class MigrationConfigTests
    {
        private static MigrationConfig InMemory(params (string Key, string Value)[] settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build();

            return new MigrationConfig(configuration);
        }

        [Fact]
        public void ReadsConnectionStringsDirectoriesAndDatabaseNames()
        {
            var config = InMemory(
                ("Databases:Db1:ConnectionString", "conn-1"),
                ("Databases:Db2:ConnectionString", "conn-2"),
                ("MigrationSettings:MigrationDirectory", "Migrations"),
                ("MigrationSettings:ScriptDirectory", "Scripts"));

            Assert.Equal("conn-1", config.GetConnectionString("Db1"));
            Assert.Equal(new[] { "Db1", "Db2" }, config.GetDatabaseNames());
            Assert.Equal("Migrations", config.MigrationDirectory);
            Assert.Equal("Scripts", config.ScriptDirectory);
        }

        [Fact]
        public void DatabaseLookupIsCaseInsensitive()
        {
            var config = InMemory(("Databases:Db1:ConnectionString", "conn-1"));

            Assert.Equal("conn-1", config.GetConnectionString("DB1"));
        }

        [Fact]
        public void UnconfiguredValuesComeBackEmptyOrNullRatherThanThrowing()
        {
            var config = InMemory(("Databases:Db1:ConnectionString", "conn-1"));

            // Empty (not null) so callers can hand it straight to SqlConnection, which then
            // fails at open time exactly as it did before - see issue #20.
            Assert.Equal(string.Empty, config.GetConnectionString("Nope"));
            Assert.Null(config.MigrationDirectory);
            Assert.Null(config.ScriptDirectory);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("not-a-bool", false)]
        public void AlertsAreEnabledOnlyByAParseableTrue(string configured, bool expected)
        {
            var config = InMemory(("AlertSettings:Enabled", configured), ("AlertSettings:WebhookUrl", "https://example.test/hook"));

            Assert.Equal(expected, config.AlertsEnabled);
            Assert.Equal("https://example.test/hook", config.AlertWebhookUrl);
        }

        [Fact]
        public void AlertsAreDisabledWhenNotConfiguredAtAll()
        {
            var config = InMemory(("Databases:Db1:ConnectionString", "conn-1"));

            Assert.False(config.AlertsEnabled);
            Assert.Null(config.AlertWebhookUrl);
        }

        [Fact]
        public void LocalOverlayFileTakesPrecedenceOverTheCommittedTemplate()
        {
            using var dir = new TempDirectory();
            dir.WriteFile("dbconfig.json", """
                { "Databases": { "Db1": { "ConnectionString": "committed" } },
                  "MigrationSettings": { "MigrationDirectory": "Migrations" } }
                """);
            dir.WriteFile("dbconfig.local.json", """
                { "Databases": { "Db1": { "ConnectionString": "local-override" } } }
                """);

            var config = MigrationConfig.FromJsonFile(Path.Combine(dir.Path, "dbconfig.json"));

            Assert.Equal("local-override", config.GetConnectionString("Db1"));
            Assert.Equal("Migrations", config.MigrationDirectory);
        }

        [Fact]
        public void AMissingConfigFileYieldsAnEmptyConfigRatherThanThrowing()
        {
            using var dir = new TempDirectory();

            var config = MigrationConfig.FromJsonFile(Path.Combine(dir.Path, "dbconfig.json"));

            Assert.Empty(config.GetDatabaseNames());
        }
    }
}
