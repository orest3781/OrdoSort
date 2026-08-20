using System.Windows;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

[Collection(HighlightContrastTests.Name)]
public class FilenameListWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public FilenameListWindowTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>The column-visibility mechanism is imperative on purpose: a
    /// DataGridColumn is not in the visual or logical tree, so a RelativeSource
    /// binding to the view model never resolves and would fail SILENTLY, leaving
    /// every optional column permanently visible. Because the wiring is code
    /// rather than a binding, this is the test that proves it is actually wired —
    /// one flag, one column, both directions. SizeColumn and FolderColumn are
    /// internal fields (XAML's default x:Name field modifier), reachable here via
    /// OrdoSort.Wpf.csproj's InternalsVisibleTo for this test assembly.</summary>
    [Fact]
    public void TogglingOneColumnFlagShowsAndHidesThatColumnOnly() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs());
        var window = new FilenameListWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();

            // Columns default off (FilenameListViewModel.Columns starts at
            // FilenameList.Columns.None), and the constructor's initial
            // SyncColumnVisibility call is what should have gotten both
            // columns to Collapsed before any toggle ever happens.
            Assert.Equal(Visibility.Collapsed, window.SizeColumn.Visibility);
            Assert.Equal(Visibility.Collapsed, window.FolderColumn.Visibility);

            vm.ShowSize = true;

            // "that column only": Size comes on, Folder — a neighbour never
            // touched — stays right where it was. A wiring bug that flips
            // every column at once, or the wrong column, would pass a test
            // that only ever checked ShowSize's own column.
            Assert.Equal(Visibility.Visible, window.SizeColumn.Visibility);
            Assert.Equal(Visibility.Collapsed, window.FolderColumn.Visibility);

            vm.ShowSize = false;

            Assert.Equal(Visibility.Collapsed, window.SizeColumn.Visibility);
            Assert.Equal(Visibility.Collapsed, window.FolderColumn.Visibility);
        }
        finally
        {
            window.Close();
        }
    });
}
