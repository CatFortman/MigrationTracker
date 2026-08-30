namespace MigrationOps.Core.Models
{
    public enum PlanEntryStatus
    {
        AlreadyApplied,
        WouldApply,
        Changed,
        ValidationError,
        VerifyPassed,
        VerifyFailed,
        NotVerified
    }

    public class PlanEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public ScriptKind Kind { get; set; }
        public string Database { get; set; } = string.Empty;

        // Classification from diffing the file against history (AlreadyApplied, WouldApply,
        // Changed, ValidationError). Verify results go in VerifyStatus so that a Changed
        // entry stays Changed — an edited applied migration must fail validation even if
        // its SQL happens to execute cleanly.
        public PlanEntryStatus Status { get; set; }
        public string? Detail { get; set; }

        // Set by the `dry-run` command only (VerifyPassed, VerifyFailed, NotVerified); null
        // when verification did not run or the entry had nothing to execute.
        public PlanEntryStatus? VerifyStatus { get; set; }
        public string? VerifyDetail { get; set; }

        public string? RecordedChecksum { get; set; }
        public string? CurrentChecksum { get; set; }

        // Loaded at plan-build time so the `dry-run` command executes exactly what was classified.
        public string? ScriptText { get; set; }
    }

    public class MigrationPlan
    {
        public List<string> TargetDatabases { get; set; } = new();

        // In real run order per database: object scripts first, then migrations.
        public List<PlanEntry> Entries { get; set; } = new();
    }
}
