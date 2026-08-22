using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC-05 for Unlock: ClearCommand carried a four-line comment
/// reasoning carefully about cancelling the PROBE token so a stale probe
/// can't update an invisible row, and never cancelled the RUN token —
/// UnlockAsync's own `rows` snapshot (its own doc comment, :800-ish) kept
/// going regardless. Clearing the list mid-run left the batch moving real
/// files: it archived a cleared row's original and wrote its unlocked file,
/// then repopulated ResultLines and Summary with results for files the user
/// had just cleared. RemoveFiles had the identical gap for a single row.
///
/// "In flight" here is ControlledWorkScheduler's queue — dispatched, not yet
/// run — the same technique UnlockReadinessProbeTests uses for the probe
/// side of this same defect family. MaxConcurrentUnlocks (4) comfortably
/// covers every file this suite adds, so every TryCandidates call for a
/// batch is queued before the test's first assertion runs.</summary>
public class UnlockClearAndRemoveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordounlockclear_" + Guid.NewGuid());

    public UnlockClearAndRemoveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    private string MakeEncrypted(string name, string userPw)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    private static bool StillNeedsPassword(string path)
    {
        try { using var _ = PdfReader.Open(path, PdfDocumentOpenMode.Import); return false; }
        catch { return true; }
    }

    [Fact]
    public async Task ClearDuringARunCancelsItAndTheWipedSummaryIsNotRepopulated()
    {
        var a = Touch("a.pdf");
        var b = Touch("b.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true,
            unlocker: (p, pw) => new Unlock.UnlockResult("ok", p, p, InPlace: true),
            probe: (path, candidates) => new Unlock.ProbeResult("needs_password", path, Message: "x"),
            scheduler: scheduler);

        var addTask = vm.AddFilesAsync(new[] { a, b });
        scheduler.ReleaseAll();
        await addTask;
        Assert.Equal(2, vm.Files.Count);

        vm.Password = "secret";
        var unlockTask = vm.UnlockAsync();   // both TryCandidates calls dispatched, neither run yet
        Assert.Equal(2, scheduler.Queued);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Files);
        Assert.Equal("", vm.Summary);

        // "One already under way finishes" — both calls were already
        // dispatched before Clear ran, so they still run to completion.
        // What must NOT happen is their results landing anywhere visible.
        scheduler.ReleaseAll();
        await unlockTask;

        Assert.Empty(vm.ResultLines);   // no result line for a row the list no longer shows
        Assert.Equal("", vm.Summary);   // not repopulated with a stale total

        // The fresh-token half of QC-05: a run started AFTER a mid-batch
        // Clear must not be born cancelled.
        var c = Touch("c.pdf");
        var addTask2 = vm.AddFilesAsync(new[] { c });
        scheduler.ReleaseAll();
        await addTask2;

        var unlockTask2 = vm.UnlockAsync();
        scheduler.ReleaseAll();
        await unlockTask2;

        Assert.Contains("1 unlocked", vm.Summary);
    }

    /// <summary>Fix-round finding: _clearedWhileUnlocking's reset used to sit
    /// AFTER the try/finally, reachable only on the no-exception path. If
    /// Clear cancelled a run that then threw (a broken _unlock/_probe
    /// delegate — both are constructor-injectable seams, not sealed to this
    /// file) the flag stayed true forever, silently skipping every FUTURE
    /// run's own Summary/ResultLines too, with no OnError-style hook to
    /// surface it. The reset now lives inside the finally itself.</summary>
    [Fact]
    public async Task AClearCancelledRunThatThenThrowsStillLetsTheNextRunReportNormally()
    {
        var shouldThrow = true;
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true,
            unlocker: (p, pw) => shouldThrow
                ? throw new InvalidOperationException("boom")
                : new Unlock.UnlockResult("ok", p, p, InPlace: true),
            probe: (path, candidates) => new Unlock.ProbeResult("needs_password", path, Message: "x"),
            scheduler: scheduler);

        var a = Touch("a.pdf");
        var addTask = vm.AddFilesAsync(new[] { a });
        scheduler.ReleaseAll();
        await addTask;

        vm.Password = "secret";
        var unlockTask = vm.UnlockAsync();   // dispatched, not yet run
        Assert.Equal(1, scheduler.Queued);

        vm.ClearCommand.Execute(null);   // sets the flag, cancels the run token

        // The dispatched TryCandidates call throws once released — the
        // exception propagates through Task.WhenAll and out of UnlockAsync
        // itself, past its own finally, with nothing here to catch it (this
        // test awaits UnlockAsync directly; AsyncRelayCommand.Execute is
        // what catches it in production, via OnError).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            scheduler.ReleaseAll();
            await unlockTask;
        });

        // A second, ordinary run — no Clear this time — must report
        // normally. Before this fix, the flag left stuck true by the run
        // above would silently skip this run's reporting too.
        shouldThrow = false;
        var b = Touch("b.pdf");
        var addTask2 = vm.AddFilesAsync(new[] { b });
        scheduler.ReleaseAll();
        await addTask2;

        var unlockTask2 = vm.UnlockAsync();
        scheduler.ReleaseAll();
        await unlockTask2;

        Assert.Contains("1 unlocked", vm.Summary);
        Assert.Single(vm.ResultLines);
    }

    [Fact]
    public async Task ClearWithNothingRunningStillWorks()
    {
        var vm = new UnlockViewModel(new Config(), () => true,
            unlocker: (p, pw) => new Unlock.UnlockResult("ok", p, p, InPlace: true),
            probe: (path, candidates) => new Unlock.ProbeResult("not_encrypted", path));
        await vm.AddFilesAsync(new[] { Touch("a.pdf") });

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Files);
        Assert.Equal("", vm.Summary);
        Assert.True(vm.IsIdle);
    }

    /// <summary>The red phase that matters most: a row removed mid-run still
    /// gets its original archived and its unlocked file written — the
    /// user's file moving out from under them for a row the list no longer
    /// shows. Real encrypted PDFs and the REAL default unlocker
    /// (Unlock.UnlockPdf), not a scripted double, so the disk-side proof is
    /// the genuine archive-and-swap Unlock.PlaceAndSwap performs, not a
    /// stand-in for it.</summary>
    [Fact]
    public async Task RemoveFilesIsRefusedWhileUnlockingIsRunningSoNoRowIsDroppedOutFromUnderTheBatch()
    {
        var a = MakeEncrypted("a.pdf", "secret");
        var b = MakeEncrypted("b.pdf", "secret");
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true, scheduler: scheduler);   // real unlocker, real probe

        var addTask = vm.AddFilesAsync(new[] { a, b });
        scheduler.ReleaseAll();
        await addTask;
        Assert.Equal(2, vm.Files.Count);
        var rowB = vm.Files.Single(r => r.Path == b);

        vm.Password = "secret";
        var unlockTask = vm.UnlockAsync();   // both real unlocks dispatched, neither run yet
        Assert.Equal(2, scheduler.Queued);

        vm.RemoveFiles(new[] { b });   // the click that slips through anyway

        scheduler.ReleaseAll();   // b's dispatch already happened — its real unlock still runs
        await unlockTask;

        var archived = Path.Combine(Unlock.ArchiveFolderFor(b), "b.pdf");
        Assert.True(File.Exists(archived), "b's locked original should be archived either way — it was already in flight");
        Assert.False(StillNeedsPassword(b), "b's unlocked copy should be readable with no password either way");

        // What the guard actually changes: the row was never removed from
        // Files in the first place, so nothing here is a surprise to the
        // person looking at the list.
        Assert.Contains(rowB, vm.Files);
        Assert.True(vm.IsIdle);   // normal service resumes
    }
}
