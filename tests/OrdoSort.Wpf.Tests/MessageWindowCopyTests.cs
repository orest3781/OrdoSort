using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-20: Copy swallowed both outcomes, so a user could not tell a
/// copy that worked from a clipboard another program was holding — and
/// this button sits on every error dialog in the app. The clipboard call
/// is injected so both branches are driven without touching the real
/// clipboard, which is shared, slow and flaky under a test runner.</summary>
[Collection(HighlightContrastTests.Name)]
public class MessageWindowCopyTests
{
    private readonly HighlightContrastFixture _fx;
    public MessageWindowCopyTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void ASuccessfulCopySaysCopied() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = MessageWindow.Build(null, "Couldn't save C:\\x.csv", "OrdoSort — test", MessageKind.Info);
        string? copied = null;
        w.SetClipboardText = text => copied = text;

        w.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal("Couldn't save C:\\x.csv", copied);
        Assert.Equal("Copied", w.CopyButton.Content);
        Assert.Equal("Copied", AutomationProperties.GetName(w.CopyButton));
    });

    [Fact]
    public void ABusyClipboardSaysTryAgain() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = MessageWindow.Build(null, "Something needs your attention.", "OrdoSort — test", MessageKind.Info);
        w.SetClipboardText = _ => throw new COMException("CLIPBRD_E_CANT_OPEN");

        w.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal("Try again", w.CopyButton.Content);
        Assert.Equal("Another program is holding the clipboard.", w.CopyButton.ToolTip);
        Assert.Equal("Copy this message — try again, another program is holding the clipboard",
            AutomationProperties.GetName(w.CopyButton));
        Assert.True(w.IsEnabled);   // the dialog itself is untouched
    });
}
