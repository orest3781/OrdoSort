using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>StandardiseNamesWindow's structural shape: one grid, no tabs, a
/// drop reaches it. The Result column's status colours are covered where
/// every sibling tool's own Result/Note column is covered — DataGridNoteColourTests
/// (per-status, both selected and unselected, every palette) and
/// DataGridSelectionContrastTests (every column, selected, against
/// Theme.Accent) — rather than duplicated here.</summary>
[Collection(HighlightContrastTests.Name)]
public class StandardiseNamesWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public StandardiseNamesWindowTests(HighlightContrastFixture fx) => _fx = fx;

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
    public void OneGridNoTabsAndADroppedFileLandsInItAfterTheDatePromptIsAnswered()
    {
        using var dir = new TempDir();
        var src = dir.File("smith, john.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            // Constructed INSIDE the STA callback: Window's constructor
            // itself needs the fixture's UI thread, same as every sibling
            // window test.
            var window = new StandardiseNamesWindow(vm)
            {
                Left = -20000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                OverflowProbe.PumpRender();
                window.UpdateLayout();

                var content = (DependencyObject)window.Content;
                Assert.Empty(Descendants<TabControl>(content));
                var grid = Assert.Single(Descendants<DataGrid>(content));
                Assert.Same(vm.Results, grid.ItemsSource);

                // InlineWorkScheduler and a scripted date answer: AddFilesAsync
                // has completed by the time AcceptDrop returns, so the row
                // count is the assertion — same reasoning as
                // MergePdfsWindowTests.OneListNoTabsAndADroppedZipLandsInIt.
                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));

                var row = Assert.Single(vm.Results);
                Assert.Equal("20260115-SMITH-JOHN.pdf", row.Result);
            }
            finally { window.Close(); }
        });
    }
}
