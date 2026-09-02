using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Records every viewer interaction; Release counts let tests prove
/// the release-before-move ordering.</summary>
public sealed class FakeViewer : IPdfViewer
{
    public List<string> Shown { get; } = new();
    public int Releases { get; private set; }
    public int Blanks { get; private set; }

    /// <summary>When set, ReleaseAsync awaits this — lets a test hold a commit
    /// mid-flight to prove reentrancy handling.</summary>
    public TaskCompletionSource? HoldRelease { get; set; }

    /// <summary>When set, ReleaseAsync throws it — stands in for any unforeseen
    /// fault inside the commit path.</summary>
    public Exception? ThrowOnRelease { get; set; }

    /// <summary>When set, ShowAsync throws it. ReleaseAsync is not on the undo
    /// path at all, so this is the only viewer seam that can stand in for an
    /// unforeseen fault while UNDOING — the load of the restored document is
    /// the last thing OnUndoAsync awaits.</summary>
    public Exception? ThrowOnShow { get; set; }

    public Task ShowAsync(string path)
    {
        Shown.Add(path);
        if (ThrowOnShow is { } boom) throw boom;
        return Task.CompletedTask;
    }

    public async Task ReleaseAsync()
    {
        Releases++;
        if (HoldRelease is { } hold) await hold.Task;
        if (ThrowOnRelease is { } boom) throw boom;
    }

    public void Blank() => Blanks++;
}

public sealed class FakeDialogs : IDialogService
{
    public List<(string Message, string Title)> Warnings { get; } = new();
    public List<(string Message, string Title)> Infos { get; } = new();
    public List<(string Message, string Title)> Confirms { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;
    public string? NextSaveFile { get; set; }
    public string? NextOpenFile { get; set; }
    public string[]? NextOpenFiles { get; set; }
    public string? NextFilePath { get; set; }
    public string? NextFolder { get; set; }

    public void Warn(string message, string title) => Warnings.Add((message, title));
    public void Info(string message, string title) => Infos.Add((message, title));

    public bool Confirm(string message, string title)
    {
        Confirms.Add((message, title));
        return ConfirmAnswer;
    }
    public string? AskSaveFile(string filter, string suggested) => NextSaveFile;
    public string? AskOpenFile(string filter) => NextOpenFile;

    /// <summary>What the last <see cref="AskOpenFile(string, string?)"/> call
    /// asked to start in — recorded, not just forwarded, so a test can prove
    /// a caller opens the picker in a specific folder. Overridden explicitly
    /// rather than left to IDialogService's default (which would silently
    /// drop the argument by calling the single-arg overload instead, the
    /// same relay hazard MainWindow.DialogRelay's own Confirm override
    /// documents) — a default nobody can observe here would be a directory
    /// argument no test in this suite could ever verify was passed.</summary>
    public string? LastOpenFileInitialDirectory { get; private set; }

    public string? AskOpenFile(string filter, string? initialDirectory)
    {
        LastOpenFileInitialDirectory = initialDirectory;
        return NextOpenFile;
    }

    public string[] AskOpenFiles(string filter) =>
        NextOpenFiles ?? (NextOpenFile is { } one ? new[] { one } : Array.Empty<string>());
    public string? AskFilePath(string filter, string suggested) => NextFilePath;
    public string? BrowseFolder(string? startAt) => NextFolder;

    /// <summary>Scripted prompt answers, one per AskPassword call; an empty
    /// queue answers null — the person skipped — so a test that never
    /// expected a prompt sees a needs_password row rather than a hang.
    /// Every request is recorded, so a test can assert on what was asked
    /// and how often, not just on what came back.</summary>
    public Queue<string?> PasswordAnswers { get; } = new();
    public List<PasswordRequest> PasswordRequests { get; } = new();

    public string? AskPassword(PasswordRequest request)
    {
        PasswordRequests.Add(request);
        return PasswordAnswers.Count > 0 ? PasswordAnswers.Dequeue() : null;
    }

    /// <summary>Scripted date-prompt answers, one per AskDate call — the
    /// same shape as PasswordAnswers/PasswordRequests above, for the same
    /// reason: an empty queue answers null (the prompt was cancelled), so a
    /// test that never expected one to be asked sees "nothing added" rather
    /// than a hang, and every request is recorded (the default offered, and
    /// how many files it covered) so a test can assert on what was asked,
    /// not just on what came back.</summary>
    public Queue<string?> DateAnswers { get; } = new();
    public List<(string DefaultDate, int FileCount)> DateRequests { get; } = new();

    public string? AskDate(string defaultDate, int fileCount)
    {
        DateRequests.Add((defaultDate, fileCount));
        return DateAnswers.Count > 0 ? DateAnswers.Dequeue() : null;
    }
}
