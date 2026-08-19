using MigrationOps.Core.MigrationFramework.Scripts;

namespace MigrationOps.Core.Tests
{
    public class SqlBatchSplitterTests
    {
        // Batches keep their original text (leading blank lines included) so SQL Server's line
        // numbers stay meaningful; comparing trimmed text keeps the assertions about what was
        // split, not about whitespace.
        private static List<string> Split(string script)
        {
            return SqlBatchSplitter.SplitIntoBatches(script).Select(batch => batch.Trim()).ToList();
        }

        [Fact]
        public void ReturnsWholeScriptAsOneBatchWhenThereIsNoSeparator()
        {
            var script = "-- Tags: Db1\nCREATE TABLE dbo.T (Id INT);\nINSERT INTO dbo.T VALUES (1);";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void SplitsOnSeparatorLines()
        {
            var script = "CREATE TABLE dbo.T (Id INT);\nGO\nCREATE VIEW dbo.V AS SELECT Id FROM dbo.T;\nGO\nSELECT 1;";

            var batches = Split(script);

            Assert.Equal(
                new[] { "CREATE TABLE dbo.T (Id INT);", "CREATE VIEW dbo.V AS SELECT Id FROM dbo.T;", "SELECT 1;" },
                batches);
        }

        [Fact]
        public void KeepsTheHeaderCommentWithTheFirstBatch()
        {
            var script = "-- Tags: Db1\nCREATE TABLE dbo.T (Id INT);\nGO\nSELECT 1;";

            var batches = Split(script);

            Assert.Equal(2, batches.Count);
            Assert.StartsWith("-- Tags: Db1", batches[0]);
            Assert.EndsWith("CREATE TABLE dbo.T (Id INT);", batches[0]);
        }

        [Theory]
        [InlineData("go")]
        [InlineData("Go")]
        [InlineData("gO")]
        [InlineData("   GO")]
        [InlineData("\tGO")]
        [InlineData("GO   ")]
        [InlineData("GO -- next batch")]
        [InlineData("  GO\t -- indented, with a trailing comment")]
        public void RecognizesSeparatorRegardlessOfCaseIndentationAndTrailingComment(string separatorLine)
        {
            var batches = Split($"SELECT 1;\n{separatorLine}\nSELECT 2;");

            Assert.Equal(new[] { "SELECT 1;", "SELECT 2;" }, batches);
        }

        [Fact]
        public void SplitsOnSeparatorWithWindowsLineEndings()
        {
            var batches = Split("SELECT 1;\r\nGO\r\nSELECT 2;\r\n");

            Assert.Equal(new[] { "SELECT 1;", "SELECT 2;" }, batches);
        }

        [Fact]
        public void SplitsOnSeparatorAtTheStartOfTheScript()
        {
            var batches = Split("GO\nSELECT 1;");

            Assert.Equal(new[] { "SELECT 1;" }, batches);
        }

        [Fact]
        public void SplitsOnSeparatorAtTheEndOfTheScriptWithoutTrailingNewline()
        {
            var batches = Split("SELECT 1;\nGO");

            Assert.Equal(new[] { "SELECT 1;" }, batches);
        }

        [Fact]
        public void RepeatsTheBatchForACountSuffix()
        {
            var batches = Split("INSERT INTO dbo.T VALUES (1);\nGO 3\nSELECT 1;");

            Assert.Equal(
                new[] { "INSERT INTO dbo.T VALUES (1);", "INSERT INTO dbo.T VALUES (1);", "INSERT INTO dbo.T VALUES (1);", "SELECT 1;" },
                batches);
        }

        [Fact]
        public void CountSuffixMayCarryATrailingComment()
        {
            var batches = Split("SELECT 1;\nGO 2 -- twice\nSELECT 2;");

            Assert.Equal(new[] { "SELECT 1;", "SELECT 1;", "SELECT 2;" }, batches);
        }

        [Theory]
        [InlineData("GOTO done")]
        [InlineData("GO3")]
        [InlineData("GO 0")]
        [InlineData("GO -1")]
        [InlineData("GO;")]
        [InlineData("GO SELECT 1;")]
        [InlineData("GO 2 SELECT 1;")]
        [InlineData("EXEC dbo.Go")]
        public void LeavesLinesThatOnlyLookLikeSeparatorsInPlace(string line)
        {
            var batches = SqlBatchSplitter.SplitIntoBatches($"SELECT 1;\n{line}\nSELECT 2;");

            // Not a separator, so the whole script stays one batch and SQL Server gets to
            // complain about the line if it is really a mistake.
            Assert.Single(batches);
            Assert.Contains(line, batches[0]);
        }

        [Fact]
        public void IgnoresSeparatorInsideAStringLiteral()
        {
            var script = "INSERT INTO dbo.T VALUES ('first\nGO\nsecond');\nSELECT 1;";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void HandlesEscapedQuotesWhenTrackingStringLiterals()
        {
            // The '' keeps the literal open, so the GO after it is still inside the string.
            var script = "INSERT INTO dbo.T VALUES ('it''s\nGO\nfine');\nGO\nSELECT 1;";

            var batches = Split(script);

            Assert.Equal(2, batches.Count);
            Assert.Contains("GO\nfine", batches[0].Replace("\r\n", "\n"));
            Assert.Equal("SELECT 1;", batches[1]);
        }

        [Fact]
        public void IgnoresSeparatorInsideABlockComment()
        {
            var script = "SELECT 1;\n/*\nGO\n*/\nSELECT 2;";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void IgnoresSeparatorInsideANestedBlockComment()
        {
            var script = "SELECT 1;\n/* outer /* inner\nGO\n*/ still commented\nGO\n*/\nSELECT 2;";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void ResumesSplittingAfterABlockCommentCloses()
        {
            var batches = Split("/* leading note */\nSELECT 1;\nGO\nSELECT 2;");

            Assert.Equal(2, batches.Count);
            Assert.EndsWith("SELECT 1;", batches[0]);
            Assert.Equal("SELECT 2;", batches[1]);
        }

        [Fact]
        public void IgnoresSeparatorInsideABracketedIdentifier()
        {
            var script = "SELECT * FROM [weird\nGO\nname];\nSELECT 2;";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void IgnoresSeparatorInsideAQuotedIdentifier()
        {
            var script = "SELECT * FROM \"weird\nGO\nname\";\nSELECT 2;";

            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            Assert.Equal(new[] { script }, batches);
        }

        [Fact]
        public void DropsBatchesThatHaveNothingExecutableInThem()
        {
            var batches = Split("-- Tags: Db1\nGO\n\nGO\n/* nothing here either */\nGO\nSELECT 1;\nGO\n   \n");

            Assert.Equal(new[] { "SELECT 1;" }, batches);
        }

        [Fact]
        public void ReturnsNoBatchesForAnEmptyOrCommentOnlyScript()
        {
            Assert.Empty(SqlBatchSplitter.SplitIntoBatches(string.Empty));
            Assert.Empty(SqlBatchSplitter.SplitIntoBatches("   \n\t\n"));
            Assert.Empty(SqlBatchSplitter.SplitIntoBatches("-- Tags: Db1\n-- nothing to run"));
            Assert.Empty(SqlBatchSplitter.SplitIntoBatches("/* nothing\nto run */"));
        }

        [Fact]
        public void ConsecutiveSeparatorsDoNotProduceEmptyBatches()
        {
            var batches = Split("SELECT 1;\nGO\nGO\nGO\nSELECT 2;");

            Assert.Equal(new[] { "SELECT 1;", "SELECT 2;" }, batches);
        }

        [Fact]
        public void SplitsARealisticProcedureScript()
        {
            var script = string.Join("\n",
                "-- Tags: Db1",
                "SET ANSI_NULLS ON;",
                "GO",
                "SET QUOTED_IDENTIFIER ON;",
                "GO",
                "CREATE OR ALTER PROCEDURE dbo.usp_Get",
                "AS",
                "BEGIN",
                "    SELECT 'GO' AS NotASeparator;",
                "END",
                "GO",
                "GRANT EXECUTE ON dbo.usp_Get TO [public];");

            var batches = Split(script);

            Assert.Equal(4, batches.Count);
            Assert.EndsWith("SET ANSI_NULLS ON;", batches[0]);
            Assert.Equal("SET QUOTED_IDENTIFIER ON;", batches[1]);
            Assert.StartsWith("CREATE OR ALTER PROCEDURE dbo.usp_Get", batches[2]);
            Assert.Contains("SELECT 'GO' AS NotASeparator;", batches[2]);
            Assert.Equal("GRANT EXECUTE ON dbo.usp_Get TO [public];", batches[3]);
        }
    }
}
