using OrdoSort.Core;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Core.Tests;

/// <summary>Bulk rename's segment controls: a name split into pieces at
/// underscores, spaces and dashes, pieces dropped per file, rejoined with a
/// chosen separator, and an optional date in front. These replaced the
/// one-click "Review files" rebuild (2026-09-25), so the owner's review-file
/// example is pinned end to end here.</summary>
public class BulkRenameSegmentTests
{
    // Plan only checks existence on disk; a folder that doesn't exist makes
    // every name free, so these stay pure.
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "ordo_seg_" + Guid.NewGuid().ToString("N"));

    private static string Src(string name) => Path.Combine(Dir, name);

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> Drops(string source, params int[] positions) =>
        new Dictionary<string, IReadOnlySet<int>> { [source] = new HashSet<int>(positions) };

    [Fact]
    public void RunsOfMixedSeparatorsCountAsOne()
    {
        var parts = SplitSegments("EVANS_BRIAN 5_14_1998__ACME-RECORDS");

        Assert.Equal(new[] { "EVANS", "BRIAN", "5", "14", "1998", "ACME", "RECORDS" }, parts.Select(p => p.Text));
        Assert.Equal(new[] { "", "_", " ", "_", "_", "__", "-" }, parts.Select(p => p.SeparatorBefore));
    }

    [Theory]
    [InlineData("_LEAD", new[] { "LEAD" })]
    [InlineData("TRAIL- ", new[] { "TRAIL" })]
    [InlineData("SINGLE", new[] { "SINGLE" })]
    [InlineData("", new string[0])]
    public void EdgesOfTheNameCarryNoEmptyPieces(string stem, string[] expected) =>
        Assert.Equal(expected, SplitSegments(stem).Select(p => p.Text));

    /// <summary>The owner's review file, done with general controls: keep the
    /// first two pieces, join with a dash, date in front.</summary>
    [Fact]
    public void TheReviewFileExampleComesOutAsDateLastFirst()
    {
        var src = Src("EVANS_BRIAN 5_14_1998_ACME_RECORDS_100000002-1_X.pdf");
        var op = new RenameOp(Join: SegmentJoin.Dash, DatePrefix: "20260925");

        var plan = Plan(new[] { src }, op, droppedSegments: Drops(src, 3, 4, 5, 6, 7, 8, 9, 10, 11)).Single();

        Assert.Equal("20260925-EVANS-BRIAN.pdf", Path.GetFileName(plan.Target));
        Assert.True(plan.Changed);
    }

    [Theory]
    [InlineData(SegmentJoin.Original, "A_B C")]
    [InlineData(SegmentJoin.Dash, "A-B-C")]
    [InlineData(SegmentJoin.Underscore, "A_B_C")]
    [InlineData(SegmentJoin.Space, "A B C")]
    public void EachJoinChoiceRejoinsThePieces(SegmentJoin join, string expected)
    {
        var src = Src("A_B C-D.pdf");

        var plan = Plan(new[] { src }, new RenameOp(Join: join), droppedSegments: Drops(src, 4)).Single();

        Assert.Equal(expected + ".pdf", Path.GetFileName(plan.Target));
    }

    /// <summary>"Keep original" with nothing dropped must not touch the name
    /// at all, not even to tidy doubled or trailing separators the user never
    /// asked about.</summary>
    [Fact]
    public void KeepOriginalWithNothingDroppedLeavesTheNameAlone()
    {
        var src = Src("A__B_.pdf");

        var plan = Plan(new[] { src }, new RenameOp()).Single();

        Assert.False(plan.Changed);
    }

    [Fact]
    public void ChoosingAJoinWithNothingDroppedStillChangesTheSeparators()
    {
        var src = Src("SMITH_JOHN.pdf");

        var plan = Plan(new[] { src }, new RenameOp(Join: SegmentJoin.Dash)).Single();

        Assert.Equal("SMITH-JOHN.pdf", Path.GetFileName(plan.Target));
    }

    [Theory]
    [InlineData(SegmentJoin.Original, "20260925-SMITH")]
    [InlineData(SegmentJoin.Underscore, "20260925_SMITH")]
    [InlineData(SegmentJoin.Space, "20260925 SMITH")]
    public void TheDateJoinsWithTheChosenSeparator(SegmentJoin join, string expected)
    {
        var src = Src("SMITH.pdf");

        var plan = Plan(new[] { src }, new RenameOp(Join: join, DatePrefix: "20260925")).Single();

        Assert.Equal(expected + ".pdf", Path.GetFileName(plan.Target));
    }

    /// <summary>Order: pieces, join, find/replace, affixes, date, case. The
    /// date goes in front of an added prefix, and case applies to all of it.</summary>
    [Fact]
    public void TheStepsRunInOrder()
    {
        var src = Src("smith_john_extra.pdf");
        var op = new RenameOp(Find: "john", Replace: "jon", Prefix: "rev-", Case: "upper",
            Join: SegmentJoin.Dash, DatePrefix: "20260925");

        var plan = Plan(new[] { src }, op, droppedSegments: Drops(src, 3)).Single();

        Assert.Equal("20260925-REV-SMITH-JON.pdf", Path.GetFileName(plan.Target));
    }

    [Fact]
    public void DropsOnlyTouchTheirOwnFile()
    {
        var a = Src("A_ONE_X.pdf");
        var b = Src("B_TWO_X.pdf");

        var plans = Plan(new[] { a, b }, new RenameOp(), droppedSegments: Drops(a, 3));

        Assert.Equal("A_ONE.pdf", Path.GetFileName(plans[0].Target));
        Assert.False(plans[1].Changed);
    }

    [Fact]
    public void PositionsPastTheEndAreIgnored()
    {
        var src = Src("A_B.pdf");

        var plan = Plan(new[] { src }, new RenameOp(), droppedSegments: Drops(src, 2, 9)).Single();

        Assert.Equal("A.pdf", Path.GetFileName(plan.Target));
    }

    [Fact]
    public void DroppingEveryPieceIsSkippedWithANote()
    {
        var src = Src("A_B.pdf");

        var plan = Plan(new[] { src }, new RenameOp(), droppedSegments: Drops(src, 1, 2)).Single();

        Assert.False(plan.Changed);
        Assert.Equal("every segment dropped — skipped", plan.Note);
    }

    [Fact]
    public void AHandEditedNameStillWins()
    {
        var src = Src("A_B_C.pdf");
        var overrides = new Dictionary<string, string> { [src] = "MINE" };

        var plan = Plan(new[] { src }, new RenameOp(Join: SegmentJoin.Dash), overrides, Drops(src, 1)).Single();

        Assert.Equal("MINE.pdf", Path.GetFileName(plan.Target));
        Assert.True(plan.Manual);
    }

    // ---- ticked files only (2026-09-26) -----------------------------------

    private static IReadOnlySet<string> Ticked(params string[] sources) =>
        new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OnlyTickedFilesChange()
    {
        var a = Src("a_x.pdf");
        var b = Src("b_x.pdf");

        var plans = Plan(new[] { a, b }, new RenameOp(Find: "x", Replace: "y"), included: Ticked(a));

        Assert.Equal("a_y.pdf", Path.GetFileName(plans[0].Target));
        Assert.False(plans[1].Changed);
        Assert.Equal("", plans[1].Note);
    }

    [Fact]
    public void NothingTickedChangesNothing()
    {
        var a = Src("a_x.pdf");

        var plan = Plan(new[] { a }, new RenameOp(Prefix: "NEW", Join: SegmentJoin.Dash), included: Ticked()).Single();

        Assert.False(plan.Changed);
    }

    [Fact]
    public void AnUntickedFilesSegmentDropsWaitUntilItIsTickedAgain()
    {
        var a = Src("A_ONE_X.pdf");

        var plan = Plan(new[] { a }, new RenameOp(), droppedSegments: Drops(a, 3), included: Ticked()).Single();

        Assert.False(plan.Changed);
    }

    [Fact]
    public void AHandEditedNameAppliesEvenWhenUnticked()
    {
        var a = Src("a_x.pdf");
        var overrides = new Dictionary<string, string> { [a] = "MINE" };

        var plan = Plan(new[] { a }, new RenameOp(), overrides, included: Ticked()).Single();

        Assert.Equal("MINE.pdf", Path.GetFileName(plan.Target));
        Assert.True(plan.Manual);
    }

    [Fact]
    public void ATickedFileCannotTakeTheNameAnUntickedFileInTheBatchIsKeeping()
    {
        // b stays "b.pdf"; a would become "b.pdf" too, so it gets a counter.
        var a = Src("a.pdf");
        var b = Src("b.pdf");

        var plans = Plan(new[] { b, a }, new RenameOp(Find: "a", Replace: "b"), included: Ticked(a));

        Assert.False(plans[0].Changed);
        Assert.Equal("b (2).pdf", Path.GetFileName(plans[1].Target));
    }
}
