using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-03: OK carries IsDefault, and WPF fires a default button from
/// any Enter that reaches the window UNHANDLED. Only three of Settings'
/// text boxes claimed Enter themselves, so Enter in the inbox path, a
/// route's folder, poll seconds and the rest validated all seven tabs and
/// closed the dialog mid-edit. The window now handles an Enter that
/// bubbles out of a single-line text box; a box with its own Enter
/// behaviour — the alert-term KeyBinding here — still gets it first.</summary>
[Collection(HighlightContrastTests.Name)]
public class SettingsEnterKeyTests
{
    private readonly HighlightContrastFixture _fx;
    public SettingsEnterKeyTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static TextBox Named(Window win, string automationName) =>
        Descendants<TextBox>(win).Single(t => AutomationProperties.GetName(t) == automationName);

    private static KeyEventArgs EnterKeyDown(UIElement target) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, Key.Return)
        { RoutedEvent = UIElement.KeyDownEvent };

    private static (SettingsWindow win, SettingsViewModel vm) Build()
    {
        // The same construction FieldClippingTests uses: fake folder checks,
        // an inline scheduler, one route so every tab has content.
        var cfg = new Config
        {
            Inbox = @"C:\inbox", Deferred = @"C:\deferred",
            Routes = { new Route { Label = "Invoices", Path = @"C:\routes\invoices", Color = "#2e7d32" } },
        };
        var vm = new SettingsViewModel(cfg, new FakeDialogs(),
            directoryExists: _ => true, fileExists: _ => true,
            scheduler: new InlineWorkScheduler());
        var win = new SettingsWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        return (win, vm);
    }

    [Fact]
    public void EnterInThePathBoxIsSwallowedBeforeItReachesOk() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, _) = Build();
        try
        {
            var args = EnterKeyDown(Named(win, "Inbox folder"));
            Named(win, "Inbox folder").RaiseEvent(args);

            Assert.True(args.Handled, "an Enter that bubbles out of the Inbox box must be handled, or IsDefault fires OK");
            Assert.True(win.IsVisible);
        }
        finally { try { win.Close(); } catch { /* best effort */ } }
    });

    [Fact]
    public void EnterInTheAlertTermBoxStillAddsTheTerm() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, vm) = Build();
        try
        {
            // The alert-term box lives on "Alerts & polling", not the
            // default-selected tab: TabControl only realizes the selected
            // TabItem's content, so an unselected tab's descendants are not
            // in the visual tree yet (same reason FieldClippingTests selects
            // "Destinations" before it goes looking there).
            var tabControl = Descendants<TabControl>(win).First();
            tabControl.SelectedItem = tabControl.Items.Cast<TabItem>()
                .First(t => AutomationProperties.GetName(t) == "Alerts & polling");
            win.UpdateLayout();

            vm.NewAlertText = "urgent";
            var box = Named(win, "New alert term");
            box.RaiseEvent(EnterKeyDown(box));

            // AddAlertCommand (SettingsViewModel.cs:743-746) runs from the box's
            // own KeyBinding and clears NewAlertText as its first act — the
            // only path that empties the box.
            Assert.Equal("", vm.NewAlertText);
        }
        finally { try { win.Close(); } catch { /* best effort */ } }
    });
}
