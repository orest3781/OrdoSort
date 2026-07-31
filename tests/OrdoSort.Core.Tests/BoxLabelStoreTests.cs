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
    public void CallbackExceptionsPropagateUnwrappedAndFileSurvives()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.LabelClients.Add(new LabelClient { Id = "A", NextNumber = 3 }); return 0; });
        Assert.Throws<InvalidOperationException>(() =>
            BoxLabelStore.Mutate<int>(p, d => throw new InvalidOperationException("callback bug")));
        Assert.Equal(3, BoxLabelStore.Read(p).LabelClients.Single().NextNumber);
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
}
