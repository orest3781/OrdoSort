using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The "More columns…" window rendered for real, off-screen, with a
/// roster as long as the owner's (15-40 columns).</summary>
[Collection(HighlightContrastTests.Name)]
public class ColumnChooserWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public ColumnChooserWindowTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItListsEveryColumnWithTheLockedOnesDisabledAndSearchNarrowsIt(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var all = new[] { "Last", "First", "Control" }.Concat(Enumerable.Range(1, 27).Select(i => $"Field {i}")).ToList();
        var vm = new ColumnChooserViewModel(all, new[] { "Last", "First", "Control" }, new[] { "Last", "First", "Control" });
        var win = new ColumnChooserWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        try
        {
            win.Show();
            win.UpdateLayout();

            var list = Descendants<ListBox>(win).Single();
            Assert.Equal(30, list.Items.Count);
            var boxes = Descendants<CheckBox>(list);
            Assert.False(boxes.Single(b => (string)b.Content == "Last").IsEnabled);
            Assert.True(boxes.Single(b => (string)b.Content == "Field 1").IsEnabled);

            win.SearchBox.Text = "field 2";
            win.UpdateLayout();
            Assert.Equal(new[] { "Field 2", "Field 20", "Field 21", "Field 22", "Field 23", "Field 24", "Field 25", "Field 26", "Field 27" },
                list.Items.Cast<ColumnChoice>().Select(c => c.Header));
        }
        finally { win.Close(); }
    });

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(Descendants<T>(child));
        }
        return results;
    }
}
