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

        public List<string> DatabaseNames { get; private set; } = new();
        public string? Database { get; private set; }
        public bool DryRunExecuted { get; private set; }
        public MigrationPlan Plan { get; private set; } = new();

        // Target databases plus "(unresolved)" when tagless files were found, matching the
        // console report's grouping.
        public List<string> Groups { get; private set; } = new();

        // Same criteria as the console's exit code: any Changed, ValidationError, or
        // VerifyFailed entry fails validation.
        public bool Succeeded { get; private set; }

        public IActionResult OnGet(string? database)
        {
            return Run(database, executeAgainstDatabase: false);
        }

        public IActionResult OnPostDryRun(string? database)
        {
            return Run(database, executeAgainstDatabase: true);
        }

        private IActionResult Run(string? database, bool executeAgainstDatabase)
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

            DryRunExecuted = executeAgainstDatabase;
            Plan = _dataService.RunDryRun(Database, executeAgainstDatabase);

            Groups = Plan.TargetDatabases.ToList();
            if (Plan.Entries.Any(e => e.Database == "(unresolved)"))
            {
                Groups.Add("(unresolved)");
            }

            Succeeded = !Plan.Entries.Any(e => e.Status == PlanEntryStatus.ValidationError
                                            || e.Status == PlanEntryStatus.Changed
                                            || e.VerifyStatus == PlanEntryStatus.VerifyFailed);

            return Page();
        }

        public List<PlanEntry> EntriesFor(string group)
        {
            return Plan.Entries
                .Where(e => e.Database.Equals(group, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
