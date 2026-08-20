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

    [Fact]
    public void EachRowCarriesItsSizeAndFullPath()
    {
        var path = Touch("report.pdf");
        File.WriteAllText(path, new string('x', 1234));

        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));

        var row = Assert.Single(listing.Rows);
        Assert.Equal("report.pdf", row.Name);
        Assert.Equal(1234L, row.Size);   // long, not int — Size is long?
        Assert.Equal(path, row.FullPath);
    }

    [Fact]
    public void ModifiedIsTheFilesLastWriteTime()
    {
        var path = Touch("report.pdf");
        var when = new DateTime(2026, 3, 4, 14, 22, 0, DateTimeKind.Local);
        File.SetLastWriteTime(path, when);

        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));

        Assert.Equal(when, Assert.Single(listing.Rows).Modified);
    }

    /// <summary>Build never throws, and a file that vanished between the walk and
    /// the stat is reported as unknown rather than as 0 bytes — the row itself
    /// stays, because it really was there in the walk.</summary>
    [Fact]
    public void AFileThatDisappearsAfterTheWalkHasNullSizeAndModified()
    {
        var path = Touch("gone.pdf");
        var listing = FilenameList.Build(new[] { path },
            new FilenameList.Options(Recursive: false, IncludeExtension: true),
            stat: _ => throw new FileNotFoundException());

        var row = Assert.Single(listing.Rows);
        Assert.Equal("gone.pdf", row.Name);
        Assert.Null(row.Size);
        Assert.Null(row.Modified);
    }

    [Fact]
    public void RowsStayInNaturalOrderByName()
    {
        Touch("item2.pdf"); Touch("item10.pdf"); Touch("item1.pdf");

        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));

        Assert.Equal(new[] { "item1.pdf", "item2.pdf", "item10.pdf" },
            listing.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void AFileAtTheRootHasNoFolder()
    {
        Touch("report.pdf");
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));
        Assert.Equal("", Assert.Single(listing.Rows).Folder);
    }

    [Fact]
    public void ANestedFileCarriesItsPathRelativeToTheRoot()
    {
        Touch(Path.Combine("2026", "march", "report.pdf"));
        var listing = FilenameList.Build(new[] { _dir },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));
        Assert.Equal(Path.Combine("2026", "march"), Assert.Single(listing.Rows).Folder);
    }

    /// <summary>A file added individually is its own root, so there is no folder
    /// for it to be relative to.</summary>
    [Fact]
    public void AnIndividuallyAddedFileHasNoFolder()
    {
        var path = Touch(Path.Combine("2026", "report.pdf"));
        var listing = FilenameList.Build(new[] { path },
            new FilenameList.Options(Recursive: false, IncludeExtension: true));
        Assert.Equal("", Assert.Single(listing.Rows).Folder);
    }

    /// <summary>Nested roots: the file sits under both, and the LONGEST wins, so
    /// Folder stays as short and as meaningful as it can be.</summary>
    [Fact]
    public void TheLongestMatchingRootWins()
    {
        Touch(Path.Combine("2026", "march", "report.pdf"));
        var listing = FilenameList.Build(
            new[] { _dir, Path.Combine(_dir, "2026") },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));

        Assert.Equal("march", listing.Rows[0].Folder);
    }

    [Fact]
    public void RootMatchingIgnoresCase()
    {
        Touch(Path.Combine("2026", "report.pdf"));
        var listing = FilenameList.Build(new[] { _dir.ToUpperInvariant() },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));
        Assert.Equal("2026", Assert.Single(listing.Rows).Folder);
    }

    /// <summary>When a root is provided with a trailing separator, the trimming operation
    /// should not break path handling. This tests the fix that prevents "C:\" from becoming
    /// the drive-RELATIVE "C:" which could resolve against the wrong directory.</summary>
    [Fact]
    public void RootWithTrailingSeparatorHandlesRelativizationCorrectly()
    {
        // Create a file in a subdirectory of _dir
        Touch(Path.Combine("archive", "data.pdf"));

        // Use _dir with a trailing separator (simulates user providing "C:\" format)
        var rootWithSeparator = _dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // Build with the root that has trailing separator
        // The fix ensures trimming and re-adding doesn't break the logic
        var listing = FilenameList.Build(new[] { rootWithSeparator },
            new FilenameList.Options(Recursive: true, IncludeExtension: true));

        // Verify we found our file with correct folder path
        var row = Assert.Single(listing.Rows);
        Assert.Equal("data.pdf", row.Name);
        Assert.Equal("archive", row.Folder);
    }
}
