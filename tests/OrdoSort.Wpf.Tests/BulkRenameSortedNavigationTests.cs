using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-02: sorting the preview grid used to desynchronise "next file
/// needing a name". The window read the selected row's VIEW index, walked
/// Preview's INSERTION order from there, and indexed the view again with the
/// answer — two index spaces that agree only while the grid is unsorted.
/// This drives the real window with a reversed view and asserts the row that
/// opens for editing is the one that actually needs a name.</summary>
[Collection(HighlightContrastTests.Name)]
public class BulkRenameSortedNavigationTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_sortednav_" + Guid.NewGuid());

    public BulkRenameSortedNavigationTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "pdf");
        return path;
    }

    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void NextStrayIgnoresTheGridsSortOrder()
    {
        // Built OUTSIDE the STA call so WaitFor can poll without starving a
        // dispatcher; the window is built inside it.
        var vm = new BulkRenameViewModel();
        vm.AddFilesAsync(new[]
        {
            Touch("SMITH_JOHN_5_5_2024_ACME_RECORDS_1-1__08_02_24_1019_X.pdf"),
            Touch("oddball one.pdf"),
            Touch("GARCIA_MARIA_8_5_2024_ACME_RECORDS_2-1__08_02_24_1020_X.pdf"),
            Touch("oddball two.pdf"),
        }).GetAwaiter().GetResult();
        vm.ReceivedDate = new DateTime(2024, 8, 2);
        vm.ReviewMode = true;
        WaitFor(() => vm.NeedsNameCount == 2, "the batch's preview should settle first");

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var win = new BulkRenameWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowActivated = false,
            };
            try
            {
                win.Show();
                win.UpdateLayout();

                // Reverse the view: insertion order [0,1,2,3] shows as [3,2,1,0].
                var view = (ListCollectionView)CollectionViewSource.GetDefaultView(vm.Preview);
                view.CustomSort = Comparer<object>.Create((a, b) =>
                    vm.Preview.IndexOf((RenameRow)b).CompareTo(vm.Preview.IndexOf((RenameRow)a)));
                win.PreviewGrid.UpdateLayout();

                // From the first stray, the next stray is the other one — the
                // pre-fix index arithmetic lands on SMITH instead.
                win.PreviewGrid.SelectedItem = vm.Preview[1];
                win.NextStrayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                var opened = Assert.IsType<RenameRow>(win.PreviewGrid.CurrentCell.Item);
                Assert.True(opened.NeedsName, $"opened {Path.GetFileName(opened.Source)}, which does not need a name");
                Assert.Same(vm.Preview[3], opened);
            }
            finally
            {
                try { win.Close(); } catch { /* best effort */ }
            }
        });
    }
}
