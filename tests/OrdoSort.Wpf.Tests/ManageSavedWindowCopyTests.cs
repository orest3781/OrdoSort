using System.Windows;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Portable-saved-passwords (2026-08-08), Task 2 Step 3: this
/// session cannot drive the real UI (screen capture returns black, input
/// injection is denied), so the new storage-note copy in ManageSavedWindow
/// ("Tried automatically on Unlock. Stored as plain text in the shared
/// config.json — anyone who can open that file can read them.") is verified
/// headlessly instead, against the real window and the real
/// Theme/Styles.xaml, off-screen.
///
/// UnlockWindow.xaml has a documented history of exactly this failure shape:
/// a horizontal StackPanel measures its children with unconstrained width
/// along the stack axis, so a TextWrapping="Wrap" TextBlock inside one never
/// finds a wrap point and clips mid-sentence instead (row 4's caption,
/// "Every saved password is tried automatically…", sits in precisely that
/// trap today). ManageSavedWindow's note lives in a plain, VERTICAL
/// StackPanel — width-constrained by construction — so it should not
/// reproduce that bug, but "should" is exactly the kind of claim this task
/// asked to be proven, not assumed, given this session can't just look at
/// the window.</summary>
[Collection(HighlightContrastTests.Name)]
public class ManageSavedWindowCopyTests
{
    private readonly HighlightContrastFixture _fx;
    public ManageSavedWindowCopyTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StorageNoteWrapsInsteadOfClippingInBothPalettes(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var vm = new UnlockViewModel(new Config(), () => true);
        var window = new ManageSavedWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var note = window.StorageNote;
            Assert.True(note.ActualWidth > 0 && note.ActualHeight > 0,
                "the storage note never measured/arranged at all");

            // Width proof: the window is 420 wide with a 14px DockPanel
            // margin on each side, so anything genuinely constrained sits
            // well under 400px. A TextBlock caught in the horizontal-
            // StackPanel trap instead renders at its full, UNWRAPPED
            // single-line width, which for this sentence is far wider than
            // that — this is the same failure shape UnlockWindow's row 4
            // has today.
            Assert.True(note.ActualWidth < 400,
                $"the note rendered {note.ActualWidth:F0}px wide — that's " +
                "as wide as an unwrapped single line, not text constrained " +
                "to the window");

            // Wrapping proof: genuinely more than one line tall. A clipped,
            // never-wrapped TextBlock still reports ONE line's worth of
            // height even though its content (and often its rendered
            // width) overflowed the container.
            var singleLineHeight = note.FontSize * note.FontFamily.LineSpacing;
            Assert.True(note.ActualHeight > singleLineHeight * 1.5,
                $"the note is only {note.ActualHeight:F0}px tall against a " +
                $"~{singleLineHeight:F0}px single line — it reads like one " +
                "clipped line, not wrapped text");
        }
        finally
        {
            window.Close();
        }
    });
}
