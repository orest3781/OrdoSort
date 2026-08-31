using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        new Scenario(Surface, "an encrypted PDF inside, password supplied", "clean", EncryptedInsideWithPassword),
        new Scenario(Surface, "loose PDFs merge into one", "clean", LoosePdfs),
        new Scenario(Surface, "a locked loose PDF is skipped", "awkward", LockedLooseSkipped),
        new Scenario(Surface, "a spreadsheet merges with the PDFs", "clean", SheetWithPdfs),
        new Scenario(Surface, "an image merges with the PDFs", "clean", ImageWithPdfs),
        new Scenario(Surface, "a type switched off is listed but not merged", "awkward", TypeSwitchedOff),
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

    /// <summary>A tiny PNG, written under the fixture's "src" folder — the
    /// same encode-in-process approach ImageToPdfTests.Png uses, so this
    /// scenario carries no binary test asset. Fixture itself has no image
    /// builder (only Pdf/Zip/Text/etc.), so this writes directly under
    /// ctx.Fx.Dir("src") rather than growing Fixture's own surface for one
    /// caller.</summary>
    private static string WritePng(Fixture fx, string fileName, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)200);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        var path = Path.Combine(fx.Dir("src"), fileName);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
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
        // .bin, not .txt: Task 8 wired TextToPdf into the default
        // converter, so a stray .txt is no longer something this window
        // can't merge — it would turn this into an ordinary one-page
        // merge instead of the "nothing to merge" case this scenario
        // exists to prove. An extension no MergeTypes group recognizes at
        // all is what that now takes.
        var stray = ctx.Fx.Text("src/readme.bin", "no documents here");
        var zip = ctx.Fx.Zip("archives/empty-of-pdfs.zip", ("readme.bin", stray));

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
        // .bin, not .txt — see NoPdfs' comment: Task 8 made .txt a type
        // this window merges (via TextToPdf), so it can no longer stand in
        // for clutter nothing here recognizes. SheetWithPdfs/ImageWithPdfs
        // below are what now prove a convertible stray file DOES take
        // part; this scenario's own job is the sibling claim — clutter
        // NOTHING recognizes is skipped, not merged.
        var other = ctx.Fx.Text("src/notes.bin", "ignore me");
        var zip = ctx.Fx.Zip("archives/mixed.zip",
            ("a.pdf", a), ("notes.bin", other), ("b.pdf", b));

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

    /// <summary>EncryptedInside's sibling: the same archive, the password
    /// queued. The prompt is reached once, the answer opens the entry, and
    /// both documents contribute a page.</summary>
    private static void EncryptedInsideWithPassword(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));
        ctx.Dialogs.QueuePassword("secret");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("the prompt was reached exactly once", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("merged", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            AssertPageCount(ctx, output, 2, "both documents contributed a page");
        }
        ctx.Capture(win);
    }

    /// <summary>Three loose documents, one output: named after their folder
    /// and placed beside the first, with every row pointing at it.</summary>
    private static void LoosePdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "TWO");
        var c = ctx.Fx.Pdf("src/c.pdf", "THREE");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, a, b, c);

        ctx.Check("every row reports the one document", vm.Rows.All(r => r.StatusKind == ZipItemRowStatus.Ok),
            "rows: " + string.Join(", ", vm.Rows.Select(r => $"{r.Display}:{r.StatusKind}")));
        var expected = Path.Combine(ctx.Fx.Root, "src", "src.pdf");
        ctx.FileExists(expected);
        ctx.Check("every row points at it",
            vm.Rows.All(r => string.Equals(r.Output, expected, StringComparison.OrdinalIgnoreCase)),
            "outputs: " + string.Join(", ", vm.Rows.Select(r => r.Output)));
        AssertPageCount(ctx, expected, 3, "three one-page documents in, three pages out");
        ctx.Capture(win);
    }

    /// <summary>Fail-whole for the loose group, end to end: one locked
    /// document, the prompt skipped, and nothing merges — the locked row
    /// waits for a password, the plain one says what held it back, and both
    /// are still runnable.</summary>
    private static void LockedLooseSkipped(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, a, locked);

        var lockedRow = vm.Rows.Single(r => r.Path == locked);
        var plainRow = vm.Rows.Single(r => r.Path == a);
        ctx.Check("the prompt was reached", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("the locked one is waiting for a password",
            lockedRow.StatusKind == ZipItemRowStatus.NeedsPassword, $"{lockedRow.StatusKind} — {lockedRow.Note}");
        ctx.Check("the plain one was held back, and says why",
            plainRow.StatusKind == ZipItemRowStatus.Pending && plainRow.Note == "not merged — locked.pdf needs a password",
            $"{plainRow.StatusKind} — \"{plainRow.Note}\"");
        ctx.Check("nothing was written", !File.Exists(Path.Combine(ctx.Fx.Root, "src", "src.pdf")),
            "a merged document appeared");
        ctx.Check("both rows are still runnable", vm.MergeButtonText == "Merge 2 items", vm.MergeButtonText);
        ctx.Capture(win);
    }

    /// <summary>A zip holding a PDF and a CSV: Task 8's conversion wiring,
    /// through the ZIP path this time (SheetWithPdfs' sibling ImageWithPdfs
    /// below covers the loose path). CSV rather than XLSX deliberately —
    /// OfficeConverter claims ".xlsx" outright when Excel is installed (it
    /// only excludes csv/tsv from its own Handles, see ConverterChain's own
    /// doc comment), so an XLSX fixture would run through real Excel on any
    /// machine that has it and stop being a smoke test. CSV always falls
    /// through to TableToPdf, on every machine, which is the deterministic,
    /// Office-free path this suite needs.</summary>
    private static void SheetWithPdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var sheet = ctx.Fx.Text("src/sheet.csv", "Name,Amount\r\nWidget,12\r\nGadget,7\r\n");
        var zip = ctx.Fx.Zip("archives/withsheet.zip", ("a.pdf", a), ("sheet.csv", sheet));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("merged despite one entry needing conversion", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            // The PDF contributes one page, the CSV's own small table
            // another — proof the entry went through TableToPdf rather than
            // being dropped as clutter the way MixedContent's stray .txt is.
            AssertPageCount(ctx, output, 2, "the PDF and the converted spreadsheet both contributed a page");
        }
        ctx.Capture(win);
    }

    /// <summary>A loose PDF and a loose photo: the LOOSE path's conversion
    /// wiring, through ImageToPdf — the one converter that is always
    /// Office-free by construction (an image is never an Office document),
    /// so this needs no CSV-style workaround to stay deterministic.</summary>
    private static void ImageWithPdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var photo = WritePng(ctx.Fx, "photo.png", 200, 150);

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, a, photo);

        ctx.Check("every row reports the one document", vm.Rows.All(r => r.StatusKind == ZipItemRowStatus.Ok),
            "rows: " + string.Join(", ", vm.Rows.Select(r => $"{r.Display}:{r.StatusKind}")));
        var expected = Path.Combine(ctx.Fx.Root, "src", "src.pdf");
        ctx.FileExists(expected);
        AssertPageCount(ctx, expected, 2, "the PDF and the converted image both contributed a page");
        ctx.Capture(win);
    }

    /// <summary>Task 7's toggle row end to end: a PDF and a text file are
    /// both listed while every type is on, Text is switched off with both
    /// rows already in the list, and the text row is excluded LIVE — still
    /// shown, its note masked, never joining the run — while the PDF merges
    /// on its own. Text (not Word/Excel/PowerPoint) is the deterministic
    /// choice here for the same reason SheetWithPdfs picks CSV over XLSX:
    /// TextToPdf handles it on every machine, so nothing about this
    /// scenario's outcome depends on what's installed.</summary>
    private static void TypeSwitchedOff(ScenarioContext ctx)
    {
        var pdf = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var notes = ctx.Fx.Text("src/notes.txt", "left out on purpose");

        var vm = NewVm(ctx);
        var win = new MergePdfsWindow(vm);
        E2EPump.ShowOffscreen(win);

        _ = vm.AddPaths(new[] { pdf, notes });
        ctx.Check("both sources are listed", vm.Rows.Count == 2, $"got {vm.Rows.Count}");

        var textRow = vm.Rows.Single(r => r.Path == notes);
        ctx.Check("the text file starts included", textRow.IsIncluded, "it began excluded");

        vm.SetTypeEnabled(MergeTypes.Text, false);
        ctx.Check("switching Text off excludes the row, live", !textRow.IsIncluded, "it stayed included");
        ctx.Check("its note explains why, rather than staying blank",
            textRow.Note == "not included — this file type is switched off", $"note was \"{textRow.Note}\"");
        ctx.Check("the button counts only the PDF", vm.MergeButtonText == "Merge 1 item", vm.MergeButtonText);

        vm.MergeCommand.Execute(null);
        ctx.Check("the window applied every result", Drained(), "the dispatcher queue never drained");

        var pdfRow = vm.Rows.Single(r => r.Path == pdf);
        ctx.Check("the PDF merged on its own", pdfRow.StatusKind == ZipItemRowStatus.Ok,
            $"{pdfRow.StatusKind} — {pdfRow.Note}");
        ctx.Check("the switched-off row never ran", textRow.StatusKind == ZipItemRowStatus.Pending,
            $"{textRow.StatusKind} — {textRow.Note}");
        if (pdfRow.Output is { } output)
        {
            ctx.FileExists(output);
            AssertPageCount(ctx, output, 1, "only the PDF contributed a page");
        }
        ctx.Capture(win);
    }
}
