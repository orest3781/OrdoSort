using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Bulk rename's tick boxes (2026-09-26): the first column's tick
/// box IS the row's selection, as File Explorer's item check boxes are, and
/// every control changes only ticked files. Driven on the real window.</summary>
[Collection(HighlightContrastTests.Name)]
public class BulkRenameTickTests : UiTest, IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_ticks_" + Guid.NewGuid().ToString("N"))).FullName;

    public BulkRenameTickTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private (BulkRenameWindow Win, BulkRenameViewModel Vm, DataGrid Grid, List<string> Files) Open(params string[] names)
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var files = names.Select(n =>
        {
            var path = Path.Combine(_dir, n);
            File.WriteAllText(path, "x");
            return path;
        }).ToList();
        var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler(), probeDelayMs: 0);
        vm.AddFilesAsync(files).GetAwaiter().GetResult();
        var win = new BulkRenameWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        Settle(win);
        return (win, vm, Descendants<DataGrid>(win).Single(), files);
    }

    private static void Settle(Window win)
    {
        win.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        win.UpdateLayout();
    }

    private static CheckBox TickBoxOf(DataGrid grid, int index)
    {
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
        return Descendants<CheckBox>(row).Single();
    }

    [Fact]
    public void TheTickColumnIsFirstFrozenAndMirrorsTheRowSelection() => _fx.Invoke(() =>
    {
        var (win, _, grid, _) = Open("alpha.pdf", "bravo.pdf");
        try
        {
            var ticks = grid.Columns.Single(c => c.DisplayIndex == 0);
            Assert.Equal(1, grid.FrozenColumnCount);
            Assert.Same(ticks, grid.Columns[0]);

            grid.SelectedIndex = 1;
            Settle(win);

            Assert.False(TickBoxOf(grid, 0).IsChecked);
            Assert.True(TickBoxOf(grid, 1).IsChecked);
            Assert.Equal("Tick bravo.pdf", AutomationPropertiesName(TickBoxOf(grid, 1)));
        }
        finally { win.Close(); }
    });

    /// <summary>A click on a tick box adds that row to the ticked set, as
    /// Ctrl-click does, rather than making it the only selected row.</summary>
    [Fact]
    public void ClickingATickBoxAddsJustThatRow() => _fx.Invoke(() =>
    {
        var (win, vm, grid, files) = Open("alpha.pdf", "bravo.pdf", "charlie.pdf");
        try
        {
            grid.SelectedIndex = 0;
            Settle(win);

            TickBoxOf(grid, 2).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Settle(win);

            Assert.Equal(new[] { files[0], files[2] }, vm.SelectedSources.OrderBy(s => s));
        }
        finally { win.Close(); }
    });

    [Fact]
    public void TheTickAllBoxTicksEveryFileAndThenNone() => _fx.Invoke(() =>
    {
        var (win, vm, _, files) = Open("alpha.pdf", "bravo.pdf");
        try
        {
            win.TickAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Settle(win);
            Assert.Equal(files, vm.SelectedSources.OrderBy(s => s));

            win.TickAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Settle(win);
            Assert.Empty(vm.SelectedSources);
        }
        finally { win.Close(); }
    });

    /// <summary>With some files ticked, the tick-all box ticks them all, as
    /// Explorer's does; WPF's own toggle would take a part-ticked box to off.</summary>
    [Fact]
    public void ThePartTickedTickAllBoxTicksEverything() => _fx.Invoke(() =>
    {
        var (win, vm, grid, files) = Open("alpha.pdf", "bravo.pdf", "charlie.pdf");
        try
        {
            grid.SelectedIndex = 1;
            Settle(win);
            Assert.Null(win.TickAllBox.IsChecked);   // part-ticked

            win.TickAllBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Settle(win);

            Assert.Equal(files, vm.SelectedSources.OrderBy(s => s));
            Assert.True(win.TickAllBox.IsChecked);
        }
        finally { win.Close(); }
    });

    /// <summary>Every rebuild of the preview briefly clears the grid's
    /// selection; with ticks feeding the plan, that echo must not untick
    /// anything or re-plan with nothing ticked.</summary>
    [Fact]
    public void TypingInFindChangesOnlyTheTickedRowAndKeepsItTicked() => _fx.Invoke(() =>
    {
        var (win, vm, grid, _) = Open("a_x.pdf", "b_x.pdf");
        try
        {
            grid.SelectedIndex = 1;
            Settle(win);

            vm.Find = "x";
            vm.Replace = "y";
            Settle(win);

            Assert.Equal("b_y.pdf", vm.Preview[1].NewName);
            Assert.False(vm.Preview[0].Changed);
            Assert.Single(grid.SelectedItems);
            Assert.True(TickBoxOf(grid, 1).IsChecked);
        }
        finally { win.Close(); }
    });

    private static string AutomationPropertiesName(DependencyObject element) =>
        System.Windows.Automation.AutomationProperties.GetName(element);

}
