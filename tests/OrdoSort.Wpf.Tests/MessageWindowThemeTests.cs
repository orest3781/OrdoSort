using System.Windows;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Every alert, warning and confirmation the app raises used to be a
/// Win32 <c>MessageBox</c>. A message box is not a WPF Window: TitleBar's
/// class-level Loaded handler never saw it, no <c>Theme.*</c> brush reached it,
/// and it ignored the configured app font — so in the four dark schemes the app
/// opened a white dialog with a light title bar on top of a dark window
/// (2026-08-22 UI audit, UI-02). It sat on every error path in the app and no
/// test touched it, because there was nothing testable about it.
///
/// <see cref="MessageWindow"/> is an ordinary Window, which is the entire fix —
/// it inherits the implicit Window style and TitleBar hooks it like any other.
/// This proves that claim rather than restating it: the brushes are read off a
/// really-constructed window, per scheme, and the glyph is held to the same
/// 4.5:1 floor as every other text pairing the app ships.
///
/// The point of the contrast assertion is not that these three tokens pass —
/// ThemeTests already enforces them against Theme.WindowBg — but that this
/// window's background really IS Theme.WindowBg, so those enforced pairings are
/// the ones actually on screen here.</summary>
[Collection(HighlightContrastTests.Name)]
public class MessageWindowThemeTests
{
    private readonly HighlightContrastFixture _fx;
    public MessageWindowThemeTests(HighlightContrastFixture fx) => _fx = fx;

    private static Rgb ToRgb(Brush b)
    {
        var c = ((SolidColorBrush)b).Color;
        return new Rgb(c.R, c.G, c.B);
    }

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var s in ThemePalette.Schemes)
            foreach (var kind in new[] { MessageKind.Info, MessageKind.Warning, MessageKind.Question })
                data.Add(s.Key, (int)kind);
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public void ItWearsTheAppThemeAndItsGlyphClearsTheContrastFloor(string schemeKey, int kindValue)
    {
        var kind = (MessageKind)kindValue;
        _fx.Invoke(() =>
        {
            var scheme = ThemePalette.FindScheme(schemeKey)!;
            ThemeManager.Apply(_fx.App, scheme);

            var w = MessageWindow.Build(null, "Something needs your attention.", "OrdoSort — test", kind);
            w.Left = -20000; w.Top = 0; w.ShowActivated = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            try
            {
                w.Show();
                w.UpdateLayout();

                // The whole fix in one assertion: this is a themed WPF window,
                // not an OS-painted box.
                Assert.Equal(scheme.Palette.WindowBg, ToRgb(w.Background));

                var glyph = ToRgb(w.Glyph.Foreground);
                var ratio = ThemePalette.ContrastRatio(glyph, scheme.Palette.WindowBg);
                Assert.True(ratio >= 4.5,
                    $"{kind} glyph is {glyph} on {scheme.Key}'s WindowBg = {ratio:F2}:1, under the " +
                    "4.5 floor this app holds every shipped text pairing to");

                // The message is text on that same background, so it has to be
                // the theme's own text colour rather than an inherited default.
                Assert.Equal(scheme.Palette.Text, ToRgb(w.MessageText.Foreground));

                // And it follows the configured app font, which a Win32 message
                // box never did — Appearance offers 6-72pt and the old dialog
                // ignored all of it.
                Assert.Equal((double)_fx.App.Resources["AppFontSize"], w.FontSize);

                // The OK button's LABEL against the accent it sits on. This is
                // a separate assertion because the first version of this window
                // passed every check above while failing this one: Content was
                // the plain string "OK", which ContentPresenter auto-wraps in a
                // TextBlock resolving the application-level implicit style
                // (Theme.Text) instead of inheriting Theme.AccentText from the
                // button. That measured 1.27:1 in graphite and rendered as
                // grey-on-grey that read as disabled — found by looking at the
                // running app, not by any test then in the suite.
                w.ConfigureAsStatement();
                w.UpdateLayout();
                var label = Assert.IsType<System.Windows.Controls.TextBlock>(w.PrimaryAction.Content);
                var labelRatio = ThemePalette.ContrastRatio(
                    ToRgb(label.Foreground), scheme.Palette.Accent);
                Assert.True(labelRatio >= 4.5,
                    $"the OK label is {ToRgb(label.Foreground)} on {scheme.Key}'s Accent = " +
                    $"{labelRatio:F2}:1 — a plain-string Content will do exactly this");
            }
            finally { w.Close(); }
        });
    }

    /// <summary>A statement gets one button; a question gets two, and the
    /// negative one is the default. That last part is deliberate and worth
    /// pinning: every question this app asks has a destructive "yes" — remove
    /// the client, reset the number, overwrite another station's settings,
    /// discard the edits — so a reflexive Enter must land on the safe answer.
    /// Win32's own habit is the opposite.</summary>
    [Fact]
    public void AQuestionDefaultsToTheSafeAnswerAndAStatementHasOneButton() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var question = MessageWindow.Build(null, "Remove it?", "OrdoSort — test", MessageKind.Question);
        question.PrimaryAction.Content = "Remove";
        question.SecondaryAction.Content = "Keep it";
        question.SecondaryAction.IsDefault = true;
        try
        {
            question.Left = -20000; question.ShowActivated = false;
            question.WindowStartupLocation = WindowStartupLocation.Manual;
            question.Show();
            question.UpdateLayout();

            Assert.True(question.SecondaryAction.IsVisible, "a question needs both buttons");
            Assert.True(question.SecondaryAction.IsDefault,
                "the safe answer must be the default — Enter is a reflex, and every 'yes' " +
                "this app asks about destroys something");
            Assert.False(question.PrimaryAction.IsDefault);

            // No IsCancel anywhere: a button carrying both IsCancel and a Click
            // handler has two things racing to close the window, which is how a
            // dialog ends up answering "yes" to an Escape.
            Assert.False(question.PrimaryAction.IsCancel);
            Assert.False(question.SecondaryAction.IsCancel);
        }
        finally { question.Close(); }
    });
}
