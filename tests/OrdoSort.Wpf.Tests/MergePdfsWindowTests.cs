using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The 2026-08-25 spec's regression pin. The defect it closes is
/// "a drop can reach a list you did not aim at", and the fact that makes
/// that impossible is structural: this window holds ZERO TabControls and
/// exactly ONE DataGrid, and a FileDrop lands one row in that one list. A
/// count assertion, not a "the right list got it" assertion — with one list
/// those are the same claim, and the count is the one that keeps failing if
/// a second list is ever reintroduced.</summary>
[Collection(HighlightContrastTests.Name)]
public class MergePdfsWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public MergePdfsWindowTests(HighlightContrastFixture fx) => _fx = fx;

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
    public void OneListNoTabsAndADroppedZipLandsInIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));
        var window = new MergePdfsWindow(vm)
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
            Assert.Same(vm.Rows, grid.ItemsSource);

            // InlineWorkScheduler and no UiContext: AddPaths has completed by
            // the time AcceptDrop returns, so the row count is the assertion.
            window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { zip }));

            Assert.Equal(zip, Assert.Single(vm.Rows).Path);
        }
        finally { window.Close(); }
    });
}
