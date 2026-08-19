namespace MigrationOps.Core.MigrationFramework.Scripts
{
    /// <summary>
    /// Splits a .sql file into the batches SQL Server should receive, the way SSMS and sqlcmd do:
    /// a line whose only content is <c>GO</c> ends the current batch. This is what makes statements
    /// that must start a batch (CREATE PROCEDURE, CREATE VIEW, SET options) usable in a migration.
    ///
    /// <c>GO</c> only separates when it stands alone on a line, so the word is left untouched inside
    /// string literals, quoted identifiers and comments (including multi-line ones) - a literal such
    /// as 'GO TO STEP 2' is never mistaken for a separator. An optional repeat count is honored as
    /// SSMS does: <c>GO 3</c> runs the batch it terminates three times.
    ///
    /// Pure text handling: nothing here touches a database or configuration.
    /// </summary>
    public static class SqlBatchSplitter
    {
        private enum ScanState
        {
            Code,
            LineComment,
            BlockComment,
            StringLiteral,
            QuotedIdentifier,
            BracketedIdentifier
        }

        /// <summary>
        /// Returns the script's batches in execution order. Batches with nothing executable in them
        /// (blank, or only comments) are dropped rather than sent to the server, so the header
        /// comment above a leading <c>GO</c> never becomes a batch of its own. A script with no
        /// <c>GO</c> line comes back as a single batch holding the whole file, which is exactly what
        /// the runner used to execute.
        /// </summary>
        public static List<string> SplitIntoBatches(string script)
        {
            var batches = new List<string>();

            if (string.IsNullOrEmpty(script))
            {
                return batches;
            }

            var state = ScanState.Code;
            var blockCommentDepth = 0;
            var batchStart = 0;
            var lineStart = 0;
            var hasExecutableContent = false;
            var index = 0;

            while (index < script.Length)
            {
                // A separator is only recognized at the start of a line and only in code: that is
                // what keeps a stray "GO" inside a comment or a multi-line string from splitting.
                if (state == ScanState.Code && index == lineStart
                    && TryMatchSeparatorLine(script, index, out var repeatCount, out var nextLineStart))
                {
                    AddBatch(batches, script, batchStart, index, hasExecutableContent, repeatCount);

                    index = nextLineStart;
                    batchStart = index;
                    lineStart = index;
                    hasExecutableContent = false;
                    continue;
                }

                var current = script[index];
                var next = index + 1 < script.Length ? script[index + 1] : '\0';

                switch (state)
                {
                    case ScanState.Code:
                        if (current == '-' && next == '-')
                        {
                            state = ScanState.LineComment;
                            index += 2;
                            continue;
                        }

                        if (current == '/' && next == '*')
                        {
                            state = ScanState.BlockComment;
                            blockCommentDepth = 1;
                            index += 2;
                            continue;
                        }

                        if (current == '\'')
                        {
                            state = ScanState.StringLiteral;
                        }
                        else if (current == '"')
                        {
                            state = ScanState.QuotedIdentifier;
                        }
                        else if (current == '[')
                        {
                            state = ScanState.BracketedIdentifier;
                        }

                        if (!char.IsWhiteSpace(current))
                        {
                            hasExecutableContent = true;
                        }

                        break;

                    case ScanState.LineComment:
                        if (current == '\n')
                        {
                            state = ScanState.Code;
                        }

                        break;

                    case ScanState.BlockComment:
                        // T-SQL block comments nest, so /* */ pairs are counted rather than the
                        // comment ending at the first */.
                        if (current == '/' && next == '*')
                        {
                            blockCommentDepth++;
                            index += 2;
                            continue;
                        }

                        if (current == '*' && next == '/')
                        {
                            blockCommentDepth--;
                            index += 2;

                            if (blockCommentDepth == 0)
                            {
                                state = ScanState.Code;
                            }

                            continue;
                        }

                        break;

                    case ScanState.StringLiteral:
                        if (current == '\'')
                        {
                            // Two quotes in a row are an escaped quote, not the end of the literal.
                            if (next == '\'')
                            {
                                index += 2;
                                continue;
                            }

                            state = ScanState.Code;
                        }

                        break;

                    case ScanState.QuotedIdentifier:
                        if (current == '"')
                        {
                            if (next == '"')
                            {
                                index += 2;
                                continue;
                            }

                            state = ScanState.Code;
                        }

                        break;

                    case ScanState.BracketedIdentifier:
                        if (current == ']')
                        {
                            if (next == ']')
                            {
                                index += 2;
                                continue;
                            }

                            state = ScanState.Code;
                        }

                        break;
                }

                if (current == '\n')
                {
                    lineStart = index + 1;
                }

                index++;
            }

            AddBatch(batches, script, batchStart, script.Length, hasExecutableContent, 1);

            return batches;
        }

        /// <summary>
        /// Matches a batch separator line: optional indentation, <c>GO</c> in any case, an optional
        /// positive repeat count, an optional trailing line comment, and nothing else before the
        /// line ends. Anything else - <c>GO 0</c>, <c>GO;</c>, <c>GOTO</c>, <c>GO SELECT 1</c> - is
        /// left in the batch for SQL Server to report, rather than being silently treated as a
        /// separator or silently dropped.
        /// </summary>
        private static bool TryMatchSeparatorLine(string script, int lineStart, out int repeatCount, out int nextLineStart)
        {
            repeatCount = 1;
            nextLineStart = lineStart;

            var index = lineStart;
            SkipSpacesAndTabs(script, ref index);

            if (index + 1 >= script.Length
                || (script[index] != 'G' && script[index] != 'g')
                || (script[index + 1] != 'O' && script[index + 1] != 'o'))
            {
                return false;
            }

            index += 2;

            var afterKeyword = index;
            SkipSpacesAndTabs(script, ref index);

            // The count needs whitespace in front of it, so "GO3" stays an identifier.
            if (index > afterKeyword && index < script.Length && char.IsAsciiDigit(script[index]))
            {
                var digitsStart = index;

                while (index < script.Length && char.IsAsciiDigit(script[index]))
                {
                    index++;
                }

                if (!int.TryParse(script.AsSpan(digitsStart, index - digitsStart), out repeatCount) || repeatCount < 1)
                {
                    return false;
                }

                SkipSpacesAndTabs(script, ref index);
            }

            if (index + 1 < script.Length && script[index] == '-' && script[index + 1] == '-')
            {
                while (index < script.Length && script[index] != '\n')
                {
                    index++;
                }
            }
            else if (index < script.Length && script[index] != '\r' && script[index] != '\n')
            {
                return false;
            }

            if (index < script.Length && script[index] == '\r')
            {
                index++;
            }

            if (index < script.Length && script[index] == '\n')
            {
                index++;
            }

            nextLineStart = index;

            return true;
        }

        private static void SkipSpacesAndTabs(string script, ref int index)
        {
            while (index < script.Length && (script[index] == ' ' || script[index] == '\t'))
            {
                index++;
            }
        }

        // The repeat count belongs to the GO line that ends the batch, so "GO 3" adds the same text
        // three times and the runner then executes it three times, as SSMS would.
        private static void AddBatch(List<string> batches, string script, int start, int end, bool hasExecutableContent, int repeatCount)
        {
            if (!hasExecutableContent || end <= start)
            {
                return;
            }

            var text = script.Substring(start, end - start);

            for (var repeat = 0; repeat < repeatCount; repeat++)
            {
                batches.Add(text);
            }
        }
    }
}
