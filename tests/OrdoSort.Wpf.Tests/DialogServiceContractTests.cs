using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>AskOpenFiles ships with a default interface implementation so the
/// eight dialog fakes across this suite and the smoke tool need no edit for a
/// method they do not use. These pin that the default actually behaves — a
/// default nobody tests is a silent hole in eight classes.</summary>
public class DialogServiceContractTests
{
    private sealed class OneFileDialogs : IDialogService
    {
        public string? Answer { get; set; }
        public void Warn(string message, string title) { }
        public void Info(string message, string title) { }
        public bool Confirm(string message, string title) => true;
        public string? AskSaveFile(string filter, string suggestedName) => null;
        public string? AskOpenFile(string filter) => Answer;
        public string? AskFilePath(string filter, string suggestedName) => null;
        public string? BrowseFolder(string? startAt) => null;
    }

    [Fact]
    public void TheDefaultAskOpenFilesFallsBackToTheSingleFilePicker()
    {
        IDialogService dialogs = new OneFileDialogs { Answer = @"C:\in\report.pdf" };
        Assert.Equal(new[] { @"C:\in\report.pdf" }, dialogs.AskOpenFiles("*.*"));
    }

    [Fact]
    public void TheDefaultAskOpenFilesReturnsEmptyWhenCancelled()
    {
        IDialogService dialogs = new OneFileDialogs { Answer = null };
        Assert.Empty(dialogs.AskOpenFiles("*.*"));
    }

    /// <summary>The 2-arg AskOpenFile (filter + an initial directory) ships
    /// with the same kind of default as AskOpenFiles above — most
    /// IDialogService implementers never care where a real dialog would
    /// have started, so they inherit the single-arg behaviour rather than
    /// each needing a throwaway override. Pinned through a minimal
    /// implementer that never overrides it (same reasoning as the two facts
    /// above): the directory argument is silently dropped, which is correct
    /// for a fake but would be the exact relay hazard MainWindow.DialogRelay's
    /// own Confirm override guards against if a real, wrapping IDialogService
    /// ever relied on this default instead of forwarding explicitly.</summary>
    [Fact]
    public void TheDefaultTwoArgAskOpenFileIgnoresTheDirectoryAndFallsBackToTheSingleArgPicker()
    {
        IDialogService dialogs = new OneFileDialogs { Answer = @"C:\in\report.pdf" };
        Assert.Equal(@"C:\in\report.pdf", dialogs.AskOpenFile("*.*", @"C:\some\folder"));
    }

    [Fact]
    public void FakeDialogsCanScriptSeveralFiles()
    {
        var dialogs = new FakeDialogs { NextOpenFiles = new[] { @"C:\a.pdf", @"C:\b.pdf" } };
        Assert.Equal(2, ((IDialogService)dialogs).AskOpenFiles("*.*").Length);
    }

    /// <summary>AskDate ships with the same kind of default (Standardise
    /// names' own addition) — most IDialogService implementers never open
    /// that window, so they inherit "cancel" rather than each needing a
    /// throwaway override. Same reasoning as the AskOpenFiles facts above,
    /// pinned the same way: through a minimal implementer that never
    /// overrides it, not through FakeDialogs (which DOES override it, to
    /// script real answers for StandardiseNamesViewModelTests).</summary>
    [Fact]
    public void TheDefaultAskDateCancelsRatherThanHanging()
    {
        IDialogService dialogs = new OneFileDialogs();
        Assert.Null(dialogs.AskDate("20260115", 3));
    }

    /// <summary>MainWindow's DialogRelay, the dashboard's dialog service,
    /// wrapping whichever real service is current.</summary>
    private static IDialogService RelayTo(IDialogService inner)
    {
        var relayType = typeof(OrdoSort.Wpf.MainWindow).GetNestedType("DialogRelay",
            System.Reflection.BindingFlags.NonPublic)!;
        Func<IDialogService> get = () => inner;
        return (IDialogService)Activator.CreateInstance(relayType, get)!;
    }

    /// <summary>DW-24: the relay forwarded only the members it spelled out.
    /// Everything with a default body (AskOpenFiles, the folder-aware
    /// AskOpenFile, AskPassword, AskDate) silently fell back to that default
    /// instead of reaching the real service: a multi-file picker through it
    /// would have allowed one file, a password prompt would never have shown.
    /// No caller goes through it for those today; now any caller would get
    /// the real thing.</summary>
    [Fact]
    public void TheDashboardsDialogRelayForwardsEveryDefaultedQuestion()
    {
        var inner = new FakeDialogs
        {
            NextOpenFiles = new[] { @"C:\a.pdf", @"C:\b.pdf" },
            NextOpenFile = @"C:\c.pdf",
        };
        inner.PasswordAnswers.Enqueue("secret");
        inner.DateAnswers.Enqueue("20260115");
        var relay = RelayTo(inner);

        Assert.Equal(2, relay.AskOpenFiles("*.*").Length);
        Assert.Equal(@"C:\c.pdf", relay.AskOpenFile("*.*", @"C:\start"));
        Assert.Equal(@"C:\start", inner.LastOpenFileInitialDirectory);
        Assert.Equal("secret", relay.AskPassword(new OrdoSort.Core.PasswordRequest("a.pdf", null, false)));
        Assert.Equal("20260115", relay.AskDate("20260101", 2));
    }
}
