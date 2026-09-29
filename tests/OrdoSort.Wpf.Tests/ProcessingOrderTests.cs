using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>The processing screen says which order the documents come in
/// (owner request 2026-09-29: "oldest to newest, etc.").</summary>
public class ProcessingOrderTests
{
    [Theory]
    [InlineData("mtime_asc", "Oldest first")]
    [InlineData("mtime_desc", "Newest first")]
    [InlineData("size_desc", "Largest first")]
    [InlineData("size_asc", "Smallest first")]
    [InlineData("filename_asc", "Filename A to Z")]
    [InlineData("filename_desc", "Filename Z to A")]
    public void TheSessionShowsItsOrder(string sort, string expected)
    {
        using var fx = new ShellFixture(c => c.Sort = sort);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();

        fx.Shell.StartProcessing();

        Assert.Equal(expected, fx.Shell.OrderLine);
    }

    /// <summary>Scanner sorts an unknown key filename A to Z; the label says
    /// what actually happens, not what was asked for.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("random")]
    [InlineData(null)]
    public void AnUnknownOrderReadsAsTheOneUsed(string? sort) =>
        Assert.Equal("Filename A to Z", SettingsViewModel.SortLabel(sort));

    /// <summary>Every order Settings offers has its own label, so a new one
    /// added there can't fall through to the fallback unnoticed.</summary>
    [Fact]
    public void EveryOrderInSettingsHasItsOwnLabel()
    {
        foreach (var choice in SettingsViewModel.SortChoices)
            Assert.Equal(choice.Value, SettingsViewModel.SortLabel(choice.Key));
    }
}

/// <summary>The order sits on the headline row, beside "N files left".</summary>
[Collection(HighlightContrastTests.Name)]
public class ProcessingOrderPlacementTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public ProcessingOrderPlacementTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    [Fact]
    public void TheOrderIsOnTheHeadlineRow() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var shellFx = new ShellFixture(c => c.Sort = "mtime_asc");
        shellFx.AddInboxFile("20240115--111111.pdf");
        shellFx.Shell.Initialize();
        var view = new ProcessingView { DataContext = shellFx.Shell };
        var window = new Window
        {
            Content = view, Left = -20000, Top = 0, Width = 420, Height = 900,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            shellFx.Shell.StartProcessing();
            window.UpdateLayout();

            Assert.Equal("Oldest first", view.OrderText.Text);
            Assert.True(view.OrderText.IsVisible);
            var row = Assert.IsType<Grid>(view.OrderText.Parent);
            Assert.Contains(row.Children.OfType<TextBlock>(), t => t.Text == shellFx.Shell.ProgressLine);
        }
        finally
        {
            window.Close();
            shellFx.Dispose();
        }
    });
}
