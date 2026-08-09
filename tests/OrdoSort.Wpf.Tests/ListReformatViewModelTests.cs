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
}
