namespace OrdoSort.Core.Tests;

/// <summary>R3: History's CSV export and the box-label PDF were written
/// straight over the file the user picked. A write cut off partway (a full
/// disk, a dropped share, a crash) left a truncated file where the previous
/// good export used to be. Both now write beside it and swap in only a
/// complete file, like the config does. The failure is forced through
/// <see cref="AtomicPlace.ReplaceFile"/>, the seam the placement tests use.</summary>
[Collection(AtomicPlaceTests.Name)]
public sealed class ExportPlacementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_export_" + Guid.NewGuid());

    public ExportPlacementTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AtomicPlace.ReplaceFile = (tmp, dest) => File.Replace(tmp, dest, null);   // process-wide seam
        AtomicPlace.Sleep = (_, delayMs) => Thread.Sleep(delayMs);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Every replace of <paramref name="dest"/> fails as a dropped
    /// share would; other tests' placements are left alone.</summary>
    private static void FailEveryReplaceOf(string dest)
    {
        var realReplace = AtomicPlace.ReplaceFile;
        AtomicPlace.ReplaceFile = (tmp, target) =>
        {
            if (target != dest) { realReplace(tmp, target); return; }
            throw new IOException("the network name is no longer available");
        };
        AtomicPlace.Sleep = (_, _) => { };
    }

    [Fact]
    public void AFailedHistoryExportLeavesThePreviousExportWhole()
    {
        var dest = Path.Combine(_dir, "history.csv");
        File.WriteAllText(dest, "the previous export");
        using (var history = new History(Path.Combine(_dir, "h.sqlite")))
        {
            history.LogCommit(@"C:\in\a.pdf", "a.pdf", "SMITH.pdf", "SMITH", "replace", "", "Done", @"C:\out", false, "");
            FailEveryReplaceOf(dest);

            Assert.ThrowsAny<IOException>(() => history.ExportCsv(dest));
        }

        Assert.Equal("the previous export", File.ReadAllText(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AFailedLabelPdfLeavesThePreviousPdfWhole()
    {
        var dest = Path.Combine(_dir, "labels.pdf");
        File.WriteAllText(dest, "the previous labels");
        var items = BoxLabels.Batch("ABCD", 1, 3, new DateTime(2026, 7, 25), 30);
        FailEveryReplaceOf(dest);

        Assert.ThrowsAny<IOException>(() => BoxLabels.RenderPdf(dest, items));

        Assert.Equal("the previous labels", File.ReadAllText(dest));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
