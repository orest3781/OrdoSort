using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-32: Delete removed a selected row in the Filename list alone;
/// the six other windows with a "Remove selected" button ignored the key.
/// One window is driven for real (Bulk rename, whose grid already routes
/// F2 and Enter through a key handler); the other five are held to the
/// wiring by a lint over their XAML, since each forwards to the same
/// OnRemoveSelected its button already uses.</summary>
[Collection(HighlightContrastTests.Name)]
public class DeleteKeyTests : UiTest, IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_deletekey_" + Guid.NewGuid());

    public DeleteKeyTests(HighlightContrastFixture fx) : base(fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void DeleteRemovesTheSelectedRowInBulkRename()
    {
        var a = Path.Combine(_dir, "a.pdf"); File.WriteAllText(a, "pdf");
        var b = Path.Combine(_dir, "b.pdf"); File.WriteAllText(b, "pdf");

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var time = new ManualTimeProvider();
            var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler(), time: time);
            InlineWorkScheduler.Finished(vm.AddFilesAsync(new[] { a, b }));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, vm.Preview.Count);

            var win = new BulkRenameWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowActivated = false,
            };
            try
            {
                win.Show();
                win.UpdateLayout();
                win.PreviewGrid.SelectedItem = vm.Preview[0];
                win.PreviewGrid.UpdateLayout();

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(win.PreviewGrid)!, 0, Key.Delete)
                { RoutedEvent = UIElement.PreviewKeyDownEvent };
                win.PreviewGrid.RaiseEvent(args);

                Assert.True(args.Handled);
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.Single(vm.Preview);
                Assert.EndsWith("b.pdf", vm.Preview[0].Source);
            }
            finally { try { win.Close(); } catch { /* best effort */ } }
        });
    }

    /// <summary>The Delete branch must never fire inside a cell editor, and
    /// an editor can open by double-click or type-to-edit — paths the
    /// window's own BeginEdit helper never saw. The grid's BeginningEdit is
    /// the one signal that covers them all.</summary>
    [Fact]
    public void DeleteInsideACellEditorEditsTextNotTheBatch()
    {
        var a = Path.Combine(_dir, "a.pdf"); File.WriteAllText(a, "pdf");
        var b = Path.Combine(_dir, "b.pdf"); File.WriteAllText(b, "pdf");

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var time = new ManualTimeProvider();
            var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler(), time: time);
            InlineWorkScheduler.Finished(vm.AddFilesAsync(new[] { a, b }));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, vm.Preview.Count);

            var win = new BulkRenameWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowActivated = false,
            };
            try
            {
                win.Show();
                win.UpdateLayout();
                win.PreviewGrid.SelectedItem = vm.Preview[0];
                win.PreviewGrid.UpdateLayout();

                var column = win.PreviewGrid.Columns.First(c => !c.IsReadOnly);
                win.PreviewGrid.CurrentCell = new DataGridCellInfo(vm.Preview[0], column);
                win.PreviewGrid.BeginEdit();

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(win.PreviewGrid)!, 0, Key.Delete)
                { RoutedEvent = UIElement.PreviewKeyDownEvent };
                win.PreviewGrid.RaiseEvent(args);

                Assert.False(args.Handled);
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(2, vm.Preview.Count);
            }
            finally { try { win.Close(); } catch { /* best effort */ } }
        });
    }

    public static IEnumerable<object[]> GridsThatMustHandleDelete() => new[]
    {
        new object[] { "PageCountsWindow.xaml", "CountsGrid" },
        new object[] { "UnlockWindow.xaml", "FileList" },
        new object[] { "ZipToolsWindow.xaml", "ItemsGrid" },
        new object[] { "MergePdfsWindow.xaml", "ItemsGrid" },
        new object[] { "MatchMergeWindow.xaml", "MatchGrid" },
    };

    [Theory, MemberData(nameof(GridsThatMustHandleDelete))]
    public void TheListElementRoutesKeysToTheDeleteHandler(string xamlFile, string elementName)
    {
        var path = Path.Combine(Repo.Root, "src", "OrdoSort.Wpf", "Windows", xamlFile);
        var xaml = File.ReadAllText(path);
        var start = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{xamlFile} has no element named {elementName}");
        var openingTag = xaml.Substring(start, xaml.IndexOf('>', start) - start);

        Assert.Contains("PreviewKeyDown=\"OnGridKeyDown\"", openingTag);
    }

}
