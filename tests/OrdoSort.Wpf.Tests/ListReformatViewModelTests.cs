using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 4 (list reformatter tool). ListReformatViewModel recomputes
/// synchronously and inline on every setter — no DebouncedProbe, no
/// InlineWorkScheduler, no WaitFor polling the way FilenameListViewModelTests
/// or PageCountsViewModelTests need for their off-thread work.</summary>
public class ListReformatViewModelTests
{
    [Fact]
    public void SettingInputTextUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel();
        vm.InputText = "a\nb\nc";
        Assert.Equal("a,b,c", vm.OutputText);
    }

    [Fact]
    public void TogglingQuoteUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel { InputText = "a\nb" };
        vm.Quote = true;
        Assert.Equal("'a','b'", vm.OutputText);
    }

    [Fact]
    public void TogglingSpaceAfterCommaUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel { InputText = "a\nb" };
        vm.SpaceAfterComma = true;
        Assert.Equal("a, b", vm.OutputText);
    }

    [Fact]
    public void TogglingDedupeUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel { InputText = "a\na\nb" };
        vm.Dedupe = true;
        Assert.Equal("a,b", vm.OutputText);
    }

    [Fact]
    public void CountsLineIsBlankWhenThereIsNothingToShow()
    {
        var vm = new ListReformatViewModel();
        Assert.Equal("", vm.CountsLine);
    }

    [Fact]
    public void CountsLineIsSingularForOneItem()
    {
        var vm = new ListReformatViewModel { InputText = "solo" };
        Assert.Equal("1 item", vm.CountsLine);
    }

    [Fact]
    public void CountsLineIsPluralForSeveralItems()
    {
        var vm = new ListReformatViewModel { InputText = "a\nb\nc" };
        Assert.Equal("3 items", vm.CountsLine);
    }

    [Fact]
    public void CountsLineAppendsTheSingularDuplicatesDroppedSuffix()
    {
        var vm = new ListReformatViewModel { InputText = "a\na\nb" };
        vm.Dedupe = true;
        Assert.Equal("2 items · 1 duplicate dropped", vm.CountsLine);
    }

    [Fact]
    public void CountsLinePluralizesDuplicatesDropped()
    {
        var vm = new ListReformatViewModel { InputText = "a\na\na\nb" };
        vm.Dedupe = true;
        Assert.Equal("2 items · 2 duplicates dropped", vm.CountsLine);
    }

    [Fact]
    public void ClearCommandResetsInputOutputAndCounts()
    {
        var vm = new ListReformatViewModel { InputText = "a\nb" };
        Assert.NotEqual("", vm.OutputText);

        vm.ClearCommand.Execute(null);

        Assert.Equal("", vm.InputText);
        Assert.Equal("", vm.OutputText);
        Assert.Equal("", vm.CountsLine);
    }

    [Fact]
    public void NoteCopiedTrueReportsConvertedAndCopied()
    {
        var vm = new ListReformatViewModel();
        vm.NoteCopied(converted: true);
        Assert.Equal("Converted and copied", vm.Status);
    }

    [Fact]
    public void NoteCopiedFalseReportsCopied()
    {
        var vm = new ListReformatViewModel();
        vm.NoteCopied(converted: false);
        Assert.Equal("Copied", vm.Status);
    }

    [Fact]
    public void NoteClipboardBusySetsStatus()
    {
        var vm = new ListReformatViewModel();
        vm.NoteClipboardBusy();
        Assert.Equal("Clipboard busy — try again", vm.Status);
    }

    [Fact]
    public void NoteNothingToCopySetsStatus()
    {
        var vm = new ListReformatViewModel();
        vm.NoteNothingToCopy();
        Assert.Equal("nothing to copy", vm.Status);
    }

    // ---- upgrade: blank-row reporting and output shape ----

    [Fact]
    public void CountsLineReportsTheSingularBlankRowRemoved()
    {
        var vm = new ListReformatViewModel { InputText = "a\n\nb" };
        Assert.Equal("2 items · 1 blank row removed", vm.CountsLine);
    }

    [Fact]
    public void CountsLinePluralizesBlankRowsRemoved()
    {
        var vm = new ListReformatViewModel { InputText = "a\n\n\nb" };
        Assert.Equal("2 items · 2 blank rows removed", vm.CountsLine);
    }

    [Fact]
    public void CountsLineListsBlankRowsBeforeDuplicates()
    {
        var vm = new ListReformatViewModel { InputText = "a\n\na\nb" };
        vm.Dedupe = true;
        Assert.Equal("2 items · 1 blank row removed · 1 duplicate dropped", vm.CountsLine);
    }

    [Fact]
    public void CountsLineSaysNothingAboutBlanksWhenThereWereNone()
    {
        var vm = new ListReformatViewModel { InputText = "a\nb" };
        Assert.Equal("2 items", vm.CountsLine);
    }

    [Fact]
    public void ChangingShapeToOnePerLineUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel { InputText = "a\n\nb" };
        vm.Shape = ListReformat.OutputShape.OnePerLine;
        Assert.Equal("a\r\nb", vm.OutputText);
    }

    [Fact]
    public void ChangingTheCustomDelimiterUpdatesOutputTextImmediately()
    {
        var vm = new ListReformatViewModel
        {
            InputText = "a\nb",
            Shape = ListReformat.OutputShape.CustomDelimiter,
        };
        vm.CustomDelimiter = "|";
        Assert.Equal("a|b", vm.OutputText);
    }

    /// <summary>Pre-filled rather than empty: an empty delimiter runs the
    /// items together, and that should be something the user typed on
    /// purpose, not what they get for picking the shape.</summary>
    [Fact]
    public void TheCustomDelimiterStartsAsASemicolon()
    {
        Assert.Equal(";", new ListReformatViewModel().CustomDelimiter);
    }

    /// <summary>Drives IsEnabled on the delimiter box — it is dead weight
    /// under either of the other two shapes.</summary>
    [Fact]
    public void IsCustomDelimiterIsTrueOnlyForTheCustomShape()
    {
        var vm = new ListReformatViewModel();
        Assert.False(vm.IsCustomDelimiter);

        vm.Shape = ListReformat.OutputShape.OnePerLine;
        Assert.False(vm.IsCustomDelimiter);

        vm.Shape = ListReformat.OutputShape.CustomDelimiter;
        Assert.True(vm.IsCustomDelimiter);
    }

    [Fact]
    public void IsCustomDelimiterRaisesPropertyChangedWhenTheShapeChanges()
    {
        var vm = new ListReformatViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Shape = ListReformat.OutputShape.CustomDelimiter;

        Assert.Contains(nameof(vm.IsCustomDelimiter), raised);
    }

    /// <summary>The picker has to offer every shape the core supports —
    /// adding one to the enum without listing it here would leave it
    /// unreachable from the window.</summary>
    [Fact]
    public void ShapeChoicesOfferEveryOutputShape()
    {
        Assert.Equal(
            Enum.GetValues<ListReformat.OutputShape>(),
            ListReformatViewModel.ShapeChoices.Select(c => c.Key).ToArray());
    }
}
