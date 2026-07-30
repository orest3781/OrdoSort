using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

public class FolderWatchServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "frwatch_" + Guid.NewGuid());

    public FolderWatchServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task AFileLandingReachesTheDebounce()
    {
        // the wiring: a real watcher event gets through to Activity. How many
        // events the OS chooses to deliver for one write is its business — this
        // asserts arrival, not a count.
        using var svc = new FolderWatchService(debounceMs: 100, pollMs: 600_000);
        var count = 0;
        svc.Activity += () => Interlocked.Increment(ref count);
        svc.SetFolders(_dir);

        File.WriteAllText(Path.Combine(_dir, "arrived.pdf"), "x");

        Assert.True(await WaitFor(() => Volatile.Read(ref count) >= 1),
            "a file landing never reached the debounce");
    }

    [Fact]
    public async Task ABurstCoalescesToOneActivity()
    {
        // the debounce contract, driven directly. Writing five files and
        // asserting exactly one Activity used to flake on loaded CI:
        // FileSystemWatcher delivery can straddle the window, so the debounce
        // legitimately fires twice. Poking in a tight loop tests the coalescing
        // itself without depending on how fast the OS reports file writes.
        using var svc = new FolderWatchService(debounceMs: 150, pollMs: 600_000);
        var count = 0;
        svc.Activity += () => Interlocked.Increment(ref count);

        for (var i = 0; i < 5; i++) svc.Poke();

        Assert.True(await WaitFor(() => Volatile.Read(ref count) >= 1), "debounce never fired");
        await Task.Delay(400);   // well past a second debounce window
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task PollFiresWithoutAnyFileEvents()
    {
        using var svc = new FolderWatchService(debounceMs: 600_000, pollMs: 150);
        var count = 0;
        svc.Activity += () => Interlocked.Increment(ref count);
        svc.SetFolders(_dir);
        Assert.True(await WaitFor(() => Volatile.Read(ref count) >= 1),
            "poll backstop never fired");
    }

    [Fact]
    public void MissingAndBlankFoldersAreSkippedWithoutThrowing()
    {
        using var svc = new FolderWatchService(debounceMs: 100, pollMs: 600_000);
        svc.SetFolders("", null, Path.Combine(_dir, "does-not-exist"), _dir, _dir);
    }

    [Fact]
    public async Task DisposeStopsActivity()
    {
        var svc = new FolderWatchService(debounceMs: 50, pollMs: 600_000);
        var count = 0;
        svc.Activity += () => Interlocked.Increment(ref count);
        svc.SetFolders(_dir);
        svc.Dispose();
        File.WriteAllText(Path.Combine(_dir, "late.pdf"), "x");
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref count));
    }
}
