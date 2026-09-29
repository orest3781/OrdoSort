using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Nothing is asked of these dialogs — the window is only built, not
/// driven.</summary>
file sealed class SilentDialogs : ILabelDialogs
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => false;
    public string? AskSaveFile(string filter, string suggestedName) => null;
}

/// <summary>BoxLabels.exe's menu (File → Change labels file…, Exit;
/// Settings…) and the line naming which box-labels.json is open. The file
/// is shown at the top because this app's whole risk is printing from the
/// wrong file, and that mistake cannot be spotted once numbers are on
/// physical boxes.
///
/// Both appear ONLY in the standalone. OrdoSort reaches the same window from
/// Tools, where the path and the label style are edited on its own Settings
/// page — a second way to change one setting is the thing being avoided, and
/// "OrdoSort is unchanged" is a promise this test keeps.</summary>
[Collection(HighlightContrastTests.Name)]
public class StandaloneMenuTests : UiTest, IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "boxlabels_menu_" + Guid.NewGuid().ToString("N"))).FullName;

    public StandaloneMenuTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LabelMakerViewModel Vm() =>
        new(null, Path.Combine(_dir, "box-labels.json"), new SilentDialogs(), "Box labels");

    private LabelMakerWindow Standalone(StandaloneMenu menu) =>
        new(Vm(), "Box Labels", "Box Labels — Print preview", standalone: true, standaloneMenu: menu);

    [Fact]
    public void OrdoSortsWindowHasNoMenu() => _fx.Invoke(() =>
    {
        var window = new LabelMakerWindow(Vm(), "OrdoSort — Box labels", "OrdoSort — Print preview");
        try
        {
            Assert.Equal(Visibility.Collapsed, window.StoreBar.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStandaloneShowsTheStoreItIsPrintingFrom() => _fx.Invoke(() =>
    {
        var store = @"\\server\records\box-labels.json";
        var window = Standalone(new StandaloneMenu(store, () => { }, () => { }));
        try
        {
            Assert.Equal(Visibility.Visible, window.StoreBar.Visibility);
            Assert.Equal(store, window.StorePathText.Text);
            // a long share path is trimmed, so the whole thing has to be
            // recoverable on hover or it is not really shown at all
            Assert.Equal(store, window.StorePathText.ToolTip);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheMenuRunsTheHostsChangeFileAndSettingsActions() => _fx.Invoke(() =>
    {
        var changed = 0;
        var settings = 0;
        var window = Standalone(new StandaloneMenu("x.json", () => changed++, () => settings++));
        try
        {
            window.ChangeFileMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.SettingsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal((1, 1), (changed, settings));
        }
        finally { window.Close(); }
    });

    /// <summary>Ctrl+, opens Settings, as in OrdoSort.</summary>
    [Fact]
    public void CtrlCommaOpensSettings() => _fx.Invoke(() =>
    {
        var settings = 0;
        var window = Standalone(new StandaloneMenu("x.json", () => { }, () => settings++));
        try
        {
            var binding = window.InputBindings.OfType<KeyBinding>()
                .Single(b => b.Key == Key.OemComma && b.Modifiers == ModifierKeys.Control);
            binding.Command.Execute(null);

            Assert.Equal(1, settings);
        }
        finally { window.Close(); }
    });

    /// <summary>BoxLabels.exe shows this as its main window, not a dialog,
    /// and a Cancel button (IsCancel) only ends dialogs: the Close button
    /// did nothing there (reported 2026-09-25). Clicked through its
    /// automation peer so the button's real click path runs.</summary>
    [Fact]
    public void TheCloseButtonClosesTheStandaloneWindow() => _fx.Invoke(() =>
    {
        var window = Standalone(new StandaloneMenu("x.json", () => { }, () => { }));
        window.Left = -20000;
        window.Top = 0;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(window.CloseButton);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Background);

            Assert.True(closed, "the Close button left the Box Labels window open");
        }
        finally { if (!closed) window.Close(); }
    });

    [Fact]
    public void FileExitClosesTheStandaloneWindow() => _fx.Invoke(() =>
    {
        var window = Standalone(new StandaloneMenu("x.json", () => { }, () => { }));
        window.Left = -20000;
        window.ShowActivated = false;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            window.ExitMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(closed);
        }
        finally { if (!closed) window.Close(); }
    });
}
