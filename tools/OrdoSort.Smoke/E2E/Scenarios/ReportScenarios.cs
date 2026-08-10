using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Turn-around time and Production, as their real windows over real
/// csv/xlsx PECF report spreadsheets read through SweptTable.Load — neither
/// window ever touches history.sqlite.
///
/// <b>Marshalling.</b> TurnaroundViewModel and ProductionViewModel share ONE
/// shape here, not two: both own a single <c>DebouncedProbe&lt;SweptTable.Table&gt;</c>
/// (<c>_tableProbe</c>) built over the SAME <c>ApplyTable</c> callback pattern
/// BulkRenameViewModel's own <c>_plansProbe</c>/<c>ApplyPlans</c> already use
/// (see BulkRenameScenarios.cs's own doc comment for the full proof). The
/// same proof holds unchanged here: <c>Refresh</c> always calls
/// <c>_tableProbe.Trigger(..., immediate: true)</c> from <c>AddPaths</c>
/// (TurnaroundViewModel.cs:132, ProductionViewModel.cs:173), which calls
/// <c>_timer.Change(immediate ? 0 : _intervalMs, Timeout.Infinite)</c>
/// (DebouncedProbe.cs:95) — a real <c>System.Threading.Timer</c> whose
/// callback always runs on a thread-pool thread regardless of the 0ms due
/// time — and <c>RunAsync</c> applies the result via
/// <c>_uiContext.Post(_ =&gt; _apply(result), null)</c> unconditionally
/// whenever uiContext is non-null (DebouncedProbe.cs:137-138), which every
/// scenario below supplies. Headers, the column mapping (FilenameColumn/
/// CategoryColumn or DatetimeColumn), Documents/Daily/Weekly/Categories (TAT)
/// and Rows/ColumnNames/GroupPicks/SumPicks (Production) are ALL set only
/// inside that one <c>ApplyTable</c> callback
/// (TurnaroundViewModel.cs:142-150, ProductionViewModel.cs:183-190) — so
/// every wait below is on one of those, never on the filesystem, and every
/// wait genuinely needs E2EPump to pump a frame.
///
/// The one exception, in both view models identically: <c>Refresh</c>'s own
/// empty-<c>_sources</c> fast path (TurnaroundViewModel.cs:125-130,
/// ProductionViewModel.cs:166-171) calls <c>_tableProbe.Cancel()</c> then
/// <c>ApplyTable(EmptyTable)</c> DIRECTLY — no Timer, no Post — mirroring
/// FilenameListViewModel's own documented Clear fast path
/// (SmallToolScenarios.cs). So after ClearCommand, Documents.Count == 0 /
/// Rows.Count == 0 is already true the instant Execute returns; TatEmpty and
/// ProdEmpty below still call E2EPump.Until on it for symmetry with the load
/// wait above it — harmless, per E2EPump.Until's own pre-pump fast path — not
/// because a pump is required there.
///
/// <b>The column mapping, and a defect the brief's own reference fixtures
/// had.</b> Both view models restore a mapping from Config first, else guess
/// by needle-priority substring match against the loaded headers — never a
/// hardcoded name (TurnaroundViewModel.RestoreMapping/Guess,
/// TurnaroundViewModel.cs:217-261; ProductionViewModel.RestoreDatetimeColumn/
/// Guess plus the owner-column guess inside RebuildPicksAndResults,
/// ProductionViewModel.cs:234-283). The guesser itself is untouched here.
///
/// The brief's own Step 1 sample code names its fixture headers
/// "Document,Category,Doc Date,Upload Date" and its report files
/// "reports/august.csv" etc. That guesses <c>FilenameColumn = "Document"</c>
/// and <c>CategoryColumn = "Category"</c> cleanly — vm.Documents does NOT
/// stay empty, so the header-guess trap the brief's Step 2 warns about does
/// not fire. But TurnaroundTime never reads a "Doc Date" or "Upload Date"
/// COLUMN at all, no matter what it's named or guessed onto: DocDate comes
/// from the leading <c>yyyyMMdd-</c> run at the FRONT of the mapped
/// filename-column CELL itself (TurnaroundTime.ExtractDocDate,
/// TurnaroundTime.cs:84-94), and UploadDate comes from the same
/// <c>yyyyMMdd-HHmm</c> shape at the front of the REPORT FILE'S OWN NAME —
/// <c>row.SourceFile</c>, i.e. the csv/xlsx path SweptTable.Load was given —
/// via UploadTimeFromReportName (TurnaroundTime.cs:56-76). A report file
/// literally named "august.csv" matches neither
/// <c>ReportUploadRegex</c> nor <c>ReportDateOnlyRegex</c>, so
/// UploadTimeFromReportName returns null for EVERY row read from it, TatDays
/// (Compute, TurnaroundTime.cs:111-124) is therefore null for every row
/// regardless of what the Document/Category cells say, and DailyAverages/
/// WeeklyAverages/ByCategory/ExceedingThreshold all filter on
/// <c>TatDays is not null</c> (TurnaroundTime.cs:144,158,182,195) — so Daily
/// and Categories stay permanently empty and TatAwkward's own "the inverted
/// row is shown as a negative TAT, not hidden" assertion could never see a
/// negative number, no matter how the dates in the row are written. That is
/// the exact "the scenario passes against something empty, proving nothing"
/// failure mode the brief's Step 2 warns about, one level deeper than the
/// header-guess check it names — a scenario whose fixture and mapping both
/// look right can still exercise none of the actual TAT arithmetic. Verified
/// with a deliberate revert: reverting the report file names below back to
/// the brief's undated "reports/august.csv" shape reproduces exactly this —
/// see task-12-report.md for the before/after console output.
///
/// Fixed here by giving every fixture report file its own real
/// <c>yyyyMMdd-HHmm-name.ext</c> upload-timestamp prefix (matching
/// TurnaroundTime.cs's own doc comment example,
/// "20250303-1144-PECF Report.xlsx") and every document row's Document cell
/// a real <c>yyyyMMdd-</c> prefix, then asserting the resulting TatDaysText
/// values by name — not merely a row count — so a regression back to
/// undated fixture files fails loudly instead of silently going green.
///
/// Production has the same shape of trap from a different angle. The
/// brief's own sample headers ("Operator,Category,Pages") match none of
/// ProductionViewModel's default group/sum columns
/// ("SOURCE-FOLDER"/"Employee"/"PDF-PAGE-COUNT",
/// ProductionViewModel.cs:274,277) and match none of the owner-column guess
/// needles ("owner","user","employee" — RebuildPicksAndResults,
/// ProductionViewModel.cs:269). With a fresh Config (ProductionGroupColumns/
/// SumColumns both empty), an unmatched default leaves _groupOrder AND
/// _sumOrder both empty — and ProductionReport.Group's own documented
/// degenerate case for an empty groupByColumns list collapses every row into
/// ONE totals group (ProductionReport.cs:118-119, "Key = []"). So
/// <c>vm.Rows.Count &gt; 0</c> — the brief's own ProdClean assertion — would
/// still be true, from a single ungrouped, unsummed blob, for a scenario
/// literally named "grouped and summed". Fixed by naming the fixture's
/// headers after the real PECF sweep columns ProductionReport.cs's own class
/// doc comment names (SOURCE-FOLDER, FILE-OWNER, PDF-PAGE-COUNT) — the SAME
/// unmodified guess/default logic then picks real group/sum columns — and by
/// asserting specific per-group sums and which HeaderPicks ended up
/// IsChosen, not just Rows.Count.</summary>
public static class ReportScenarios
{
    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario("Turn-around time", "a csv report loads and aggregates", "clean", TatCsv),
        new Scenario("Turn-around time", "an xlsx report loads", "clean", TatXlsx),
        new Scenario("Turn-around time", "unparseable and inverted dates", "awkward", TatAwkward),
        new Scenario("Turn-around time", "no sources", "awkward", TatEmpty),
        new Scenario("Production", "grouped and summed", "clean", ProdClean),
        new Scenario("Production", "a non-numeric value in a summed column", "awkward", ProdAwkward),
        new Scenario("Production", "no sources", "awkward", ProdEmpty),
    };

    private static TurnaroundViewModel NewTat(ScenarioContext ctx, Config cfg) =>
        new(cfg, ctx.Dialogs, saveCfg: null, new InlineScheduler(),
            SynchronizationContext.Current, probeDelayMs: 0);

    private static ProductionViewModel NewProd(ScenarioContext ctx, Config cfg) =>
        new(cfg, ctx.Dialogs, saveCfg: null, new InlineScheduler(),
            SynchronizationContext.Current, probeDelayMs: 0);

    // ------------------------------------------------------------ Turn-around time

    // Report uploaded 2026-08-04 12:00 (the FILE's own name — TurnaroundTime
    // never reads an "Upload Date" column, see the class doc comment above).
    // Document cells carry a real yyyyMMdd- prefix so ExtractDocDate has
    // something to parse. TatDays: 3, 2, 1.
    private const string TatReportFile = "reports/20260804-1200-august.csv";
    private const string TatCsvBody =
        "Document,Category\n" +
        "20260801--1111.pdf,INVOICE\n" +
        "20260802--2222.pdf,INVOICE\n" +
        "20260803--3333.pdf,STATEMENT\n";

    private static void TatCsv(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text(TatReportFile, TatCsvBody);

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("headers loaded", vm.Headers.Contains("Document"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Documents.Count == 3, 10000);
        ctx.Check("three document rows", vm.Documents.Count == 3, $"got {vm.Documents.Count}");

        // The mapping took effect for real, not just "Documents is non-empty":
        // every row's TatDaysText is a genuine number, computed from the
        // report file's own upload-timestamp name and each Document cell's
        // own leading date — not the "—" every row would show if
        // UploadTimeFromReportName had failed to parse the file name (the
        // brief's own undated fixture names, see the class doc comment).
        ctx.Check("every row got a real (non-dash) turnaround",
            vm.Documents.All(d => d.TatDaysText != "—"),
            "some row shows —: " + string.Join(", ", vm.Documents.Select(d => $"{d.FileName}={d.TatDaysText}")));

        var d1 = vm.Documents.FirstOrDefault(d => d.FileName == "20260801--1111.pdf");
        var d2 = vm.Documents.FirstOrDefault(d => d.FileName == "20260802--2222.pdf");
        var d3 = vm.Documents.FirstOrDefault(d => d.FileName == "20260803--3333.pdf");
        ctx.Check("2026-08-01 doc is 3 days behind the 2026-08-04 upload",
            d1?.TatDaysText == "3", $"got {d1?.TatDaysText}");
        ctx.Check("2026-08-02 doc is 2 days behind",
            d2?.TatDaysText == "2", $"got {d2?.TatDaysText}");
        ctx.Check("2026-08-03 doc is 1 day behind",
            d3?.TatDaysText == "1", $"got {d3?.TatDaysText}");

        ctx.Check("aggregates computed", vm.Daily.Count > 0 || vm.Categories.Count > 0,
            "no daily or category aggregate");
        ctx.Capture(win);
    }

    private static void TatXlsx(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        // Uploaded 2026-08-03 09:00. TatDays: 2, 1.
        var xlsx = ctx.Fx.Xlsx("reports/20260803-0900-august.xlsx",
            new[] { "Document", "Category" },
            new[]
            {
                new[] { "20260801--1111.pdf", "INVOICE" },
                new[] { "20260802--2222.pdf", "INVOICE" },
            });

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { xlsx });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("xlsx headers loaded", vm.Headers.Contains("Document"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Documents.Count == 2, 10000);
        ctx.Check("two document rows", vm.Documents.Count == 2, $"got {vm.Documents.Count}");

        var d1 = vm.Documents.FirstOrDefault(d => d.FileName == "20260801--1111.pdf");
        var d2 = vm.Documents.FirstOrDefault(d => d.FileName == "20260802--2222.pdf");
        ctx.Check("the xlsx path computes a real turnaround too (2 days)",
            d1?.TatDaysText == "2", $"got {d1?.TatDaysText}");
        ctx.Check("...and the second row (1 day)",
            d2?.TatDaysText == "1", $"got {d2?.TatDaysText}");
        ctx.Capture(win);
    }

    /// <summary>A document dated after its own report's upload gives a
    /// negative TAT, which TurnaroundTime.DocRow deliberately shows as-is
    /// rather than clamping — honest data a reviewer needs to see. An
    /// unparseable Document cell renders as an em dash. Report uploaded
    /// 2026-08-01 08:00; "20260810--1111.pdf" is dated 2026-08-10 — nine days
    /// AFTER the upload — which is what makes TatDays negative (-9), not
    /// merely small.</summary>
    private static void TatAwkward(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/20260801-0800-odd.csv",
            "Document,Category\n" +
            "20260810--1111.pdf,INVOICE\n" +
            "nonsense.pdf,INVOICE\n");

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Documents.Count == 2, 10000);

        ctx.Check("both rows survive", vm.Documents.Count == 2, $"got {vm.Documents.Count}");

        var inverted = vm.Documents.FirstOrDefault(d => d.FileName == "20260810--1111.pdf");
        ctx.Check("the inverted row is shown as a genuine negative TAT, not hidden",
            inverted?.TatDaysText == "-9", $"got {inverted?.TatDaysText}");

        var unparseable = vm.Documents.FirstOrDefault(d => d.FileName == "nonsense.pdf");
        ctx.Check("the unparseable doc date renders as a dash",
            unparseable?.DocDateText == "—", $"got {unparseable?.DocDateText}");
        ctx.Check("...and so does its TAT, since there is no doc date to compute one from",
            unparseable?.TatDaysText == "—", $"got {unparseable?.TatDaysText}");
        ctx.Capture(win);
    }

    private static void TatEmpty(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text(TatReportFile, TatCsvBody);

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Documents.Count == 3, 10000);
        ctx.Check("real rows loaded before clearing", vm.Documents.Count == 3,
            $"got {vm.Documents.Count}");
        ctx.Check("...with a real (non-dash) aggregate to lose",
            vm.Daily.Count > 0 || vm.Categories.Count > 0, "no aggregate before Clear");

        // ClearCommand hits Refresh's empty-_sources fast path (Cancel() then
        // ApplyTable(EmptyTable) called directly — no Timer, no Post — see
        // the class doc comment), so Documents.Count == 0 is already true the
        // instant Execute returns; this Until is here for symmetry with the
        // load wait above, not because a pump is required.
        vm.ClearCommand.Execute(null);
        E2EPump.Until(() => vm.Documents.Count == 0, 8000);

        ctx.Check("clearing empties the report", vm.Documents.Count == 0, $"got {vm.Documents.Count}");
        ctx.Check("and the aggregates too", vm.Daily.Count == 0 && vm.Categories.Count == 0,
            "an aggregate survived the clear");
        ctx.Capture(win);
    }

    // ------------------------------------------------------------------ Production

    private static string ColKey(IReadOnlyList<string> columns, string name)
    {
        var idx = columns.ToList().IndexOf(name);
        return idx.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Real PECF sweep column names (see ProductionReport.cs's own
    /// class doc comment: SOURCE-FOLDER, FILE-OWNER, PDF-PAGE-COUNT) so the
    /// UNMODIFIED default group/sum guess in RebuildPicksAndResults actually
    /// picks SOURCE-FOLDER + the derived Employee to group by and
    /// PDF-PAGE-COUNT to sum — see the class doc comment for why the brief's
    /// own "Operator,Category,Pages" headers would NOT do that.
    /// "ACME\SMITH"/"ACME\JONES" exercise DeriveEmployee's own
    /// domain-prefix stripping (ProductionReport.cs:83-87).</summary>
    private const string ProdCleanBody =
        "SOURCE-FOLDER,FILE-OWNER,PDF-PAGE-COUNT\n" +
        "INVOICE,ACME\\SMITH,12\n" +
        "STATEMENT,ACME\\SMITH,8\n" +
        "INVOICE,ACME\\JONES,20\n";

    private static void ProdClean(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod.csv", ProdCleanBody);

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("headers loaded", vm.Headers.Contains("SOURCE-FOLDER"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);

        // The mapping took effect for real: the default guess picked
        // SOURCE-FOLDER and the derived Employee to group by, and
        // PDF-PAGE-COUNT to sum — not the empty picks the brief's own
        // "Operator,Category,Pages" headers would have produced (see the
        // class doc comment).
        var groupTicked = new HashSet<string>(vm.GroupPicks.Where(p => p.IsChosen).Select(p => p.Name));
        ctx.Check("SOURCE-FOLDER and the derived Employee were picked to group by",
            groupTicked.SetEquals(new[] { "SOURCE-FOLDER", "Employee" }),
            "ticked: " + string.Join(", ", groupTicked));
        var sumTicked = new HashSet<string>(vm.SumPicks.Where(p => p.IsChosen).Select(p => p.Name));
        ctx.Check("PDF-PAGE-COUNT was picked to sum",
            sumTicked.SetEquals(new[] { "PDF-PAGE-COUNT" }),
            "ticked: " + string.Join(", ", sumTicked));

        // Three distinct (SOURCE-FOLDER, Employee) pairs in the fixture ->
        // three groups, not the one collapsed totals-row an empty group pick
        // would have produced.
        ctx.Check("three distinct groups, not one collapsed blob", vm.Rows.Count == 3,
            $"got {vm.Rows.Count}");

        var so = ColKey(vm.ColumnNames, "SOURCE-FOLDER");
        var emp = ColKey(vm.ColumnNames, "Employee");
        var sum = ColKey(vm.ColumnNames, "PDF-PAGE-COUNT");
        var recs = ColKey(vm.ColumnNames, "Records");

        var jones = vm.Rows.FirstOrDefault(r => r[so] == "INVOICE" && r[emp] == "JONES");
        ctx.Check("an INVOICE/JONES group exists", jones is not null,
            "no such group: " + string.Join(" | ", vm.Rows.Select(r => string.Join(",", r.Values))));
        if (jones is not null)
        {
            ctx.Check("its page count summed to 20 (its only row)", jones[sum] == "20", $"got {jones[sum]}");
            ctx.Check("its record count is 1", jones[recs] == "1", $"got {jones[recs]}");
        }

        var statement = vm.Rows.FirstOrDefault(r => r[so] == "STATEMENT" && r[emp] == "SMITH");
        ctx.Check("a STATEMENT/SMITH group exists (separate from SMITH's INVOICE group)",
            statement is not null,
            "no such group: " + string.Join(" | ", vm.Rows.Select(r => string.Join(",", r.Values))));
        if (statement is not null)
            ctx.Check("its page count summed to 8", statement[sum] == "8", $"got {statement[sum]}");

        ctx.Capture(win);
    }

    private static void ProdAwkward(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod-odd.csv",
            "SOURCE-FOLDER,FILE-OWNER,PDF-PAGE-COUNT\n" +
            "INVOICE,ACME\\SMITH,12\n" +
            "INVOICE,ACME\\JONES,not-a-number\n");

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);

        ctx.Check("the report still loads", vm.Rows.Count == 2, $"got {vm.Rows.Count}");
        ctx.Check("the window survived a non-numeric value", win.IsLoaded, "window went away");

        var so = ColKey(vm.ColumnNames, "SOURCE-FOLDER");
        var emp = ColKey(vm.ColumnNames, "Employee");
        var sum = ColKey(vm.ColumnNames, "PDF-PAGE-COUNT");

        // ProductionReport.Group's own doc comment: "a blank or non-numeric
        // sum cell contributes 0 rather than throwing" — check that it
        // genuinely landed as 0, not that the group merely exists.
        var jones = vm.Rows.FirstOrDefault(r => r[so] == "INVOICE" && r[emp] == "JONES");
        ctx.Check("the non-numeric row's group still exists", jones is not null,
            "no INVOICE/JONES group: " + string.Join(" | ", vm.Rows.Select(r => string.Join(",", r.Values))));
        if (jones is not null)
            ctx.Check("its non-numeric page count contributed 0, not a crash",
                jones[sum] == "0", $"got {jones[sum]}");

        var smith = vm.Rows.FirstOrDefault(r => r[so] == "INVOICE" && r[emp] == "SMITH");
        ctx.Check("the good row alongside it is untouched by the bad one",
            smith is not null && smith[sum] == "12",
            smith is null ? "no INVOICE/SMITH group" : $"got {smith[sum]}");

        ctx.Capture(win);
    }

    private static void ProdEmpty(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod.csv",
            "SOURCE-FOLDER,FILE-OWNER,PDF-PAGE-COUNT\nINVOICE,ACME\\SMITH,12\n");

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);

        // Prove this is a real, mapped group (not the one-row collapsed blob
        // an unmatched guess would also produce) before proving Clear empties
        // it — otherwise "no rows after Clear" would be indistinguishable
        // from "no rows ever, because nothing was ever really grouped".
        ctx.Check("one real group loaded, mapped by SOURCE-FOLDER/Employee",
            vm.Rows.Count == 1
            && vm.GroupPicks.Any(p => p is { Name: "SOURCE-FOLDER", IsChosen: true }),
            $"Rows.Count={vm.Rows.Count}, GroupPicks=" +
            string.Join(", ", vm.GroupPicks.Select(p => $"{p.Name}:{p.IsChosen}")));

        vm.ClearCommand.Execute(null);
        // Same empty-_sources fast path as TAT's ClearCommand above (no
        // Timer, no Post) — Until is here for symmetry, not necessity.
        E2EPump.Until(() => vm.Rows.Count == 0, 8000);

        ctx.Check("clearing empties the report", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Capture(win);
    }
}
