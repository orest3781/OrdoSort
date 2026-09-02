using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Services;
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

    /// <summary>Fix round 1, item 3: a batch running against the real
    /// filesystem is a handful of File.Moves inside one directory — sub-
    /// second — so rather than threading a cancellation token through
    /// BulkRename.Execute/Revert (Core methods Bulk rename and MatchMerge
    /// also call), the window simply refuses to close while the view model
    /// is busy.</summary>
    private sealed class GatedScheduler : IWorkScheduler
    {
        private readonly System.Threading.ManualResetEventSlim _gate = new(false);
        private int _dispatchCount;
        public int DispatchCount => _dispatchCount;

        public Task<T> Run<T>(Func<T> work) => Task.Run(() =>
        {
            System.Threading.Interlocked.Increment(ref _dispatchCount);
            _gate.Wait();
            return work();
        });

        public Task Run(Action work) => Run(() => { work(); return true; });

        public void Release() => _gate.Set();
    }

    /// <summary>Measured, not assumed: an EARLIER draft of this fact called
    /// vm.AddFilesAsync from the bare test thread (deliberately outside
    /// _fx.Invoke, to avoid awaiting a call that could hang on a broken
    /// guard — the exact lesson the first round's own re-entrancy fact
    /// learned). It failed here instead, for a completely different and
    /// very real reason: StandardiseNamesWindow.xaml binds Command="{Binding
    /// UndoCommand}" on its Undo button, so IsBusy's setter (which raises
    /// UndoCommand.CanExecuteChanged synchronously) touches that BOUND
    /// BUTTON — a DependencyObject owned by the fixture's dispatcher thread
    /// — the moment AddFilesAsync's very first line runs. Calling it from
    /// any other thread throws "the calling thread cannot access this
    /// object because a different thread owns it," which is exactly what a
    /// real production StandardiseNamesWindow never risks: OnAddFiles and
    /// AcceptDrop are themselves UI-thread event handlers, so AddFilesAsync
    /// is always ENTERED on the dispatcher thread there, and every await
    /// inside it resumes on that same thread via the ordinary,
    /// automatically-captured DispatcherSynchronizationContext — no
    /// explicit uiContext parameter needed. This fact now starts the call
    /// the same way: FROM INSIDE _fx.Invoke, but not awaited there — an
    /// async method returns to its caller (here, Dispatcher.Invoke's own
    /// callback) the instant it hits its OWN first await, so this does not
    /// block the dispatcher for the batch's duration; the fixture's thread
    /// runs Dispatcher.Run() continuously in the background (not only
    /// during an _fx.Invoke call), so the posted continuations keep being
    /// processed after this method returns, which is what lets the
    /// existing safe, non-blocking poll loop below keep working exactly as
    /// it did before this fix.</summary>
    [Fact]
    public async Task ClosingWhileABatchIsRunningIsRefusedThenSucceedsOnceItFinishes()
    {
        using var dir = new TempDir();
        var src = dir.File("smith, john.pdf");
        var scheduler = new GatedScheduler();
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);

        StandardiseNamesWindow? window = null;
        Task addTask = Task.CompletedTask;
        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            window = new StandardiseNamesWindow(vm)
            {
                Left = -20000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            window.Show();
            window.UpdateLayout();
            // Entered here so IsBusy's own CanExecuteChanged touches the
            // bound Undo button on the thread that owns it — see this
            // fact's own doc comment. Not awaited: returns the instant it
            // hits its first await, so this callback stays quick.
            addTask = vm.AddFilesAsync(new[] { src });
        });

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scheduler.DispatchCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(scheduler.DispatchCount > 0, "the batch should have been dispatched to the scheduler");

        _fx.Invoke(() =>
        {
            window!.Close();
            Assert.True(window.IsVisible, "closing mid-batch must be refused");
        });
        Assert.Contains("wait", vm.Status, StringComparison.OrdinalIgnoreCase);

        scheduler.Release();
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (!addTask.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(addTask.IsCompleted, "the batch should have finished once released");

        _fx.Invoke(() =>
        {
            window!.Close();
            Assert.False(window.IsVisible, "closing once idle should succeed");
        });
    }

    /// <summary>The new wiring this task adds: ResultsGrid's SelectionChanged
    /// pushes into StandardiseNamesViewModel.SelectedRows (OnSelectionChanged,
    /// code-behind), which is what PeelCommand's own CanExecute reads. Proven
    /// through a REAL DataGrid selection, not by setting SelectedRows
    /// directly the way the view-model-only tests do — that would prove
    /// PeelCommand reacts to the property, not that the grid ever reaches it.
    /// InlineWorkScheduler makes PeelCommand.Execute's own async run complete
    /// synchronously (same reasoning as OneGridNoTabsAndADroppedFileLandsInItAfterTheDatePromptIsAnswered's
    /// own comment on AddFilesAsync), so the row's Result is safe to assert
    /// immediately after Execute returns.</summary>
    [Fact]
    public void SelectingAGridRowArmsRemoveLastSegmentAndItPeelsTheSelectedRow()
    {
        using var dir = new TempDir();
        var src = dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var window = new StandardiseNamesWindow(vm)
            {
                Left = -20000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));
                var grid = Assert.Single(Descendants<DataGrid>((DependencyObject)window.Content));
                Assert.Same(vm.Results, grid.ItemsSource);

                Assert.False(vm.PeelCommand.CanExecute(null));   // nothing selected yet

                grid.SelectedIndex = 0;
                grid.UpdateLayout();

                Assert.True(vm.PeelCommand.CanExecute(null));

                vm.PeelCommand.Execute(null);

                var row = Assert.Single(vm.Results);
                Assert.Equal("20260115-A-B-C-D.pdf", row.Result);
                Assert.True(File.Exists(Path.Combine(dir.Path, "20260115-A-B-C-D.pdf")));
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Rule 7: Undo last batch moved into the toolbar, immediately
    /// after Remove last segment — it used to sit in the footer beside the
    /// status line. Pinned by container membership and declaration order
    /// within that container's Children, not by screen position (which an
    /// off-screen probe window makes an unreliable signal): the button's
    /// logical Parent must be the SAME StackPanel Remove last segment's is,
    /// and it must come immediately after it. "Exactly one button anywhere
    /// in the window carries this Content" is the other half of the brief —
    /// MOVE it, don't leave a second copy in the footer.</summary>
    [Fact]
    public void UndoLastBatchLivesInTheToolbarImmediatelyAfterRemoveLastSegment()
    {
        var vm = new StandardiseNamesViewModel(new FakeDialogs());
        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var window = new StandardiseNamesWindow(vm)
            {
                Left = -20000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var allButtons = Descendants<Button>((DependencyObject)window.Content).ToList();
                var undo = Assert.Single(allButtons, b => b.Content as string == "Undo last batch");
                var removeLastSegment = Assert.Single(allButtons, b => b.Content as string == "Remove last segment");

                var toolbar = Assert.IsType<StackPanel>(removeLastSegment.Parent);
                Assert.Same(toolbar, undo.Parent);

                var order = toolbar.Children.OfType<Button>().Select(b => (string)b.Content).ToList();
                var removeIndex = order.IndexOf("Remove last segment");
                Assert.Equal(removeIndex + 1, order.IndexOf("Undo last batch"));
            }
            finally { window.Close(); }
        });
    }
}
