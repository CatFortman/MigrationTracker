using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MigrationOps.Core.Models;
using MigrationOps.Dashboard.Data;

namespace MigrationOps.Dashboard.Pages
{
    public class ValidateModel : PageModel
    {
        private readonly MigrationDataService _dataService;

        public ValidateModel(MigrationDataService dataService)
        {
            _dataService = dataService;
        }

        private enum RunMode { Validate, DryRun, Apply }

        public List<string> DatabaseNames { get; private set; } = new();
        public string? Database { get; private set; }
        public bool DryRunExecuted { get; private set; }
        public bool ApplyExecuted { get; private set; }

        // Set only when OnPostApply's real apply threw (a genuine SQL failure, same as the
        // console app) — Plan is still populated (rebuilt via a plain validate) so the page can
        // show what did and didn't make it through before the failure.
        public string? ApplyError { get; private set; }

        public MigrationPlan Plan { get; private set; } = new();

        // Target databases plus "(unresolved)" when tagless files were found, matching the
        // console report's grouping.
        public List<string> Groups { get; private set; } = new();

        // Same criteria as the console's exit code: any Changed, ValidationError, or
        // DryRunFailed entry fails validation; an apply that threw also fails.
        public bool Succeeded { get; private set; }

        public IActionResult OnGet(string? database)
        {
            return Run(database, RunMode.Validate);
        }

        public IActionResult OnPostDryRun(string? database)
        {
            return Run(database, RunMode.DryRun);
        }

        public IActionResult OnPostApply(string? database)
        {
            return Run(database, RunMode.Apply);
        }

        private IActionResult Run(string? database, RunMode mode)
        {
            DatabaseNames = _dataService.GetDatabaseNames();

            if (!string.IsNullOrEmpty(database))
            {
                Database = DatabaseNames
                    .FirstOrDefault(n => string.Equals(n, database, StringComparison.OrdinalIgnoreCase));

                if (Database == null)
                {
                    return NotFound();
                }
            }

            DryRunExecuted = mode == RunMode.DryRun;
            ApplyExecuted = mode == RunMode.Apply;

            if (mode == RunMode.Apply)
            {
                try
                {
                    Plan = _dataService.RunApply(Database);
                }
                catch (Exception ex)
                {
                    ApplyError = ex.Message;
                    Plan = _dataService.VerifyMigrationPlan(Database);
                }
            }
            else if (mode == RunMode.DryRun)
            {
                Plan = _dataService.DryRunMigrationPlan(Database);
            }
            else
            {
                Plan = _dataService.VerifyMigrationPlan(Database);
            }

            Groups = Plan.TargetDatabases.ToList();
            if (Plan.Entries.Any(e => e.Database == "(unresolved)"))
            {
                Groups.Add("(unresolved)");
            }

            Succeeded = ApplyError == null
                && !Plan.Entries.Any(e => e.Status == EntryStatus.ValidationError
                                       || e.Status == EntryStatus.Changed
                                       || e.DryRunStatus == EntryStatus.DryRunFailed);

            return Page();
        }

        public List<Entry> EntriesFor(string group)
        {
            return Plan.Entries
                .Where(e => e.Database.Equals(group, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
