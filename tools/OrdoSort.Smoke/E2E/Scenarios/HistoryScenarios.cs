using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The audit log: rows written by a real <see cref="History"/> into a
/// real SQLite file, read back by the real HistoryWindow, and exported to a
/// real spreadsheet. The only surface in the suite that touches
/// history.sqlite — the two report surfaces read csv/xlsx and never open it.
///
/// <b>Marshalling.</b> HistoryViewModel has no DebouncedProbe and no uiContext
/// parameter; its one hop is <c>LoadAsync</c>'s
/// <c>await _scheduler.Run(...)</c> (HistoryViewModel.cs:109), and EVERYTHING
/// the window displays is assigned in the continuation after it: Rows (cleared
/// and refilled, :117-118), _total and _showedAll (:115-116), CanShowAll
/// (:120), and — via the ApplyFilter call that ends it (:121) — IsEmpty,
/// NoMatches and FooterText (:133-138). Nothing the scenarios below assert is
/// assigned before that await.
///
/// That makes the choice of scheduler load-bearing, and it is why these
/// scenarios pass NO third constructor argument. The default is the production
/// <c>TaskWorkScheduler</c> (WorkScheduler.cs:16, <c>Task.Run</c>), so the
/// await really does hop to the thread pool and really does resume through the
/// SynchronizationContext E2ERunner installs — which means the waits below
/// genuinely need a pumped frame and genuinely can fail. Handing it an
/// <see cref="InlineScheduler"/> instead, the way every tool-window scenario
/// does, would return an already-completed <c>Task.FromResult</c>, the await
/// would continue synchronously inside the constructor, Rows would be full
/// before <c>new HistoryWindow(vm)</c> was even reached, and every
/// <c>E2EPump.Until</c> below would be answered by its own pre-pump fast path
/// without ever arming a frame: exactly the class of assertion-that-cannot-
/// fail documented on <c>ScenarioKit.Added</c>. The unit tests DO pass
/// InlineWorkScheduler (HistoryViewModelTests.cs:39) — that is right for them
/// and wrong here, which is the whole difference between the two suites.
///
/// Each scenario waits on <c>FooterText.Length &gt; 0</c> rather than on a row
/// count, because FooterText is the one property set at the very END of that
/// continuation and is non-empty in BOTH the seeded and the empty case
/// ("3 of 3 filings shown" / "0 of 0 filings shown"). A <c>Rows.Count == 0</c>
/// wait for the empty log would have been true before the load even started —
/// a wait that proves the load happened has to be a wait the un-loaded state
/// fails.
///
/// <b>What the export actually writes.</b> HistoryViewModel.ExportAsync asks
/// for a save path through <c>AskSaveFile("Spreadsheet files (*.csv)|*.csv",
/// "ordosort_history.csv")</c> and hands it to <c>History.ExportCsv</c>
/// (HistoryViewModel.cs:143-148) — a UTF-8-BOM CSV, header row from
/// <see cref="History.Columns"/>, one line per filing in id order. So the
/// target below is a .csv, and the assertion reads the file back and checks
/// that header and those rows rather than checking that some bytes exist:
/// "a file was created and is non-empty" is satisfied by a single stray
/// newline.</summary>
public static class HistoryScenarios
{
    private const string Surface = "History";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "rows load from the audit log", "clean", RowsLoad),
        new Scenario(Surface, "export writes a spreadsheet", "clean", Export),
        new Scenario(Surface, "an empty log", "awkward", EmptyLog),
    };

    /// <summary>Seed the audit database through the app's OWN History, so the
    /// schema can never drift from what the window reads.
    ///
    /// The recording call is <c>History.LogCommit</c> (History.cs:155) with
    /// the same ten arguments HistoryViewModelTests.Seed uses
    /// (HistoryViewModelTests.cs:31-32); only the values differ, and only so
    /// they look like the real thing — an inbox path, an insert-mode filed
    /// name, the _INVOICE suffix, the Invoices route. Nothing on disk is
    /// created for them: LogCommit stores strings, and this surface is about
    /// the log, not the files it describes.</summary>
    private static History Seed(ScenarioContext ctx, int rows)
    {
        var inbox = ctx.Fx.Dir("inbox");
        var filed = ctx.Fx.Dir("filed");
        var history = new History(Path.Combine(ctx.Fx.Root, "history.sqlite"));
        for (var i = 1; i <= rows; i++)
        {
            var original = $"2024010{i}--{i}{i}{i}{i}.pdf";
            history.LogCommit(
                originalPath: Path.Combine(inbox, original),
                originalName: original,
                newName: $"2024010{i}-CLIENT {i}-{i}{i}{i}{i}_INVOICE.pdf",
                nameEntered: $"CLIENT {i}",
                namingMode: "insert",
                suffixApplied: "_INVOICE",
                routeLabel: "Invoices",
                routePath: filed,
                tagged: false,
                collisionSuffix: "");
        }
        return history;
    }

    /// <summary>Row count, ordering, footer — then the live Find box, which is
    /// the one thing about this window that is not just "read the table":
    /// filtering narrows RowsView without ever touching Rows.</summary>
    private static void RowsLoad(ScenarioContext ctx)
    {
        var history = Seed(ctx, 3);
        try
        {
            var vm = new HistoryViewModel(history, ctx.Dialogs);
            var win = new HistoryWindow(vm);
            E2EPump.ShowOffscreen(win);
            E2EPump.Until(() => vm.FooterText.Length > 0, 10_000);

            ctx.Check("three rows loaded", vm.Rows.Count == 3, $"got {vm.Rows.Count}");
            ctx.Check("newest first", vm.Rows.Count == 3 && vm.Rows[0].Name == "CLIENT 3",
                "top row: " + (vm.Rows.Count == 0 ? "<none>" : vm.Rows[0].Name));
            ctx.Check("the row carries what was filed, not just that something was",
                vm.Rows.Count == 3
                && vm.Rows[0].FiledAs == "20240103-CLIENT 3-3333_INVOICE.pdf"
                && vm.Rows[0].Original == "20240103--3333.pdf"
                && vm.Rows[0].Route == "Invoices"
                && !vm.Rows[0].Reverted,
                vm.Rows.Count == 0 ? "no rows"
                    : $"{vm.Rows[0].Original} -> {vm.Rows[0].FiledAs} via {vm.Rows[0].Route}");
            ctx.Check("the footer counts them", vm.FooterText == "3 of 3 filings shown",
                $"got \"{vm.FooterText}\"");
            ctx.Check("a log with rows in it is not the empty state", !vm.IsEmpty, "IsEmpty");

            // Filter is assigned directly and its setter calls ApplyFilter
            // synchronously (HistoryViewModel.cs:88) — no hop, so no wait.
            vm.Filter = "CLIENT 2";
            ctx.Check("Find narrows what the grid shows",
                vm.RowsView.Cast<HistoryRow>().Count() == 1,
                $"got {vm.RowsView.Cast<HistoryRow>().Count()} visible");
            ctx.Check("...without narrowing the log itself", vm.Rows.Count == 3,
                $"Rows.Count fell to {vm.Rows.Count}");

            vm.Filter = "nothing matches this";
            ctx.Check("a search that finds nothing says so, and is not the empty state",
                vm.NoMatches && !vm.IsEmpty, $"NoMatches={vm.NoMatches}, IsEmpty={vm.IsEmpty}");

            vm.Filter = "";
            ctx.Check("clearing Find brings every row back",
                vm.RowsView.Cast<HistoryRow>().Count() == 3,
                $"got {vm.RowsView.Cast<HistoryRow>().Count()} visible");

            ctx.Capture(win);
        }
        finally { history.Dispose(); }
    }

    private static void Export(ScenarioContext ctx)
    {
        var history = Seed(ctx, 3);
        try
        {
            var target = Path.Combine(ctx.Fx.Dir("out"), "history.csv");
            ctx.Dialogs.QueueSaveFile(target);

            var vm = new HistoryViewModel(history, ctx.Dialogs);
            var win = new HistoryWindow(vm);
            E2EPump.ShowOffscreen(win);
            E2EPump.Until(() => vm.FooterText.Length > 0, 10_000);
            ctx.Check("three rows to export", vm.Rows.Count == 3, $"got {vm.Rows.Count}");

            // ExportAsync hops to the thread pool for ExportCsv and reports
            // through _dialogs.Info in the continuation
            // (HistoryViewModel.cs:148-149). Waiting on THAT rather than on
            // File.Exists is the difference between the window having finished
            // the export and the disk having received some bytes: ExportCsv
            // creates the file on the pool thread, so a File.Exists predicate
            // can be answered before the UI thread has been told anything.
            vm.ExportCommand.Execute(null);
            var reported = E2EPump.Until(() => ctx.Dialogs.Infos.Count > 0, 15_000);
            ctx.Check("the window reports the export finished", reported,
                "no Info message within 15000ms");
            ctx.Check("...and says how many rows went out",
                ctx.Dialogs.Infos.Any(m => m.StartsWith("Exported 3 rows to ", StringComparison.Ordinal)),
                "got: " + string.Join(" | ", ctx.Dialogs.Infos));

            ctx.FileExists(target);
            var lines = ReadLines(target);
            ctx.Check("the export has a header and one line per filing", lines.Count == 4,
                $"got {lines.Count} line(s): " + string.Join(" / ", lines.Take(6)));
            ctx.Check("the header is the audit table's own column list",
                lines.Count > 0 && lines[0] == string.Join(",", History.Columns),
                lines.Count == 0 ? "empty file" : "got: " + lines[0]);
            ctx.Check("the rows carry the real filed names, oldest first",
                lines.Count == 4
                && lines[1].Contains("20240101-CLIENT 1-1111_INVOICE.pdf", StringComparison.Ordinal)
                && lines[3].Contains("20240103-CLIENT 3-3333_INVOICE.pdf", StringComparison.Ordinal),
                lines.Count == 4 ? "first: " + lines[1] : "wrong line count");
            ctx.Check("...and the names that were typed, and the route",
                lines.Count == 4
                && lines[1].Contains("CLIENT 1", StringComparison.Ordinal)
                && lines[1].Contains("Invoices", StringComparison.Ordinal),
                lines.Count == 4 ? "first: " + lines[1] : "wrong line count");

            ctx.Capture(win);
        }
        finally { history.Dispose(); }
    }

    /// <summary>Nothing filed yet. The window still has to come up, and the
    /// empty state has to be the EMPTY one rather than the "your search
    /// matched nothing" one — the distinction HistoryViewModel.IsEmpty's own
    /// doc comment exists for.</summary>
    private static void EmptyLog(ScenarioContext ctx)
    {
        var history = Seed(ctx, 0);
        try
        {
            var vm = new HistoryViewModel(history, ctx.Dialogs);
            var win = new HistoryWindow(vm);
            E2EPump.ShowOffscreen(win);

            // Waits on the footer, not on Rows.Count == 0 — that was already
            // true before the load ran; see the class doc.
            var loaded = E2EPump.Until(() => vm.FooterText.Length > 0, 10_000);
            ctx.Check("the window finished loading an empty log", loaded,
                "FooterText never appeared within 10000ms");

            ctx.Check("no rows", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
            ctx.Check("the footer says zero rather than staying blank",
                vm.FooterText == "0 of 0 filings shown", $"got \"{vm.FooterText}\"");
            ctx.Check("this reads as an empty log, not as a search with no hits",
                vm.IsEmpty && !vm.NoMatches, $"IsEmpty={vm.IsEmpty}, NoMatches={vm.NoMatches}");
            ctx.Check("nothing to Show all", !vm.CanShowAll, "Show all is still offered");
            ctx.Check("the window came up anyway", win.IsLoaded, "window not loaded");
            ctx.Capture(win);
        }
        finally { history.Dispose(); }
    }

    /// <summary>Read the exported CSV back without ever throwing: a file that
    /// cannot be read must surface as a failed assertion carrying the reason,
    /// the same discipline ScenarioContext's own I/O helpers keep.</summary>
    private static IReadOnlyList<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path)
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            return new[] { $"<unreadable: {ex.GetType().Name}: {ex.Message}>" };
        }
    }
}
