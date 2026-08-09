namespace OrdoSort.Core.Tests;

/// <summary>SweptTable.Load sits on top of Csv.ReadTable (already pinned by
/// CsvTests) — these tests focus on the combiner's own contract: the union
/// header set, per-file error isolation, ragged-row tolerance, and the
/// blank/duplicate header key rules.</summary>
public class SweptTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swepttabletest_" + Guid.NewGuid());
    public SweptTableTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TwoFilesWithIdenticalHeadersSumRowsAndTagEachRowsSourceFile()
    {
        var a = Write("a.csv", "Name,Id\nAlice,1\nBob,2\n");
        var b = Write("b.csv", "Name,Id\nCarl,3\n");

        var table = SweptTable.Load(new[] { a, b });

        Assert.Equal(new[] { "Name", "Id" }, table.Headers);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(2, table.FilesRead);
        Assert.Empty(table.FileErrors);
        Assert.Equal(new[] { a, a, b }, table.Rows.Select(r => r.SourceFile));
        Assert.Equal("Alice", table.Rows[0].Cells["Name"]);
        Assert.Equal("Carl", table.Rows[2].Cells["Name"]);
    }

    [Fact]
    public void DifferentHeadersUnionInFirstSeenOrderAndFillTheOtherFilesColumnWithEmptyString()
    {
        var a = Write("a.csv", "Name,OnlyA\nAlice,x\n");
        var b = Write("b.csv", "Name,OnlyB\nBob,y\n");

        var table = SweptTable.Load(new[] { a, b });

        Assert.Equal(new[] { "Name", "OnlyA", "OnlyB" }, table.Headers);
        Assert.Equal("x", table.Rows[0].Cells["OnlyA"]);
        Assert.Equal("", table.Rows[0].Cells["OnlyB"]);
        Assert.Equal("", table.Rows[1].Cells["OnlyA"]);
        Assert.Equal("y", table.Rows[1].Cells["OnlyB"]);
    }

    [Fact]
    public void RaggedRowsFillMissingCellsAndDropExtraOnes()
    {
        var path = Write("r.csv", "A,B,C\n1,2\n1,2,3,4\n");

        var table = SweptTable.Load(new[] { path });

        Assert.Equal(new[] { "A", "B", "C" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("1", table.Rows[0].Cells["A"]);
        Assert.Equal("2", table.Rows[0].Cells["B"]);
        Assert.Equal("", table.Rows[0].Cells["C"]);
        Assert.Equal("1", table.Rows[1].Cells["A"]);
        Assert.Equal("2", table.Rows[1].Cells["B"]);
        Assert.Equal("3", table.Rows[1].Cells["C"]);
    }

    [Fact]
    public void OneGoodFileAndOneMissingFileStillLoadsTheGoodRowsWithOneFileError()
    {
        var good = Write("good.csv", "A,B\n1,2\n");
        var missing = Path.Combine(_dir, "ghost.csv");

        var table = SweptTable.Load(new[] { good, missing });

        Assert.Single(table.Rows);
        Assert.Equal(1, table.FilesRead);
        Assert.Single(table.FileErrors);
        Assert.Contains(missing, table.FileErrors[0]);
    }

    [Fact]
    public void ATabDelimitedFileAndACommaDelimitedFileCombine()
    {
        var tab = Write("tab.csv", "A\tB\n1\t2\n");
        var comma = Write("comma.csv", "A,B\n3,4\n");

        var table = SweptTable.Load(new[] { tab, comma });

        Assert.Equal(new[] { "A", "B" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("1", table.Rows[0].Cells["A"]);
        Assert.Equal("3", table.Rows[1].Cells["A"]);
    }

    [Fact]
    public void BlankHeaderCellBecomesColumnNAndADuplicateNameIsSuffixedWithItsColumnIndex()
    {
        var path = Write("dup.csv", "Name,,Name\nAlice,x,y\n");

        var table = SweptTable.Load(new[] { path });

        Assert.Equal(new[] { "Name", "Column 2", "Name (3)" }, table.Headers);
        Assert.Equal("Alice", table.Rows[0].Cells["Name"]);
        Assert.Equal("x", table.Rows[0].Cells["Column 2"]);
        Assert.Equal("y", table.Rows[0].Cells["Name (3)"]);
    }

    /// <summary>Regression for a generated duplicate-suffix key colliding
    /// with a distinct literal header in the same row: "X" at column 1 is
    /// the first occurrence and stays plain; the second "X" at column 2 is
    /// a duplicate and would naively become "X (2)" — but the third column
    /// is *literally* named "X (2)", so that candidate is already taken.
    /// Every column must still get its own key and keep its own value; none
    /// of the three may silently overwrite another.</summary>
    [Fact]
    public void ADuplicateSuffixThatCollidesWithALiteralHeaderStillGetsAUniqueKey()
    {
        var path = Write("collide.csv", "X,X,X (2)\n1,2,3\n");

        var table = SweptTable.Load(new[] { path });

        Assert.Equal(3, table.Headers.Count);
        Assert.Equal(table.Headers.Count, table.Headers.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("1", table.Rows[0].Cells[table.Headers[0]]);
        Assert.Equal("2", table.Rows[0].Cells[table.Headers[1]]);
        Assert.Equal("3", table.Rows[0].Cells[table.Headers[2]]);
    }

    [Fact]
    public void EmptyPathsListReturnsAnEmptyTableWithNoError()
    {
        var table = SweptTable.Load(Array.Empty<string>());

        Assert.Empty(table.Headers);
        Assert.Empty(table.Rows);
        Assert.Equal(0, table.FilesRead);
        Assert.Empty(table.FileErrors);
    }

    [Fact]
    public void HeaderOnlyFileContributesHeadersAndZeroRowsButStillCountsInFilesRead()
    {
        var path = Write("headeronly.csv", "A,B\n");

        var table = SweptTable.Load(new[] { path });

        Assert.Equal(new[] { "A", "B" }, table.Headers);
        Assert.Empty(table.Rows);
        Assert.Equal(1, table.FilesRead);
    }
}
