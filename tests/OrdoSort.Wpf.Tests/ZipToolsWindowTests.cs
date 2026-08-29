using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The 2026-08-25 spec's regression pin, on the Zip and unzip
/// window: zero TabControls, exactly one DataGrid, and a FileDrop lands one
/// row in that one list. The test this file used to hold —
/// FooterActionsFollowTheSelectedTab — is deleted, not ported: it guarded
/// the footer-swapping machinery the tab split needed, and both the
/// machinery and its guard go together.</summary>
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

    private static ZipExtractViewModel QuietVm() =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));

    [Fact]
    public void OneListNoTabsAndADroppedZipLandsInIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var vm = QuietVm();
        var window = new ZipToolsWindow(vm)
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

            window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { zip }));

            Assert.Equal(zip, Assert.Single(vm.Rows).Path);
        }
        finally { window.Close(); }
    });

    /// <summary>All three actions, all showing, all bound to the one view
    /// model — the footer no longer swaps with anything.</summary>
    [Fact]
    public void TheFooterHoldsZipZipToAndExtractForTheOneList() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = QuietVm();
        var window = new ZipToolsWindow(vm)
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

            var buttons = Descendants<Button>((DependencyObject)window.Content).ToList();
            // Identified by the command instance rather than by label: the
            // labels are bound and count-dependent ("Zip", "Zip 2 items").
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ZipCommand)).IsVisible);
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ZipAsCommand)).IsVisible);
            Assert.True(buttons.Single(b => ReferenceEquals(b.Command, vm.ExtractCommand)).IsVisible);
        }
        finally { window.Close(); }
    });
}
