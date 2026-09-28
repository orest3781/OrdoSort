namespace OrdoSort.Wpf.Services;

/// <summary>Live folder monitoring: any Created/Deleted/Renamed restarts a
/// 1.5 s debounce (lets a file finish downloading before we rescan), and a
/// periodic poll backstops network shares where FileSystemWatcher change
/// notifications never fire (SMB). The poll cadence is the config's
/// poll_seconds — see <see cref="OrdoSort.Core.Config.PollSeconds"/>.
///
/// <see cref="Activity"/> is raised on the provided SynchronizationContext
/// (the UI thread in the app) or inline when none is given (tests).</summary>
public sealed class FolderWatchService : IDisposable
{
    // Keyed by folder. SetFolders runs on a pool thread while Dispose and
    // the watchers' own error callbacks run elsewhere, so every access holds
    // _gate (QC-27: a plain List raced them).
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    // Folders whose watch Windows ended with an error, waiting for _retry.
    private readonly HashSet<string> _broken = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ITimer _retry;
    private readonly int _retryMs;
    private readonly ITimer _debounce;
    private readonly ITimer _poll;
    private readonly int _debounceMs;
    private readonly SynchronizationContext? _context;
    private volatile bool _disposed;

    public event Action? Activity;

    /// <summary>Raised by the poll timer, just before its <see cref="Activity"/>:
    /// the refresh that follows is the periodic one, not a reaction to a file
    /// moving in the inbox (ShellViewModel sweeps watched folders mid-session
    /// only then).</summary>
    public event Action? Polled;

    /// <param name="time">The clock the timers run on; null is the real
    /// clock. Tests pass a manual one so no test sleeps.</param>
    /// <param name="retryMs">How often a watch Windows ended is tried again
    /// until its folder is back.</param>
    public FolderWatchService(int debounceMs = 1500,
        int pollMs = OrdoSort.Core.Config.DefaultPollSeconds * 1000,
        SynchronizationContext? context = null, TimeProvider? time = null, int retryMs = 5000)
    {
        _debounceMs = debounceMs;
        _retryMs = retryMs;
        _context = context;
        var clock = time ?? TimeProvider.System;
        _debounce = clock.CreateTimer(_ => RaiseActivity(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var interval = TimeSpan.FromMilliseconds(pollMs);
        _poll = clock.CreateTimer(_ => { RaisePolled(); RaiseActivity(); }, null, interval, interval);
        _retry = clock.CreateTimer(_ => RetryBroken(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>(Re)build the watcher set. Blank or missing folders are
    /// skipped — a not-yet-created deferred folder must not throw.</summary>
    public void SetFolders(params string?[] folders)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
            _broken.Clear();
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                Watch(folder);
            }
        }
    }

    /// <summary>True while a live watch covers <paramref name="folder"/>.</summary>
    internal bool WatchingNow(string folder)
    {
        lock (_gate) return _watchers.ContainsKey(folder);
    }

    // Callers hold _gate.
    private void Watch(string folder)
    {
        var w = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            // The most Windows allows, so a burst of files overflows it later.
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };
        w.Created += (_, _) => Poke();
        w.Deleted += (_, _) => Poke();
        w.Renamed += (_, _) => Poke();
        w.Error += (_, _) => OnWatchEnded(folder, w);
        _watchers[folder] = w;
    }

    /// <summary>Windows ended this watch: its buffer overflowed, or the folder
    /// (often a share) went away (QC-20). Events may have been missed, so
    /// rescan now; and set the watch up again once the folder is back,
    /// rather than leaving only the poll.</summary>
    private void OnWatchEnded(string folder, FileSystemWatcher watcher)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_watchers.TryGetValue(folder, out var current) && ReferenceEquals(current, watcher))
                _watchers.Remove(folder);
            watcher.Dispose();
            _broken.Add(folder);
            _retry.Change(TimeSpan.FromMilliseconds(_retryMs), Timeout.InfiniteTimeSpan);
        }
        Poke();
    }

    private void RetryBroken()
    {
        var rearmed = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var folder in _broken.ToList())
            {
                if (!Directory.Exists(folder)) continue;
                try { Watch(folder); }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    continue;   // not back yet; try again next time
                }
                _broken.Remove(folder);
                rearmed = true;
            }
            if (_broken.Count > 0)
                _retry.Change(TimeSpan.FromMilliseconds(_retryMs), Timeout.InfiniteTimeSpan);
        }
        // anything that landed while the watch was down
        if (rearmed) Poke();
    }

    /// <summary>Restart the debounce window; fires <see cref="Activity"/> once
    /// when the burst goes quiet.</summary>
    public void Poke()
    {
        if (_disposed) return;
        _debounce.Change(TimeSpan.FromMilliseconds(_debounceMs), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Change the backstop poll period live (Settings adjusted it).</summary>
    public void SetPollInterval(int pollMs)
    {
        if (_disposed) return;
        _poll.Change(TimeSpan.FromMilliseconds(pollMs), TimeSpan.FromMilliseconds(pollMs));
    }

    private void RaisePolled()
    {
        if (_disposed) return;
        if (_context is null) Polled?.Invoke();
        else _context.Post(_ => { if (!_disposed) Polled?.Invoke(); }, null);
    }

    private void RaiseActivity()
    {
        if (_disposed) return;
        if (_context is null) Activity?.Invoke();
        else _context.Post(_ => { if (!_disposed) Activity?.Invoke(); }, null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
            _broken.Clear();
        }
        _debounce.Dispose();
        _poll.Dispose();
        _retry.Dispose();
    }
}
