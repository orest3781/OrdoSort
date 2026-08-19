using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>"Merge PDFs from zip" as the real ZipToolsWindow's Merge PDFs
/// tab, with the `merger` seam left at its default so ZipMerge.MergeZip
/// really opens the archives and really writes a merged document.</summary>
public static class ZipMergeScenarios
{
    private const string Surface = "Zip merge";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "three PDFs merge into one", "clean", ThreePdfs),
        new Scenario(Surface, "archive holds no PDFs", "awkward", NoPdfs),
        new Scenario(Surface, "archive mixes PDFs with other files", "awkward", MixedContent),
        new Scenario(Surface, "an encrypted PDF inside", "awkward", EncryptedInside),
        new Scenario(Surface, "one bad archive among good ones", "awkward", BatchWithOneBad),
    };

    private static ZipToolsViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, SynchronizationContext.Current, new InlineScheduler());

    /// <summary>Add every zip, run the merge, and wait for every row to leave
    /// Pending.
    ///
    /// Waits on each row's own StatusKind, never on vm.Status — and that
    /// choice is load-bearing, not cosmetic, the same trap UnzipScenarios.cs
    /// documents for the other tab. In ZipListViewModel.RunBatchAsync, Status
    /// is assigned DIRECTLY inside the loop body ("Merging N of M…", then the
    /// final `Status = string.Join(...)` verdict line) — no UiContext hop
    /// anywhere in that assignment. ZipItemRow's StatusKind/Note/Output, by
    /// contrast, are only ever set from inside ZipItemRow.Apply, and
    /// ZipListViewModel.ApplyOnUi only ever calls Apply from inside
    /// `UiContext.Post(_ => apply(row, result), null)`.
    ///
    /// Under InlineScheduler every `Scheduler.Run(...)` awaits an
    /// already-completed Task, so `MergeCommand.Execute(null)` — an async
    /// void method whose body runs synchronously up to its first real
    /// suspension point — runs MergeAsync's ENTIRE for-loop to completion,
    /// including that final Status assignment, before returning control
    /// here. vm.Status would therefore already read the finished verdict
    /// line on E2EPump.Until's very first (pre-pump) check — exactly the
    /// "predicate that's already true" trap ScenarioKit.Settle's own doc
    /// comment warns about, except the trap here is a VIEW MODEL property,
    /// not the filesystem. Waiting on every row's StatusKind instead
    /// genuinely blocks until the dispatcher actually runs each queued
    /// ApplyOnUi Post, which is the one thing that proves the marshalling
    /// hop was exercised rather than just the underlying merge.
    ///
    /// This does not call ScenarioKit.Settle directly — Settle's signature
    /// waits on ONE status string, and BatchWithOneBad drives three rows at
    /// once — but it keeps Settle's contract: wait on a hop-guarded surface
    /// property, and record that the wait succeeded as its own assertion.
    ///
    /// Index 1 is the Merge PDFs tab: a TabControl realizes only the selected
    /// tab's content, so the tab has to be current before anything reads or
    /// photographs the grid.</summary>
    private static ZipToolsWindow Merge(ScenarioContext ctx, ZipToolsViewModel tools, params string[] zips)
    {
        var win = new ZipToolsWindow(tools);
        E2EPump.ShowOffscreen(win);
        win.Tabs.SelectedIndex = 1;
        win.UpdateLayout();

        var vm = tools.MergePdfs;
        // AddPaths' one await is `Scheduler.Run(...)`, which InlineScheduler
        // completes synchronously, so Rows is already populated by the time
        // this call returns — the row-count check below is the real assertion
        // that intake worked; see ScenarioKit's class doc comment for why an
        // Added(ctx, ...) wrapper here could never have failed.
        _ = vm.AddPaths(zips);
        ctx.Check("every archive is listed", vm.Rows.Count == zips.Length,
            $"got {vm.Rows.Count} of {zips.Length}");

        vm.MergeCommand.Execute(null);

        var settled = E2EPump.Until(
            () => vm.Rows.Count == zips.Length
                && vm.Rows.All(r => r.StatusKind != ZipItemRowStatus.Pending),
            20000);
        ctx.Check("every row reported a result", settled,
            "rows: " + string.Join(", ", vm.Rows.Select(r => $"{r.Display}:{r.StatusKind}")));

        return win;
    }

    /// <summary>Read a page count back from a merged output and assert it
    /// succeeded before trusting the number — PageCounts.Count never throws,
    /// it comes back as a CountResult with a nullable Pages and an Error
    /// string, so reading Pages off a failed count would just compare 3
    /// against null and print a confusing "got " with nothing after it.</summary>
    private static void AssertPageCount(ScenarioContext ctx, string output, int expected, string description)
    {
        var counted = PageCounts.Count(output);
        ctx.Check($"page count succeeded for {System.IO.Path.GetFileName(output)}",
            counted.Pages is not null, $"couldn't count: {counted.Error}");
        ctx.Check(description, counted.Pages == expected, $"got {counted.Pages}");
    }

    private static void ThreePdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "PAGE TWO");
        var c = ctx.Fx.Pdf("src/c.pdf", "PAGE THREE");
        var zip = ctx.Fx.Zip("archives/bundle.zip", ("a.pdf", a), ("b.pdf", b), ("c.pdf", c));

        var tools = NewVm(ctx);
        var win = Merge(ctx, tools, zip);
        var vm = tools.MergePdfs;

        ctx.Check("row reports ok", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        var output = vm.Rows[0].Output;
        ctx.Check("an output path was reported", output is not null, "none");
        if (output is not null)
        {
            ctx.FileExists(output);
            // Three one-page fixtures in, one document out.
            AssertPageCount(ctx, output, 3, "merged document has three pages");
        }
        ctx.Capture(win);
    }

    private static void NoPdfs(ScenarioContext ctx)
    {
        var txt = ctx.Fx.Text("src/readme.txt", "no documents here");
        var zip = ctx.Fx.Zip("archives/empty-of-pdfs.zip", ("readme.txt", txt));

        var tools = NewVm(ctx);
        var win = Merge(ctx, tools, zip);
        var vm = tools.MergePdfs;

        ctx.Check("reported as holding no PDFs", vm.Rows[0].StatusKind == ZipItemRowStatus.NoPdfs,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("nothing was written", vm.Rows[0].Output is null,
            $"wrote {vm.Rows[0].Output}");
        ctx.Capture(win);
    }

    private static void MixedContent(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "PAGE TWO");
        var txt = ctx.Fx.Text("src/notes.txt", "ignore me");
        var zip = ctx.Fx.Zip("archives/mixed.zip",
            ("a.pdf", a), ("notes.txt", txt), ("b.pdf", b));

        var tools = NewVm(ctx);
        var win = Merge(ctx, tools, zip);
        var vm = tools.MergePdfs;

        ctx.Check("merged despite the extra file", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            AssertPageCount(ctx, output, 2, "only the two PDFs contributed pages");
        }
        ctx.Capture(win);
    }

    /// <summary>An encrypted document cannot be merged without its password.
    /// ZipMerge.MergeZip fails the WHOLE zip on any entry PdfSharp can't
    /// open — an encrypted one included — rather than silently dropping just
    /// that entry from the merge (see ZipMerge's own "fail-whole-zip" doc
    /// comment, pinned by
    /// ZipMergeTests.AnEncryptedEntryFailsTheWholeZipAndLeavesNoOutput). So
    /// the row here is expected to come back Error, name the entry it could
    /// not read, and leave no output at all — never a throw, and never a
    /// merged document that quietly has fewer pages than it claims.</summary>
    private static void EncryptedInside(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));

        var tools = NewVm(ctx);
        var win = Merge(ctx, tools, zip);
        var vm = tools.MergePdfs;

        ctx.Check("reported as an error rather than a silent partial success",
            vm.Rows[0].StatusKind == ZipItemRowStatus.Error,
            $"status was {vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("the outcome names the entry that could not be read",
            vm.Rows[0].Note.Contains("locked.pdf", StringComparison.Ordinal),
            $"note was \"{vm.Rows[0].Note}\"");
        ctx.Check("a failed merge writes nothing", vm.Rows[0].Output is null,
            $"wrote {vm.Rows[0].Output}");
        ctx.Capture(win);
    }

    /// <summary>A batch must not be all-or-nothing: one unreadable archive
    /// cannot cost the user the other two.</summary>
    private static void BatchWithOneBad(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "TWO");
        var good1 = ctx.Fx.Zip("archives/good1.zip", ("a.pdf", a), ("b.pdf", b));
        var bad = ctx.Fx.Text("archives/bad.zip", "not a zip");
        var good2 = ctx.Fx.Zip("archives/good2.zip", ("a.pdf", a), ("b.pdf", b));

        var tools = NewVm(ctx);
        var win = Merge(ctx, tools, good1, bad, good2);
        var vm = tools.MergePdfs;

        var ok = vm.Rows.Count(r => r.StatusKind == ZipItemRowStatus.Ok);
        var errors = vm.Rows.Count(r => r.StatusKind == ZipItemRowStatus.Error);
        ctx.Check("both good archives merged", ok == 2, $"got {ok}");
        ctx.Check("the bad one is reported as an error", errors == 1, $"got {errors}");
        ctx.Capture(win);
    }
}
