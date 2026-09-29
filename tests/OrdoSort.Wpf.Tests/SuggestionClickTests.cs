using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Clicking a name in the suggestion list puts it in the name box
/// (owner request 2026-09-29: "make sure mouse click works").
///
/// The click is the real one as far as WPF is concerned: a left button down
/// and up raised on the row's own container, which runs ListBoxItem's own
/// handling (it takes keyboard focus, then selects). That focus change is
/// the trap: losing focus closes the suggestions, so a row able to take
/// focus empties the list under the click. The name box must keep focus
/// throughout, so typing can go on straight after.
///
/// Real ProcessingView in a real off-screen window on the shared STA
/// fixture, with a real filing loop (ShellFixture), as
/// ProcessingViewImeGuardTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class SuggestionClickTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public SuggestionClickTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private (ShellFixture ShellFx, ProcessingView View, Window Window) Build()
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var shellFx = new ShellFixture();
        shellFx.WriteNamesFile("SMITH JOHN", "SMYTHE ANNE", "JONES MARY");
        shellFx.AddInboxFile("20240115--111111.pdf");
        shellFx.Shell.Initialize();

        var view = new ProcessingView { DataContext = shellFx.Shell };
        var window = new Window
        {
            Content = view, Left = -20000, Top = 0, Width = 900, Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        shellFx.Shell.StartProcessing();
        window.UpdateLayout();
        return (shellFx, view, window);
    }

    /// <summary>A left click as WPF's input system delivers one: the
    /// tunnelling PreviewMouseDown through every element from the popup's
    /// root to the row, then the bubbling MouseDown back up unless something
    /// handled it (and the same for the button coming up). WPF itself turns
    /// each into the left-button events on every element on the way, which
    /// is where the row's own handling and the list's handler both live.</summary>
    private static void Click(UIElement target)
    {
        var pairs = new[]
        {
            (UIElement.PreviewMouseDownEvent, UIElement.MouseDownEvent),
            (UIElement.PreviewMouseUpEvent, UIElement.MouseUpEvent),
        };
        foreach (var (preview, bubble) in pairs)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = preview,
                Source = target,
            };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = bubble,
                    Source = target,
                });
            }
            PumpRender();
        }
    }

    /// <summary>A row of the open suggestion list, laid out as on screen.
    /// The popup draws in its own top-level window, which the host window's
    /// layout never reaches; unlaid, its rows hang off nothing and a click on
    /// one would never reach the list (as WatchListRowTemplateTests does).</summary>
    private static ListBoxItem Row(ProcessingView view, int index)
    {
        Assert.True(view.SuggestPopup.IsOpen, "the suggestion list is not open");
        var card = (FrameworkElement)view.SuggestPopup.Child;
        card.Measure(new Size(400, 300));
        card.Arrange(new Rect(0, 0, 400, 300));
        card.UpdateLayout();
        var row = view.SuggestList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
            ?? throw new InvalidOperationException($"no row {index} in the suggestion list");
        Assert.Same(view.SuggestList, ItemsControl.ItemsControlFromItemContainer(row));
        Assert.NotNull(System.Windows.Media.VisualTreeHelper.GetParent(row));
        return row;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ClickingASuggestionPutsItInTheNameBox(int row) => _fx.Invoke(() =>
    {
        var (shellFx, view, window) = Build();
        try
        {
            view.NameBox.Focus();
            Keyboard.Focus(view.NameBox);
            PumpRender();
            Assert.True(view.NameBox.IsKeyboardFocused, "the name box never took keyboard focus");
            shellFx.Shell.TypedName = "SM";
            PumpRender();
            Assert.True(shellFx.Shell.HasSuggestions);
            var clicked = (string)view.SuggestList.Items[row]!;

            Click(Row(view, row));

            Assert.Equal(clicked, shellFx.Shell.TypedName);
            Assert.False(shellFx.Shell.HasSuggestions);
            Assert.True(view.NameBox.IsKeyboardFocused, "typing must carry on in the name box after the click");
        }
        finally
        {
            window.Close();
            shellFx.Dispose();
        }
    });
}
