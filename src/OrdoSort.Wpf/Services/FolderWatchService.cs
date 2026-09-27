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
    private readonly List<FileSystemWatcher> _watchers = new();
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

    /// <param name="time">The clock both timers run on; null is the real
    /// clock. Tests pass a manual one so no test sleeps.</param>
    public FolderWatchService(int debounceMs = 1500,
        int pollMs = OrdoSort.Core.Config.DefaultPollSeconds * 1000,
        SynchronizationContext? context = null, TimeProvider? time = null)
    {
        _debounceMs = debounceMs;
        _context = context;
        var clock = time ?? TimeProvider.System;
        _debounce = clock.CreateTimer(_ => RaiseActivity(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var interval = TimeSpan.FromMilliseconds(pollMs);
        _poll = clock.CreateTimer(_ => { RaisePolled(); RaiseActivity(); }, null, interval, interval);
    }

    /// <summary>(Re)build the watcher set. Blank or missing folders are
    /// skipped — a not-yet-created deferred folder must not throw.</summary>
    public void SetFolders(params string?[] folders)
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            var w = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            w.Created += (_, _) => Poke();
            w.Deleted += (_, _) => Poke();
            w.Renamed += (_, _) => Poke();
            _watchers.Add(w);
        }
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
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
