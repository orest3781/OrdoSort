using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Views;

public partial class ProcessingView : UserControl
{
    private ShellViewModel? _shell;

    public ProcessingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_shell is not null) _shell.RequestNameFocus -= FocusNameBox;
            _shell = DataContext as ShellViewModel;
            if (_shell is not null) _shell.RequestNameFocus += FocusNameBox;
        };
    }

    private void FocusNameBox()
    {
        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text.Length;
    }

    /// <summary>True while an IME composition owns this keystroke. WPF
    /// reports the key that resolves a composition (often Enter, confirming
    /// a CJK candidate) as <see cref="Key.ImeProcessed"/> rather than the
    /// literal key — treating that as one of this view's own Enter/Tab/arrow
    /// verbs would file the document (or otherwise act) on what was really
    /// just confirming a composed name, not a genuine keystroke aimed at this
    /// app. Internal + static (rather than inlined into the handler below) so
    /// a test can drive it directly with a real KeyEventArgs.</summary>
    internal static bool IsImeComposing(KeyEventArgs e) => e.Key == Key.ImeProcessed;

    /// <summary>Internal, not private, so a test can call it directly with a
    /// synthesized KeyEventArgs and prove the Enter contract end to end: a
    /// genuine Enter still files (see ShellViewModel.EnterTargetIndex/
    /// OnEnter — that contract is unchanged here), while one WPF reports as
    /// IME-owned does not.</summary>
    internal void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shell is null || IsImeComposing(e)) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Tab when !shift:
                var before = _shell.TypedName.Length;
                if (_shell.CompleteNextWord())
                {
                    // The word Tab just added is SELECTED, so
                    // each press visibly claims one more word (and typing
                    // over it discards just that word)
                    NameBox.Select(before, NameBox.Text.Length - before);
                    e.Handled = true;
                }
                break;
            case Key.Tab when shift:
                if (_shell.DropLastWord())
                {
                    NameBox.CaretIndex = NameBox.Text.Length;
                    e.Handled = true;
                }
                break;
            case Key.Down:
            case Key.Up:
                // walk the matches instead of taking only the top one: the
                // list can run to 8, and arrowing reaches all of them without the mouse
                if (_shell.CycleSuggestion(e.Key == Key.Down ? 1 : -1))
                {
                    NameBox.CaretIndex = NameBox.Text.Length;
                    e.Handled = true;
                }
                break;
            case Key.Enter:
                _shell.OnEnter();
                e.Handled = true;
                break;
            case Key.Escape when _shell.HasSuggestions:
                // first Esc closes the popup; a second one stops the session
                _shell.DismissSuggestions();
                e.Handled = true;
                break;
        }
    }

    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _shell?.DismissSuggestions();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // programmatic sets (Tab-complete, clear-on-advance) park the caret at
        // 0; the filing loop always wants it at the end
        if (NameBox.CaretIndex == 0 && NameBox.Text.Length > 0)
            NameBox.CaretIndex = NameBox.Text.Length;
    }

    private void SuggestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shell is null || e.AddedItems.Count == 0) return;
        _shell.TypedName = (string)e.AddedItems[0]!;
        _shell.DismissSuggestions();
        FocusNameBox();
    }
}
