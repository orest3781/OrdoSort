using OrdoSort.Core;
using OrdoSort.TestSupport;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>After a run, the list holds only what is still locked (owner's
/// call, 2026-09-28). Before, every row kept its pre-run note: a file just
/// unlocked still read "needs a password" beside a results box saying "✓
/// unlocked", and retrying the one failure re-ran the whole batch.</summary>
public class UnlockAfterRunTests
{
    private static UnlockViewModel MakeVm(Func<string, string, Unlock.UnlockResult> unlocker) =>
        new(new Config(), () => true,
            unlocker: unlocker,
            fileSize: _ => 1,
            probe: (path, _) => new Unlock.ProbeResult("needs_password", path, Message: "needs one"),
            scheduler: new InlineWorkScheduler());

    private static Unlock.UnlockResult Outcome(string path) => Path.GetFileName(path) switch
    {
        "ok.pdf" => new("ok", path, NewPath: path),
        "plain.pdf" => new("not_encrypted", path, Message: "This PDF isn't password-protected."),
        _ => new("wrong_password", path, Message: "That password didn't work."),
    };

    [Fact]
    public async Task OnlyTheFilesStillLockedStayListedWithTheirReason()
    {
        using var dir = new TempDir();
        var vm = MakeVm((path, _) => Outcome(path));
        await vm.AddFilesAsync(new[] { dir.File("ok.pdf"), dir.File("plain.pdf"), dir.File("wrong.pdf") });

        await vm.UnlockAsync();

        var left = Assert.Single(vm.Files);
        Assert.Equal("wrong.pdf", left.FileName);
        Assert.Equal(ReadinessStatus.Failed, left.Status);
        Assert.Equal("  —  ✗ That password didn't work.", left.Note);
        Assert.Equal("wrong.pdf  —  ✗ That password didn't work.", left.DisplayText);
        Assert.Equal(3, vm.ResultLines.Count);   // the run's full record stays
    }

    [Fact]
    public async Task UnlockAgainRetriesOnlyWhatFailed()
    {
        using var dir = new TempDir();
        var tried = new List<string>();
        var vm = MakeVm((path, _) => { tried.Add(Path.GetFileName(path)); return Outcome(path); });
        await vm.AddFilesAsync(new[] { dir.File("ok.pdf"), dir.File("wrong.pdf") });
        await vm.UnlockAsync();
        tried.Clear();

        await vm.UnlockAsync();

        Assert.Equal(new[] { "wrong.pdf" }, tried.Distinct());
    }

    [Fact]
    public async Task AFileCancelledBeforeItStartedStaysListedAsCancelled()
    {
        using var dir = new TempDir();
        UnlockViewModel? vm = null;
        vm = MakeVm((path, _) => { vm!.CancelUnlock(); return Outcome(path); });
        await vm.AddFilesAsync(new[] { dir.File("ok.pdf"), dir.File("later.pdf") });

        await vm.UnlockAsync();

        var left = Assert.Single(vm.Files);
        Assert.Equal("later.pdf", left.FileName);
        Assert.Equal(ReadinessStatus.Cancelled, left.Status);
        Assert.Equal("  —  cancelled", left.Note);
    }

    [Fact]
    public async Task ALongReasonIsShortenedOnTheRowAndKeptWholeInTheTooltip()
    {
        using var dir = new TempDir();
        var message = "It's open in Windows Explorer — close it there and unlock it again.";
        var vm = MakeVm((path, _) => new Unlock.UnlockResult("error", path, Message: message));
        await vm.AddFilesAsync(new[] { dir.File("held.pdf") });

        await vm.UnlockAsync();

        var row = Assert.Single(vm.Files);
        Assert.Equal("  —  ✗ It's open in Windows Explorer", row.Note);
        Assert.Contains(message, row.ToolTipText);
    }
}
