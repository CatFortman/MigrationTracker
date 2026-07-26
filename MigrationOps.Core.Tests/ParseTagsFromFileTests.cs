using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class ParseTagsFromFileTests
    {
        [Fact]
        public void ParsesCommaSeparatedTags()
        {
            using var file = new TempFile("-- Tags: Db1, Db2\n-- Checksum: abc\nSELECT 1;");

            var tags = ScriptParser.ParseTagsFromFile(file.Path);

            Assert.Equal(new[] { "Db1", "Db2" }, tags);
        }

        [Fact]
        public void TrimsWhitespaceAroundTags()
        {
            using var file = new TempFile("-- Tags:   Db1 ,   Db2   \nSELECT 1;");

            var tags = ScriptParser.ParseTagsFromFile(file.Path);

            Assert.Equal(new[] { "Db1", "Db2" }, tags);
        }

        [Fact]
        public void UsesFirstTagsLineWhenMultiplePresent()
        {
            using var file = new TempFile("-- Tags: Db1\n-- Tags: Db2\nSELECT 1;");

            var tags = ScriptParser.ParseTagsFromFile(file.Path);

            Assert.Equal(new[] { "Db1" }, tags);
        }

        [Fact]
        public void ThrowsWhenNoTagsCommentPresent()
        {
            using var file = new TempFile("-- Checksum: abc\nSELECT 1;");

            var ex = Assert.Throws<InvalidOperationException>(() => ScriptParser.ParseTagsFromFile(file.Path));

            Assert.Contains(Path.GetFileName(file.Path), ex.Message);
        }

        [Fact]
        public void ThrowsForEmptyFile()
        {
            using var file = new TempFile("");

            Assert.Throws<InvalidOperationException>(() => ScriptParser.ParseTagsFromFile(file.Path));
        }
    }
}
