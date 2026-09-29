namespace OrdoSort.Core;

/// <summary>
/// What the filing loop needs from the local read-ahead (spec
/// 2026-09-29-filing-loop-design.md): copies of the documents about to be
/// shown, made on the local disk while the user is still reading the one
/// before, so the preview never waits on the network and never holds an
/// inbox file open.
/// </summary>
public interface IDocumentStage : IDisposable
{
    /// <summary>The documents worth keeping copies of, most urgent first.
    /// Copies are started in this order; copies of anything not listed are
    /// deleted.</summary>
    void Want(IReadOnlyList<string> paths);

    /// <summary>A local copy of <paramref name="path"/> that still matches
    /// the inbox file, waiting up to <paramref name="maxWait"/> for one still
    /// being copied. Null means "show the inbox file itself": not copied in
    /// time, too big, the copy failed, or the file changed since. Never
    /// throws.</summary>
    Task<string?> CopyForAsync(string path, TimeSpan maxWait);

    /// <summary>The document is about to be moved: no new copy of it is
    /// started, and one still being made is waited for (up to
    /// <paramref name="maxWait"/>), because the copy holds the inbox file
    /// open and the move would fail against it. Never throws.</summary>
    Task LetGoAsync(string path, TimeSpan maxWait);

    /// <summary>False when <paramref name="path"/> was shown from a copy and
    /// the inbox file has changed since: what is about to be filed is not
    /// what the user looked at. True when unchanged, or never copied (the
    /// inbox file itself was shown). Never throws.</summary>
    Task<bool> IsUnchangedAsync(string path);

    /// <summary>The document has moved: its copy is deleted, and it is not
    /// copied again unless it is asked for (an undo brings it back).</summary>
    void Discard(string path);
}

/// <summary>
/// The real read-ahead: one folder per session under
/// <see cref="Root"/>, one copy at a time in the background.
///
/// A copy is only ever handed out while the inbox file still has the size
/// and last-write time it had when the copy started; anything else is
/// treated as a new document and copied again, so the preview can never
/// show a stale page. Every failure (a locked file, a dead share, a full
/// disk) degrades to "show the inbox file", which is what OrdoSort did
/// before this class existed, and is never reported to the user.
///
/// The session folder holds an open, unshared lock file for as long as the
/// stage lives. A folder whose lock can be taken belongs to no running
/// OrdoSort (it crashed, or was killed mid-session) and is swept when the
/// next stage starts, so copies of documents never pile up on the disk.
/// </summary>
public sealed class DocumentStage : IDocumentStage
{
    /// <summary>Where sessions' copies go: under the user's own local
    /// profile, never beside config.json (which may be on a share other
    /// stations read).</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "stage");

    /// <summary>The folder in use: <see cref="DefaultRoot"/>, except in the
    /// test run, which points it at its own temp folder.</summary>
    public static string Root { get; set; } = DefaultRoot;

    /// <summary>Bigger files are shown straight from the inbox: a copy of a
    /// 500 MB scan would still be running when the user got to it.</summary>
    public const long DefaultMaxFileBytes = 200L * 1024 * 1024;

    /// <summary>The most one session keeps on the disk at once.</summary>
    public const long DefaultMaxTotalBytes = 1024L * 1024 * 1024;

    internal const string LockName = ".lock";

    /// <summary>How old a folder with no lock file must be before the sweep
    /// takes it: a new session creates its folder a moment before its lock.</summary>
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(1);
    private const string PartialSuffix = ".partial";

    private sealed class Entry
    {
        public Entry(string source) => Source = source;
        public string Source { get; }
        public string? CopyPath;
        public string? Folder;
        public long Length;
        public DateTime WriteUtc;
        public bool Started;
        public readonly TaskCompletionSource<string?> Ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly string _dir;
    private readonly FileStream _lock;
    private readonly Action<string, string> _copy;
    private readonly long _maxFileBytes;
    private readonly long _maxTotalBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(PathIdentity.PathComparer.Instance);
    private List<string> _wanted = new();
    // documents being moved: never copied again until asked for by CopyForAsync
    private readonly HashSet<string> _leaving = new(PathIdentity.PathComparer.Instance);
    private bool _pumping;
    private bool _disposed;
    private int _nextFolder;

    /// <summary>The session's own folder, for tests and for the sweep.</summary>
    public string Folder => _dir;

    /// <param name="root">The parent of every session's folder.</param>
    /// <param name="copy">Copies source to destination; File.Copy unless a
    /// test hands in a slow or failing one.</param>
    public DocumentStage(string root, Action<string, string>? copy = null,
        long maxFileBytes = DefaultMaxFileBytes, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        _copy = copy ?? ((source, destination) => File.Copy(source, destination, overwrite: true));
        _maxFileBytes = maxFileBytes;
        _maxTotalBytes = maxTotalBytes;
        _dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lock = new FileStream(Path.Combine(_dir, LockName), FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        // local disk only, but never on the caller's (the UI) thread
        _ = Task.Run(() => SweepOrphans(root));
    }

    public void Want(IReadOnlyList<string> paths)
    {
        List<Entry> evicted;
        lock (_gate)
        {
            if (_disposed) return;
            _wanted = paths.Where(p => !_leaving.Contains(p)).Distinct(PathIdentity.PathComparer.Instance).ToList();
            var keep = new HashSet<string>(_wanted, PathIdentity.PathComparer.Instance);
            // a copy still being made is left alone (the pump drops it when it
            // lands), and so is one whose document is on its way out: whether
            // it still matches is asked just before the move
            evicted = _entries.Values
                .Where(e => !keep.Contains(e.Source) && !_leaving.Contains(e.Source) && e.Ready.Task.IsCompleted)
                .ToList();
            foreach (var entry in evicted) _entries.Remove(entry.Source);
        }
        foreach (var entry in evicted) DeleteCopy(entry);
        Pump();
    }

    public async Task<string?> CopyForAsync(string path, TimeSpan maxWait)
    {
        Entry entry;
        lock (_gate)
        {
            if (_disposed) return null;
            // shown again (an undo brought it back): copyable again
            _leaving.Remove(path);
            // the document asked for jumps the queue
            _wanted.RemoveAll(p => PathIdentity.Same(p, path));
            _wanted.Insert(0, path);
            if (!_entries.TryGetValue(path, out entry!))
            {
                entry = new Entry(path);
                _entries[path] = entry;
            }
        }
        Pump();

        var ready = entry.Ready.Task;
        if (await Task.WhenAny(ready, Task.Delay(maxWait)).ConfigureAwait(false) != ready) return null;
        var copy = await ready.ConfigureAwait(false);
        if (copy is null) return null;
        // the inbox file can be replaced after its copy was made (a rescan
        // that re-delivers it, a user saving over it): a stat on the share,
        // off the caller's thread
        var current = await Task.Run(() => Stat(entry.Source)).ConfigureAwait(false);
        if (current is { } now && now.Length == entry.Length && now.WriteUtc == entry.WriteUtc
            && File.Exists(copy))
            return copy;
        Forget(entry);
        return null;
    }

    public async Task LetGoAsync(string path, TimeSpan maxWait)
    {
        Task? copying = null;
        lock (_gate)
        {
            if (_disposed) return;
            _leaving.Add(path);
            _wanted.RemoveAll(p => PathIdentity.Same(p, path));
            if (_entries.TryGetValue(path, out var entry) && entry.Started && !entry.Ready.Task.IsCompleted)
                copying = entry.Ready.Task;
        }
        if (copying is not null)
            await Task.WhenAny(copying, Task.Delay(maxWait)).ConfigureAwait(false);
    }

    public void Discard(string path)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_entries.Remove(path, out entry)) return;
        }
        DeleteCopy(entry);
    }

    public async Task<bool> IsUnchangedAsync(string path)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out entry) || entry.CopyPath is null) return true;
        }
        var current = await Task.Run(() => Stat(path)).ConfigureAwait(false);
        return current is { } now && now.Length == entry.Length && now.WriteUtc == entry.WriteUtc;
    }

    /// <summary>Starts the copy loop if it isn't running. One copy at a
    /// time: two at once off one share are slower than one after another,
    /// and the one the user needs next must not share the link.</summary>
    private void Pump()
    {
        lock (_gate)
        {
            if (_pumping || _disposed) return;
            _pumping = true;
        }
        _ = Task.Run(PumpLoop);
    }

    private void PumpLoop()
    {
        while (true)
        {
            Entry? next = null;
            lock (_gate)
            {
                if (!_disposed)
                {
                    foreach (var path in _wanted)
                    {
                        if (_leaving.Contains(path)) continue;
                        if (!_entries.TryGetValue(path, out var entry))
                        {
                            entry = new Entry(path);
                            _entries[path] = entry;
                        }
                        if (!entry.Started)
                        {
                            entry.Started = true;
                            next = entry;
                            break;
                        }
                    }
                }
                if (next is null)
                {
                    _pumping = false;
                    return;
                }
            }
            CopyOne(next);
        }
    }

    private void CopyOne(Entry entry)
    {
        string? result = null;
        try
        {
            result = TryCopy(entry);
        }
        catch (Exception)
        {
            // a locked file, a dropped share, a full disk: the inbox file is shown instead
            result = null;
        }
        bool drop;
        lock (_gate)
        {
            // disposed, evicted, or forgotten while it copied
            drop = _disposed || !_entries.TryGetValue(entry.Source, out var live) || !ReferenceEquals(live, entry);
            if (!drop) entry.CopyPath = result;
        }
        if (drop) DeleteCopy(entry);
        entry.Ready.TrySetResult(drop ? null : result);
    }

    private string? TryCopy(Entry entry)
    {
        if (Stat(entry.Source) is not { } before) return null;
        if (before.Length > _maxFileBytes) return null;
        lock (_gate)
        {
            var held = _entries.Values.Where(e => e.CopyPath is not null).Sum(e => e.Length);
            if (held + before.Length > _maxTotalBytes) return null;
            entry.Folder = Path.Combine(_dir, (++_nextFolder).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(entry.Folder);
        // the copy keeps the document's own name: Edge shows it in its toolbar
        var final = Path.Combine(entry.Folder, Path.GetFileName(entry.Source));
        var partial = final + PartialSuffix;
        _copy(entry.Source, partial);
        // changed while it copied: what was copied may be half of each version
        if (Stat(entry.Source) is not { } after || after != before)
        {
            DeleteCopy(entry);
            return null;
        }
        File.Move(partial, final, overwrite: true);
        entry.Length = before.Length;
        entry.WriteUtc = before.WriteUtc;
        return final;
    }

    private readonly record struct FileStamp(long Length, DateTime WriteUtc);

    private static FileStamp? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A copy that no longer matches its document: dropped, so the
    /// next ask copies the document afresh.</summary>
    private void Forget(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Source, out var live) && ReferenceEquals(live, entry))
                _entries.Remove(entry.Source);
        }
        DeleteCopy(entry);
    }

    private static void DeleteCopy(Entry entry)
    {
        if (entry.Folder is not { } folder) return;
        // Edge can still have it open for a moment: left for the session's
        // own delete, or the next start-up's sweep
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            _wanted.Clear();
            _leaving.Clear();
        }
        _lock.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { }
    }

    /// <summary>Deletes every session folder under <paramref name="root"/>
    /// whose OrdoSort is no longer running: its lock file is gone or can be
    /// taken. A folder still locked belongs to a running session (another
    /// OrdoSort on this PC) and is left alone.</summary>
    public static void SweepOrphans(string root)
    {
        string[] folders;
        try { folders = Directory.GetDirectories(root); }
        catch (Exception) { return; }
        foreach (var folder in folders)
        {
            try
            {
                var lockPath = Path.Combine(folder, LockName);
                if (File.Exists(lockPath))
                {
                    // throws while its session holds it; closed again before
                    // the delete, which Windows refuses on a file held open
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                else if (Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow - OrphanGrace)
                {
                    // a session that has made its folder but not yet its lock
                    continue;
                }
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // held, or not deletable right now: the next sweep tries again
            }
        }
    }
}
