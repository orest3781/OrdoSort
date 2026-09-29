using System.Windows;
using System.Windows.Controls.Primitives;
using BoxLabelsApp.Services;
using BoxLabelsApp.Windows;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Box Labels' Settings window (2026-09-28): OK refuses while a
/// setting is unusable and says why; Change… puts the picked labels file
/// into the settings, applied only on OK.</summary>
[Collection(HighlightContrastTests.Name)]
public class BoxLabelsSettingsWindowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public BoxLabelsSettingsWindowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static BoxLabelsSettingsViewModel Vm() =>
        new("x.json", "auto", "", 0, BoxLabels.LabelStyle.Default, "");

    private static BoxLabelsSettingsWindow OffScreen(BoxLabelsSettingsViewModel vm, Func<string?> pick) =>
        new(vm, pick)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

    [Fact]
    public void OkWithABadTextSizeIsRefusedAndSaysWhy() => _fx.Invoke(() =>
    {
        var vm = Vm();
        var window = OffScreen(vm, () => null);
        window.Show();
        try
        {
            Settle(window);
            vm.UiFontSizeText = "5";

            Assert.False(window.TryAccept());
            Assert.Contains("6 to 72", window.ProblemText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ChangePutsThePickedFileIntoTheSettings() => _fx.Invoke(() =>
    {
        var vm = Vm();
        var window = OffScreen(vm, () => "picked.json");
        window.Show();
        try
        {
            Settle(window);
            window.ChangeFileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("picked.json", vm.LabelsFile);
        }
        finally { window.Close(); }
    });

    /// <summary>The spec's "Discard / Keep editing": closing with unsaved
    /// edits asks first; Keep editing leaves the window open.</summary>
    [Fact]
    public void ClosingWithUnsavedEditsAsksAndKeepEditingStays() => _fx.Invoke(() =>
    {
        var vm = Vm();
        var asked = 0;
        var window = new BoxLabelsSettingsWindow(vm, () => null, confirmDiscard: () => { asked++; return false; })
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            vm.ThemeDark = true;

            window.Close();

            Assert.Equal(1, asked);
            Assert.False(closed);
        }
        finally { if (!closed) { vm.ThemeAuto = true; window.Close(); } }
    });

    [Fact]
    public void ClosingWithNoEditsDoesNotAsk() => _fx.Invoke(() =>
    {
        var asked = 0;
        var window = new BoxLabelsSettingsWindow(Vm(), () => null, confirmDiscard: () => { asked++; return false; })
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        Settle(window);

        window.Close();

        Assert.Equal(0, asked);
    });

    [Fact]
    public void CancellingThePickerKeepsTheFile() => _fx.Invoke(() =>
    {
        var vm = Vm();
        var window = OffScreen(vm, () => null);
        window.Show();
        try
        {
            Settle(window);
            window.ChangeFileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("x.json", vm.LabelsFile);
        }
        finally { window.Close(); }
    });
}
