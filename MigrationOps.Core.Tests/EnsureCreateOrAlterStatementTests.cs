using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class EnsureCreateOrAlterStatementTests
    {
        [Fact]
        public void AllowsCreateOrAlterAfterHeaderComments()
        {
            var script = "-- Tags: Db1\n-- Checksum: abc\nCREATE OR ALTER PROCEDURE dbo.Foo AS SELECT 1;";

            var exception = Record.Exception(() => ScriptParser.EnsureCreateOrAlterStatement(script, "Foo.sql"));

            Assert.Null(exception);
        }

        [Fact]
        public void AllowsBlankLinesBeforeTheStatement()
        {
            var script = "-- Tags: Db1\n\n\nCREATE OR ALTER VIEW dbo.V AS SELECT 1;";

            var exception = Record.Exception(() => ScriptParser.EnsureCreateOrAlterStatement(script, "V.sql"));

            Assert.Null(exception);
        }

        [Fact]
        public void IsCaseInsensitive()
        {
            var script = "-- Tags: Db1\ncreate or alter function dbo.F() returns int as begin return 1 end";

            var exception = Record.Exception(() => ScriptParser.EnsureCreateOrAlterStatement(script, "F.sql"));

            Assert.Null(exception);
        }

        [Fact]
        public void ThrowsWhenFirstStatementIsPlainCreate()
        {
            var script = "-- Tags: Db1\nCREATE PROCEDURE dbo.Foo AS SELECT 1;";

            var ex = Assert.Throws<InvalidOperationException>(
                () => ScriptParser.EnsureCreateOrAlterStatement(script, "Foo.sql"));

            Assert.Contains("Foo.sql", ex.Message);
            Assert.Contains("CREATE OR ALTER", ex.Message);
        }

        [Fact]
        public void ValidatesOnlyTheFirstBatch()
        {
            // What follows the object definition is legitimately not a CREATE OR ALTER.
            var script = "-- Tags: Db1\nCREATE OR ALTER PROCEDURE dbo.Foo AS SELECT 1;\nGO\nGRANT EXECUTE ON dbo.Foo TO [public];";

            var exception = Record.Exception(() => ScriptParser.EnsureCreateOrAlterStatement(script, "Foo.sql"));

            Assert.Null(exception);
        }

        [Fact]
        public void SkipsALeadingBatchThatHasNothingExecutableInIt()
        {
            var script = "-- Tags: Db1\nGO\nCREATE OR ALTER VIEW dbo.V AS SELECT 1;";

            var exception = Record.Exception(() => ScriptParser.EnsureCreateOrAlterStatement(script, "V.sql"));

            Assert.Null(exception);
        }

        [Fact]
        public void ThrowsWhenALaterBatchIsTheOnlyCreateOrAlter()
        {
            var script = "-- Tags: Db1\nCREATE PROCEDURE dbo.Foo AS SELECT 1;\nGO\nCREATE OR ALTER PROCEDURE dbo.Bar AS SELECT 2;";

            var ex = Assert.Throws<InvalidOperationException>(
                () => ScriptParser.EnsureCreateOrAlterStatement(script, "Foo.sql"));

            Assert.Contains("CREATE OR ALTER", ex.Message);
        }

        [Fact]
        public void ThrowsForEmptyScript()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ScriptParser.EnsureCreateOrAlterStatement("", "Empty.sql"));

            Assert.Contains("empty", ex.Message);
        }

        [Fact]
        public void ThrowsWhenScriptIsOnlyHeaderComments()
        {
            var script = "-- Tags: Db1\n-- Checksum: abc\n-- just a trailing comment, no statement";

            var ex = Assert.Throws<InvalidOperationException>(
                () => ScriptParser.EnsureCreateOrAlterStatement(script, "OnlyComments.sql"));

            Assert.Contains("empty", ex.Message);
        }

        [Fact]
        public void ThrowsWhenTheScriptIsOnlyHeaderCommentsAndSeparators()
        {
            var script = "-- Tags: Db1\nGO\n/* nothing to run */\nGO\n";

            var ex = Assert.Throws<InvalidOperationException>(
                () => ScriptParser.EnsureCreateOrAlterStatement(script, "OnlySeparators.sql"));

            Assert.Contains("empty", ex.Message);
        }
    }
}
