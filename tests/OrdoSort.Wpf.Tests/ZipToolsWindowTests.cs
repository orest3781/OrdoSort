using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The footer is shared chrome outside the TabControl, so which set
/// of actions it shows rides on two ElementName bindings to the TabItems'
/// own IsSelected. Nothing else in the suite can see those: measured by
/// pointing one of them at a name no element carries, the panel falls back to
/// Visibility's own default — Visible — so BOTH footers render stacked in the
/// same Grid cell, one tab's actions sitting on top of the other's, and
/// WindowOverflowTests still passes because the pile fits. This is the check
/// that the swap really happens, in both directions: one direction alone
/// would also be satisfied by a footer permanently stuck on one tab.</summary>
[Collection(HighlightContrastTests.Name)]
public class ZipToolsWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public ZipToolsWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public void FooterActionsFollowTheSelectedTab() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new ZipToolsViewModel(new FakeDialogs());
        var window = new ZipToolsWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            var buttons = Descendants<Button>((DependencyObject)window.Content).ToList();
            // Identified by the command instance rather than by label: the
            // labels are bound and count-dependent ("Zip", "Zip 2 items").
            var zip = buttons.Single(b => ReferenceEquals(b.Command, vm.ZipExtract.ZipCommand));
            var merge = buttons.Single(b => ReferenceEquals(b.Command, vm.MergePdfs.MergeCommand));
            var tabs = Descendants<TabControl>((DependencyObject)window.Content).Single();

            // IsVisible, not Visibility: it accounts for the ancestor
            // StackPanel the converter actually collapses.
            Assert.True(zip.IsVisible, "the Zip & unzip tab is selected but its Zip action is not showing");
            Assert.False(merge.IsVisible, "the Merge action is showing while the Zip & unzip tab is selected");

            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            Assert.True(merge.IsVisible, "the Merge PDFs tab is selected but its Merge action is not showing");
            Assert.False(zip.IsVisible, "the Zip action is showing while the Merge PDFs tab is selected");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Each tab's grid is bound to its OWN list — the whole reason
    /// the tabs have two view models. A copy-paste pointing both grids at one
    /// of them would show the same rows on both tabs, and every other suite
    /// works on the view models directly, where the mistake cannot exist.
    /// A TabControl realizes only the selected tab's content, so the grid is
    /// read one tab at a time rather than both at once.</summary>
    [Fact]
    public void EachTabsGridShowsOnlyItsOwnList() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new ZipToolsViewModel(new FakeDialogs());
        vm.ZipExtract.Rows.Add(new ZipItemRow(@"C:\inbox\loose.pdf", "file"));
        var window = new ZipToolsWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();
            var tabs = Descendants<TabControl>((DependencyObject)window.Content).Single();

            var expected = new object[] { vm.ZipExtract.Rows, vm.MergePdfs.Rows };
            for (var i = 0; i < expected.Length; i++)
            {
                tabs.SelectedIndex = i;
                window.UpdateLayout();
                OverflowProbe.PumpRender();
                window.UpdateLayout();

                var grid = Descendants<DataGrid>((DependencyObject)window.Content).Single();
                Assert.Same(expected[i], grid.ItemsSource);
            }
        }
        finally
        {
            window.Close();
        }
    });
}
