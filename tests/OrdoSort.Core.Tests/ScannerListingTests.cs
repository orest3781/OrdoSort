using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The inbox scan reads each file's size and modified time from the
/// folder listing and sorts on those, rather than asking the disk about every
/// file again: on a share that second question is one round trip per file.</summary>
public class ScannerListingTests
{
    private static Scanner.ListedFile Listed(string name, long length = 1, long? modifiedTicks = 1) =>
        new(@"C:\inbox\" + name, length, modifiedTicks);

    private static string[] Names(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetFileName(p)!).ToArray();

    // ---- the listing ----

    [Theory]
    [InlineData("")]     // as typed
    [InlineData(@"\")]   // a trailing separator
    [InlineData("/")]    // a forward slash, as a hand-edited config has
    public void TheListingSpellsEachPathTheWayDirectoryGetFilesDoes(string ending)
    {
        // These paths become the session queue and the history's
        // original_path, so their spelling must not change.
        using var tmp = new TempDir();
        tmp.File("b.pdf");
        tmp.File("a.pdf");
        var folder = tmp.Path + ending;

        var listed = Scanner.ListVisible(folder).Select(f => f.Path).Order(StringComparer.Ordinal);

        Assert.Equal(Directory.GetFiles(folder).Order(StringComparer.Ordinal), listed);
    }

    [Fact]
    public void TheListingCarriesEachFilesSizeAndModifiedTime()
    {
        using var tmp = new TempDir();
        var path = tmp.File("scan.pdf", "twelve bytes");
        var modified = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        var file = Assert.Single(Scanner.ListVisible(tmp.Path));

        Assert.Equal(12, file.Length);
        Assert.Equal(modified.Ticks, file.ModifiedTicks);
    }

    [Fact]
    public void ATimeOfTheMissingFileSentinelIsUnknownNotTheYear1601()
    {
        // QC-13: Windows reports 1601-01-01 for a time it doesn't have. Read
        // as a real date, one such file made the set-aside folder look about
        // 155,000 days old and sorted first under "Oldest first".
        Assert.Null(Scanner.ModifiedTicksOf(DateTime.FromFileTimeUtc(0)));
    }

    [Fact]
    public void AKnownTimeIsKeptAsItsTicks()
    {
        var modified = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(modified.Ticks, Scanner.ModifiedTicksOf(modified));
    }

    // ---- the order ----

    [Fact]
    public void BiggestFirstSortsBySizeThenByName()
    {
        var files = new[] { Listed("b.pdf", 100), Listed("small.pdf", 5), Listed("a.pdf", 100) };

        Assert.Equal(new[] { "a.pdf", "b.pdf", "small.pdf" }, Names(Scanner.Order(files, "size_desc")));
    }

    [Fact]
    public void SmallestFirstSortsBySizeThenByName()
    {
        var files = new[] { Listed("b.pdf", 100), Listed("small.pdf", 5), Listed("a.pdf", 100) };

        Assert.Equal(new[] { "small.pdf", "a.pdf", "b.pdf" }, Names(Scanner.Order(files, "size_asc")));
    }

    [Theory]
    [InlineData("mtime_asc", new[] { "old.pdf", "new.pdf", "unknown.pdf" })]
    [InlineData("mtime_desc", new[] { "new.pdf", "old.pdf", "unknown.pdf" })]
    public void AFileWithNoKnownTimeGoesLastInEitherTimeOrder(string sort, string[] expected)
    {
        var files = new[]
        {
            Listed("unknown.pdf", modifiedTicks: null),
            Listed("new.pdf", modifiedTicks: 200),
            Listed("old.pdf", modifiedTicks: 100),
        };

        Assert.Equal(expected, Names(Scanner.Order(files, sort)));
    }

    [Theory]
    [InlineData("filename_asc", new[] { "a.pdf", "B.pdf", "c.pdf" })]
    [InlineData("filename_desc", new[] { "c.pdf", "B.pdf", "a.pdf" })]
    [InlineData("banana", new[] { "a.pdf", "B.pdf", "c.pdf" })]   // an unknown key reads as A to Z
    public void FilenameOrdersIgnoreCase(string sort, string[] expected)
    {
        var files = new[] { Listed("c.pdf"), Listed("a.pdf"), Listed("B.pdf") };

        Assert.Equal(expected, Names(Scanner.Order(files, sort)));
    }
}
