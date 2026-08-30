using MigrationOps.Core.Models;

/// <summary>
/// Renders a MigrationPlan to the console and reports overall success. The run fails (exit 1)
/// on any ValidationError, Changed, or VerifyFailed entry — Changed is deliberate: catching
/// a forbidden edit of an applied migration before a real run re-executes it is validate's
/// primary safety job, and a warning that exits 0 would sail through CI.
/// </summary>
static class PlanReportRenderer
{
    // executed = true for the `dry-run` command (pending scripts actually ran against the
    // database, rolled back); false for `validate` (report only, no database touched).
    public static bool Render(MigrationPlan plan, bool executed)
    {
        var verb = executed ? "Dry-run" : "Validate";
        Console.WriteLine($"{verb} — {plan.TargetDatabases.Count} database(s): {string.Join(", ", plan.TargetDatabases)}");

        var groups = plan.TargetDatabases.ToList();
        if (plan.Entries.Any(e => e.Database == "(unresolved)"))
        {
            groups.Add("(unresolved)");
        }

        var nameWidth = plan.Entries.Count == 0 ? 0 : plan.Entries.Max(e => e.FileName.Length) + 2;

        foreach (var database in groups)
        {
            var entries = plan.Entries
                .Where(e => e.Database.Equals(database, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine();
            Console.WriteLine($"{database}:");

            if (entries.Count == 0)
            {
                Console.WriteLine("  (nothing to do)");
                continue;
            }

            foreach (var entry in entries)
            {
                RenderEntry(entry, nameWidth);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");

        foreach (var database in groups)
        {
            var entries = plan.Entries
                .Where(e => e.Database.Equals(database, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var counts =
                $"{entries.Count(e => e.Status == PlanEntryStatus.AlreadyApplied)} applied, " +
                $"{entries.Count(e => e.Status == PlanEntryStatus.WouldApply)} pending, " +
                $"{entries.Count(e => e.Status == PlanEntryStatus.Changed)} changed, " +
                $"{entries.Count(e => e.Status == PlanEntryStatus.ValidationError)} errors";

            if (executed && entries.Any(e => e.VerifyStatus != null))
            {
                counts +=
                    $"   (dry-run: {entries.Count(e => e.VerifyStatus == PlanEntryStatus.VerifyPassed)} passed, " +
                    $"{entries.Count(e => e.VerifyStatus == PlanEntryStatus.VerifyFailed)} failed, " +
                    $"{entries.Count(e => e.VerifyStatus == PlanEntryStatus.NotVerified)} not run)";
            }

            Console.WriteLine($"  {database}: {counts}");
        }

        var succeeded =
            !plan.Entries.Any(e => e.Status == PlanEntryStatus.ValidationError
                                || e.Status == PlanEntryStatus.Changed
                                || e.VerifyStatus == PlanEntryStatus.VerifyFailed);

        Console.WriteLine();
        var verbUpper = verb.ToUpperInvariant();
        Console.WriteLine(succeeded ? $"{verbUpper} SUCCEEDED" : $"{verbUpper} FAILED");

        return succeeded;
    }

    private static void RenderEntry(PlanEntry entry, int nameWidth)
    {
        var (marker, description) = entry.Status switch
        {
            PlanEntryStatus.AlreadyApplied => ("=", "already applied"),
            PlanEntryStatus.WouldApply => ("+", entry.Detail ?? "would apply"),
            PlanEntryStatus.Changed => ("~", $"CHANGED: {entry.Detail}"),
            _ => ("x", $"ERROR: {entry.Detail}"),
        };

        var line = $"  {marker} {entry.FileName.PadRight(nameWidth)}{description}";

        if (entry.VerifyStatus != null)
        {
            line += entry.VerifyStatus switch
            {
                PlanEntryStatus.VerifyPassed => "   [dry-run: PASSED]",
                PlanEntryStatus.VerifyFailed => $"   [dry-run: FAILED: {entry.VerifyDetail}]",
                _ => $"   [dry-run: {entry.VerifyDetail}]",
            };
        }

        Console.WriteLine(line);

        if (entry.Status == PlanEntryStatus.Changed && entry.Kind == ScriptKind.Migration)
        {
            Console.WriteLine("      WARNING: a real run would RE-EXECUTE this migration. Applied migrations must");
            Console.WriteLine("      never be edited; put the fix in a new migration.");
        }
    }
}
