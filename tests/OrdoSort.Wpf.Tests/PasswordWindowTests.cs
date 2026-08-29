using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The prompt a locked zip or PDF raises mid-run. Built through the
/// same internal Build seam MessageWindow exposes, so the real window is
/// constructed and shown off-screen without entering the modal loop Ask
/// would then have to escape. Escape is simulated through InputManager, the
/// way UnlockEnterKeyTests drives a keystroke: the window handles it in
/// PreviewKeyDown, which a tunnelling event from the root reaches.</summary>
[Collection(HighlightContrastTests.Name)]
public class PasswordWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public PasswordWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static PasswordWindow Show(PasswordRequest request)
    {
        var w = PasswordWindow.Build(null, request);
        w.Left = -20000; w.Top = 0; w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Show();
        w.UpdateLayout();
        OverflowProbe.PumpRender();
        w.UpdateLayout();
        return w;
    }

    [Fact]
    public void ALooseItemIsNamedOnItsOwn() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("Batch 12.zip", null, false));
        try
        {
            Assert.Equal("Batch 12.zip is password-protected.", w.MessageText.Text);
            Assert.False(w.FailedText.IsVisible);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void AnItemInsideAnArchiveSaysWhereItLivesAndAFailedTrySaysSo() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("report.pdf", "Batch 12.zip", true));
        try
        {
            Assert.Equal("report.pdf inside Batch 12.zip is password-protected.", w.MessageText.Text);
            Assert.True(w.FailedText.IsVisible);
            Assert.Equal("That password didn't open it.", w.FailedText.Text);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void OpenAnswersWithWhatWasTypedAndIsTheDefaultButton() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            Assert.True(w.OpenButton.IsDefault, "Enter must mean Open");
            w.PwBox.Password = "secret";
            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("secret", w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void OpenWithNothingTypedStaysOpenRatherThanAnsweringNothing() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(w.IsVisible);
            Assert.Null(w.Answer);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void SkipAnswersNull() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "typed but abandoned";
            w.SkipButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void EscapeIsASkipEvenWithAPasswordTyped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "typed but abandoned";
            var source = PresentationSource.FromVisual(w)!;
            InputManager.Current.ProcessInput(
                new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void ShowRevealsTheTypedPasswordAndHidingItKeepsIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show(new PasswordRequest("a.zip", null, false));
        try
        {
            w.PwBox.Password = "secret";
            w.ShowPw.IsChecked = true;
            w.UpdateLayout();
            Assert.True(w.PwPlain.IsVisible);
            Assert.False(w.PwBox.IsVisible);
            Assert.Equal("secret", w.PwPlain.Text);

            w.PwPlain.Text = "secret2";
            w.ShowPw.IsChecked = false;
            w.UpdateLayout();
            Assert.True(w.PwBox.IsVisible);
            Assert.Equal("secret2", w.PwBox.Password);

            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("secret2", w.Answer);
        }
        finally { if (w.IsVisible) w.Close(); }
    });
}
