using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class ShellReadyTests
{
    [Fact]
    public void FreshShellShowsReadyCount()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();

        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        Assert.Equal("2 files ready", fx.Shell.CountLine);
        Assert.Equal("2", fx.Shell.BigCount);
        Assert.Equal("PDFs in the inbox", fx.Shell.CountCaption);
        Assert.True(fx.Shell.StartEnabled);
        Assert.True(fx.Shell.Viewer0Blanked(fx.Viewer));
    }

    [Fact]
    public void NonMatchingFilesAreCountedAsIgnored()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        File.WriteAllText(Path.Combine(fx.Inbox, "not-a-fax.pdf"), "x");
        fx.Shell.Initialize();

        Assert.Equal("1 file ready", fx.Shell.CountLine);
        Assert.Equal("1 other file ignored", fx.Shell.DetailLine);
    }

    [Fact]
    public void EmptyInboxDisablesStart()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        Assert.Equal("0 files ready", fx.Shell.CountLine);
        Assert.False(fx.Shell.StartEnabled);
        Assert.False(fx.Shell.StartCommand.CanExecute(null));
    }

    [Fact]
    public void DeferredFilesRaiseTheSetAsideAlert()
    {
        using var fx = new ShellFixture();
        File.WriteAllText(Path.Combine(fx.Deferred, "waiting.pdf"), "x");
        fx.Shell.Initialize();
        Assert.True(fx.Shell.HasDeferred);
        Assert.Contains("1 set-aside file waiting", fx.Shell.DeferredAlert);
    }

    [Fact]
    public void UnknownOldestAgeRendersAsUnknownNotAHugeNumber()
    {
        // QC-13: OldestAgeDays is null when every set-aside file's mtime read
        // failed -- the pre-fix bug reported a ~155,000-day-old folder
        // instead. Constructed directly (see ApplyDeferred's doc comment):
        // reaching this through a real Scanner.DeferredSummary call needs a
        // file gone between Directory.GetFiles and its mtime read, which
        // isn't a race this machine can reproduce.
        using var fx = new ShellFixture();
        fx.Shell.ApplyDeferred(new Scanner.DeferredInfo(1, null));
        Assert.Contains("unknown", fx.Shell.DeferredAlert);
        Assert.DoesNotContain("155", fx.Shell.DeferredAlert);

        // The rail-facing _deferredDetail switch is a separate, hand-edited
        // copy of the same OldestAgeDays switch (ShellViewModel.cs:659-664)
        // -- a copy-paste omission there would not be a compile error, only
        // a silently blank "oldest  " (Nullable<int>.ToString() on null).
        var deferredNotice = fx.Shell.Notices.FirstOrDefault(n => n.Key == "deferred");
        Assert.NotNull(deferredNotice);
        Assert.Contains("unknown", deferredNotice!.Detail);
    }

    [Fact]
    public void FolderActivityRefreshesTheReadyCount()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        Assert.Equal("0 files ready", fx.Shell.CountLine);

        fx.AddInboxFile();
        fx.Shell.OnFolderActivity();
        Assert.Equal("1 file ready", fx.Shell.CountLine);
        Assert.True(fx.Shell.StartEnabled);
    }

    [Fact]
    public void InboxProblemIsReadableNotFatal()
    {
        using var fx = new ShellFixture(cfg => cfg.Inbox = Path.Combine(cfg.Inbox, "missing"));
        fx.Shell.Initialize();
        Assert.Equal("Inbox problem", fx.Shell.CountLine);
        Assert.Equal("⚠", fx.Shell.BigCount);
        Assert.False(fx.Shell.StartEnabled);
        Assert.NotEqual("", fx.Shell.DetailLine);
    }

    /// <summary>app-qc-2026-08-21 finding 1 (Important): Config.Load already
    /// computes SideFileCollisionWarning for a config.json that already has
    /// two side-file keys pointing at the same file (a hand edit, or a save
    /// made before that collision check existed), but nothing in src/ read
    /// it -- a user in that state got no indication at all until a Save was
    /// refused or they happened to open Settings. This pins that the rail
    /// now surfaces it, the same non-blocking way the set-aside notice
    /// already does.</summary>
    [Fact]
    public void SideFileCollisionWarningAppearsAsANotice()
    {
        using var fx = new ShellFixture(cfg => cfg.SideFileCollisionWarning =
            "monitored_folders_file and destinations_file both point at destinations.json");

        var notice = fx.Shell.Notices.FirstOrDefault(n => n.Key == "config-collision");
        Assert.NotNull(notice);
        Assert.Contains("monitored_folders_file", notice!.Message);
        Assert.Contains("destinations_file", notice.Message);
    }
}

internal static class ShellAsserts
{
    /// <summary>Ready always blanks the viewer at least once.</summary>
    public static bool Viewer0Blanked(this ShellViewModel _, FakeViewer viewer) =>
        viewer.Blanks >= 1;
}
