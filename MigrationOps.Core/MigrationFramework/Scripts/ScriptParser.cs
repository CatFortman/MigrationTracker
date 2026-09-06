namespace MigrationOps.Core.MigrationFramework.Scripts
{
    /// <summary>
    /// Pure parsing, hashing and validation of script content. Nothing here touches a database
    /// or configuration, so every rule the runner enforces on a .sql file can be tested directly.
    /// </summary>
    public static class ScriptParser
    {
        /// <summary>
        /// Computes the script's integrity checksum from its own content instead of trusting a
        /// header written by something else. A leading "-- Checksum:" line (left over from files
        /// committed before this change, or a stray hand-edit) is stripped before hashing, so its
        /// presence or removal never changes the result. Line endings are hashed as-is rather than
        /// normalized: this reproduces the SHA-256 the pre-commit hook used to compute for a file's
        /// first commit (verified against the real headers already checked into this repo).
        /// </summary>
        public static string ComputeChecksum(string script)
        {
            var newlineIndex = script.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                var firstLine = script.Substring(0, newlineIndex).TrimEnd('\r');
                if (firstLine.StartsWith("-- Checksum:"))
                {
                    script = script.Substring(newlineIndex + 1);
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(script);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Pure decision function behind the "editing an applied migration is forbidden" guard:
        /// given the checksum of the migration's last successful apply (null if it has never
        /// successfully applied) and the checksum of the file on disk now, returns a descriptive
        /// error if they've diverged, or null if it's safe to proceed (never applied, or applied
        /// with this exact checksum already).
        /// </summary>
        public static string? DetectEditedMigration(string scriptName, string? recordedChecksum, string currentChecksum)
        {
            if (recordedChecksum == null || recordedChecksum == currentChecksum)
            {
                return null;
            }

            return $"Migration '{scriptName}' was already applied with checksum {ShortChecksum(recordedChecksum)} " +
                   $"but the file now has checksum {ShortChecksum(currentChecksum)}. Migrations are immutable once " +
                   "applied - create a new migration instead of editing this one.";
        }

        /// <summary>
        /// Database object scripts must be idempotent, since they are re-run on every deploy.
        /// This enforces that the first executable statement (after the checksum/tags header
        /// comments) is a CREATE OR ALTER, rather than a plain CREATE that fails on redeploy.
        ///
        /// Only the first batch is validated. A file may legitimately continue past a GO with
        /// statements of other shapes - the grants, extended properties or SET options that follow
        /// an object definition - and those are not what this rule is about.
        /// </summary>
        public static void EnsureCreateOrAlterStatement(string script, string scriptName)
        {
            var batches = SqlBatchSplitter.SplitIntoBatches(script);

            if (batches.Count == 0)
            {
                throw new InvalidOperationException($"Database object script '{scriptName}' is empty or contains no executable statement.");
            }

            var lines = batches[0].Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith("--"))
                {
                    continue;
                }

                if (!trimmed.StartsWith("CREATE OR ALTER", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Database object script '{scriptName}' must begin with a 'CREATE OR ALTER' statement.");
                }

                return;
            }

            throw new InvalidOperationException($"Database object script '{scriptName}' is empty or contains no executable statement.");
        }

        public static string ShortChecksum(string? checksum)
        {
            return string.IsNullOrEmpty(checksum) ? "(none)" : checksum.Length <= 8 ? checksum : checksum.Substring(0, 8) + "...";
        }
    }
}
