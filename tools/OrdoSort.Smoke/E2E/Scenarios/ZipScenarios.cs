using System.IO.Compression;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The Zip tool, driven as the real ZipWindow against real files.</summary>
public static class ZipScenarios
{
    private const string Surface = "Zip";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "files and a folder in one archive", "clean", FilesAndFolder),
        new Scenario(Surface, "save-as to an explicit path", "clean", SaveAs),
        new Scenario(Surface, "default output name already taken", "awkward", NameTaken),
        new Scenario(Surface, "save-as onto an existing archive", "awkward", SaveAsOverExisting),
        new Scenario(Surface, "unicode and spaces in names", "awkward", UnicodeNames),
        new Scenario(Surface, "nothing selected", "awkward", EmptySelection),
    };

    /// <summary>Real seams: only dialogs and the scheduler are injected, and
    /// uiContext is the live dispatcher context (E2ERunner installs one on the
    /// STA thread) so results marshal back the way they do in production. The
    /// zipper seam is left at its default on purpose — every archive these
    /// scenarios assert about is written by the real Zipper.CreateZip, which is
    /// the whole difference between this suite and ZipViewModelTests.</summary>
    private static ZipViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static ZipWindow Open(ZipViewModel vm)
    {
        var win = new ZipWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    private static string[] Archives(ScenarioContext ctx) =>
        Directory.GetFiles(ctx.Fx.Root, "*.zip", SearchOption.AllDirectories);

    /// <summary>Wait for the window's own verdict line, then record that it
    /// arrived. Status is what the result text under the Zip button binds to,
    /// and — unlike "the file appeared on disk" — it only becomes non-empty
    /// once ZipViewModel.ApplyResult has posted back through uiContext. Waiting
    /// on it therefore exercises that marshalling hop rather than stepping
    /// around it, which is the difference between driving the window and
    /// driving the filesystem.</summary>
    private static void Settle(ScenarioContext ctx, ZipViewModel vm)
    {
        var settled = E2EPump.Until(
            () => vm.Status.Length > 0 || ctx.Dialogs.Warnings.Count > 0, 15000);
        ctx.Check("the window reported a result", settled,
            "no status line and no warning within 15s");
    }

    private static void CheckCreated(ScenarioContext ctx, ZipViewModel vm, string archiveName) =>
        ctx.Check($"the status line reports {archiveName}",
            vm.Status.StartsWith("Created ", StringComparison.Ordinal)
            && vm.Status.Contains(archiveName, StringComparison.Ordinal),
            $"status was \"{vm.Status}\"");

    /// <summary>Entry names, sorted, and never throwing: an archive that cannot
    /// be opened has to surface as a failed assertion carrying the reason, not
    /// as an exception that ends the scenario — same discipline as
    /// ScenarioContext's own I/O helpers.</summary>
    private static IReadOnlyList<string> EntriesOf(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Select(e => e.FullName)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            return new[] { $"<unreadable: {ex.GetType().Name}: {ex.Message}>" };
        }
    }

    private static void FilesAndFolder(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var b = ctx.Fx.Pdf("src/two.pdf", "BETA");
        var folder = ctx.Fx.Dir("src", "nested");
        ctx.Fx.Pdf("src/nested/three.pdf", "GAMMA");

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a, b, folder });
        E2EPump.Until(() => add.IsCompleted, 8000);
        ctx.Check("three sources listed", vm.Rows.Count == 3, $"got {vm.Rows.Count}");
        ctx.Check("the folder is listed as a folder, not expanded into files",
            vm.Rows.Count(r => r.Kind == "folder") == 1,
            string.Join(", ", vm.Rows.Select(r => $"{r.Display}:{r.Kind}")));

        vm.CreateCommand.Execute(null);
        Settle(ctx, vm);

        var zips = Archives(ctx);
        ctx.Check("exactly one archive written", zips.Length == 1, $"got {zips.Length}");
        if (zips.Length == 1)
        {
            CheckCreated(ctx, vm, Path.GetFileName(zips[0]));
            // Names, not just a count: the folder row has to arrive as
            // "nested/three.pdf" — its own name as a prefix, forward-slashed —
            // and that is the part a count would happily miss.
            var entries = EntriesOf(zips[0]);
            ctx.Check("archive holds all three documents, the folder one under its own prefix",
                entries.SequenceEqual(new[] { "nested/three.pdf", "one.pdf", "two.pdf" }),
                string.Join(", ", entries));
        }
        ctx.Capture(win);
    }

    private static void SaveAs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var target = Path.Combine(ctx.Fx.Dir("out"), "chosen-name.zip");
        ctx.Dialogs.QueueSaveFile(target);

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a });
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.CreateAsCommand.Execute(null);
        Settle(ctx, vm);

        ctx.FileExists(target);
        CheckCreated(ctx, vm, "chosen-name.zip");
        ctx.Check("the chosen name holds the document",
            EntriesOf(target).SequenceEqual(new[] { "one.pdf" }),
            string.Join(", ", EntriesOf(target)));
        ctx.Check("nothing was written anywhere else",
            Archives(ctx).Length == 1, $"got {Archives(ctx).Length} archives");
        ctx.Capture(win);
    }

    /// <summary>The never-overwrite guarantee, driven through the window.
    ///
    /// WHICH button it lives behind matters, and is easy to get backwards. The
    /// plain Zip button passes a null output path, so Zipper.CreateZip picks
    /// the default name beside the first item and runs it through
    /// Collision.FreeFile: a taken name counters to " (2)" and the file already
    /// there is never touched (pinned in ZipperTests.
    /// ADefaultNameCollisionGetsACollisionSuffix). "Zip to…" is deliberately
    /// the opposite — see SaveAsOverExisting below — so it is this button, not
    /// that one, that carries the guarantee worth proving end to end.</summary>
    private static void NameTaken(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");

        // Zipper.DefaultName's own pick for a single loose file is the name of
        // the folder CONTAINING it, placed beside it — <root>\src\src.zip.
        // Occupying exactly that name is what forces the collision; anything
        // else would just be a file the run never looks at.
        var taken = Path.Combine(ctx.Fx.Dir("src"), "src.zip");
        File.WriteAllText(taken, "I was here first");
        var before = File.ReadAllBytes(taken);

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a });
        E2EPump.Until(() => add.IsCompleted, 8000);
        ctx.Check("the source is listed", vm.Rows.Count == 1, $"got {vm.Rows.Count}");

        vm.CreateCommand.Execute(null);
        Settle(ctx, vm);

        ctx.BytesUnchanged(taken, before, "the archive already there is untouched");

        // Deliberately not left at the line above: "the old bytes survived"
        // would also be true if the app had simply refused to do anything at
        // all, so the run has to be shown to have produced a second archive.
        var zips = Archives(ctx);
        var fresh = zips.FirstOrDefault(z => !z.Equals(taken, StringComparison.OrdinalIgnoreCase));
        ctx.Check("a second archive was written alongside it", zips.Length == 2,
            "archives: " + string.Join(", ", zips.Select(Path.GetFileName)));
        ctx.Check("the new one is counter-suffixed rather than clobbering",
            fresh is not null && Path.GetFileName(fresh) == "src (2).zip",
            fresh is null ? "no second archive appeared" : Path.GetFileName(fresh));
        if (fresh is not null)
        {
            CheckCreated(ctx, vm, Path.GetFileName(fresh));
            ctx.Check("and it holds the document",
                EntriesOf(fresh).SequenceEqual(new[] { "one.pdf" }),
                string.Join(", ", EntriesOf(fresh)));
        }
        ctx.Capture(win);
    }

    /// <summary>"Zip to…" onto a name that already holds a file — the exact
    /// opposite outcome to NameTaken above, and deliberately so.
    /// Zipper.CreateZip treats a non-null output path as an answer that came
    /// back from a Save-As dialog, which has already asked the user to confirm
    /// the overwrite, so it replaces the file instead of countering around it
    /// (pinned in ZipperTests.AnExplicitOutputPathOverwritesWhateverWasThere).
    ///
    /// Worth a scenario of its own precisely because it reads as a
    /// contradiction of the never-overwrite guarantee next to it, and because
    /// the part a user would actually notice if it regressed is that the
    /// replacement is a WHOLE, readable archive — not a half-written file, and
    /// not the old bytes with a zip appended to them.</summary>
    private static void SaveAsOverExisting(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var target = Path.Combine(ctx.Fx.Dir("out"), "taken.zip");
        File.WriteAllText(target, "I was here first, and I am not an archive");
        var before = new FileInfo(target).Length;
        ctx.Dialogs.QueueSaveFile(target);

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a });
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.CreateAsCommand.Execute(null);
        Settle(ctx, vm);

        CheckCreated(ctx, vm, "taken.zip");
        ctx.Check("the chosen name now holds a real, readable archive",
            EntriesOf(target).SequenceEqual(new[] { "one.pdf" }),
            string.Join(", ", EntriesOf(target)));
        ctx.Check("the old contents are gone rather than appended to",
            new FileInfo(target).Length != before,
            $"still {before} bytes");
        ctx.Check("and nothing was countered around it",
            Archives(ctx).Length == 1,
            "archives: " + string.Join(", ", Archives(ctx).Select(Path.GetFileName)));
        ctx.Capture(win);
    }

    private static void UnicodeNames(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/rapport café — 2026.pdf", "CAFE");
        var b = ctx.Fx.Pdf("src/文件 名.pdf", "CJK");

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a, b });
        E2EPump.Until(() => add.IsCompleted, 8000);
        ctx.Check("both sources listed", vm.Rows.Count == 2, $"got {vm.Rows.Count}");

        vm.CreateCommand.Execute(null);
        Settle(ctx, vm);

        var zips = Archives(ctx);
        ctx.Check("archive written", zips.Length == 1, $"got {zips.Length}");
        if (zips.Length == 1)
        {
            // Whole names, not substrings: a round trip that mangled the space,
            // the em dash or the accent while keeping "café" recognisable would
            // slip past a Contains check.
            var names = EntriesOf(zips[0]);
            ctx.Check("both names survive the round trip intact",
                names.SequenceEqual(new[] { "rapport café — 2026.pdf", "文件 名.pdf" }
                    .OrderBy(n => n, StringComparer.Ordinal).ToList()),
                string.Join(" | ", names));
        }
        ctx.Capture(win);
    }

    private static void EmptySelection(ScenarioContext ctx)
    {
        var vm = NewVm(ctx);
        var win = Open(vm);

        ctx.Check("nothing listed", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Check("the button reads as empty", vm.ZipButtonText == "Zip", vm.ZipButtonText);
        ctx.Check("create is refused", !vm.CreateCommand.CanExecute(null), "the command was enabled");
        ctx.Check("save-as is refused too", !vm.CreateAsCommand.CanExecute(null),
            "the command was enabled");
        ctx.Check("no archive written", Archives(ctx).Length == 0, "an archive appeared");
        ctx.Capture(win);
    }
}

/// <summary>Runs scheduled work inline so a scenario's assertions follow the
/// call rather than a sleep. Mirrors OrdoSort.Wpf.Tests.InlineWorkScheduler,
/// duplicated here because the test project's types are not visible to the
/// Smoke tool — the project dependency runs the other way.</summary>
internal sealed class InlineScheduler : IWorkScheduler
{
    public Task<T> Run<T>(Func<T> work) => Task.FromResult(work());
    public Task Run(Action work) { work(); return Task.CompletedTask; }
}
