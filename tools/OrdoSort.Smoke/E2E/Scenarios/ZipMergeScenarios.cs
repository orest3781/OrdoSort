using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>"Merge PDFs from zip" as the real MergePdfsWindow, with the
/// `merger` seam left at its default so PdfMerge.MergeZip really opens the
/// archives and really writes a merged document.</summary>
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

    private static MergePdfsViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, Array.Empty<string>(), new InlineScheduler(), SynchronizationContext.Current);

    /// <summary>Add every source, run the merge, and wait for every result
    /// the run posted to land (ScenarioKit.Drained — the old wait on "every
    /// row left Pending" can no longer end: fail-whole leaves the rows a
    /// culprit held back Pending on purpose).</summary>
    private static MergePdfsWindow Merge(ScenarioContext ctx, MergePdfsViewModel vm, params string[] sources)
    {
        var win = new MergePdfsWindow(vm);
        E2EPump.ShowOffscreen(win);

        _ = vm.AddPaths(sources);   // synchronous under InlineScheduler — see ZipScenarios
        ctx.Check("every source is listed", vm.Rows.Count == sources.Length,
            $"got {vm.Rows.Count} of {sources.Length}");

        vm.MergeCommand.Execute(null);
        ctx.Check("the window applied every result", Drained(), "the dispatcher queue never drained");
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

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

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

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

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

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("merged despite the extra file", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            AssertPageCount(ctx, output, 2, "only the two PDFs contributed pages");
        }
        ctx.Capture(win);
    }

    /// <summary>An encrypted document inside, and no password anyone knows:
    /// PdfMerge.MergeZip asks (ScriptedDialogs, nothing queued, answers
    /// null — a skip) and fails the WHOLE zip as needs_password, naming the
    /// entry, with no output — fail-whole is unchanged, but the row is
    /// runnable rather than a dead end. Task 10 adds the sibling scenario
    /// where the password is supplied.</summary>
    private static void EncryptedInside(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("reported as needing a password rather than a silent partial success",
            vm.Rows[0].StatusKind == ZipItemRowStatus.NeedsPassword,
            $"status was {vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("the outcome names the entry that could not be read",
            vm.Rows[0].Note.Contains("locked.pdf", StringComparison.Ordinal),
            $"note was \"{vm.Rows[0].Note}\"");
        ctx.Check("a failed merge writes nothing", vm.Rows[0].Output is null,
            $"wrote {vm.Rows[0].Output}");
        ctx.Check("the row is still runnable", vm.Rows[0].IsRunnable, "it was finished");
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

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, good1, bad, good2);

        var ok = vm.Rows.Count(r => r.StatusKind == ZipItemRowStatus.Ok);
        var errors = vm.Rows.Count(r => r.StatusKind == ZipItemRowStatus.Error);
        ctx.Check("both good archives merged", ok == 2, $"got {ok}");
        ctx.Check("the bad one is reported as an error", errors == 1, $"got {errors}");
        ctx.Capture(win);
    }
}
