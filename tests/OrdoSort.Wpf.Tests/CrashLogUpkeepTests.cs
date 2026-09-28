namespace OrdoSort.Wpf.Tests;

/// <summary>R4 and DW-37: how crash.log is kept. Joins the shared collection
/// because it changes the static <c>App._crashDir</c> and
/// <c>App.CrashLogRetryWait</c>, as <see cref="CrashLogPlaceTests"/> does.</summary>
[Collection(HighlightContrastTests.Name)]
public class CrashLogUpkeepTests : UiTest, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordocrashupkeep_").FullName;
    private readonly string _before = App._crashDir;

    public CrashLogUpkeepTests(HighlightContrastFixture fx) : base(fx) => App._crashDir = _dir;

    public void Dispose()
    {
        App._crashDir = _before;
        App.CrashLogRetryWait = () => Thread.Sleep(App.CrashLogRetryMs);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Log => Path.Combine(_dir, "crash.log");
    private string OldLog => Path.Combine(_dir, "crash.old.log");

    /// <summary>R4: crash.log was appended to forever. A station with a
    /// recurring fault grew it without limit. Once it reaches the cap it is
    /// kept as crash.old.log (replacing the one before) and a fresh log
    /// starts, so the two together stay near twice the cap.</summary>
    [Fact]
    public void AFullCrashLogIsSetAsideAndAFreshOneStarted()
    {
        File.WriteAllText(Log, new string('x', (int)App.MaxCrashLogBytes));
        File.WriteAllText(OldLog, "the oldest entries");

        Assert.True(App.LogCrash(new InvalidOperationException("the newest crash")));

        var fresh = File.ReadAllText(Log);
        Assert.Contains("the newest crash", fresh);
        Assert.True(fresh.Length < 10_000, $"crash.log should have started over, is {fresh.Length} chars");
        Assert.Equal(App.MaxCrashLogBytes, new FileInfo(OldLog).Length);
    }

    [Fact]
    public void ACrashLogUnderTheCapIsAppendedTo()
    {
        File.WriteAllText(Log, "an earlier crash\n");

        Assert.True(App.LogCrash(new InvalidOperationException("the next crash")));

        var text = File.ReadAllText(Log);
        Assert.StartsWith("an earlier crash", text);
        Assert.Contains("the next crash", text);
        Assert.False(File.Exists(OldLog));
    }

    /// <summary>DW-37: crash.log is this PC's own file now (QC-21), so two
    /// stations no longer write to one log. Two OrdoSort processes on one PC
    /// still can, and a write takes the file exclusively: the second crash
    /// found it busy and was simply not recorded. It now waits briefly for
    /// the other writer and tries again.</summary>
    [Fact]
    public void ACrashLogBusyWithAnotherWriterIsWaitedForNotSkipped()
    {
        var other = new FileStream(Log, FileMode.Append, FileAccess.Write, FileShare.Read);
        App.CrashLogRetryWait = () => other.Dispose();   // the other process finishes its entry

        Assert.True(App.LogCrash(new InvalidOperationException("the second crash")));

        Assert.Contains("the second crash", File.ReadAllText(Log));
    }
}
