using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Bulk rename as the real BulkRenameWindow, with the `plan` seam
/// left null so BulkRename's own Plan/Execute run against real files and the
/// files on disk really move.
///
/// BulkRenameViewModel's marshalling shape is a THIRD one, distinct from
/// both halves of the split UnzipViewModel/ZipMergeViewModel already
/// document and from UnlockViewModel's no-seam-at-all shape:
///
/// Preview/NeedsNameCount/CountsLine/RenameButtonText and RenameCommand's
/// own CanExecute (via `_changed`) are ALL set together, only inside
/// ApplyPlans — the one callback DebouncedProbe&lt;T&gt; ever invokes — and
/// that callback is ALWAYS reached through `_uiContext.Post`, unconditionally.
/// This holds even with InlineScheduler and probeDelayMs: 0, unlike the
/// Zip/Unzip/ZipMerge "InlineScheduler collapses the await to synchronous"
/// trap: DebouncedProbe.Trigger doesn't call `compute` itself, it re-arms a
/// real System.Threading.Timer (`_timer.Change(immediate ? 0 : _intervalMs,
/// Timeout.Infinite)`), and a Timer callback always runs on a thread-pool
/// thread regardless of due time — so Fire()/RunAsync always start on a
/// background thread, and the scheduler choice only changes what happens
/// BEFORE the Post, never whether one happens. Every wait on Preview (or
/// anything ApplyPlans touches) below therefore genuinely needs
/// E2EPump.Until to pump a frame — it is never already true on the
/// pre-pump check.
///
/// Status is the opposite shape, and it is direct, not a hazard: Apply() and
/// UndoBatch() assign Status themselves, synchronously, on the calling
/// thread — no Post anywhere in either method. Critically, unlike Summary in
/// Unzip/ZipMerge (an aggregate that can be non-empty from a live "N of M…"
/// line while individual rows are still Pending), there is no async
/// per-row completion Status could race ahead of here: BulkRename.Execute
/// and BulkRename.Revert are plain foreach/File.Move loops with no Task.Run
/// of their own, called directly at the top of Apply()/UndoBatch() before
/// Status is ever touched. So by the time RenameCommand.Execute(null)
/// returns, the real rename has already happened and Status already
/// reports it — Settle(ctx, () => vm.Status) below is the right verdict
/// property precisely because there is nothing left in flight for it to be
/// stale against, not merely because it happens to compile.
///
/// Two of the brief's own waits had exactly the trap ScenarioKit.Settle
/// warns about, just aimed at a view-model property instead of the
/// filesystem: `E2EPump.Until(() => vm.Preview.Count == 2, ...)` immediately
/// AFTER a SetOverride call is already true before the override lands —
/// SetOverride never changes Preview.Count, only NewName/Manual — so the
/// pump never arms and the assertions that follow can run against the
/// PRE-override preview. Fixed below by waiting on `.Manual`/`.Changed`
/// instead, the properties an override actually flips. The brief's
/// IllegalName check had a second, sharper version of the same bug:
/// `!vm.RenameCommand.CanExecute(null)` is already true for a single file
/// with NO rule applied yet (nothing to rename == nothing changed == command
/// disabled for an unrelated reason), so the assertion would have passed
/// even if the illegal-name rejection never ran. Fixed by giving the file a
/// real rule first (RenameCommand really offered), then overriding to the
/// illegal name and asserting the row was pushed back to Changed == false
/// for THAT reason.</summary>
public static class BulkRenameScenarios
{
    private const string Surface = "Bulk rename";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "a rule applied to real files", "clean", AppliedToRealFiles),
        new Scenario(Surface, "two files would take the same name", "awkward", Collision),
        new Scenario(Surface, "a rule producing an illegal name", "awkward", IllegalName),
        new Scenario(Surface, "undo puts the names back", "awkward", Undo),
    };

    private static BulkRenameViewModel NewVm() =>
        new(plan: null, scheduler: new InlineScheduler(),
            uiContext: SynchronizationContext.Current, probeDelayMs: 0);

    private static BulkRenameWindow Open(BulkRenameViewModel vm)
    {
        var win = new BulkRenameWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    /// <summary>A real rule (Prefix) applied end to end: the preview shows
    /// the new names BEFORE anything is renamed, then Rename produces
    /// exactly those names on disk — not merely "two files still exist
    /// under some name", which would pass even if the rule silently did
    /// nothing.</summary>
    private static void AppliedToRealFiles(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/beta.pdf", "TWO");
        var aBefore = File.ReadAllBytes(a);
        var bBefore = File.ReadAllBytes(b);

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Preview.Count == 2, 8000);
        ctx.Check("preview shows both files", vm.Preview.Count == 2, $"got {vm.Preview.Count}");

        // A real rule: prefix every stem. Waiting on Changed rather than
        // Count — Count is already 2 and would never flip false — is what
        // actually proves the debounced probe re-ran for this edit.
        vm.Prefix = "RENAMED-";
        var previewed = E2EPump.Until(
            () => vm.Preview.Count == 2 && vm.Preview.All(r => r.Changed), 8000);
        ctx.Check("the preview reflects the rule before anything is renamed", previewed,
            "rows: " + string.Join(", ", vm.Preview.Select(r => $"{r.Current}->{r.NewName} changed={r.Changed}")));
        ctx.Check("the preview already shows the new names",
            vm.Preview.Any(r => r.NewName == "RENAMED-alpha.pdf")
            && vm.Preview.Any(r => r.NewName == "RENAMED-beta.pdf"),
            "got: " + string.Join(", ", vm.Preview.Select(r => r.NewName)));
        ctx.Check("rename is offered", vm.RenameCommand.CanExecute(null), "command disabled");

        vm.RenameCommand.Execute(null);
        Settle(ctx, () => vm.Status);

        var expectedA = Path.Combine(ctx.Fx.Root, "in", "RENAMED-alpha.pdf");
        var expectedB = Path.Combine(ctx.Fx.Root, "in", "RENAMED-beta.pdf");
        ctx.FileMissing(a);
        ctx.FileMissing(b);
        ctx.BytesUnchanged(expectedA, aBefore, "alpha's content survived the rename");
        ctx.BytesUnchanged(expectedB, bBefore, "beta's content survived the rename");
        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("exactly the two renamed files are present, nothing else", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        ctx.Capture(win);
    }

    /// <summary>Two sources colliding on one target must not cost a file —
    /// the counter suffix exists for exactly this.</summary>
    private static void Collision(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/beta.pdf", "TWO");

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Preview.Count == 2, 8000);

        // Force both to the same target name via the per-file override. The
        // wait below keys off Manual, not Count — Count was already 2 before
        // either override landed, so waiting on it would prove nothing (see
        // class doc comment).
        vm.SetOverride(a, "SAME NAME");
        vm.SetOverride(b, "SAME NAME");
        var overridden = E2EPump.Until(() => vm.Preview.Count(r => r.Manual) == 2, 8000);
        ctx.Check("both overrides landed in the preview", overridden,
            "rows: " + string.Join(", ", vm.Preview.Select(r => $"{r.Current}->{r.NewName} manual={r.Manual}")));

        vm.RenameCommand.Execute(null);
        Settle(ctx, () => vm.Status);

        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("no file was lost to the collision", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        var names = onDisk.Select(Path.GetFileName).ToList();
        ctx.Check("the two names are distinct",
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "both ended up with the same name");
        // Pinned to the exact counter-suffixed name, not just "distinct" —
        // proves the counter mechanism specifically fired rather than the
        // names merely staying different by accident.
        ctx.Check("the collision was resolved with the counter suffix",
            names.Any(n => n!.Equals("SAME NAME (2).pdf", StringComparison.OrdinalIgnoreCase))
            && names.Any(n => n!.Equals("SAME NAME.pdf", StringComparison.OrdinalIgnoreCase)),
            "expected \"SAME NAME.pdf\" and \"SAME NAME (2).pdf\", got: " + string.Join(", ", names));
        ctx.Capture(win);
    }

    /// <summary>A colon would otherwise hide the document in an NTFS
    /// alternate data stream — the README calls this out specifically. The
    /// file is first given a rule that DOES change its name (so
    /// RenameCommand is genuinely offered beforehand); only then is it
    /// overridden to an illegal name, so "the command is disabled" proves
    /// the illegal name was refused rather than proving only that a
    /// single unruled file has nothing to rename in the first place.</summary>
    private static void IllegalName(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var before = File.ReadAllBytes(a);

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a });
        E2EPump.Until(() => vm.Preview.Count == 1, 8000);

        vm.Prefix = "OK-";
        var offered = E2EPump.Until(() => vm.Preview.Count == 1 && vm.Preview[0].Changed, 8000);
        ctx.Check("a normal rule is offered before the illegal override", offered,
            vm.Preview.Count > 0 ? $"NewName={vm.Preview[0].NewName} changed={vm.Preview[0].Changed}" : "no row");
        ctx.Check("rename is offered for the valid rule", vm.RenameCommand.CanExecute(null),
            "command disabled even with a valid rule");

        vm.SetOverride(a, "BAD:NAME");
        var refused = E2EPump.Until(() => vm.Preview.Count == 1 && !vm.Preview[0].Changed, 8000);
        ctx.Check("the illegal name is refused in the preview, not merely warned about later",
            refused,
            vm.Preview.Count > 0
                ? $"row still shows Changed={vm.Preview[0].Changed} New={vm.Preview[0].NewName}"
                : "no preview row");
        ctx.Check("the row names the reserved character as the problem",
            vm.Preview.Count > 0
            && vm.Preview[0].Note.Contains("Windows forbids", StringComparison.OrdinalIgnoreCase),
            vm.Preview.Count > 0 ? $"note was \"{vm.Preview[0].Note}\"" : "no preview row");
        ctx.Check("Rename is no longer offered once the only row was refused",
            !vm.RenameCommand.CanExecute(null), "command still enabled after the only row was refused");

        // Even a direct Execute() call — bypassing CanExecute, which a real
        // UI binding never would — must not touch the file:
        // BulkRename.Execute skips any PlannedRename with Changed == false,
        // so this proves the safety net, not just the UI gate.
        vm.RenameCommand.Execute(null);
        Settle(ctx, () => vm.Status);
        ctx.BytesUnchanged(a, before, "the original is untouched even after a forced Execute");
        ctx.FileExists(a);
        ctx.Capture(win);
    }

    private static void Undo(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var beforeBytes = File.ReadAllBytes(a);

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a });
        E2EPump.Until(() => vm.Preview.Count == 1, 8000);

        vm.SetOverride(a, "RENAMED");
        var overridden = E2EPump.Until(() => vm.Preview.Count == 1 && vm.Preview[0].Manual, 8000);
        ctx.Check("the hand-typed name landed in the preview", overridden,
            vm.Preview.Count > 0 ? $"NewName={vm.Preview[0].NewName} manual={vm.Preview[0].Manual}" : "no row");

        vm.RenameCommand.Execute(null);
        Settle(ctx, () => vm.Status);

        var renamedPath = Path.Combine(ctx.Fx.Root, "in", "RENAMED.pdf");
        ctx.FileExists(renamedPath);
        ctx.FileMissing(a);
        ctx.Check("undo is offered after a rename", vm.UndoCommand.CanExecute(null), "command disabled");

        vm.UndoCommand.Execute(null);
        Settle(ctx, () => vm.Status);
        ctx.FileExists(a);
        ctx.BytesUnchanged(a, beforeBytes, "the restored file is byte-identical to the original");
        ctx.FileMissing(renamedPath);
        ctx.Check("undo is no longer offered once the batch is restored",
            !vm.UndoCommand.CanExecute(null), "command still enabled after undo");
        ctx.Capture(win);
    }
}
