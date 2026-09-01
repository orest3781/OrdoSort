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
}
