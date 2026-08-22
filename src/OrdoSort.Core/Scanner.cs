namespace OrdoSort.Core;

/// <summary>Inbox scanning: which files enter the queue, which are ignored.</summary>
public static class Scanner
{
    public sealed record ScanResult(
        IReadOnlyList<string> Matching, int IgnoredCount, string Error)
    {
        public int Count => Matching.Count;
    }

    private static long SafeSize(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    // QC-13: unlike .Length, FileInfo.LastWriteTimeUtc does NOT throw for a
    // file gone by read time -- it silently returns this fixed sentinel
    // instead. Before this, files.Min(SafeMtime) latched that 1601 date and
    // reported a ~155,000-day-old folder over one vanished file. Internal so
    // the listed-then-vanished case (not reproducible as a real race on this
    // machine) can be pinned directly -- capture Directory.GetFiles's
    // result, delete the file, then read it. See PipelineTests.
    private static readonly DateTime MissingFileSentinel = DateTime.FromFileTimeUtc(0);

    internal static long? SafeMtime(string p)
    {
        try
        {
            var t = new FileInfo(p).LastWriteTimeUtc;
            return t == MissingFileSentinel ? null : t.Ticks;
        }
        catch { return null; }
    }

    /// <summary>Which files the inbox picks up: insert mode needs the "--"
    /// marker to splice into; every other mode works on ANY pdf.</summary>
    public static bool Eligible(string filename, string mode) =>
        mode == Naming.ModeInsert
            ? Naming.InboxRegex().IsMatch(filename)
            : filename.EndsWith(Naming.PdfExt, StringComparison.OrdinalIgnoreCase);

    /// <summary>Snapshot the inbox. Never throws — problems come back in Error.</summary>
    public static ScanResult Scan(string inbox, string sort = "size_desc",
        string mode = Naming.ModeInsert)
    {
        if (string.IsNullOrWhiteSpace(inbox))
            return new ScanResult(Array.Empty<string>(), 0, "No inbox folder is configured yet.");
        if (!Directory.Exists(inbox))
            return File.Exists(inbox)
                ? new ScanResult(Array.Empty<string>(), 0, $"Inbox path is not a folder: {inbox}")
                : new ScanResult(Array.Empty<string>(), 0, $"Inbox folder does not exist: {inbox}");

        string[] files;
        try { files = Directory.GetFiles(inbox); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScanResult(Array.Empty<string>(), 0, $"Can't read the inbox folder: {ex.Message}");
        }

        var matching = files
            .Where(f => Eligible(System.IO.Path.GetFileName(f), mode))
            .ToList();
        var ignored = files.Length - matching.Count;

        matching = sort switch
        {
            "filename_desc" => matching.OrderByDescending(f => System.IO.Path.GetFileName(f).ToLowerInvariant()).ToList(),
            // QC-13: an unknown mtime (SafeMtime -- a file gone by read time)
            // must never read as the oldest/newest file it's sorted by; ??
            // pushes it to the back of either direction instead of defaulting
            // to 0, the front of mtime_asc.
            "mtime_asc" => matching.OrderBy(f => SafeMtime(f) ?? long.MaxValue).ToList(),
            "mtime_desc" => matching.OrderByDescending(f => SafeMtime(f) ?? long.MinValue).ToList(),
            "size_asc" => matching.OrderBy(SafeSize).ThenBy(f => System.IO.Path.GetFileName(f).ToLowerInvariant()).ToList(),
            "size_desc" => matching.OrderByDescending(SafeSize).ThenBy(f => System.IO.Path.GetFileName(f).ToLowerInvariant()).ToList(),
            _ => matching.OrderBy(f => System.IO.Path.GetFileName(f).ToLowerInvariant()).ToList(),
        };
        return new ScanResult(matching, ignored, "");
    }

    /// <summary>Files (any name) sitting in a folder — the set-aside alert
    /// count. Unset/missing/unreadable folders count as 0; never throws.</summary>
    public static int CountFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        try { return Directory.GetFiles(folder).Length; } catch { return 0; }
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
    /// skipping any SafeMtime couldn't read — null, not a number, when none
    /// are known. Internal so DeferredSummary's Min-skips-unknown behaviour
    /// is pinnable without a real vanished-mid-scan race.</summary>
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
            var files = Directory.GetFiles(folder);
            if (files.Length == 0) return new DeferredInfo(0, null);
            return new DeferredInfo(files.Length, OldestAgeDays(files.Select(SafeMtime), now ?? DateTime.Now));
        }
        catch { return new DeferredInfo(0, null); }
    }
}
