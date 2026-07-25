using MigrationOps.Core.MigrationFramework.Services;

namespace MigrationOps.Core.Tests
{
    // MigrationService's parameterless constructor resolves Configurations/dbconfig.json
    // relative to the current directory and calls IConfigurationBuilder.SetBasePath on it,
    // which throws DirectoryNotFoundException if that folder doesn't exist (as it doesn't
    // under the test project's bin output). Tests that just need *a* MigrationService
    // instance to call an instance method on (and don't care about its configuration)
    // should go through here instead of `new MigrationService()`.
    internal static class TestMigrationService
    {
        public static MigrationService Create()
        {
            using var configFile = new TempFile("{}", ".json");
            return new MigrationService(configFile.Path);
        }
    }


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

        public string WriteFile(string fileName, string content)
        {
            var filePath = System.IO.Path.Combine(Path, fileName);
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
