namespace MigrationOps.Core.Models
{
    public class MigrationFileStatus
    {
        public string FileName { get; set; } = string.Empty;
        public bool IsApplied { get; set; }
        public bool HasDrift { get; set; }
        public string? RecordedChecksum { get; set; }
        public string CurrentChecksum { get; set; } = string.Empty;

        // Set when an object script fails validation (missing CREATE OR ALTER).
        public string? ValidationError { get; set; }
    }
}
