namespace MigrationOps.Core.Models
{
    public class MigrationFileStatus
    {
        public string FileName { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public bool IsApplied { get; set; }
        public bool HasDrift { get; set; }
        public string? RecordedChecksum { get; set; }
        public string CurrentChecksum { get; set; } = string.Empty;

        // Set when the file fails validation (no "-- Tags:" comment, or an object script
        // without CREATE OR ALTER). A tagless file cannot be matched to a database, so it
        // is reported regardless of the database filter; callers dedupe by filename.
        public string? ValidationError { get; set; }
    }
}
