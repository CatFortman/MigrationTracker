namespace MigrationOps.Core.MigrationFramework.Scripts
{
    /// <summary>
    /// Finds the .sql files a run operates on, in the order the run executes them. File discovery
    /// only — nothing here reads file contents.
    /// </summary>
    public static class ScriptCatalog
    {
        /// <summary>The object-script folders, in the order the apply pipeline runs them.</summary>
        public static readonly string[] DatabaseObjectFolders = { "Functions", "Views", "StoredProcedures", "Triggers" };

        /// <summary>The four object folders, flattened in run order; missing folders are skipped.</summary>
        public static List<string> ListDatabaseObjectFiles(string scriptsRootDirectory)
        {
            return DatabaseObjectFolders
                .Select(folder => Path.Combine(scriptsRootDirectory, folder))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.sql").OrderBy(f => Path.GetFileName(f)))
                .ToList();
        }

        /// <summary>Migrations run in filename order — the yyyyMMdd-NNN prefix is what orders them.</summary>
        public static List<string> ListMigrationFiles(string migrationsDirectory)
        {
            return Directory.GetFiles(migrationsDirectory, "*.sql")
                            .OrderBy(f => Path.GetFileName(f))
                            .ToList();
        }

        /// <summary>
        /// Locates an object script by file name across the four folders. Falls back to the bare
        /// file name when it can't be found, so callers always have something to report.
        /// </summary>
        public static string FindDatabaseObjectFilePath(string scriptsRootDirectory, string fileName)
        {
            return DatabaseObjectFolders
                .Select(folder => Path.Combine(scriptsRootDirectory, folder, fileName))
                .FirstOrDefault(File.Exists) ?? fileName;
        }
    }
}
