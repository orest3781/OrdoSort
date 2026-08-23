using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace OrdoSort.Wpf.Windows;

/// <summary>What kind of thing the app is saying. Decides the glyph and its
/// colour, nothing else — the button layout is decided by whether a question is
/// being asked.</summary>
public enum MessageKind
{
    Info,
    Warning,
    Question,
}

/// <summary>The app's own replacement for <c>MessageBox.Show</c>. See the
/// XAML's own comment for why a Win32 message box could not be themed.
///
/// Two shapes only, because that is all <see cref="Services.IDialogService"/>
/// asks for: a statement with one dismiss button, and a question with two.
///
/// <para>No button uses <c>IsCancel</c>. A button carrying both
/// <c>IsCancel</c> and a <c>Click</c> handler has two things racing to close
/// the window and set <c>DialogResult</c>, which is exactly the kind of
/// ambiguity that produces a dialog that answers "yes" when Esc was
/// pressed. Escape is handled once, at the window, and always means the
/// negative answer.</para></summary>
public partial class MessageWindow : Window
{
    // Segoe Fluent Icons, matching the glyphs the rest of the app uses.
    private const string WarningGlyph  = "\uE7BA";
    private const string InfoGlyph     = "\uE946";
    private const string QuestionGlyph = "\uE9CE";

    private bool _answeredYes;

    private MessageWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _answeredYes = false;
            Close();
        };
    }

    /// <summary>A statement: one button, which is the default, so Enter and
    /// Space dismiss it the way they dismiss a Win32 message box. Escape does
    /// too, via the window handler above.</summary>
    public static void Show(Window? owner, string message, string title, MessageKind kind)
    {
        var w = Build(owner, message, title, kind);
        w.ConfigureAsStatement();
        w.ShowDialog();
    }

    /// <summary>A question: two buttons, labelled with what they DO.
    ///
    /// The negative is the default and takes focus, and that is deliberate
    /// rather than a copy of Win32's Yes-first habit. Every place this app asks
    /// a question, "yes" is the destructive answer — remove the client, reset
    /// the number, overwrite another station's settings, discard the edits — so
    /// the safe answer is the one a reflexive Enter should land on. Escape
    /// agrees with it.
    ///
    /// Neither button is styled as the primary action: an accent here would
    /// read as a recommendation, and the app is asking, not advising.</summary>
    public static bool Confirm(Window? owner, string message, string title,
        string yesLabel, string noLabel)
    {
        var w = Build(owner, message, title, MessageKind.Question);
        w.PrimaryAction.Content = yesLabel;
        w.SecondaryAction.Content = noLabel;
        w.SecondaryAction.IsDefault = true;
        w.Loaded += (_, _) => w.SecondaryAction.Focus();
        w.ShowDialog();
        return w._answeredYes;
    }

    /// <summary>The one-button shape. Internal so MessageWindowThemeTests can
    /// build the real thing and read its resolved brushes without entering the
    /// modal loop it would then have to escape.
    ///
    /// The label is a TextBlock carrying PrimaryButtonLabel, NOT the plain
    /// string "OK". A plain string Content is auto-wrapped by ContentPresenter
    /// using an internal template whose TextBlock resolves the
    /// APPLICATION-level implicit TextBlock style (Foreground=Theme.Text) — a
    /// Style Setter, which outranks the inheritance that would otherwise bring
    /// Theme.AccentText down from the button. On the accent background that
    /// measures 1.27:1 in graphite against AccentText's 11.48:1, and it renders
    /// as grey-on-grey that reads as disabled. This is the trap Styles.xaml and
    /// HighlightContrastTests both document at length; it was reproduced here
    /// by eye in the live app before being fixed, and every other PrimaryButton
    /// in the app already passes a styled TextBlock for the same reason.</summary>
    internal void ConfigureAsStatement()
    {
        PrimaryAction.Style = (Style)FindResource("PrimaryButton");
        PrimaryAction.Content = new System.Windows.Controls.TextBlock
        {
            Text = "OK",
            Style = (Style)FindResource("PrimaryButtonLabel"),
        };
        PrimaryAction.IsDefault = true;
        SecondaryAction.Visibility = Visibility.Collapsed;
    }

    /// <summary>Internal, not private, so MessageWindowThemeTests can build a
    /// real one and read its resolved brushes without entering a modal
    /// ShowDialog loop it would then have to escape.</summary>
    internal static MessageWindow Build(Window? owner, string message, string title, MessageKind kind)
    {
        var w = new MessageWindow { Title = title };
        // WPF throws if handed an owner that has never been shown, and a
        // hidden one cannot be centred on either — the startup failures run
        // before any window exists.
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.CentreOnTheWorkArea();

        w.MessageText.Text = message;
        // The window's accessible name is its title, so without this a screen
        // reader announces the dialog and then has nothing to say about why it
        // appeared.
        AutomationProperties.SetName(w.MessageText, message);

        var (glyph, brushKey) = kind switch
        {
            MessageKind.Warning => (WarningGlyph, "Theme.StatusAmber"),
            MessageKind.Question => (QuestionGlyph, "Theme.AccentBronze"),
            _ => (InfoGlyph, "Theme.AccentBronze"),
        };
        w.Glyph.Text = glyph;
        // SetResourceReference, not a one-off brush: this keeps the glyph on a
        // DynamicResource so it follows a live theme switch like everything
        // else, and both keys are pairings ThemeTests already enforces against
        // Theme.WindowBg.
        w.Glyph.SetResourceReference(ForegroundProperty, brushKey);
        return w;
    }

    /// <summary>Where an OWNERLESS dialog goes — the three startup failures,
    /// which are the ones most likely to be the only thing a user ever sees.
    ///
    /// Not <c>WindowStartupLocation.CenterScreen</c>, which was the first
    /// attempt and was measured landing this window at y=-713 on a machine with
    /// a second monitor mounted above the primary: "screen" there is not the
    /// screen anyone is looking at. This centres on the primary monitor's WORK
    /// AREA, so it also stays clear of the taskbar, and clamps so a dialog
    /// taller than the work area still has its top edge reachable rather than
    /// hanging above it.
    ///
    /// Positioned on Loaded rather than up front because SizeToContent means
    /// the size is not known until the content has measured.</summary>
    private void CentreOnTheWorkArea()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = Math.Max(wa.Left, wa.Left + (wa.Width - ActualWidth) / 2);
            Top = Math.Max(wa.Top, wa.Top + (wa.Height - ActualHeight) / 2);
        };
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _answeredYes = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _answeredYes = false;
        Close();
    }

    /// <summary>Puts the message on the clipboard. Clipboard access genuinely
    /// fails when another process holds it open, and a dialog that threw while
    /// reporting a problem would replace the message the user came here to
    /// read.</summary>
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(MessageText.Text); }
        catch (Exception) { /* clipboard busy — nothing worth saying about it */ }
    }
}
