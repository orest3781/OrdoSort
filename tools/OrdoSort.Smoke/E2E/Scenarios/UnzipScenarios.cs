using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The Unzip tool as the real ZipToolsWindow's Zip &amp; unzip tab.
/// The `extractor` seam is left at its default throughout, so Zipper.Extract
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
    };

    private static ZipToolsViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, SynchronizationContext.Current, new InlineScheduler());

    /// <summary>Opens the real window on the tab this surface drives. A
    /// TabControl realizes only the selected tab's content, so the tab has to
    /// be current before anything reads or photographs the grid — index 0 is
    /// Zip &amp; unzip, which is where extraction lives.</summary>
    private static ZipToolsWindow Open(ZipToolsViewModel vm)
    {
        var win = new ZipToolsWindow(vm);
        E2EPump.ShowOffscreen(win);
        win.Tabs.SelectedIndex = 0;
        win.UpdateLayout();
        return win;
    }

    /// <summary>Drive one archive through the window and wait for its row to
    /// leave Pending.
    ///
    /// Waits on Rows[0].Note, not vm.Status, and that choice is load-bearing
    /// rather than cosmetic. With InlineScheduler every await inside
    /// ExtractAsync completes synchronously (Task.FromResult is already
    /// IsCompleted), so ExtractCommand.Execute(null) runs the WHOLE method —
    /// including RunBatchAsync's final `Status = string.Join(...)` line — to
    /// completion before returning control here. vm.Status would therefore
    /// already be non-empty on E2EPump.Until's very first (pre-pump) check,
    /// exactly the "filesystem predicate that's already true" trap
    /// ScenarioKit.Settle's own doc comment warns about — except here the
    /// trap is a VIEW MODEL property, not the filesystem. ZipItemRow.Apply,
    /// by contrast, is only ever invoked from inside
    /// ZipListViewModel.ApplyOnUi's `UiContext.Post(...)`, which
    /// DispatcherSynchronizationContext always queues via BeginInvoke even
    /// when called from its own thread — so Note genuinely does not flip
    /// until something pumps. (The Zip button's own verdict doesn't have this
    /// split: ZipAsync assigns Status inside a RunOnUi closure, so
    /// Settle(ctx, () => vm.Status) in ZipScenarios is safe as written; the
    /// shared batch runner separates the aggregate status line from the
    /// per-row Apply, and only the latter is where the marshalling hop
    /// actually lives.)</summary>
    private static ZipToolsWindow Extract(ScenarioContext ctx, ZipToolsViewModel tools, string zip)
    {
        var win = Open(tools);
        var vm = tools.ZipExtract;
        // AddPaths' one await is `_scheduler.Run(...)`, which InlineScheduler
        // completes synchronously, so Rows is already populated by the time
        // this call returns — the row-count check below is the real assertion
        // that intake worked; see ScenarioKit's class doc comment for why an
        // Added(ctx, ...) wrapper here could never have failed.
        _ = vm.AddPaths(new[] { zip });
        ctx.Check("the archive is listed", vm.Rows.Count == 1, $"got {vm.Rows.Count}");
        vm.ExtractCommand.Execute(null);
        Settle(ctx, () => vm.Rows.Count > 0 ? vm.Rows[0].Note : "");
        return win;
    }

    private static void NestedFolders(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var two = ctx.Fx.Pdf("src/two.pdf", "BETA");
        var zip = ctx.Fx.Zip("archives/bundle.zip",
            ("one.pdf", one), (@"nested\deeper\two.pdf", two));

        var tools = NewVm(ctx);
        var win = Extract(ctx, tools, zip);

        var outDir = Path.Combine(ctx.Fx.Root, "archives", "bundle");
        ctx.Check("output folder created", Directory.Exists(outDir), $"expected {outDir}");
        ctx.FileExists(Path.Combine(outDir, "one.pdf"));
        ctx.FileExists(Path.Combine(outDir, "nested", "deeper", "two.pdf"));
        ctx.Check("the archive itself is left alone", File.Exists(zip), "the zip was consumed");
        ctx.Capture(win);
    }

    /// <summary>An entry named ..\..\escaped.txt must not land outside the
    /// output folder. ZipFile.ExtractToDirectory throws IOException for this,
    /// which ExtractCore turns into an error result and — because it created
    /// the folder on this call — cleans the folder up.</summary>
    private static void ZipSlip(ScenarioContext ctx)
    {
        var archives = ctx.Fx.Dir("archives");
        var zip = ctx.Fx.RawZip("archives/evil.zip",
            (@"..\..\escaped.txt", new byte[] { 66, 65, 68 }));

        var outDir = Path.Combine(archives, "evil");
        var before = ctx.Snapshot();   // take it BEFORE the extract

        var tools = NewVm(ctx);
        var win = Extract(ctx, tools, zip);
        var vm = tools.ZipExtract;

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

        var tools = NewVm(ctx);
        var win = Extract(ctx, tools, zip);
        var vm = tools.ZipExtract;

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

        var tools = NewVm(ctx);
        var win = Extract(ctx, tools, zip);
        var vm = tools.ZipExtract;

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

        var tools = NewVm(ctx);
        var win = Extract(ctx, tools, zip);
        var vm = tools.ZipExtract;

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
}
