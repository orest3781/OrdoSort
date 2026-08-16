using System.Globalization;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>DocumentDate is the hub's one place for the three filename date
/// conventions (spec rule 1). Pure string→DateOnly?, no disk.</summary>
public class DocumentDateTests
{
    private static void UnderCulture(string culture, Action body)
    {
        var prev = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo(culture); body(); }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void DashFormParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 22),
            DocumentDate.Parse("20260722-DOE,JANE [048962880].PDF"));
    }

    [Fact]
    public void DashFormAsAFullPathStillParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 22),
            DocumentDate.Parse(@"C:\inbox\20260722-DOE,JANE.PDF"));
    }

    [Fact]
    public void DashFormWithImpossibleDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("20261332-X.PDF"));
    }

    [Fact]
    public void DottedFormParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 15),
            DocumentDate.Parse("07.15.2026 DOE JANE 123456789_ABC.PDF"));
    }

    [Fact]
    public void DottedFormWithImpossibleDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("13.45.2026 DOE JANE 123.PDF"));
    }

    [Fact]
    public void SpaceFormParsesAsMonthFirst()
    {
        Assert.Equal(new DateOnly(2026, 7, 15),
            DocumentDate.Parse("07152026 DOE JANE 123456789_ABC.PDF"));
    }

    /// <summary>"20260101 " can't be MMddyyyy (month 20) — the documented
    /// fallback reads it as yyyyMMdd instead of losing the date.</summary>
    [Fact]
    public void SpaceFormFallsBackToYearFirst()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), DocumentDate.Parse("20260101 X.PDF"));
    }

    [Fact]
    public void SpaceFormImpossibleUnderBothReadingsIsNull()
    {
        Assert.Null(DocumentDate.Parse("99999999 X.PDF"));
    }

    [Fact]
    public void NoLeadingDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("DOE,JANE [048962880].PDF"));
        Assert.Null(DocumentDate.Parse(""));
    }

    /// <summary>The dotted form is exactly the shape a culture-sensitive
    /// parse would mangle — pin invariance the way the repo's
    /// CultureInvariantDatesTests does.</summary>
    [Fact]
    public void DottedFormIsCultureInvariant()
    {
        UnderCulture("de-DE", () =>
            Assert.Equal(new DateOnly(2026, 7, 15),
                DocumentDate.Parse("07.15.2026 DOE JANE 123.PDF")));
    }
}
