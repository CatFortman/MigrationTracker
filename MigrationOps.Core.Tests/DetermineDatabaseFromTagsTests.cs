using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class DetermineDatabaseFromTagsTests
    {
        [Fact]
        public void ReturnsMatchingDatabaseName()
        {
            Assert.Equal("Db1", ScriptParser.DetermineDatabaseFromTags(
                new List<string> { "Db1" }, new List<string> { "Db1", "Db2" }));
        }

        [Fact]
        public void MatchesConfiguredDatabaseCaseInsensitively()
        {
            // Note: the match is case-insensitive, but the method returns the tag as written
            // in the file, not the configured key's casing. That's safe downstream only
            // because IConfiguration's own indexer (used by GetConnectionString) is itself
            // case-insensitive - this test documents current behavior rather than asserting
            // it is the ideal API.
            Assert.Equal("db1", ScriptParser.DetermineDatabaseFromTags(
                new List<string> { "db1" }, new List<string> { "Db1" }));
        }

        [Fact]
        public void ReturnsFirstTagThatMatchesAConfiguredDatabase()
        {
            Assert.Equal("Db2", ScriptParser.DetermineDatabaseFromTags(
                new List<string> { "UnknownTag", "Db2" }, new List<string> { "Db1", "Db2" }));
        }

        [Fact]
        public void ThrowsWhenNoTagMatchesAConfiguredDatabase()
        {
            Assert.Throws<InvalidOperationException>(() => ScriptParser.DetermineDatabaseFromTags(
                new List<string> { "Db3" }, new List<string> { "Db1", "Db2" }));
        }
    }
}
