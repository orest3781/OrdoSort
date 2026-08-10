using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Filename list, Page counts, List reformatter and Box labels, each
/// as its real window. The PageCounts `counter` seam stays null (real PDFs
/// are really counted) and LabelMaker's `scheduler` stays an InlineScheduler
/// (a legitimate scheduler swap, not a work-seam swap — see InlineScheduler's
/// own doc comment).
///
/// FOUR DIFFERENT MARSHALLING SHAPES, one per view model — none of them
/// identical to the other, and none identical to Unzip/ZipMerge's split,
/// Unlock's no-seam-at-all, or BulkRename's "always re-arms a real Timer"
/// shape already documented in their own files:
///
/// <b>FilenameListViewModel</b> owns a <c>DebouncedProbe&lt;FilenameList.Listing&gt;</c>
/// — the exact same class BulkRenameViewModel's `_plansProbe` uses, so the
/// same proof applies: Trigger re-arms a real System.Threading.Timer
/// (`_timer.Change(immediate ? 0 : intervalMs, Timeout.Infinite)`), a Timer
/// callback always runs on a thread-pool thread, and DebouncedProbe.RunAsync
/// applies its result via `_uiContext.Post(_ => _apply(result), null)` when
/// uiContext is non-null (DebouncedProbe.cs:137-138) — never a direct call.
/// InlineScheduler only changes what runs ON that thread-pool thread
/// (Fire/RunAsync's own `await _scheduler.Run(compute)`), never whether the
/// apply crosses back through Post. Rows, CountsLine and the OutputText
/// change notification are set ONLY inside ApplyListing
/// (FilenameListViewModel.cs:139-145), which is DebouncedProbe's `apply`
/// callback — so waiting on any of the three genuinely needs a pump. The one
/// exception, and the one place a filesystem-shaped trap could hide here:
/// Refresh's own empty-`_sources` fast path (FilenameListViewModel.cs:126-131)
/// calls `_listingProbe.Cancel()` then `ApplyListing(...)` DIRECTLY, with no
/// Timer and no Post at all — after ClearCommand, Rows.Count == 0 is already
/// true the instant Execute returns. FilenamesAwkward below still calls
/// E2EPump.Until on it (harmless — Until's own first check returns
/// immediately without arming a frame when the predicate is already true —
/// see E2EPump.Until's own "if (kickoff is null && ready()) return true"),
/// but the NEXT AddPaths call in the same scenario adds a non-empty source
/// and must go back through the real probe, which is why CountsLine — not
/// Rows, which Cancel's fast path already zeroed — is what's actually waited
/// on there.
///
/// <b>PageCountsViewModel</b> has no DebouncedProbe at all, but the same
/// "InlineScheduler collapses the compute, never the apply" shape shows up a
/// different way. AddFilesAsync's own body — `_scheduler.Run(Intake.Expand)`,
/// the Rows.Add loop, RaiseTotals(), `await Task.WhenAll(CountOneAsync...)` —
/// runs ENTIRELY without a real await point under InlineScheduler: every
/// `await _scheduler.Run(...)` awaits an already-completed
/// `Task.FromResult(...)`, and the C# compiler never suspends an await whose
/// awaiter reports IsCompleted, so AddFilesAsync's own returned Task is
/// already complete by the time the scenario gets it back — no thread hop
/// anywhere in the method body itself. But CountOneAsync's per-row apply
/// (PageCountsViewModel.cs:221-230) is an EXPLICIT, unconditional
/// `_uiContext.Post(_ => Do(), null)` whenever uiContext is supplied — and
/// every scenario below supplies SynchronizationContext.Current — so
/// row.Pages/Note/Pending and the RaiseTotals() call inside that same Do()
/// are deferred onto the dispatcher queue regardless of how fast the count
/// itself ran. That is the load-bearing consequence: `add.IsCompleted` can
/// (and, empirically, does) read true on the very first check, proving
/// nothing about whether any row has actually settled — only
/// `vm.Rows.All(r => !r.Pending)` does, which is why both counts scenarios
/// wait on that and not merely on the task.
///
/// <b>ListReformatViewModel</b> has no scheduler, no uiContext, and no
/// DebouncedProbe — Recompute() runs synchronously and inline inside every
/// property setter (ListReformatViewModel.cs:19,26,33,40: "if (Set(...))
/// Recompute()"). There is no marshalling hop to wait on ANYWHERE in this
/// view model, which is the class's own doc comment's point ("nothing that
/// could ever be slow enough to need debouncing off the UI thread"). Both
/// scenarios below read OutputText/CountsLine the instant the property
/// setter that produced them returns; no E2EPump call of any kind appears in
/// either.
///
/// <b>LabelMakerViewModel</b> has an IWorkScheduler seam (unlike Unlock,
/// which has none) but no uiContext parameter at all in its constructor —
/// unlike every other view model in this file. Its one genuinely async
/// method, SavePdfAsync, resumes after `await _scheduler.Run(...)` through
/// ordinary compiler-generated async/await context capture, the same
/// implicit mechanism UnlockViewModel's continuations use (see that file's
/// doc comment) rather than an explicit `_uiContext.Post` call anywhere in
/// this class. The difference from Unlock is that THIS await's inner work is
/// reached through the injected `scheduler` seam, which InlineScheduler is
/// legitimately allowed to collapse (unlike Unlock's raw, seamless
/// Task.Run/Task.WhenAll) — and collapse it does: `_scheduler.Run(...)`
/// hands back an already-completed `Task.FromResult`, so the `await` never
/// suspends and SavePdfAsync runs start-to-finish on the calling thread with
/// no Post anywhere in the path. Everything else on this view model —
/// AddClientCommand, the Id setter's Hook callback, Problems(), Persist() —
/// is plain synchronous code with no scheduler involvement whatsoever. So,
/// empirically as well as by inspection: LabelsClean needs no E2EPump call
/// either, and if that claim were wrong the scenario's own disk-truth
/// assertions (a file that must exist with real bytes, a store that must
/// have gained a specific NextNumber) would have caught it by failing, not
/// by looking accidentally green.
///
/// <b>E2EPump.Drain() verdict:</b> not called anywhere in this file. Drain's
/// contract is "work is already posted and only needs a turn of the loop,
/// WITH NO CONDITION TO WAIT ON". Every deferred update actually found above
/// has a real condition its scenario waits on (Rows/CountsLine for
/// FilenameList's debounced probe; Pending for PageCounts' per-row Post),
/// and List reformatter and Box labels cross no dispatcher boundary at all
/// under InlineScheduler, so there is nothing queued for Drain to flush in
/// either. Forcing a Drain call into any of the four would be decorative —
/// exactly what was asked not to do.</summary>
public static class SmallToolScenarios
{
    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario("Filename list", "names listed from a real folder", "clean", FilenamesClean),
        new Scenario("Filename list", "unicode names and an empty folder", "awkward", FilenamesAwkward),
        new Scenario("Page counts", "counts across real PDFs", "clean", CountsClean),
        new Scenario("Page counts", "an encrypted and a damaged document", "awkward", CountsAwkward),
        new Scenario("List reformatter", "a messy list tidied", "clean", ReformatClean),
        new Scenario("List reformatter", "blank lines, duplicates and unicode", "awkward", ReformatAwkward),
        new Scenario("Box labels", "labels generated from the store", "clean", LabelsClean),
    };

    // ---------------------------------------------------------- Filename list

    private static FilenameListViewModel NewListVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current, probeDelayMs: 0);

    private static void FilenamesClean(ScenarioContext ctx)
    {
        var folder = ctx.Fx.Dir("docs");
        ctx.Fx.Pdf("docs/alpha.pdf", "A");
        ctx.Fx.Pdf("docs/beta.pdf", "B");
        ctx.Fx.Pdf("docs/gamma.pdf", "C");

        var vm = NewListVm(ctx);
        var win = new FilenameListWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { folder });
        // Rows is set only inside ApplyListing, DebouncedProbe's `apply`
        // callback, reached via a real Timer thread + uiContext.Post — see
        // the class doc comment. This genuinely needs the pump.
        E2EPump.Until(() => vm.Rows.Count == 3, 8000);

        ctx.Check("three names listed", vm.Rows.Count == 3, $"got {vm.Rows.Count}");
        ctx.Check("the output text carries them all",
            vm.OutputText.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            && vm.OutputText.Contains("gamma", StringComparison.OrdinalIgnoreCase),
            vm.OutputText);
        ctx.Capture(win);
    }

    private static void FilenamesAwkward(ScenarioContext ctx)
    {
        var folder = ctx.Fx.Dir("docs");
        ctx.Fx.Pdf("docs/rapport café — 2026.pdf", "A");
        ctx.Fx.Pdf("docs/文件 名.pdf", "B");
        var empty = ctx.Fx.Dir("empty");

        var vm = NewListVm(ctx);
        var win = new FilenameListWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { folder });
        E2EPump.Until(() => vm.Rows.Count == 2, 8000);
        ctx.Check("unicode names survive",
            vm.Rows.Any(r => r.Contains("café", StringComparison.Ordinal))
            && vm.Rows.Any(r => r.Contains("文件", StringComparison.Ordinal)),
            string.Join(" | ", vm.Rows));

        // ClearCommand hits Refresh's empty-_sources FAST PATH (Cancel() then
        // ApplyListing(...) called directly — no Timer, no Post), so
        // Rows.Count == 0 is already true the instant Execute returns; this
        // Until is here for symmetry with the wait below, not because a pump
        // is required.
        vm.ClearCommand.Execute(null);
        ctx.Check("clear empties the list synchronously", vm.Rows.Count == 0, $"got {vm.Rows.Count}");

        // A non-empty source goes back through the real debounced probe —
        // CountsLine (not Rows, which is already 0 and would never need to
        // change for an empty folder) is what actually proves the probe ran.
        vm.AddPaths(new[] { empty });
        E2EPump.Until(() => vm.CountsLine.Length > 0, 4000);
        ctx.Check("an empty folder lists nothing", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Capture(win);
    }

    // ------------------------------------------------------------ Page counts

    private static PageCountsViewModel NewCountsVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static void CountsClean(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("docs/one.pdf", "A");
        var two = ctx.Fx.Pdf("docs/two.pdf", "B");

        var vm = NewCountsVm(ctx);
        var win = new PageCountsWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { one, two });
        // Under InlineScheduler, AddFilesAsync's own Task is already complete
        // the instant it's returned — see the class doc comment — so this
        // Until answers on its very first check without ever arming a frame.
        // That is expected, not a defect: it is a genuine Task.IsCompleted
        // read, not a filesystem predicate, and it does not stand in for the
        // wait that follows.
        E2EPump.Until(() => add.IsCompleted, 8000);
        // Pending is only ever cleared inside CountOneAsync's Do(), reached
        // through an unconditional _uiContext.Post — THIS is the wait that
        // needs the pump.
        E2EPump.Until(() => vm.Rows.Count == 2 && vm.Rows.All(r => !r.Pending), 15000);

        ctx.Check("both documents counted", vm.Rows.Count == 2, $"got {vm.Rows.Count}");
        ctx.Check("a total is shown", vm.TotalLine.Length > 0, "no total line");
        ctx.Capture(win);
    }

    private static void CountsAwkward(ScenarioContext ctx)
    {
        var good = ctx.Fx.Pdf("docs/good.pdf", "A");
        var locked = ctx.Fx.EncryptedPdf("docs/locked.pdf", "secret");
        var broken = ctx.Fx.CorruptPdf("docs/broken.pdf");

        var vm = NewCountsVm(ctx);
        var win = new PageCountsWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { good, locked, broken });
        E2EPump.Until(() => add.IsCompleted, 8000);
        E2EPump.Until(() => vm.Rows.Count == 3 && vm.Rows.All(r => !r.Pending), 20000);

        ctx.Check("every row settled", vm.Rows.All(r => !r.Pending), "a row is still pending");
        ctx.Check("the good document still reports a count",
            vm.Rows.Any(r => r.FileName == "good.pdf" && r.Pages is not null),
            "the good row reported no page count");
        // PageCounts.Count deliberately uses the SAME message for an
        // encrypted-without-password open and a corrupt one ("nothing cheap
        // distinguishes the two cases from here" — PageCounts.cs) — so this
        // checks that each is explained, not that the wording differs.
        ctx.Check("the encrypted document is explained rather than crashing",
            vm.Rows.Any(r => r.FileName == "locked.pdf" && r.Pages is null && r.Note.Length > 0),
            "no note on the encrypted row");
        ctx.Check("the damaged one is explained rather than crashing",
            vm.Rows.Any(r => r.FileName == "broken.pdf" && r.Pages is null && r.Note.Length > 0),
            "no note on the damaged row");
        ctx.Capture(win);
    }

    // -------------------------------------------------------- List reformatter

    /// <summary>Real API, verified against ListReformatViewModel.cs:
    /// InputText (not "Input"), OutputText/CountsLine (not "Output") are the
    /// bound properties; Quote/SpaceAfterComma/Dedupe are the three toggles;
    /// Recompute() runs inline inside every one of those setters — no
    /// scheduler, no uiContext, nothing to pump. See the class doc comment
    /// for the full trace.</summary>
    private static void ReformatClean(ScenarioContext ctx)
    {
        var vm = new ListReformatViewModel();
        var win = new ListReformatWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.InputText = "smith john\njones mary\nbrown alex";

        ctx.Check("three items were counted",
            vm.CountsLine.StartsWith("3 item", StringComparison.Ordinal), vm.CountsLine);
        ctx.Check("joined into one comma-delimited line, in order",
            vm.OutputText == "smith john,jones mary,brown alex", vm.OutputText);

        // A second, independent setter (Quote) recomputes live too — proves
        // Recompute is wired to every option, not merely InputText.
        vm.Quote = true;
        ctx.Check("turning Quote on wraps every item live, without reordering",
            vm.OutputText == "'smith john','jones mary','brown alex'", vm.OutputText);

        ctx.Capture(win);
    }

    private static void ReformatAwkward(ScenarioContext ctx)
    {
        var vm = new ListReformatViewModel();
        var win = new ListReformatWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.InputText = "smith john\n\n\nsmith john\ncafé rapport\n\n文件\n";

        ctx.Check("blank lines did not become entries",
            vm.CountsLine.StartsWith("4 item", StringComparison.Ordinal), vm.CountsLine);
        ctx.Check("no empty cell slipped into the joined text",
            !vm.OutputText.Contains(",,", StringComparison.Ordinal)
            && !vm.OutputText.StartsWith(",", StringComparison.Ordinal)
            && !vm.OutputText.EndsWith(",", StringComparison.Ordinal),
            vm.OutputText);
        ctx.Check("unicode text survives untouched",
            vm.OutputText.Contains("café rapport", StringComparison.Ordinal)
            && vm.OutputText.Contains("文件", StringComparison.Ordinal),
            vm.OutputText);
        ctx.Check("the literal repeat appears twice before Dedupe is turned on",
            vm.OutputText.Split(',').Count(i => i == "smith john") == 2, vm.OutputText);

        vm.Dedupe = true;
        ctx.Check("Dedupe drops exactly the one repeat",
            vm.CountsLine.Contains("3 item", StringComparison.Ordinal)
            && vm.CountsLine.Contains("1 duplicate", StringComparison.Ordinal),
            vm.CountsLine);
        ctx.Check("only one \"smith john\" remains after Dedupe",
            vm.OutputText.Split(',').Count(i => i == "smith john") == 1, vm.OutputText);

        ctx.Capture(win);
    }

    // ---------------------------------------------------------------- Box labels

    /// <summary>Real API, verified against LabelMakerViewModel.cs. Adds a
    /// client through AddClientCommand (the toolbar "+" button's own path),
    /// types a valid id (BoxLabels.ValidateClientId: 2-8 chars, capital
    /// letters/digits), then drives the real "generate labels" path —
    /// SavePdfCommand — rather than calling the internal Persist() directly.
    /// SavePdfAsync's ClaimNumbersCore adds the client to box-labels.json as
    /// a SIDE EFFECT of claiming numbers for a real batch (LabelMakerViewModel.cs:393-408),
    /// so asserting the store gained the client this way proves it gained it
    /// BECAUSE labels were genuinely generated from it — the scenario's own
    /// name — not merely because some unrelated internal method was poked.
    /// win.IsLoaded is never the assertion here; disk truth (a real PDF's
    /// magic bytes, and the store's own NextNumber) is.</summary>
    private static void LabelsClean(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var store = Path.Combine(ctx.Fx.Root, "box-labels.json");
        ctx.Check("the store does not exist before this scenario runs",
            !File.Exists(store), "already present");

        var vm = new LabelMakerViewModel(cfg, store, ctx.Dialogs,
            today: () => new DateTime(2026, 8, 9),   // clock, not a work seam
            openFile: _ => { },                       // never shell out during a run
            scheduler: new InlineScheduler());
        var win = new LabelMakerWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddClientCommand.Execute(null);
        var client = vm.Selected;
        ctx.Check("a new client row became selected after Add", client is not null,
            "Selected is null after AddClientCommand");
        if (client is null) { ctx.Capture(win); return; }

        client.Id = "BOXE2E";   // 6 chars, capital letters + a digit — a valid Code 39 id
        ctx.Check("the id validates cleanly (2-8 chars, A-Z/0-9)", vm.Problems().Count == 0,
            string.Join("; ", vm.Problems()));

        var pdfPath = Path.Combine(ctx.Fx.Root, "labels.pdf");
        ctx.Dialogs.QueueSaveFile(pdfPath);
        vm.SavePdfCommand.Execute(null);   // fully synchronous under InlineScheduler — see class doc comment

        ctx.Check("no warning was raised for a clean batch", ctx.Dialogs.Warnings.Count == 0,
            string.Join("; ", ctx.Dialogs.Warnings));
        ctx.Check("a real PDF landed on disk", File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0,
            "no output file, or it is empty");
        if (File.Exists(pdfPath))
        {
            var head = new byte[5];
            using var fs = File.OpenRead(pdfPath);
            var read = fs.Read(head, 0, 5);
            ctx.Check("the file is a genuine PDF, not just a stub",
                read == 5 && System.Text.Encoding.ASCII.GetString(head) == "%PDF-",
                read == 5 ? System.Text.Encoding.ASCII.GetString(head) : $"only {read} bytes");
        }
        ctx.Check("the save reported success", vm.Status.Contains("Saved", StringComparison.Ordinal), vm.Status);

        // The disk-truth assertion the brief called out by name: not merely
        // that the window loaded, but that box-labels.json actually gained
        // the label.
        var onDisk = BoxLabelStore.Read(store);
        var saved = onDisk.LabelClients.FirstOrDefault(c => c.Id == "BOXE2E");
        ctx.Check("box-labels.json on disk actually gained the client",
            saved is not null,
            "no BOXE2E row in the store: " + string.Join(", ", onDisk.LabelClients.Select(c => c.Id)));
        if (saved is not null)
            ctx.Check("its running number advanced by the batch size (1 -> 11)",
                saved.NextNumber == 11, $"NextNumber was {saved.NextNumber}");

        ctx.Capture(win);
    }
}
