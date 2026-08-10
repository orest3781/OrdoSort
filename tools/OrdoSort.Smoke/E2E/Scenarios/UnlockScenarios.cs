using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;
using static OrdoSort.Smoke.E2E.Scenarios.ScenarioKit;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Unlock PDFs as the real UnlockWindow. Every seam on
/// UnlockViewModel — unlocker, fileSize, tryReveal, probe — is left at its
/// default, so the real Unlock.UnlockPdf/ProbeReadiness run against real
/// PdfSharp-encrypted documents. That is the only way the never-overwrite
/// guarantee (README, UnlockNeverOverwritesTests) can actually be
/// demonstrated rather than merely asserted at unit level.
///
/// UnlockViewModel has NO uiContext/IWorkScheduler seam at all — unlike
/// ZipMergeViewModel/UnzipViewModel, its constructor takes no
/// SynchronizationContext and no scheduler, and there is no InlineScheduler
/// equivalent that could collapse it to synchronous completion. UnlockAsync
/// and ProbeRowsAsync always call the real Task.Run/Task.WhenAll — genuine
/// thread-pool hops, not something a seam here could remove — so EVERY
/// continuation past the first Task.Run resumes however the ambient
/// SynchronizationContext.Current says to resume (ordinary C# async/await
/// capture, not an explicit `_uiContext.Post` call anywhere in this view
/// model). E2ERunner.InstallUiSynchronizationContext gives this thread a real
/// DispatcherSynchronizationContext, so that resumption is a genuine
/// dispatcher Post requiring a pump — for Summary, for ResultLines, and for
/// every row's Status/Message from ApplyProbeResult alike. There is no
/// UnzipViewModel/ZipMergeViewModel-shaped split here (one property set
/// directly, another only from inside an explicit Post) to trip on: I traced
/// every assignment reachable before the first await in AddFilesAsync
/// (candidate/ignored bookkeeping only) and in UnlockAsync (ResetBanner,
/// ResultLines.Clear(), the candidates list, IsUnlocking = true) and none of
/// them is the property this file waits on, so nothing here can already read
/// "done" on E2EPump.Until's pre-pump check. See RunUnlock's own doc comment
/// for the property actually chosen and why.</summary>
public static class UnlockScenarios
{
    private const string Surface = "Unlock";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "correct password unlocks", "clean", CorrectPassword),
        new Scenario(Surface, "wrong password leaves the original intact", "awkward", WrongPassword),
        new Scenario(Surface, "an already-unlocked document", "awkward", AlreadyPlain),
        new Scenario(Surface, "a damaged document", "awkward", Damaged),
    };

    private static UnlockViewModel NewVm(ScenarioContext ctx, Config cfg) =>
        new(cfg, () => true, dialogs: ctx.Dialogs);

    /// <summary>Add every file, run the unlock, and wait for every row's
    /// result to land — for AlreadyPlain and Damaged, which need no typed
    /// password and so have no reason to duplicate CorrectPassword/
    /// WrongPassword's own inline flow.
    ///
    /// Waits on ResultLines.Count reaching the file count, never on
    /// vm.Summary, even though (unlike Unzip/ZipMerge) both would actually be
    /// safe here — see the class doc comment for why UnlockViewModel has no
    /// InlineScheduler-shaped trap to fall into. ResultLines is still the
    /// better choice on its own terms: UnlockAsync clears it synchronously at
    /// the top of the run and repopulates it in one synchronous burst, in
    /// input order, only after `await Task.WhenAll(...)` over every row's
    /// real Task.Run has finished — so its count is an exact, per-row verdict
    /// count, not a live-progress string like Summary ("Unlocking N of M…")
    /// that a batch could pass through several times before settling.</summary>
    private static UnlockWindow RunUnlock(ScenarioContext ctx, UnlockViewModel vm, params string[] files)
    {
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        Added(ctx, vm.AddFilesAsync(files));
        ctx.Check("every file was added", vm.Files.Count == files.Length,
            $"added {vm.Files.Count} of {files.Length}");

        vm.UnlockCommand.Execute(null);
        var settled = E2EPump.Until(() => vm.ResultLines.Count == files.Length, 20000);
        ctx.Check("every file reported a result", settled,
            $"ResultLines: {vm.ResultLines.Count} of {files.Length}");

        return win;
    }

    /// <summary>The clean case: a saved-in-the-box-typed password unlocks a
    /// real PdfSharp-encrypted, multi-page document.
    ///
    /// This does NOT reuse the brief's own suggested verification — see the
    /// task-9 report for the full defect writeup. In short: PlaceAndSwap
    /// keeps the ORIGINAL PATH for the newly unlocked content and moves the
    /// still-encrypted original into a dated locked_archive_&lt;date&gt;
    /// folder beside it. A check that walks every *.pdf under the fixture and
    /// excludes whatever equals the pre-unlock `locked` path (the brief's
    /// approach) therefore excludes the genuinely unlocked file — which kept
    /// that exact path — and picks up the still-encrypted archived copy
    /// instead, then asserts THAT one opens without a password. It doesn't,
    /// so that assertion would fail even on a correct unlock. Checking the
    /// two known paths directly (the swapped-in-place path, and
    /// Unlock.ArchiveFolderFor's own path) verifies the real guarantee
    /// instead: the same name/place for the unlocked content, and the
    /// original preserved byte-for-byte in the archive, not merely
    /// "some second file exists".</summary>
    private static void CorrectPassword(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var locked = ctx.Fx.EncryptedPdf("in/locked.pdf", "right-one", pages: 2);
        var lockedBytesBefore = File.ReadAllBytes(locked);

        var vm = NewVm(ctx, cfg);
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        Added(ctx, vm.AddFilesAsync(new[] { locked }));
        vm.Password = "right-one";

        vm.UnlockCommand.Execute(null);
        var settled = E2EPump.Until(() => vm.ResultLines.Count == 1, 20000);
        ctx.Check("the unlock reported a result", settled, "no result line within 20s");

        ctx.Check("the result reports success",
            vm.ResultLines.Count > 0 && vm.ResultLines[0].Kind == UnlockResultKind.Ok,
            vm.ResultLines.Count > 0
                ? $"kind was {vm.ResultLines[0].Kind} — \"{vm.ResultLines[0].Text}\""
                : "no result line");

        // Same name, same place: the unlocked content lands back at `locked`.
        ctx.Check("the unlocked document opens without a password",
            File.Exists(locked)
            && Unlock.ProbeReadiness(locked, Array.Empty<string>()).Status == "not_encrypted",
            File.Exists(locked)
                ? $"probe said {Unlock.ProbeReadiness(locked, Array.Empty<string>()).Status}"
                : "the file at the original path is gone");

        // The locked original is never deleted — only moved aside, byte for
        // byte, into the dated archive folder Unlock.ArchiveFolderFor names.
        var archiveDir = Unlock.ArchiveFolderFor(locked);
        ctx.Check("the locked original was archived beside it, not deleted",
            Directory.Exists(archiveDir), $"no folder at {archiveDir}");
        if (Directory.Exists(archiveDir))
        {
            var archived = Path.Combine(archiveDir, Path.GetFileName(locked));
            ctx.BytesUnchanged(archived, lockedBytesBefore,
                "the archived original is byte-identical to what was locked");
        }
        ctx.Capture(win);
    }

    /// <summary>The never-overwrite guarantee. A wrong password must leave
    /// the encrypted original byte-identical — this is the assertion the
    /// whole tool's trustworthiness rests on. Also checks that the failure is
    /// reported BY NAME (mentions the password didn't work), not just that
    /// some result line exists — a bare Count &gt; 0 check passes on success
    /// too and would not have caught a regression that reported "ok" for a
    /// silently-unmoved file — and that no archive folder was even created,
    /// since a failed unlock must not attempt to move anything aside at
    /// all.</summary>
    private static void WrongPassword(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var locked = ctx.Fx.EncryptedPdf("in/locked.pdf", "the-real-one");
        var before = File.ReadAllBytes(locked);

        var vm = NewVm(ctx, cfg);
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        Added(ctx, vm.AddFilesAsync(new[] { locked }));
        vm.Password = "definitely-wrong";
        vm.UnlockCommand.Execute(null);
        var settled = E2EPump.Until(() => vm.ResultLines.Count == 1, 20000);
        ctx.Check("the unlock reported a result", settled, "no result line within 20s");

        ctx.BytesUnchanged(locked, before, "the encrypted original is byte-identical after a failed unlock");

        ctx.Check("the result names the wrong password as the reason",
            vm.ResultLines.Count > 0
            && vm.ResultLines[0].Kind == UnlockResultKind.Fail
            && vm.ResultLines[0].Text.Contains("didn't work", StringComparison.OrdinalIgnoreCase),
            vm.ResultLines.Count > 0
                ? $"kind={vm.ResultLines[0].Kind} text=\"{vm.ResultLines[0].Text}\""
                : "no result line");

        ctx.Check("no locked_archive folder was created for a failed unlock",
            !Directory.Exists(Unlock.ArchiveFolderFor(locked)),
            "an archive folder exists despite the unlock failing");
        ctx.Capture(win);
    }

    private static void AlreadyPlain(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var plain = ctx.Fx.Pdf("in/plain.pdf", "NOT LOCKED");
        var before = File.ReadAllBytes(plain);

        var vm = NewVm(ctx, cfg);
        var win = RunUnlock(ctx, vm, plain);

        ctx.BytesUnchanged(plain, before, "an unencrypted document is left alone");
        ctx.Check("the result reports it needed no unlocking",
            vm.ResultLines.Count > 0
            && vm.ResultLines[0].Kind == UnlockResultKind.Skip
            && vm.ResultLines[0].Text.Contains("isn't password-protected", StringComparison.OrdinalIgnoreCase),
            vm.ResultLines.Count > 0
                ? $"kind={vm.ResultLines[0].Kind} text=\"{vm.ResultLines[0].Text}\""
                : "no result line");
        ctx.Capture(win);
    }

    private static void Damaged(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var broken = ctx.Fx.CorruptPdf("in/broken.pdf");
        var before = File.ReadAllBytes(broken);

        var vm = NewVm(ctx, cfg);
        var win = RunUnlock(ctx, vm, broken);

        ctx.BytesUnchanged(broken, before, "a damaged file is left alone");
        ctx.Check("the result reports it as an error, not a silent skip",
            vm.ResultLines.Count > 0 && vm.ResultLines[0].Kind == UnlockResultKind.Fail,
            vm.ResultLines.Count > 0
                ? $"kind={vm.ResultLines[0].Kind} text=\"{vm.ResultLines[0].Text}\""
                : "no result line");
        ctx.Check("the window survived", win.IsLoaded, "the window went away");
        ctx.Capture(win);
    }
}
