using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Several commands were live with nothing to act on: "Save as .txt…"
/// on an empty list opened a save dialog and wrote an empty file, and Filename
/// list's whole removal stack reported CanExecute=true with zero rows loaded and
/// nothing ever removed (2026-08-22 UI audit, UI-12).
///
/// <para><b>The half that is easy to get wrong.</b> This app's
/// <c>RelayCommand</c> has no CommandManager hookup — it raises
/// CanExecuteChanged only when told to. So adding a CanExecute predicate
/// without a matching RaiseCanExecuteChanged leaves the button stuck in
/// whatever state it was born in, and a permanently DISABLED button is worse
/// than the permanently enabled one it replaced: the ungated version at least
/// still worked. Every test here therefore checks both directions — off when
/// there is nothing to do, and back on when there is.</para></summary>
public class CommandGateTests
{
    private static FilenameListViewModel List() =>
        new(new FakeDialogs(), new InlineWorkScheduler());

    [Fact]
    public void PageCountsSaveIsOffUntilThereIsSomethingToSave()
    {
        var vm = new PageCountsViewModel(new FakeDialogs(), new InlineWorkScheduler());
        Assert.False(vm.SaveCommand.CanExecute(null),
            "Save was live on an empty list — it opens a save dialog and writes an empty file");

        vm.Rows.Add(new PageCountRow(@"C:\in\a.pdf"));
        Assert.True(vm.SaveCommand.CanExecute(null),
            "Save stayed disabled after a row arrived — the gate was added without wiring the " +
            "RaiseCanExecuteChanged that RelayCommand needs, which is worse than no gate");
    }

    [Fact]
    public void FilenameListSaveIsOffUntilThereAreRows()
    {
        var vm = List();
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveSelectedIsOffWithoutASelectionAndOnWithOne()
    {
        var vm = List();
        Assert.False(vm.RemoveSelectedCommand.CanExecute(null));

        vm.SelectedPaths = new[] { @"C:\in\a.pdf" };
        Assert.True(vm.RemoveSelectedCommand.CanExecute(null),
            "selecting a row left Remove selected disabled — the SelectedPaths setter is the " +
            "only place that can re-ask this command, and it must");

        vm.SelectedPaths = Array.Empty<string>();
        Assert.False(vm.RemoveSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void TheRemovalStackIsOffWithNothingToUndoOrRestore()
    {
        var vm = List();
        Assert.False(vm.UndoRemovalCommand.CanExecute(null),
            "Undo last removal was live with nothing ever removed");
        Assert.False(vm.RestoreRemovedCommand.CanExecute(null),
            "Restore removed was live with nothing ever removed");
    }
}
