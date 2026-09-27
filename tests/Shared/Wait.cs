using System.Diagnostics;

namespace OrdoSort.TestSupport;

/// <summary>The one way a test waits for something to become true. Replaces
/// the copies each test file used to keep, whose 3-second ceilings timed out
/// whenever the machine was busy (docs/testing.md). It returns as soon as the
/// condition holds, so a generous ceiling costs nothing on a passing run.</summary>
public static class Wait
{
    /// <summary>How long a condition may take before the test fails. Long on
    /// purpose: it only matters when something is actually stuck.</summary>
    public const int DefaultCeilingMs = 30_000;

    /// <summary>Polls <paramref name="condition"/> until it is true, failing
    /// with <paramref name="because"/> after <paramref name="timeoutMs"/>.
    /// A read of a collection that another thread is rebuilding can throw
    /// mid-read (<see cref="ArgumentOutOfRangeException"/>,
    /// <see cref="InvalidOperationException"/>); that means "not yet", so it is
    /// retried. Any other exception fails the test at once.</summary>
    public static void WaitFor(Func<bool> condition, string because, int timeoutMs = DefaultCeilingMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            bool done;
            try
            {
                done = condition();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                done = false;
            }
            if (done) return;
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }
}
