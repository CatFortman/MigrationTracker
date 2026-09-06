namespace MigrationOps.Core.Models
{
    public enum EntryStatus
    {
        AlreadyApplied,
        WouldApply,
        Changed,
        ValidationError,
        DryRunPassed,
        DryRunFailed,
        NotRun
    }

    public class Entry
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public ScriptKind Kind { get; set; }
        public string Database { get; set; } = string.Empty;

        // Classification from diffing the file against history (AlreadyApplied, WouldApply,
        // Changed, ValidationError). Dry-run results go in DryRunStatus so that a Changed
        // entry stays Changed — an edited applied migration must fail validation even if
        // its SQL happens to execute cleanly.
        public EntryStatus Status { get; set; }
        public string? Detail { get; set; }

        // Set by the `dry-run` command only (DryRunPassed, DryRunFailed, NotRun); null
        // when the dry-run did not run or the entry had nothing to execute.
        public EntryStatus? DryRunStatus { get; set; }
        public string? DryRunDetail { get; set; }

        public string? RecordedChecksum { get; set; }
        public string? CurrentChecksum { get; set; }

        // Loaded at plan-build time so the `dry-run` command executes exactly what was classified.
        public string? ScriptText { get; set; }
    }

    public class MigrationPlan
    {
        public List<string> TargetDatabases { get; set; } = new();

        // In real run order per database: object scripts first, then migrations.
        public List<Entry> Entries { get; set; } = new();
    }
}
