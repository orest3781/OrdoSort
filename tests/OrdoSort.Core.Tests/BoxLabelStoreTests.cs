using System.Text.Json;

namespace OrdoSort.Core.Tests;

/// <summary>The exclusive box-labels writer: fresh reads, atomic increments,
/// readable failure when another station holds the file.</summary>
public class BoxLabelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordolabels_").FullName;
    private string PathOf(string n) => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ReadOfMissingFileIsEmptyDoc()
    {
        var doc = BoxLabelStore.Read(PathOf("box-labels.json"));
        Assert.Empty(doc.LabelClients);
    }

    [Fact]
    public void MutateCreatesPersistsAndReturns()
    {
        var p = PathOf("box-labels.json");
        var start = BoxLabelStore.Mutate(p, doc =>
        {
            var c = new LabelClient { Id = "ACME", NextNumber = 5 };
            doc.LabelClients.Add(c);
            var s = c.NextNumber;
            c.NextNumber += 3;
            return s;
        });
        Assert.Equal(5, start);
        Assert.Equal(8, BoxLabelStore.Read(p).LabelClients.Single().NextNumber);
    }

    [Fact]
    public void MutateCreatesAMissingNestedDirectory()
    {
        // a path under a subdirectory that doesn't exist yet at all (not just
        // a missing file) still has to succeed — a re-pointed box-labels path
        // is exactly this shape the first time it's used
        var p = Path.Combine(_dir, "nested", "deeper", "box-labels.json");
        var start = BoxLabelStore.Mutate(p, doc =>
        {
            doc.LabelClients.Add(new LabelClient { Id = "ACME", NextNumber = 1 });
            return 0;
        });
        Assert.Equal(0, start);
        Assert.True(File.Exists(p));
        Assert.Equal("ACME", BoxLabelStore.Read(p).LabelClients.Single().Id);
    }

    [Fact]
    public void ConcurrentIncrementsNeverCollide()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.LabelClients.Add(
            new LabelClient { Id = "ACME", NextNumber = 1 }); return 0; });

        var starts = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 8, _ =>
            starts.Add(BoxLabelStore.Mutate(p, d =>
            {
                var c = d.LabelClients.Single(x => x.Id == "ACME");
                var s = c.NextNumber;
                c.NextNumber += 1;
                return s;
            })));

        Assert.Equal(8, starts.Distinct().Count());          // no duplicates
        Assert.Equal(9, BoxLabelStore.Read(p).LabelClients[0].NextNumber); // gapless
    }

    [Fact]
    public void HeldFileFailsReadablyAfterRetries()
    {
        var p = PathOf("box-labels.json");
        File.WriteAllText(p, """{"label_clients":[]}""");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() =>
            BoxLabelStore.Mutate(p, d => 0, maxWaitMs: 700));
        Assert.Contains("another station", ex.Message);
        Assert.True(sw.ElapsedMilliseconds >= 600, "should have retried before failing");
    }

    [Fact]
    public void CallbackIOExceptionPropagatesWithoutRetryAndFileSurvives()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.LabelClients.Add(
            new LabelClient { Id = "A", NextNumber = 3 }); return 0; });
        var calls = 0;
        Assert.Throws<IOException>(() =>
            BoxLabelStore.Mutate<int>(p, d => { calls++; throw new IOException("callback io bug"); }));
        Assert.Equal(1, calls);   // never retried
        Assert.Equal(3, BoxLabelStore.Read(p).LabelClients.Single().NextNumber); // never truncated
    }

    [Fact]
    public void CallbackJsonExceptionPropagatesUnwrapped()
    {
        var p = PathOf("box-labels.json");
        Assert.Throws<JsonException>(() =>
            BoxLabelStore.Mutate<int>(p, d => throw new JsonException("callback json bug")));
    }

    [Fact]
    public void ReadRetriesThenFailsReadablyWhenHeld()
    {
        var p = PathOf("box-labels.json");
        File.WriteAllText(p, """{"label_clients":[]}""");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() => BoxLabelStore.Read(p, maxWaitMs: 700));
        Assert.Contains("another station", ex.Message);
        Assert.True(sw.ElapsedMilliseconds >= 600);
    }

    [Fact]
    public void DateStyleRoundTripsAndDefaultsToBars()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.DateStyle = "plain"; return 0; });
        Assert.Equal("plain", BoxLabelStore.Read(p).DateStyle);
        Assert.Contains("\"date_style\"", File.ReadAllText(p));
        Assert.Equal("bars", new BoxLabelsDoc().DateStyle);
        Assert.Equal("bars", BoxLabels.NormalizeDateStyle("neon"));
        Assert.Equal("plain", BoxLabels.NormalizeDateStyle("plain"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]   // sharing violation
    [InlineData(unchecked((int)0x80070021), true)]   // lock violation
    [InlineData(unchecked((int)0x80070070), false)]  // disk full
    [InlineData(unchecked((int)0x80070035), false)]  // bad network path
    public void ContentionClassificationIsHResultBased(int hresult, bool contention) =>
        Assert.Equal(contention, BoxLabelStore.IsContention(new IOException("x", hresult)));

    [Fact]
    public void NonContentionIOExceptionFailsFastWithItsOwnMessage()
    {
        // a directory where the box-labels PATH is itself an existing DIRECTORY
        // -> FileStream open throws a non-sharing IOException immediately
        var p = PathOf("box-labels.json");
        Directory.CreateDirectory(p);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() => BoxLabelStore.Mutate(p, d => 0));
        Assert.True(sw.ElapsedMilliseconds < 1000, "must not burn the retry budget");
        Assert.DoesNotContain("another station", ex.Message);
    }

    [Fact]
    public void NonContentionIOExceptionThroughMutateFailsFastWithItsOwnMessage()
    {
        // an overlong filename component (>260 chars) -> FileStream open
        // throws a genuine IOException (ERROR_INVALID_NAME, 0x8007007B) that
        // is NOT the directory-as-file case above (that one is actually
        // UnauthorizedAccessException on this platform) — this is the vehicle
        // that exercises IsContention's IOException branch through Mutate.
        var p = PathOf(new string('x', 300) + ".json");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() => BoxLabelStore.Mutate(p, d => 0));
        Assert.True(sw.ElapsedMilliseconds < 1000, "must not burn the retry budget");
        Assert.Contains("box-labels file error", ex.Message);
        Assert.DoesNotContain("another station", ex.Message);
    }
}
