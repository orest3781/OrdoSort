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
    public string[] AskOpenFiles(string filter) =>
        NextOpenFiles ?? (NextOpenFile is { } one ? new[] { one } : Array.Empty<string>());
    public string? AskFilePath(string filter, string suggested) => NextFilePath;
    public string? BrowseFolder(string? startAt) => NextFolder;
}
