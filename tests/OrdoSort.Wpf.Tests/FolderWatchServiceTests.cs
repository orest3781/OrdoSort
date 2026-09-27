using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

public class FolderWatchServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private int _activity;

    public void Dispose() => _dir.Dispose();

    private FolderWatchService Build(int debounceMs, int pollMs)
    {
        var svc = new FolderWatchService(debounceMs, pollMs, time: _time);
        svc.Activity += () => _activity++;
        return svc;
    }

    [Fact]
    public void AFileLandingReachesTheDebounce()
    {
        // The wiring, end to end: a real watcher event from the OS gets
        // through to Activity, so this one runs on the real clock. How many
        // events the OS delivers for one write is its business; this asserts
        // arrival, not a count.
        using var svc = new FolderWatchService(debounceMs: 100, pollMs: 600_000);
        var count = 0;
        svc.Activity += () => Interlocked.Increment(ref count);
        svc.SetFolders(_dir.Path);

        _dir.File("arrived.pdf");

        WaitFor(() => Volatile.Read(ref count) >= 1, "a file landing should reach the debounce");
    }

    [Fact]
    public void ABurstCoalescesToOneActivity()
    {
        using var svc = Build(debounceMs: 150, pollMs: 600_000);

        for (var i = 0; i < 5; i++) svc.Poke();
        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(0, _activity);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _activity);

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _activity);
    }

    [Fact]
    public void PollFiresOnItsIntervalWithoutAnyFileEvents()
    {
        using var svc = Build(debounceMs: 600_000, pollMs: 150);
        svc.SetFolders(_dir.Path);

        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, _activity);

        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(2, _activity);
    }

    [Fact]
    public void MissingAndBlankFoldersAreSkippedWithoutThrowing()
    {
        using var svc = Build(debounceMs: 100, pollMs: 600_000);
        svc.SetFolders("", null, Path.Combine(_dir.Path, "does-not-exist"), _dir.Path, _dir.Path);
    }

    [Fact]
    public void DisposeStopsActivity()
    {
        var svc = Build(debounceMs: 50, pollMs: 150);
        svc.SetFolders(_dir.Path);
        svc.Poke();   // a debounce already armed

        svc.Dispose();
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, _activity);
    }
}
