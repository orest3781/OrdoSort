using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Enter reaches the one primary button on a screen or window.
/// UX-19: Ready and Done had none, although Processing makes Enter the
/// commit; a keyboard user had to Tab to Start or Back to inbox. UX-22
/// (below): three batch windows styled a primary button without wiring it
/// to Enter while their siblings did. Read off the LOGICAL tree so no
/// window needs to be shown; a button's Style resolves at parse time.</summary>
[Collection(HighlightContrastTests.Name)]
public class DefaultButtonTests
{
    private readonly HighlightContrastFixture _fx;
    public DefaultButtonTests(HighlightContrastFixture fx) => _fx = fx;

    internal static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var x in LogicalDescendants<T>(d)) yield return x;
        }
    }

    private static Button ByName(DependencyObject root, string automationName) =>
        LogicalDescendants<Button>(root).Single(b => AutomationProperties.GetName(b) == automationName);

    [Fact]
    public void StartProcessingAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var view = new ReadyView();
        Assert.True(ByName(view, "Start processing").IsDefault, "Enter on Ready must start the session");
    });

    [Fact]
    public void BackToInboxAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var view = new DoneView();
        Assert.True(ByName(view, "Back to inbox").IsDefault, "Enter on Done must return to the inbox");
    });
}
