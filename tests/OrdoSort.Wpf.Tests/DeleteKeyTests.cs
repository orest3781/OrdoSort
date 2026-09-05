using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
public class DeleteKeyTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_deletekey_" + Guid.NewGuid());

    public DeleteKeyTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Polls for a debounced-probe result to land, pumping THIS
    /// thread's own dispatcher on every iteration.
    ///
    /// BulkRenameViewModel's Refresh (armed by both AddFilesAsync and
    /// RemoveSelected) arms DebouncedProbe.Trigger, which schedules the
    /// actual re-plan via a real System.Threading.Timer — due time 0 for
    /// "immediate", but still a threadpool callback, never an inline call.
    /// InlineWorkScheduler makes the PLAN computation itself synchronous
    /// once that callback fires; it does not make the firing synchronous.
    /// Constructing the view model with uiContext: SynchronizationContext.
    /// Current (matching MainWindow.xaml.cs's own construction) makes that
    /// callback apply the result via Dispatcher.BeginInvoke instead of
    /// straight off the threadpool thread — required once PreviewGrid is
    /// actually bound and showing, where an off-thread Preview.Clear/Add
    /// throws (WPF's CollectionView thread-affinity check) and, being
    /// inside a fire-and-forget async Task, is silently swallowed: measured
    /// as Preview left EMPTY (Clear ran, the Add that followed did not)
    /// rather than the removal simply not having happened yet. But nothing
    /// pumps a captured DispatcherSynchronizationContext's queue except the
    /// dispatcher itself, and the whole test body already runs inside one
    /// Dispatcher.Invoke (HighlightContrastFixture.Invoke) — so waiting here
    /// needs a nested pump, the same shape as
    /// TriageWindowDecisionRaceTests.PumpQueued.</summary>
    private static void WaitForProbeToSettle(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void DeleteRemovesTheSelectedRowInBulkRename()
    {
        var a = Path.Combine(_dir, "a.pdf"); File.WriteAllText(a, "pdf");
        var b = Path.Combine(_dir, "b.pdf"); File.WriteAllText(b, "pdf");

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler(),
                uiContext: SynchronizationContext.Current);
            vm.AddFilesAsync(new[] { a, b }).GetAwaiter().GetResult();
            WaitForProbeToSettle(() => vm.Preview.Count == 2, "the preview should settle after adding two files");
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
                WaitForProbeToSettle(() => vm.Preview.Count == 1, "the preview should settle after removing the selected row");
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
            var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler(),
                uiContext: SynchronizationContext.Current);
            vm.AddFilesAsync(new[] { a, b }).GetAwaiter().GetResult();
            WaitForProbeToSettle(() => vm.Preview.Count == 2, "the preview should settle after adding two files");
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
        var path = Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Windows", xamlFile);
        var xaml = File.ReadAllText(path);
        var start = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{xamlFile} has no element named {elementName}");
        var openingTag = xaml.Substring(start, xaml.IndexOf('>', start) - start);

        Assert.Contains("PreviewKeyDown=\"OnGridKeyDown\"", openingTag);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OrdoSort.sln not found above the test output");
    }
}
