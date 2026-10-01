using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>After a merge, the originals can be moved out of the way into a
/// dated folder beside them (owner request 2026-10-01). Files are only ever
/// moved: nothing is deleted and nothing already in the archive is replaced.</summary>
public class MergeArchiveTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 14, 30, 0);

    [Fact]
    public void TheFolderIsNamedForTheDayAndSitsBesideTheOriginal()
    {
        Assert.Equal(@"C:\Jobs\Job 4471\merged_archive_20261001",
            MergeArchive.FolderFor(@"C:\Jobs\Job 4471\cover.pdf", Day));
    }

    [Fact]
    public void EachOriginalMovesIntoTheDatedFolderBesideIt()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.pdf", "first");
        var b = tmp.File("b.pdf", "second");

        var moved = MergeArchive.MoveOriginals(new[] { a, b }, mergedOutput: tmp.File("Job.pdf"), Day);

        var folder = Path.Combine(tmp.Path, "merged_archive_20261001");
        Assert.Equal(new[] { Path.Combine(folder, "a.pdf"), Path.Combine(folder, "b.pdf") },
            moved.Select(m => m.MovedTo).ToArray());
        Assert.All(moved, m => Assert.Equal("", m.Problem));
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "a.pdf")));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void OriginalsFromTwoFoldersGoToAnArchiveBesideEach()
    {
        using var tmp = new TempDir();
        var first = tmp.File(@"one\a.pdf");
        var second = tmp.File(@"two\b.pdf");

        MergeArchive.MoveOriginals(new[] { first, second }, mergedOutput: null, Day);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "one", "merged_archive_20261001", "a.pdf")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "two", "merged_archive_20261001", "b.pdf")));
    }

    [Fact]
    public void ANameAlreadyInTheArchiveGetsACounterAndIsNotReplaced()
    {
        using var tmp = new TempDir();
        var earlier = tmp.File(@"merged_archive_20261001\a.pdf", "this morning's");
        var a = tmp.File("a.pdf", "this afternoon's");

        var moved = Assert.Single(MergeArchive.MoveOriginals(new[] { a }, mergedOutput: null, Day));

        Assert.Equal(Path.Combine(tmp.Path, "merged_archive_20261001", "a (2).pdf"), moved.MovedTo);
        Assert.Equal("this morning's", File.ReadAllText(earlier));
        Assert.Equal("this afternoon's", File.ReadAllText(moved.MovedTo!));
    }

    /// <summary>Merge to… can save over one of the files that went into the
    /// merge. That path now holds the new PDF, which is not an original.</summary>
    [Fact]
    public void TheMergedPdfItselfIsNeverMoved()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.pdf");
        var savedOver = tmp.File("b.pdf", "the merged document");

        var moved = MergeArchive.MoveOriginals(new[] { a, savedOver }, mergedOutput: savedOver.ToUpperInvariant(), Day);

        Assert.Equal(a, Assert.Single(moved).Source);
        Assert.Equal("the merged document", File.ReadAllText(savedOver));
    }

    [Fact]
    public void AnOriginalThatCannotBeMovedStaysAndSaysWhyAndTheRestStillMove()
    {
        using var tmp = new TempDir();
        var held = tmp.File("held.pdf");
        var free = tmp.File("free.pdf");

        List<MergeArchive.Moved> moved;
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            moved = MergeArchive.MoveOriginals(new[] { held, free }, mergedOutput: null, Day);

        var stuck = moved.Single(m => m.Source == held);
        Assert.Null(stuck.MovedTo);
        Assert.NotEqual("", stuck.Problem);
        Assert.True(File.Exists(held));
        Assert.NotNull(moved.Single(m => m.Source == free).MovedTo);
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void AnOriginalThatIsGoneIsReportedNotThrown()
    {
        using var tmp = new TempDir();
        var gone = Path.Combine(tmp.Path, "gone.pdf");

        var moved = Assert.Single(MergeArchive.MoveOriginals(new[] { gone }, mergedOutput: null, Day));

        Assert.Null(moved.MovedTo);
        Assert.Contains("no longer there", moved.Problem);
    }
}
