using System.Collections.Concurrent;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>
/// Photos and GIFs in the filing loop (spec 2026-09-29-media-filing-loop).
/// A photo is named by the date it was taken, so the shell reads that date
/// once per file (off the UI thread, from the local copy when there is one),
/// shows it above the name box, and hands the same date to the move.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Shown in the name preview while a photo's date is still being
    /// read; the move itself never uses it.</summary>
    internal const string StampPending = "YYYYMMDD";

    private readonly IDateTakenReader _dates;
    private readonly IPreviewImages _previews;
    private readonly bool _ownsPreviews;

    /// <summary>Each photo's date, read once: the preview, the date line and
    /// the move (on the filing thread) all see the same one.</summary>
    private readonly ConcurrentDictionary<string, DateTaken> _takenDates =
        new(PathIdentity.PathComparer.Instance);

    private string _takenLine = "";
    /// <summary>"Taken 29 Sep 2026 · from the photo"; empty for a PDF.</summary>
    public string TakenLine
    {
        get => _takenLine;
        private set { if (Set(ref _takenLine, value)) Raise(nameof(HasTakenLine)); }
    }

    public bool HasTakenLine => TakenLine.Length > 0;

    private bool _takenIsGuess;
    /// <summary>The date is only the file's modified date, often the day it
    /// was copied rather than taken: shown as a warning.</summary>
    public bool TakenIsGuess { get => _takenIsGuess; private set => Set(ref _takenIsGuess, value); }

    private bool IsMedia(string? path) => path is not null && MediaFiles.IsMedia(path, _cfg.Media);

    /// <summary>HEIC previews, made once per photo and ahead of time for the
    /// next ones, so a key press never waits for a decode.</summary>
    private readonly ConcurrentDictionary<string, Task<string>> _previewTasks =
        new(PathIdentity.PathComparer.Instance);

    /// <summary>How many photos ahead of the one on screen get their date and
    /// preview ready: the same reach as the read-ahead copies.</summary>
    private const int MediaLookAhead = 2;

    /// <summary>Read on first use, from wherever the caller can read fastest.
    /// A file that couldn't be read at all is not kept: the next read, the
    /// move's own, tries again rather than filing it under today.</summary>
    private DateTaken TakenFor(string path, string readFrom)
    {
        if (_takenDates.TryGetValue(path, out var known)) return known;
        var taken = _dates.Read(readFrom);
        if (taken.Source == DateTakenSource.Unreadable) return taken;
        return _takenDates.GetOrAdd(path, taken);
    }

    /// <summary>The date for the name preview: null for a PDF, the pending
    /// placeholder while a photo's date is still being read.</summary>
    private string? PreviewStamp(string path)
    {
        if (!IsMedia(path)) return null;
        return _takenDates.TryGetValue(path, out var taken) ? taken.Stamp : StampPending;
    }

    /// <summary>What the move needs; reads the date itself if the key was
    /// pressed before the screen had it. Runs on the filing thread.</summary>
    private MediaTarget? MediaTargetFor(string path) =>
        MediaFiles.TargetFor(path, _cfg.Media, () => TakenFor(path, path));

    /// <summary>The date line as far as it is known the moment a document
    /// comes on screen: nothing for a PDF, the date when it has been read,
    /// otherwise a note that it is being read.</summary>
    private void ShowKnownTaken(string path)
    {
        if (!IsMedia(path))
        {
            TakenLine = "";
            TakenIsGuess = false;
        }
        else if (_takenDates.TryGetValue(path, out var taken))
        {
            TakenLine = DescribeTaken(taken);
            TakenIsGuess = IsGuess(taken);
        }
        else
        {
            TakenLine = "Reading the date…";
            TakenIsGuess = false;
        }
    }

    private static bool IsGuess(DateTaken taken) =>
        taken.Source is DateTakenSource.Modified or DateTakenSource.Unreadable;

    /// <summary>Reads a photo's date if it isn't known yet, then shows it and
    /// the name it gives. Never throws: the move reads it again if needed.
    /// Not awaited by the load, so a key press never waits for it.</summary>
    private async Task ShowTakenAsync(string path, string readFrom)
    {
        DateTaken taken;
        try
        {
            taken = _takenDates.TryGetValue(path, out var known)
                ? known
                : await _scheduler.Run(() => TakenFor(path, readFrom));
        }
        catch (Exception)
        {
            return;
        }
        // moved on while it was read: that document shows its own
        if (!PathIdentity.Same(ShownPath, path)) return;
        TakenLine = DescribeTaken(taken);
        TakenIsGuess = IsGuess(taken);
        UpdatePreview();
    }

    /// <summary>Starts the date read and HEIC preview for the next photos in
    /// the queue while this one is read. Fire and forget: both are made again
    /// on the spot if they aren't ready in time.</summary>
    private void PrepareNextMedia()
    {
        var queue = _session.Queue;
        var start = _shownIndex + 1;
        for (var i = start; i < queue.Count && i < start + MediaLookAhead; i++)
        {
            var next = queue[i];
            if (!IsMedia(next)) continue;
            if (!_takenDates.ContainsKey(next)) _ = ReadAheadDateAsync(next);
            if (_previews.NeedsConversion(next)) _ = PreviewTaskFor(next, next);
        }
    }

    private async Task ReadAheadDateAsync(string path)
    {
        try { await _scheduler.Run(() => TakenFor(path, path)); }
        catch (Exception) { }
    }

    private Task<string> PreviewTaskFor(string path, string readFrom) =>
        _previewTasks.GetOrAdd(path, _ => _scheduler.Run(() => _previews.Prepare(readFrom)));

    /// <summary>A photo filed or set aside: its date and preview are done
    /// with (an undo reads them again).</summary>
    private void ForgetMedia(string path)
    {
        _takenDates.TryRemove(path, out _);
        _previewTasks.TryRemove(path, out _);
    }

    internal static string DescribeTaken(DateTaken taken)
    {
        var date = taken.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (taken.Source == DateTakenSource.Unreadable)
            return $"Couldn't read the photo's date · {date} (today) for now";
        var from = taken.Source switch
        {
            DateTakenSource.Metadata => "from the photo",
            DateTakenSource.FileName => "from the file name",
            _ => "from the modified date",
        };
        return $"Taken {date} · {from}";
    }

    /// <summary>What Edge is pointed at: the file itself, or a JPEG made from
    /// a photo Edge can't show (HEIC), made ahead when it could be. Never
    /// throws.</summary>
    private async Task<string> PreviewSourceAsync(string path, string source)
    {
        if (!_previews.NeedsConversion(path)) return source;
        try
        {
            return await PreviewTaskFor(path, source);
        }
        catch (Exception)
        {
            _previewTasks.TryRemove(path, out _);
            return source;
        }
    }
}
