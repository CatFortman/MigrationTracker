namespace MigrationOps.Core.Tests
{
    // Writes content to a uniquely-named temp file so tests can exercise file-based parsing
    // (ParseTagsFromFile, ComputeChecksum, etc.) without checking in .sql fixtures that the
    // pre-commit hook would otherwise expect a "-- Tags:" comment on.
    internal sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string content, string extension = ".sql")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}{extension}");
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    // Same idea as TempFile but for tests (e.g. GetMigrationFileStatuses) that scan a whole
    // migrations directory.
    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path);
        }

        // fileName may include a relative subfolder (e.g. "Views/Foo.sql"), which is created on
        // demand - object scripts only get discovered when they sit under Functions, Views,
        // StoredProcedures or Triggers.
        public string WriteFile(string fileName, string content)
        {
            var filePath = System.IO.Path.Combine(Path, fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
