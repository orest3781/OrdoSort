using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>The "✓ Filed to …" confirmation (owner request 2026-09-29):
/// a slim bar at the top of the side panel, not a card at the bottom, and
/// one that never moves the name box or the buttons when it comes and goes.
/// Real ProcessingView in a real off-screen window, as
/// ProcessingViewImeGuardTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class LastActionBarTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public LastActionBarTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private (ShellFixture ShellFx, ProcessingView View, Window Window) Build()
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var shellFx = new ShellFixture();
        shellFx.AddInboxFile("20240115--111111.pdf");
        shellFx.AddInboxFile("20240116--222222.pdf");
        shellFx.Shell.Initialize();
        var view = new ProcessingView { DataContext = shellFx.Shell };
        var window = new Window
        {
            Content = view, Left = -20000, Top = 0, Width = 420, Height = 900,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        shellFx.Shell.StartProcessing();
        window.UpdateLayout();
        return (shellFx, view, window);
    }

    private static double TopOf(FrameworkElement element, Visual within) =>
        element.TransformToAncestor(within).Transform(new Point(0, 0)).Y;

    [Fact]
    public void ItSitsAtTheTopOnOneSlimLine() => _fx.Invoke(() =>
    {
        var (shellFx, view, window) = Build();
        try
        {
            var panel = (StackPanel)view.Content;
            Assert.Same(view.LastActionBar, panel.Children[0]);
            Assert.True(view.LastActionBar.ActualHeight <= 32,
                $"the bar is {view.LastActionBar.ActualHeight:0} px tall; one slim line was asked for");
        }
        finally
        {
            window.Close();
            shellFx.Dispose();
        }
    });

    [Fact]
    public void ShowingItMovesNothingBelowIt() => _fx.Invoke(() =>
    {
        var (shellFx, view, window) = Build();
        try
        {
            Assert.Equal(Visibility.Hidden, view.LastActionBar.Visibility);   // idle, but its line is kept
            var nameBoxBefore = TopOf(view.NameBox, view);

            // inline scheduler and fake viewer: the whole press lands before this returns
            var press = shellFx.Shell.OnRouteAsync(0);
            Assert.True(press.IsCompleted);
            window.UpdateLayout();

            Assert.True(shellFx.Shell.LastActionVisible);
            Assert.Equal(Visibility.Visible, view.LastActionBar.Visibility);
            Assert.Equal(nameBoxBefore, TopOf(view.NameBox, view), 1);
        }
        finally
        {
            window.Close();
            shellFx.Dispose();
        }
    });
}
