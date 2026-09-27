using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Review matches' "More columns…" chooser (2026-09-26): every
/// spreadsheet column with a tick, a search box for rosters of 15 to 40
/// columns, and the name and id columns locked on.</summary>
public class ColumnChooserViewModelTests
{
    private static ColumnChooserViewModel Chooser() => new(
        all: new[] { "Last", "First", "DOB", "Address", "Birth place", "Control" },
        locked: new[] { "Last", "First", "Control" },
        shown: new[] { "Last", "First", "Control", "DOB" });

    [Fact]
    public void EveryColumnIsOfferedInSpreadsheetOrderWithTheShownOnesTicked()
    {
        var chooser = Chooser();

        Assert.Equal(new[] { "Last", "First", "DOB", "Address", "Birth place", "Control" },
            chooser.Visible.Select(c => c.Header));
        Assert.Equal(new[] { "Last", "First", "DOB", "Control" },
            chooser.Visible.Where(c => c.IsChosen).Select(c => c.Header));
    }

    [Fact]
    public void TheNameAndIdColumnsCannotBeUnticked()
    {
        var chooser = Chooser();
        var last = chooser.Visible.Single(c => c.Header == "Last");

        last.IsChosen = false;

        Assert.True(last.IsLocked);
        Assert.True(last.IsChosen);
        Assert.Contains("Last", chooser.Chosen);
    }

    [Fact]
    public void SearchNarrowsTheListIgnoringCaseAndClearingItBringsEverythingBack()
    {
        var chooser = Chooser();

        chooser.Search = "BIRTH";
        Assert.Equal(new[] { "Birth place" }, chooser.Visible.Select(c => c.Header));

        chooser.Search = "";
        Assert.Equal(6, chooser.Visible.Count);
    }

    [Fact]
    public void ATickMadeWhileSearchingSurvivesClearingTheSearch()
    {
        var chooser = Chooser();
        chooser.Search = "addr";

        chooser.Visible.Single().IsChosen = true;
        chooser.Search = "";

        Assert.Equal(new[] { "Last", "First", "DOB", "Address", "Control" }, chooser.Chosen);
    }
}
