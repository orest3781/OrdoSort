using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The Unzip tool, driven as the real ZipToolsWindow. The
/// `extractor` seam is left at its default throughout, so Zipper.Extract
/// really runs — including its ZipSlip guard and its created-gate cleanup,
/// neither of which a fake extractor could demonstrate.</summary>
public static class UnzipScenarios
{
    private const string Surface = "Unzip";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "nested folders extract intact", "clean", NestedFolders),
        new Scenario(Surface, "path traversal is refused", "awkward", ZipSlip),
        new Scenario(Surface, "corrupt archive", "awkward", CorruptArchive),
        new Scenario(Surface, "output folder already exists", "awkward", TargetExists),
        new Scenario(Surface, "empty archive", "awkward", EmptyArchive),
        new Scenario(Surface, "password-protected archive", "clean", LockedArchive),
        new Scenario(Surface, "password-protected archive, prompt skipped", "awkward", LockedArchiveSkipped),
    };

    private static ZipExtractViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, Array.Empty<string>(), new InlineScheduler(), SynchronizationContext.Current);

    /// <summary>Opens the real window: one list, no tab to select.</summary>
    private static ZipToolsWindow Open(ZipExtractViewModel vm)
    {
        var win = new ZipToolsWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    /// <summary>Drive one archive through the window and wait for every
    /// result the run posted to land — see ScenarioKit.Drained for why the
    /// old wait on Rows[0].Note settles too early now that the probe on add
    /// fills the note before the run.</summary>
    private static ZipToolsWindow Extract(ScenarioContext ctx, ZipExtractViewModel vm, string zip)
    {
        var win = Open(vm);
        _ = vm.AddPaths(new[] { zip });   // synchronous under InlineScheduler — see ZipScenarios
        ctx.Check("the archive is listed", vm.Rows.Count == 1, $"got {vm.Rows.Count}");
        vm.ExtractCommand.Execute(null);
        ctx.Check("the window applied every result", Drained(), "the dispatcher queue never drained");
        return win;
    }

    private static void NestedFolders(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var two = ctx.Fx.Pdf("src/two.pdf", "BETA");
        var zip = ctx.Fx.Zip("archives/bundle.zip",
            ("one.pdf", one), (@"nested\deeper\two.pdf", two));

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        var outDir = Path.Combine(ctx.Fx.Root, "archives", "bundle");
        ctx.Check("output folder created", Directory.Exists(outDir), $"expected {outDir}");
        ctx.FileExists(Path.Combine(outDir, "one.pdf"));
        ctx.FileExists(Path.Combine(outDir, "nested", "deeper", "two.pdf"));
        ctx.Check("the archive itself is left alone", File.Exists(zip), "the zip was consumed");
        ctx.Capture(win);
    }

    /// <summary>An entry named ..\..\escaped.txt must not land outside the
    /// output folder. Zipper's own path guard refuses it (the SharpZipLib
    /// move, 2026-08-28), which ExtractCore turns into an error result and —
    /// because it created the folder on this call — cleans the folder up.</summary>
    private static void ZipSlip(ScenarioContext ctx)
    {
        var archives = ctx.Fx.Dir("archives");
        var zip = ctx.Fx.RawZip("archives/evil.zip",
            (@"..\..\escaped.txt", new byte[] { 66, 65, 68 }));

        var outDir = Path.Combine(archives, "evil");
        var before = ctx.Snapshot();   // take it BEFORE the extract

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("extraction refused", vm.Rows[0].StatusKind != ZipItemRowStatus.Ok,
            $"status was {vm.Rows[0].StatusKind}");

        // Not "the file I predicted is absent" — nothing new appeared
        // anywhere outside the output folder, so an entry escaping to
        // somewhere unanticipated is caught just the same.
        ctx.NothingNewOutside(outDir, before, "nothing escaped the output folder");
        ctx.FileMissing(Path.Combine(ctx.Fx.Root, "escaped.txt"));

        ctx.Check("no orphaned output folder", !Directory.Exists(outDir),
            "the created-gate cleanup left the folder behind");
        ctx.Capture(win);
    }

    private static void CorruptArchive(ScenarioContext ctx)
    {
        var zip = ctx.Fx.Text("archives/broken.zip", "this is not a zip file at all");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("reported as invalid", vm.Rows[0].StatusKind != ZipItemRowStatus.Ok,
            $"status was {vm.Rows[0].StatusKind}");
        ctx.Check("says it is not a valid zip",
            vm.Rows[0].Note.Contains("valid zip", StringComparison.OrdinalIgnoreCase),
            $"note was \"{vm.Rows[0].Note}\"");
        ctx.Check("no output folder left behind",
            !Directory.Exists(Path.Combine(ctx.Fx.Root, "archives", "broken")),
            "an empty folder was orphaned");
        ctx.Capture(win);
    }

    /// <summary>A taken output name counters (Collision.FreeDirectory)
    /// rather than merging into — or emptying — the folder already there.</summary>
    private static void TargetExists(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.Zip("archives/bundle.zip", ("one.pdf", one));

        var squatter = ctx.Fx.Dir("archives", "bundle");
        var squatterFile = Path.Combine(squatter, "i-was-here.txt");
        File.WriteAllText(squatterFile, "existing content");
        var before = File.ReadAllBytes(squatterFile);

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.BytesUnchanged(squatterFile, before, "the folder already there is untouched");
        ctx.Check("extraction still succeeded", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"status was {vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        // Pinned to the exact counter-suffixed name, not just "differs from
        // the squatter" — parity with ZipScenarios.NameTaken, which pins
        // "src (2).zip" rather than merely asserting a second archive exists.
        var expected = Path.Combine(Path.GetDirectoryName(squatter)!, "bundle (2)");
        ctx.Check("output went to the counter-suffixed name",
            vm.Rows[0].Output is not null
            && string.Equals(Path.GetFullPath(vm.Rows[0].Output!),
                Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase),
            $"output was {vm.Rows[0].Output}, expected {expected}");
        ctx.Capture(win);
    }

    private static void EmptyArchive(ScenarioContext ctx)
    {
        var zip = ctx.Fx.EmptyZip("archives/nothing.zip");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        // Not Note.Length > 0 — Note is non-empty on BOTH outcomes ("→
        // folder" on Ok, the error message on Error), so that check would
        // still read as passed if empty-archive extraction ever regressed to
        // an error. Pin StatusKind explicitly, same as every other awkward
        // scenario in this file.
        ctx.Check("extracted without an error", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"status was {vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("did not crash the window", win.IsLoaded, "window went away");
        ctx.Capture(win);
    }

    /// <summary>The prompt, end to end: the archive is AES-encrypted, no
    /// password is saved, so Extract reaches the prompt and ScriptedDialogs
    /// answers it — through the same Send hop the real window uses, because
    /// uiContext here is the live dispatcher context.</summary>
    private static void LockedArchive(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.EncryptedZip("archives/locked.zip", "secret", ("one.pdf", one));
        ctx.Dialogs.QueuePassword("secret");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("the prompt was reached exactly once", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("extracted", vm.Rows[0].StatusKind == ZipItemRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.FileExists(Path.Combine(ctx.Fx.Root, "archives", "locked", "one.pdf"));
        ctx.Capture(win);
    }

    /// <summary>The same archive, nothing queued: the prompt is skipped, the
    /// row waits for a password and is still runnable, and nothing at all
    /// is written.</summary>
    private static void LockedArchiveSkipped(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.EncryptedZip("archives/locked.zip", "secret", ("one.pdf", one));

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("the prompt was reached", ctx.Dialogs.PasswordPrompts == 1,
            $"prompted {ctx.Dialogs.PasswordPrompts} times");
        ctx.Check("the row is waiting for a password, and still runnable",
            vm.Rows[0].StatusKind == ZipItemRowStatus.NeedsPassword && vm.Rows[0].IsRunnable,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("the button still counts it", vm.ExtractButtonText == "Extract 1 zip", vm.ExtractButtonText);
        ctx.Check("nothing was written",
            !Directory.Exists(Path.Combine(ctx.Fx.Root, "archives", "locked")), "an output folder appeared");
        ctx.Capture(win);
    }
}
