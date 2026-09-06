namespace MigrationOps.Core.MigrationFramework.Scripts
{
    /// <summary>
    /// Finds the .sql files a run operates on, in the order the run executes them, scoped to one
    /// database's subfolder. File discovery only — nothing here reads file contents.
    /// </summary>
    public static class ScriptCatalog
    {
        /// <summary>The object-script folders, in the order the apply pipeline runs them.</summary>
        public static readonly string[] DatabaseObjectFolders = { "Functions", "Views", "StoredProcedures", "Triggers" };

        /// <summary>The four object folders under the database's own subfolder, flattened in run
        /// order; missing folders (including a missing database subfolder) are skipped.</summary>
        public static List<string> ListDatabaseObjectFiles(string scriptsRootDirectory, string database)
        {
            var databaseRoot = Path.Combine(scriptsRootDirectory, database);

            return DatabaseObjectFolders
                .Select(folder => Path.Combine(databaseRoot, folder))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.sql").OrderBy(f => Path.GetFileName(f)))
                .ToList();
        }

        /// <summary>Migrations run in filename order — the yyyyMMdd-NNN prefix is what orders them
        /// — scoped to the database's own subfolder. A database with no migrations yet (folder
        /// doesn't exist) yields an empty list rather than throwing.</summary>
        public static List<string> ListMigrationFiles(string migrationsDirectory, string database)
        {
            var databaseRoot = Path.Combine(migrationsDirectory, database);

            if (!Directory.Exists(databaseRoot))
            {
                return new List<string>();
            }

            return Directory.GetFiles(databaseRoot, "*.sql")
                            .OrderBy(f => Path.GetFileName(f))
                            .ToList();
        }

        /// <summary>
        /// Locates an object script by file name across the four folders under the database's own
        /// subfolder. Falls back to the bare file name when it can't be found, so callers always
        /// have something to report.
        /// </summary>
        public static string FindDatabaseObjectFilePath(string scriptsRootDirectory, string database, string fileName)
        {
            var databaseRoot = Path.Combine(scriptsRootDirectory, database);

            return DatabaseObjectFolders
                .Select(folder => Path.Combine(databaseRoot, folder, fileName))
                .FirstOrDefault(File.Exists) ?? fileName;
        }

        /// <summary>
        /// Top-level subfolder names under <paramref name="rootDirectory"/> (e.g. Migrations/ or
        /// Scripts/) that don't match any configured database, case-insensitively. A stray folder
        /// here is almost always a typo — its scripts would otherwise be silently never discovered
        /// since routing is now purely by folder location. Returns empty when the root itself
        /// doesn't exist yet.
        /// </summary>
        public static List<string> FindUnrecognizedDatabaseFolders(string rootDirectory, IReadOnlyList<string> knownDatabases)
        {
            if (!Directory.Exists(rootDirectory))
            {
                return new List<string>();
            }

            return Directory.GetDirectories(rootDirectory)
                .Select(Path.GetFileName)
                .Where(name => !knownDatabases.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => name!)
                .ToList();
        }
    }
}
