using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Windows;

/// <summary>The password prompt behind <see cref="Services.IDialogService.AskPassword"/>.
/// One question, two answers: a password (Open, the default button) or
/// null (Skip, and Escape). The Core operation that raised it is blocked on
/// a SynchronizationContext.Send while this is up — see
/// ZipListViewModel.AskPassword — so a closed window always answers, and
/// answers exactly once.</summary>
public partial class PasswordWindow : Window
{
    private string? _answer;

    private PasswordWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _answer = null;
            Close();
        };
        // Focus lands in the box, so the next keystroke is the password and
        // Enter is live immediately.
        Loaded += (_, _) => PwBox.Focus();
    }

    /// <summary>The answer: the typed password, or null for a skip. Internal
    /// so PasswordWindowTests can read it off a window driven by simulated
    /// clicks and keys instead of the modal loop.</summary>
    internal string? Answer => _answer;

    /// <summary>Owner-modal. Returns the password, or null when skipped.</summary>
    public static string? Ask(Window? owner, PasswordRequest request)
    {
        var w = Build(owner, request);
        w.ShowDialog();
        return w._answer;
    }

    /// <summary>Internal, not private, so tests can build the real thing and
    /// drive it without entering ShowDialog — the seam MessageWindow.Build
    /// already established.</summary>
    internal static PasswordWindow Build(Window? owner, PasswordRequest request)
    {
        var w = new PasswordWindow();
        // WPF throws if handed an owner that has never been shown.
        if (owner is { IsVisible: true }) w.Owner = owner;

        w.MessageText.Text = request.Inside is null
            ? $"{request.Item} is password-protected."
            : $"{request.Item} inside {request.Inside} is password-protected.";
        // The window's accessible name is its title, so without this a
        // screen reader announces the dialog and then has nothing to say
        // about what wants a password.
        AutomationProperties.SetName(w.MessageText, w.MessageText.Text);
        w.FailedText.Visibility = request.PreviousAttemptFailed ? Visibility.Visible : Visibility.Collapsed;
        // SetResourceReference, not a one-off brush, so the glyph follows a
        // live theme switch like everything else; AccentBronze is a pairing
        // ThemeTests already enforces against Theme.WindowBg.
        w.Glyph.SetResourceReference(ForegroundProperty, "Theme.AccentBronze");
        return w;
    }

    private string Typed => ShowPw.IsChecked == true ? PwPlain.Text : PwBox.Password;

    /// <summary>Nothing typed is not an answer: the window stays, rather than
    /// "opening" with an empty password Core would only reject and re-ask.</summary>
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var typed = Typed;
        if (typed.Length == 0) return;
        _answer = typed;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _answer = null;
        Close();
    }

    private void OnShowPw(object sender, RoutedEventArgs e)
    {
        var show = ShowPw.IsChecked == true;
        if (show) PwPlain.Text = PwBox.Password;
        else PwBox.Password = PwPlain.Text;
        PwPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PwBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        // Keyboard.Focus on a Collapsed element silently lands nowhere, so
        // focus follows whichever box is actually showing.
        if (show) PwPlain.Focus();
        else PwBox.Focus();
    }
}
