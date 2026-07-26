using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class ShouldApplyScriptTests
    {
        [Fact]
        public void MatchesExactTag()
        {
            Assert.True(ScriptParser.ShouldApplyScript(new List<string> { "Db1", "Db2" }, "Db1"));
        }

        [Fact]
        public void IsCaseInsensitive()
        {
            Assert.True(ScriptParser.ShouldApplyScript(new List<string> { "db1" }, "DB1"));
        }

        [Fact]
        public void ReturnsFalseWhenTagNotPresent()
        {
            Assert.False(ScriptParser.ShouldApplyScript(new List<string> { "Db2" }, "Db1"));
        }

        [Fact]
        public void ReturnsFalseForEmptyTagList()
        {
            Assert.False(ScriptParser.ShouldApplyScript(new List<string>(), "Db1"));
        }
    }
}
