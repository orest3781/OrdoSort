using OrdoSort.Core;
using OrdoSort.TestSupport;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Core.Tests;

/// <summary>Standardise names' rule with controls (owner request
/// 2026-09-29). The defaults must name files exactly as the tool's original
/// fixed rule, TidyStem, did.</summary>
public class StandardiseTests
{
    private const string Date = "20260929";
    private static readonly StandardiseOptions Defaults = new(Date);

    // ---------------------------------------------------------- defaults

    [Theory]
    [InlineData("smith john 12345")]
    [InlineData("DOE,  JANE_998")]
    [InlineData("20251201-smith_john")]
    [InlineData("o'brien (jr) st. clair")]
    [InlineData("12345678-REPORT")]          // eight digits that aren't a date stay
    [InlineData("---")]
    [InlineData("")]
    [InlineData("  van  der   berg  ")]
    [InlineData("20260229-leap")]             // not a real date: stays as a word
    [InlineData("smith-20251201")]            // a date at the end is a word unless the date goes there
    public void TheDefaultsNameFilesAsTheOriginalRuleDid(string stem) =>
        Assert.Equal(TidyStem(stem, Date), Standardise.Stem(stem, Defaults));

    /// <summary>The one place the rules differ on purpose: eight real-date
    /// digits glued to more digits or letters are part of an ID, not a
    /// date, and are no longer chopped.</summary>
    [Theory]
    [InlineData("202512011234-SMITH", "20260929-202512011234-SMITH")]
    [InlineData("20251201SMITH", "20260929-20251201SMITH")]
    public void DigitsGluedToAnIdAreNotTakenForADate(string stem, string expected) =>
        Assert.Equal(expected, Standardise.Stem(stem, Defaults));

    // ---------------------------------------------------------- words

    [Fact]
    public void ReadingSplitsOffARealLeadingDateAndTheWords()
    {
        var read = Standardise.Read("20251201-smith, john_12345");

        Assert.Equal("20251201", read.LeadingDate);
        Assert.Equal(new[] { "smith", "john", "12345" }, read.Words);
    }

    [Fact]
    public void PunctuationInsideANameIsPartOfTheWord() =>
        Assert.Equal(new[] { "o'brien", "st.", "clair", "(jr)" }, Standardise.Read("o'brien st. clair (jr)").Words);

    [Fact]
    public void DroppedWordsAreLeftOut() =>
        Assert.Equal("20260929-SMITH-12345",
            Standardise.Stem("smith john 12345", Defaults, new HashSet<int> { 2 }));

    [Fact]
    public void DroppingEveryWordLeavesJustTheDate() =>
        Assert.Equal(Date, Standardise.Stem("smith john", Defaults, new HashSet<int> { 1, 2 }));

    [Fact]
    public void DroppingEveryWordWithNoDateLeavesNothing() =>
        Assert.Equal("", Standardise.Stem("smith", Defaults with { Date = DatePlacement.None }, new HashSet<int> { 1 }));

    // ---------------------------------------------------------- case

    [Theory]
    [InlineData(NameCase.Upper, "20260929-SMITH-JOHN")]
    [InlineData(NameCase.Title, "20260929-Smith-John")]
    [InlineData(NameCase.AsIs, "20260929-sMiTh-john")]
    public void TheLetterCaseIsAControl(NameCase nameCase, string expected) =>
        Assert.Equal(expected, Standardise.Stem("sMiTh john", Defaults with { Case = nameCase }));

    [Theory]
    [InlineData("o'brien", "O'Brien")]
    [InlineData("D'ANGELO", "D'Angelo")]
    [InlineData("smith's", "Smith's")]
    [InlineData("rock'n", "Rock'n")]
    [InlineData("st.clair", "St.Clair")]
    [InlineData("(jr)", "(Jr)")]
    [InlineData("12345", "12345")]
    [InlineData("MCDONALD", "Mcdonald")]
    public void TitleCaseReadsLikeAName(string word, string expected) =>
        Assert.Equal(expected, Standardise.TitleCase(word));

    /// <summary>Invariant, so the same file gets the same name on a PC with
    /// a Turkish locale, where "i" upper-cases to "İ".</summary>
    [Fact]
    public void UpperCaseDoesNotDependOnTheLocale()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("20260929-SMITH", Standardise.Stem("smith", Defaults));
            Assert.Equal("20260929-Imre", Standardise.Stem("imre", Defaults with { Case = NameCase.Title }));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    // ---------------------------------------------------------- separator

    [Theory]
    [InlineData(SegmentJoin.Dash, "20260929-SMITH-JOHN")]
    [InlineData(SegmentJoin.Underscore, "20260929_SMITH_JOHN")]
    [InlineData(SegmentJoin.Space, "20260929 SMITH JOHN")]
    [InlineData(SegmentJoin.Original, "20260929-SMITH-JOHN")]
    public void TheSeparatorIsAControl(SegmentJoin separator, string expected) =>
        Assert.Equal(expected, Standardise.Stem("smith_john", Defaults with { Separator = separator }));

    // ---------------------------------------------------------- date

    [Theory]
    [InlineData(DatePlacement.Front, "20260929-SMITH")]
    [InlineData(DatePlacement.Back, "SMITH-20260929")]
    [InlineData(DatePlacement.None, "SMITH")]
    public void TheDatesPlaceIsAControl(DatePlacement placement, string expected) =>
        Assert.Equal(expected, Standardise.Stem("20251201-smith", Defaults with { Date = placement }));

    /// <summary>Standardising a name this tool already gave a date at the
    /// end replaces that date rather than adding a second one.</summary>
    [Fact]
    public void ADateAtTheEndIsReplacedNotStacked() =>
        Assert.Equal("SMITH-JOHN-20260929",
            Standardise.Stem("SMITH-JOHN-20251201", Defaults with { Date = DatePlacement.Back }));

    [Fact]
    public void ADateAtTheEndCanBeKept() =>
        Assert.Equal("SMITH-20251201",
            Standardise.Stem("smith_20251201", Defaults with { Date = DatePlacement.Back, Source = DateSource.InName }));

    [Fact]
    public void ReadingWithTheDateAtTheEndSplitsItOff()
    {
        var read = Standardise.Read("smith john 20251201", dateAtEnd: true);

        Assert.Equal("20251201", read.TrailingDate);
        Assert.Equal(new[] { "smith", "john" }, read.Words);
        Assert.Null(Standardise.Read("smith john 20251201").TrailingDate);
    }

    [Fact]
    public void TheDateAlreadyInTheNameCanBeKept() =>
        Assert.Equal("20251201-SMITH",
            Standardise.Stem("20251201_smith", Defaults with { Source = DateSource.InName }));

    [Fact]
    public void ANameWithNoDateOfItsOwnGetsTheTypedOne() =>
        Assert.Equal("20260929-SMITH", Standardise.Stem("smith", Defaults with { Source = DateSource.InName }));

    [Fact]
    public void TheFilesModifiedDateCanBeUsed() =>
        Assert.Equal("20240317-SMITH",
            Standardise.Stem("smith", Defaults with { Source = DateSource.Modified },
                modified: new DateTime(2024, 3, 17, 23, 59, 0)));

    [Fact]
    public void AnUnknownModifiedDateFallsBackToTheTypedOne() =>
        Assert.Equal("20260929-SMITH", Standardise.Stem("smith", Defaults with { Source = DateSource.Modified }));

    // ---------------------------------------------------------- plan

    [Fact]
    public void ThePlanShowsEachNewNameWithoutTouchingTheDisk()
    {
        using var tmp = new TempDir();
        var a = tmp.File("smith john.pdf");
        var b = tmp.File("doe_jane.pdf");

        var plan = Standardise.Plan(new[] { a, b }, Defaults);

        Assert.Equal(new[] { "20260929-SMITH-JOHN.pdf", "20260929-DOE-JANE.pdf" },
            plan.Select(p => Path.GetFileName(p.Target)));
        Assert.All(plan, p => Assert.True(p.Changed));
        Assert.All(plan, p => Assert.False(p.Manual));
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    /// <summary>Two files that standardise to one name: the preview says so
    /// and shows the counter the second will get, in the name's own style.</summary>
    [Theory]
    [InlineData(SegmentJoin.Dash, "20260929-SMITH-2.pdf")]
    [InlineData(SegmentJoin.Underscore, "20260929_SMITH_2.pdf")]
    [InlineData(SegmentJoin.Space, "20260929 SMITH (2).pdf")]
    public void AClashInTheBatchGetsACounterInTheNamesStyle(SegmentJoin separator, string second)
    {
        using var tmp = new TempDir();
        var a = tmp.File("smith.pdf");
        var b = tmp.File("SMITH_.pdf");

        var plan = Standardise.Plan(new[] { a, b }, Defaults with { Separator = separator });

        Assert.Equal(second, Path.GetFileName(plan[1].Target));
        Assert.Contains("counter", plan[1].Note);
    }

    [Fact]
    public void AFileAlreadyStandardisedIsLeftAlone()
    {
        using var tmp = new TempDir();
        var done = tmp.File("20260929-SMITH-JOHN.pdf");

        Assert.False(Assert.Single(Standardise.Plan(new[] { done }, Defaults)).Changed);
    }

    /// <summary>A case-only change is still a change (TidyStem's old bug:
    /// a case-insensitive compare called "smith" already SMITH).</summary>
    [Fact]
    public void ACaseOnlyChangeIsStillPlanned()
    {
        using var tmp = new TempDir();
        var lower = tmp.File("20260929-smith.pdf");

        var plan = Assert.Single(Standardise.Plan(new[] { lower }, Defaults));

        Assert.True(plan.Changed);
        Assert.Equal("20260929-SMITH.pdf", Path.GetFileName(plan.Target));
    }

    [Fact]
    public void ANameWithNothingLeftIsSkippedWithAReason()
    {
        using var tmp = new TempDir();
        var file = tmp.File("smith.pdf");

        var plan = Assert.Single(Standardise.Plan(new[] { file }, Defaults with { Date = DatePlacement.None },
            dropped: new Dictionary<string, IReadOnlySet<int>> { [file] = new HashSet<int> { 1 } }));

        Assert.False(plan.Changed);
        Assert.Contains("empty", plan.Note);
    }

    [Fact]
    public void ExecutingThePlanRenamesAndRevertPutsItBack()
    {
        using var tmp = new TempDir();
        var file = tmp.File("smith john.pdf");
        var options = Defaults with { Case = NameCase.Title, Separator = SegmentJoin.Space, Date = DatePlacement.Back };

        var outcomes = Execute(Standardise.Plan(new[] { file }, options), Standardise.CounterStyle(options.Separator));

        Assert.Equal("Smith John 20260929.pdf", Path.GetFileName(Assert.Single(outcomes).Final));
        Assert.Empty(Revert(outcomes));
        Assert.True(File.Exists(file));
    }
}
