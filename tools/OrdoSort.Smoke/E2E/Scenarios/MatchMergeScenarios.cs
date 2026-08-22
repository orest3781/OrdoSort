using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Match and merge as the real MatchMergeWindow: a roster
/// spreadsheet matched against real PDFs by NAME — MatchMerge.MatchFiles
/// reads (last, first) out of the file's own stem via NameCandidates and
/// looks it up in the roster; there is no ID-based matching anywhere in this
/// surface. Fixtures below use the "yyyyMMdd-Last-First.pdf" stem shape
/// MatchMerge.NameCandidates' DatedStemRegex recognizes, against a roster
/// with separate Last/First/Control columns — not a combined "Name" column —
/// because LoadRosterFrom/ReloadRoster require three distinct headers
/// (MatchMergeViewModel(cfg, saveHeaders, dialogs) has no work seam to
/// substitute for this; it is the real MatchMerge.LoadRoster call).
///
/// MatchMergeViewModel has NO uiContext/IWorkScheduler seam at all — its
/// constructor takes neither. Unlike UnlockViewModel (which still hits a
/// real Task.Run/await on every call and so always needs a genuine
/// SynchronizationContext.Post to resume), every method these scenarios
/// exercise — LoadRosterFrom, AddFiles, MergeCommand.Execute (DoMerge -&gt;
/// Absorb), UndoCommand.Execute — runs to completion SYNCHRONOUSLY on the
/// calling thread: ReadHeaders/LoadRoster parse the CSV inline, MatchFiles
/// is a plain foreach with no I/O, and DoMerge's rename goes through
/// MatchMerge.ExecuteMerges -&gt; BulkRename.Plan/BulkRename.Execute, the
/// same non-async foreach/File.Move pair — called straight from the command
/// here, which is what BulkRenameViewModel did before audit QC-04 moved its
/// own copy onto a scheduler (see BulkRenameScenarios' class doc comment). The one async
/// method on this view model, AutoLoadRosterAsync, is never reached by any
/// scenario here (it needs Config.MergeRoster already set from a previous
/// run; ConfigFixture.Write always starts it blank, so MatchMergeWindow's
/// own `Loaded += async (_, _) => await _vm.AutoLoadRosterAsync()` no-ops
/// immediately on the empty-string fast path).
///
/// Concretely: there is no hop to trip on here, and no property split
/// between "assigned directly" and "assigned from inside a Post" the way
/// BulkRenameViewModel.Preview/Status or Unzip/ZipMerge's Summary/rows are —
/// Rows, Headers, MergeCount and Status are all just plain fields set before
/// each method returns. That is exactly why ScenarioKit.Settle does not
/// belong on the two MergeCommand.Execute(null) call sites below: DoMerge
/// sets Status synchronously, so `vm.Status.Length > 0` was already true
/// before Settle's own E2EPump.Until wait could ever run, and Settle's
/// recorded "the window reported a result" assertion could not fail no
/// matter what DoMerge actually did — see ScenarioKit.Settle's doc comment.
/// Both sites assert `vm.Status` directly instead, for what it actually
/// says (`vm.Status.StartsWith("Merged", …)`), which is a check that
/// genuinely fails if the merge didn't do what it claims.</summary>
public static class MatchMergeScenarios
{
    private const string Surface = "Match and merge";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "a roster matched against real documents", "clean", CleanMatch),
        new Scenario(Surface, "a document with no roster row", "awkward", NoMatch),
        new Scenario(Surface, "two roster rows match one document", "awkward", Ambiguous),
    };

    private static MatchMergeViewModel NewVm(ScenarioContext ctx, Config cfg) =>
        new(cfg, _ => { }, ctx.Dialogs);

    private static MatchMergeWindow Open(MatchMergeViewModel vm)
    {
        var win = new MatchMergeWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    /// <summary>A roster matched by name against two real documents, merged,
    /// and the RESULT checked by the actual new filenames on disk — not
    /// merely "two documents still exist under some name".</summary>
    private static void CleanMatch(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var a = ctx.Fx.Pdf("in/20240101-SMITH-JOHN.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/20240102-JONES-MARY.pdf", "TWO");
        var roster = ctx.Fx.Text("roster.csv",
            "Last,First,Control\nSMITH,JOHN,1111\nJONES,MARY,2222\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        ctx.Check("roster headers loaded", vm.Headers.Contains("Control"),
            "got: " + string.Join(", ", vm.Headers));
        ctx.Check("the header mapping auto-guessed the right columns",
            vm.FirstHeader == "First" && vm.LastHeader == "Last" && vm.ControlHeader == "Control",
            $"First={vm.FirstHeader} Last={vm.LastHeader} Control={vm.ControlHeader}");

        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Rows.Count == 2, 8000);
        ctx.Check("both documents listed", vm.Rows.Count == 2, $"got {vm.Rows.Count}");
        ctx.Check("both matched by name against the roster",
            vm.Rows.All(r => r.Status == "merge"),
            "statuses: " + string.Join(", ", vm.Rows.Select(r => $"{r.File}:{r.Status}")));
        ctx.Check("the preview already shows the control id merged in",
            vm.Rows.Any(r => r.Becomes == "20240101-SMITH-JOHN-1111.pdf")
            && vm.Rows.Any(r => r.Becomes == "20240102-JONES-MARY-2222.pdf"),
            "got: " + string.Join(", ", vm.Rows.Select(r => r.Becomes)));
        ctx.Check("merge is offered", vm.MergeCommand.CanExecute(null), "command disabled");

        vm.MergeCommand.Execute(null);
        ctx.Check("the merge is reported", vm.Status.StartsWith("Merged", StringComparison.Ordinal), vm.Status);

        var expectedA = Path.Combine(ctx.Fx.Root, "in", "20240101-SMITH-JOHN-1111.pdf");
        var expectedB = Path.Combine(ctx.Fx.Root, "in", "20240102-JONES-MARY-2222.pdf");
        ctx.FileExists(expectedA);
        ctx.FileExists(expectedB);
        ctx.FileMissing(a);
        ctx.FileMissing(b);
        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("no document was lost", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        ctx.Capture(win);
    }

    /// <summary>A parseable name that simply isn't in the roster — the
    /// "no roster row" case the scenario name promises, and a real name, not
    /// a plain-digit filename that fails to parse into (last, first) at all
    /// (that is the DIFFERENT no_name case, which MatchFiles reports
    /// separately). Also proves an unmatched document sitting alongside a
    /// real match doesn't get pulled in by mistake.</summary>
    private static void NoMatch(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var known = ctx.Fx.Pdf("in/20240101-SMITH-JOHN.pdf", "ONE");
        var stranger = ctx.Fx.Pdf("in/20240103-NOBODY-HERE.pdf", "TWO");
        var strangerBefore = File.ReadAllBytes(stranger);
        var roster = ctx.Fx.Text("roster.csv", "Last,First,Control\nSMITH,JOHN,1111\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        vm.AddFiles(new[] { known, stranger });
        E2EPump.Until(() => vm.Rows.Count == 2, 8000);

        var strangerRow = vm.Rows.Single(r => r.Source == stranger);
        ctx.Check("the document with no roster row is flagged no_match, not silently matched or guessed",
            strangerRow.Status == "no_match",
            $"status was \"{strangerRow.Status}\" — note=\"{strangerRow.Note}\" becomes=\"{strangerRow.Becomes}\"");
        ctx.Check("the real match is unaffected by the stranger",
            vm.Rows.Single(r => r.Source == known).Status == "merge",
            "the known document's status was disturbed");
        ctx.Check("merge count only includes the real match", vm.MergeCount == 1, $"got {vm.MergeCount}");

        vm.MergeCommand.Execute(null);
        ctx.Check("the merge is reported", vm.Status.StartsWith("Merged", StringComparison.Ordinal), vm.Status);

        ctx.BytesUnchanged(stranger, strangerBefore, "the unmatched document is left where it was");
        ctx.FileExists(Path.Combine(ctx.Fx.Root, "in", "20240101-SMITH-JOHN-1111.pdf"));
        ctx.Capture(win);
    }

    /// <summary>Two roster rows claiming the same document must NOT be
    /// silently resolved — the app opens Review matches instead. Asserts the
    /// real MatchRow.Status ("ambiguous"), not a bare vm.Status.Length &gt; 0:
    /// that placeholder is non-discriminating here in a very literal sense —
    /// ReloadRoster already set Status to "Roster loaded: N people." the
    /// moment the roster loaded, so vm.Status.Length &gt; 0 would read true
    /// even on a build that resolved the ambiguity by guessing, merged
    /// nothing, and never touched Status again.</summary>
    private static void Ambiguous(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var doc = ctx.Fx.Pdf("in/20240101-SMITH-JOHN.pdf", "ONE");
        var docBefore = File.ReadAllBytes(doc);
        var roster = ctx.Fx.Text("roster.csv",
            "Last,First,Control\nSMITH,JOHN,1111\nSMITH,JOHN,2222\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        vm.AddFiles(new[] { doc });
        E2EPump.Until(() => vm.Rows.Count == 1, 8000);

        ctx.Check("the document is listed once", vm.Rows.Count == 1, $"got {vm.Rows.Count}");
        var row = vm.Rows.Count > 0 ? vm.Rows[0] : null;
        ctx.Check("the row is flagged ambiguous, not silently resolved to one of the two candidates",
            row?.Status == "ambiguous",
            row is null ? "no row" : $"status was \"{row.Status}\" — becomes=\"{row.Becomes}\"");
        ctx.Check("the ambiguity names both candidates, not just a bare flag",
            row?.Note.Contains("2 candidates") == true,
            row is null ? "no row" : $"note was \"{row.Note}\"");
        ctx.Check("an ambiguous row proposes no new name",
            row?.Becomes.Length == 0,
            row is null ? "no row" : $"got \"{row.Becomes}\"");
        ctx.Check("merge count excludes the ambiguous row", vm.MergeCount == 0, $"got {vm.MergeCount}");
        ctx.Check("merge is not offered while the row is ambiguous",
            !vm.MergeCommand.CanExecute(null), "command enabled despite an ambiguous match");

        // What Review matches (TriageWindow) would actually be handed: the
        // ambiguous MatchResult, carrying both candidates.
        ctx.Check("the row is exactly what Review matches would show",
            vm.ReviewItems.Count == 1
            && vm.ReviewItems[0].Source == doc
            && vm.ReviewItems[0].Status == "ambiguous"
            && vm.ReviewItems[0].Candidates?.Count == 2,
            "ReviewItems: " + string.Join(", ", vm.ReviewItems.Select(
                r => $"{r.Source}:{r.Status}:{r.Candidates?.Count}")));
        ctx.Check("Review matches is offered", vm.CanReview, "CanReview is false");

        ctx.BytesUnchanged(doc, docBefore, "the ambiguous document is left untouched — nothing merges without a decision");
        ctx.Capture(win);
    }
}
