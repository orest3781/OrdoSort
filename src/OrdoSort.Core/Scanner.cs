namespace OrdoSort.Core;

/// <summary>Inbox scanning: which files enter the queue, which are ignored.</summary>
public static class Scanner
{
    public sealed record ScanResult(
        IReadOnlyList<string> Matching, int IgnoredCount, string Error)
    {
        public int Count => Matching.Count;
    }

    /// <summary>One file as the folder listing reported it. Size and modified
    /// time come with the listing, so nothing asks the disk about each file
    /// again: on a share that second question was one round trip per file,
    /// after every filed document (2,000 files over a loopback share:
    /// 1,170 ms, against 8 ms from the listing). The listing can lag behind
    /// a file that is still being written, so such a file may sort by an
    /// older size or time until its writer closes it.</summary>
    /// <param name="ModifiedTicks">UTC ticks, or null when Windows has no
    /// time for the file (see <see cref="ModifiedTicksOf"/>).</param>
    internal readonly record struct ListedFile(string Path, long Length, long? ModifiedTicks);

    // QC-13: Windows reports this fixed date for a time it doesn't have. Read
    // as a real one it made the set-aside folder look ~155,000 days old.
    private static readonly DateTime MissingFileSentinel = DateTime.FromFileTimeUtc(0);

    internal static long? ModifiedTicksOf(DateTime lastWriteUtc) =>
        lastWriteUtc == MissingFileSentinel ? null : lastWriteUtc.Ticks;

    /// <summary>Which files the inbox picks up: insert mode needs the "--"
    /// marker to splice into; every other mode works on ANY pdf. Photos and
    /// GIFs, when turned on, are picked up in every mode: they are named by
    /// date taken, not by the mode.</summary>
    public static bool Eligible(string filename, string mode, MediaSettings? media = null)
    {
        if (MediaFiles.IsMedia(filename, media)) return true;
        return mode == Naming.ModeInsert
            ? Naming.InboxRegex().IsMatch(filename)
            : filename.EndsWith(Naming.PdfExt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot the inbox. Never throws — problems come back in Error.</summary>
    public static ScanResult Scan(string inbox, string sort = "size_desc",
        string mode = Naming.ModeInsert, MediaSettings? media = null)
    {
        if (string.IsNullOrWhiteSpace(inbox))
            return new ScanResult(Array.Empty<string>(), 0, "No inbox folder is configured yet.");
        if (!Directory.Exists(inbox))
            return File.Exists(inbox)
                ? new ScanResult(Array.Empty<string>(), 0, $"Inbox path is not a folder: {inbox}")
                : new ScanResult(Array.Empty<string>(), 0, $"Inbox folder does not exist: {inbox}");

        List<ListedFile> files;
        // Hidden, system and dot files are not documents: not queued, and
        // not counted as ignored (see ListVisible).
        try { files = ListVisible(inbox); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScanResult(Array.Empty<string>(), 0, $"Can't read the inbox folder: {ex.Message}");
        }

        var matching = files
            .Where(f => Eligible(System.IO.Path.GetFileName(f.Path), mode, media))
            .ToList();
        var ignored = files.Count - matching.Count;
        return new ScanResult(Order(matching, sort), ignored, "");
    }

    /// <summary>The paths of <paramref name="files"/> in the order the
    /// <paramref name="sort"/> setting asks for. An unknown key reads as
    /// filename A to Z. No disk access: it sorts on what the listing said.</summary>
    internal static List<string> Order(IEnumerable<ListedFile> files, string sort)
    {
        static string Name(ListedFile f) => System.IO.Path.GetFileName(f.Path).ToLowerInvariant();

        var ordered = sort switch
        {
            "filename_desc" => files.OrderByDescending(Name),
            // QC-13: a file with no known time must never read as the oldest
            // or the newest; ?? sends it to the back in either direction.
            "mtime_asc" => files.OrderBy(f => f.ModifiedTicks ?? long.MaxValue),
            "mtime_desc" => files.OrderByDescending(f => f.ModifiedTicks ?? long.MinValue),
            "size_asc" => files.OrderBy(f => f.Length).ThenBy(Name),
            "size_desc" => files.OrderByDescending(f => f.Length).ThenBy(Name),
            _ => files.OrderBy(Name),
        };
        return ordered.Select(f => f.Path).ToList();
    }

    /// <summary>Files (any name) sitting in a folder — the set-aside alert
    /// count. Unset/missing/unreadable folders count as 0; never throws.</summary>
    public static int CountFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        try { return ListVisible(folder).Count; } catch { return 0; }
    }

    private static readonly EnumerationOptions VisibleOnly = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = false,
    };

    /// <summary>A folder's files, leaving out what isn't a document: hidden
    /// and system files (a share's desktop.ini and Thumbs.db) and dot-files
    /// (a .gitkeep placeholder), which would otherwise show as set-aside
    /// "files waiting" or inbox "other files ignored" — and a hidden PDF is
    /// not queued for filing (owner's decision, 2026-09-23). The attribute
    /// test, each size and each modified time all come from the directory
    /// listing itself — no extra per-file round trip over SMB.
    /// IgnoreInaccessible is off so an unreadable folder still throws, as
    /// Directory.GetFiles did, and the callers' own catch decides. Each path
    /// is spelled as Directory.GetFiles spelled it (the folder as given, then
    /// the name): these paths become the session queue and the history's
    /// original_path.</summary>
    internal static List<ListedFile> ListVisible(string folder)
    {
        var listed = new List<ListedFile>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", VisibleOnly))
        {
            if (info.Name.StartsWith('.')) continue;
            listed.Add(new ListedFile(
                System.IO.Path.Join(folder, info.Name), info.Length, ModifiedTicksOf(info.LastWriteTimeUtc)));
        }
        return listed;
    }

    /// <summary>Set-aside folder summary: how many files, and how old the
    /// oldest is in whole days — age is the point in a retention shop. Never
    /// throws; empty/missing/unreadable → (0, null). OldestAgeDays is
    /// nullable, not a number, when no file's mtime could be read (QC-13) —
    /// the same "rather than lying with 0 bytes or a 1601 date" direction
    /// docs/superpowers/specs/2026-08-19-filename-list-upgrade-design.md
    /// chose for FilenameList.FileRow. "now" is injectable for tests.</summary>
    public sealed record DeferredInfo(int Count, int? OldestAgeDays);

    /// <summary>Oldest-file age in whole days from a set of per-file mtimes,
    /// skipping any that aren't known — null, not a number, when none are.
    /// Internal so DeferredSummary's Min-skips-unknown behaviour is pinnable
    /// without a real file that has no time.</summary>
    internal static int? OldestAgeDays(IEnumerable<long?> mtimes, DateTime now)
    {
        var known = mtimes.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        if (known.Count == 0) return null;
        var oldest = known.Min();   // ticks; smallest = oldest
        var age = now - new DateTime(oldest, DateTimeKind.Utc).ToLocalTime();
        return Math.Max(0, (int)age.TotalDays);
    }

    public static DeferredInfo DeferredSummary(string? folder, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new DeferredInfo(0, null);
        try
        {
            var files = ListVisible(folder);
            if (files.Count == 0) return new DeferredInfo(0, null);
            return new DeferredInfo(files.Count, OldestAgeDays(files.Select(f => f.ModifiedTicks), now ?? DateTime.Now));
        }
        catch { return new DeferredInfo(0, null); }
    }
}
