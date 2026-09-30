using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Records every viewer interaction; Release counts let tests prove
/// the release-before-move ordering.</summary>
public sealed class FakeViewer : IPdfViewer
{
    public List<string> Shown { get; } = new();

    public event Action<string>? Stopped;

    /// <summary>Stands in for the preview's Edge process dying.</summary>
    public void Stop(string message = "The document preview stopped.") => Stopped?.Invoke(message);
    /// <summary>The page size each document was shown with, in order.</summary>
    public List<OrdoSort.Core.PageSize?> ShownPages { get; } = new();
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

    public Task ShowAsync(string path, OrdoSort.Core.PageSize? page = null)
    {
        Shown.Add(path);
        ShownPages.Add(page);
        if (ThrowOnShow is { } boom) throw boom;
        return Task.CompletedTask;
    }

    public async Task ReleaseAsync()
    {
        Releases++;
        if (HoldRelease is { } hold) await hold.Task;
        if (ThrowOnRelease is { } boom) throw boom;
    }

    /// <summary>When set, Blank throws it: stands in for a viewer whose
    /// window has already been closed and its WebView2 disposed.</summary>
    public Exception? ThrowOnBlank { get; set; }

    public void Blank()
    {
        Blanks++;
        if (ThrowOnBlank is { } boom) throw boom;
    }
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
    /// <summary>Every startAt a BrowseFolder call was handed, in order, so a
    /// test can see where the picker would have opened.</summary>
    public List<string?> BrowseStarts { get; } = new();

    public string? BrowseFolder(string? startAt)
    {
        BrowseStarts.Add(startAt);
        return NextFolder;
    }

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
}

/// <summary>Stands in for the local read-ahead: every document has a "copy"
/// at once, under a folder no real file lives in, unless the test says
/// otherwise. Records what it was asked to keep.</summary>
public sealed class FakeStage : IDocumentStage
{
    public const string CopyFolder = @"C:\ordo-stage-fake";

    public List<List<string>> Wants { get; } = new();

    /// <summary>Documents with no copy: shown from the inbox instead.</summary>
    public HashSet<string> NoCopy { get; } = new(PathIdentity.PathComparer.Instance);

    public bool Disposed { get; private set; }

    public static string CopyOf(string path) => Path.Combine(CopyFolder, Path.GetFileName(path));

    public void Want(IReadOnlyList<string> paths) => Wants.Add(paths.ToList());

    public Task<string?> CopyForAsync(string path, TimeSpan maxWait) =>
        Task.FromResult(NoCopy.Contains(path) ? null : CopyOf(path));

    /// <summary>Documents let go of before their move, in order.</summary>
    public List<string> LetGo { get; } = new();

    /// <summary>Documents to report as changed since they were shown.</summary>
    public HashSet<string> Changed { get; } = new(PathIdentity.PathComparer.Instance);

    public List<string> Discarded { get; } = new();

    public Task LetGoAsync(string path, TimeSpan maxWait)
    {
        LetGo.Add(path);
        return Task.CompletedTask;
    }

    public Task<bool> IsUnchangedAsync(string path) => Task.FromResult(!Changed.Contains(path));

    public void Discard(string path) => Discarded.Add(path);

    public void Dispose() => Disposed = true;
}

/// <summary>A video player with a hand-driven clock: tests set where the video
/// is and how long it is, and read back every call.</summary>
public sealed class FakeVideoPlayer : IVideoPlayer
{
    /// <summary>Every call, in order ("open C:\…", "release", "seek 00:00:05"…).</summary>
    public List<string> Calls { get; } = new();
    public List<string> Opened { get; } = new();
    public int Releases { get; private set; }
    public int WarmUps { get; private set; }

    public bool IsOpen { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Length { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan FrameTime { get; set; } = TimeSpan.FromMilliseconds(40);
    public bool IsMuted { get; private set; } = true;
    public float Rate { get; private set; } = 1f;

    /// <summary>When set, the next open fails with this message.</summary>
    public string? FailNextOpen { get; set; }

    /// <summary>When set, an open waits for it, as a real one waits for the
    /// engine to load.</summary>
    public TaskCompletionSource? HoldOpen { get; set; }

    /// <summary>Holds the open file as a real player does (no delete or
    /// rename sharing), so a move that doesn't wait for the release fails.</summary>
    public bool LockFiles { get; set; }

    private FileStream? _held;

    public event Action<string>? Failed;

    public async Task OpenAsync(string path)
    {
        Calls.Add("open " + path);
        Opened.Add(path);
        if (HoldOpen is { } hold) await hold.Task;
        if (LockFiles)
        {
            _held?.Dispose();
            _held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        IsOpen = true;
        IsPlaying = false;
        Position = TimeSpan.Zero;
        if (FailNextOpen is { } message)
        {
            FailNextOpen = null;
            Failed?.Invoke(message);
        }
    }

    public Task ReleaseAsync()
    {
        Calls.Add("release");
        _held?.Dispose();
        _held = null;
        Releases++;
        IsOpen = false;
        IsPlaying = false;
        return Task.CompletedTask;
    }

    public void WarmUp() => WarmUps++;

    public void Play()
    {
        Calls.Add("play");
        IsPlaying = true;
    }

    public void Pause()
    {
        Calls.Add("pause");
        IsPlaying = false;
    }

    public void Seek(TimeSpan position)
    {
        Calls.Add("seek " + position);
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > Length ? Length : position;
    }

    public void SetRate(float rate)
    {
        Calls.Add("rate " + rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Rate = rate;
    }

    public void SetMuted(bool muted)
    {
        Calls.Add(muted ? "mute" : "unmute");
        IsMuted = muted;
    }

    public void NextFrame()
    {
        Calls.Add("next frame");
        IsPlaying = false;
        Position += FrameTime;
    }
}
