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

    /// <summary>Task 7 review finding 3: IsIncluded fed IsRunnable, the note
    /// and the button's count, but nothing visual — an excluded row was
    /// indistinguishable from an ordinary pending one except for its text.
    /// Reads the REALIZED DataGridRow's Opacity (the DataTrigger's actual,
    /// resolved effect), not the static XAML declaration — the style this
    /// codebase's own DataGridSelectionContrastTests/DataGridNoteColourTests
    /// families already use for pinning a trigger's real behaviour rather
    /// than its source text.</summary>
    [Fact]
    public async Task AnExcludedRowIsDimmedAndAnIncludedRowIsNot()
    {
        // AddPaths/SetTypeEnabled run OUTSIDE _fx.Invoke, before any window
        // exists — MergePdfsViewModel and its Rows are plain objects with no
        // thread affinity of their own until a WPF DataGrid starts observing
        // them, and InlineWorkScheduler with no uiContext means nothing here
        // needs a Dispatcher at all. This is what lets the test await
        // naturally instead of blocking inside the STA lambda the way
        // AutoFitColumnTests.ShowOffscreenAndDriveCurrent has to for a REAL
        // async operation (WebView2 init) that only exists once its window
        // is realized — no such requirement here.
        using var dir = new TempDir();
        var word = dir.File("a.docx");
        var pdf = dir.File("b.pdf");
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));
        await vm.AddPaths(new[] { word, pdf });
        vm.SetTypeEnabled(MergeTypes.Word, false);

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
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

                var grid = Assert.Single(Descendants<DataGrid>((DependencyObject)window.Content));
                var excludedRow = vm.Rows.Single(r => r.Kind == "word");
                var includedRow = vm.Rows.Single(r => r.Kind == "pdf");
                var excludedContainer = grid.ItemContainerGenerator.ContainerFromItem(excludedRow) as DataGridRow
                    ?? throw new InvalidOperationException("the excluded row was not realized");
                var includedContainer = grid.ItemContainerGenerator.ContainerFromItem(includedRow) as DataGridRow
                    ?? throw new InvalidOperationException("the included row was not realized");

                Assert.Equal(0.5, excludedContainer.Opacity);
                Assert.Equal(1.0, includedContainer.Opacity);
            }
            finally { window.Close(); }
        });
    }
}
