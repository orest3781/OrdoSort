using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace OrdoSort.Wpf.Windows;

/// <summary>The date prompt behind <see cref="Services.IDialogService.AskDate"/>.
/// One question, two answers: a YYYYMMDD date (Rename, the default button)
/// or null (Cancel, and Escape) — see PasswordWindow's own doc comment for
/// why the modal shape is identical.</summary>
public partial class StandardiseDateWindow : Window
{
    private string? _answer;

    private StandardiseDateWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _answer = null;
            Close();
        };
        // Focus lands in the box with the default text selected, so typing
        // immediately replaces it rather than inserting into the middle.
        Loaded += (_, _) => { DateBox.Focus(); DateBox.SelectAll(); };
    }

    /// <summary>The answer: the typed date, or null for a cancel. Internal
    /// so StandardiseDateWindowTests can read it off a window driven by
    /// simulated clicks and keys instead of the modal loop.</summary>
    internal string? Answer => _answer;

    /// <summary>Owner-modal. Returns the accepted date, or null when
    /// cancelled.</summary>
    public static string? Ask(Window? owner, string defaultDate, int fileCount)
    {
        var w = Build(owner, defaultDate, fileCount);
        w.ShowDialog();
        return w._answer;
    }

    /// <summary>Internal, not private, so tests can build the real thing
    /// and drive it without entering ShowDialog — the seam PasswordWindow.Build
    /// already established.</summary>
    internal static StandardiseDateWindow Build(Window? owner, string defaultDate, int fileCount)
    {
        var w = new StandardiseDateWindow();
        // WPF throws if handed an owner that has never been shown.
        if (owner is { IsVisible: true }) w.Owner = owner;

        w.DateBox.Text = defaultDate;
        w.MessageText.Text = fileCount == 1
            ? "Enter the date for this file."
            : $"Enter the date for these {fileCount} files.";
        // The window's accessible name is its title, so without this a
        // screen reader announces the dialog and then has nothing to say
        // about what it's asking.
        AutomationProperties.SetName(w.MessageText, w.MessageText.Text);
        // SetResourceReference, not a one-off brush, so the glyph follows a
        // live theme switch like everything else; AccentBronze is a pairing
        // ThemeTests already enforces against Theme.WindowBg.
        w.Glyph.SetResourceReference(ForegroundProperty, "Theme.AccentBronze");
        return w;
    }

    /// <summary>Refuses anything that isn't a real calendar date spelled as
    /// exactly 8 digits — wrong length, non-digits, and an impossible date
    /// (month 13, Feb 30) are all "nonsense" the brief says to refuse, and
    /// TryParseExact rejects all three in one call rather than needing a
    /// hand-rolled length/regex/range check that could disagree with it.
    /// Internal, not private, so a window test can pin the exact boundary
    /// without driving the real dialog — the same testability reason
    /// MergePdfsWindow.SupportedFilesFilter is internal.</summary>
    internal static bool IsValidDate(string text) =>
        DateTime.TryParseExact(text.Trim(), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Nothing usable typed is not an answer: the window stays,
    /// rather than "renaming" with a date Core would only reject and
    /// produce a garbage name from.</summary>
    private void OnRename(object sender, RoutedEventArgs e)
    {
        var typed = DateBox.Text.Trim();
        if (!IsValidDate(typed))
        {
            FailedText.Visibility = Visibility.Visible;
            return;
        }
        _answer = typed;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _answer = null;
        Close();
    }
}
