namespace OrdoSort.Core.Tests;

public class FilenameListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "filenamelisttest_" + Guid.NewGuid());
    public FilenameListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void IncludeExtensionFalseStripsTheExtension()
    {
        Touch("report.pdf");
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: false));
        Assert.Equal(new[] { "report" }, listing.Names);
    }

    [Fact]
    public void IncludeExtensionTrueKeepsTheExtension()
    {
        Touch("report.pdf");
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));
        Assert.Equal(new[] { "report.pdf" }, listing.Names);
    }

    /// <summary>ParseFiletypes' own separator/leading-dot handling is already
    /// pinned by FolderMonitorTests — this only proves FilenameList.Build
    /// actually routes ExtensionFilter through it (comma+space separator,
    /// one entry with a leading dot, per the brief's own "pdf, docx"
    /// example) rather than filtering some other way.</summary>
    [Fact]
    public void ExtensionFilterRoutesThroughParseFiletypes()
    {
        var pdf = Touch("a.pdf");
        var docx = Touch("b.docx");
        Touch("c.txt");
        Touch("d.csv");
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true, ExtensionFilter: ".pdf, docx"));
        Assert.Equal(new[] { Path.GetFileName(pdf), Path.GetFileName(docx) }
            .OrderBy(n => n, NaturalSort.Instance), listing.Names);
        Assert.Equal(2, listing.Ignored);
    }

    [Fact]
    public void NamesComeBackInNaturalOrderNotOrdinalOrder()
    {
        Touch("10.txt");
        Touch("2.txt");
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));
        Assert.Equal(new[] { "2.txt", "10.txt" }, listing.Names);
    }

    /// <summary>Same name under two different folders is still two rows in
    /// a filename list — Build must not silently collapse them (it's a
    /// list, not a set), per the brief's explicit note.</summary>
    [Fact]
    public void DuplicateNamesFromDifferentFoldersAreKeptNotDeduped()
    {
        Touch(Path.Combine("sub1", "same.txt"));
        Touch(Path.Combine("sub2", "same.txt"));
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));
        Assert.Equal(new[] { "same.txt", "same.txt" }, listing.Names);
    }

    [Fact]
    public void ToTextJoinsWithEnvironmentNewLine()
    {
        var text = FilenameList.ToText(new[] { "a.txt", "b.txt", "c.txt" });
        Assert.Equal(string.Join(Environment.NewLine, "a.txt", "b.txt", "c.txt"), text);
    }

    [Fact]
    public void EmptyInputReturnsAnEmptyListing()
    {
        var listing = FilenameList.Build(Array.Empty<string>(),
            new FilenameList.Options(Recursive: false, IncludeExtension: true));
        Assert.Empty(listing.Names);
        Assert.Equal(0, listing.Ignored);
        Assert.Equal("", listing.Error);
    }

    /// <summary>Mirrors IntakeTests.MissingPathIsIgnoredNotThrown: a path
    /// that's neither a file nor a folder is silently counted as ignored,
    /// not surfaced as an Error — Build never throws either way. Pins that
    /// Build passes Intake's own Error field through unchanged rather than
    /// inventing its own "not found" text.</summary>
    [Fact]
    public void MissingFolderNeverThrowsAndIsIgnoredNotErrored()
    {
        var missing = Path.Combine(_dir, "ghost-folder");
        var listing = FilenameList.Build(new[] { missing },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));
        Assert.Empty(listing.Names);
        Assert.Equal(1, listing.Ignored);
        Assert.Equal("", listing.Error);
    }
}
