using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Manage saved passwords is a two-field entry form — a name and a
/// password — whose Close button carries <c>IsDefault="True"</c>, and the
/// window is opened with <c>ShowDialog()</c> (UnlockWindow.OnManageSaved), so
/// that default really is live. Enter, the gesture every two-field form in
/// Windows answers with "submit", clicked Close and took both typed fields with
/// it. Nothing was saved and nothing said so (2026-08-22 UI audit, UI-08).
///
/// The app already knew this fix and had applied it twice: Settings' "New alert
/// term" box carries its own Return KeyBinding, and UnlockWindow's save-offer
/// box carries OnSaveNameKeyDown for the same reason. This window was missed.
///
/// <para><b>What is asserted, and why not "the window closed".</b> IsDefault
/// and IsCancel only act inside a modal <c>ShowDialog()</c> loop, which cannot
/// be driven from a headless test without pushing a dispatcher frame and
/// scheduling the keystroke into it — fragile machinery to prove a WPF
/// built-in. The mechanism that actually fixes the bug is one step earlier and
/// is directly observable: the entry fields must mark Enter
/// <c>Handled</c>, because a handled key never reaches the default button at
/// all. So these tests assert the handling and its effect, plus — importantly —
/// that the handling is SCOPED to the two fields. That last one is the guard
/// against over-correcting: a window-level Return binding would also "fix" this
/// while silently stealing Enter from Close everywhere else in the window.</para>
///
/// Keystrokes are real PreviewKeyDown events handed to InputManager against a
/// live PresentationSource from a shown (off-screen) window — the pairing
/// UnlockEnterKeyTests established, including <c>Focus()</c> AND
/// <c>Keyboard.Focus()</c>, since InputManager routes to whatever holds
/// KEYBOARD focus and <c>Focus()</c> alone only sets the logical kind.</summary>
[Collection(HighlightContrastTests.Name)]
public class ManageSavedEnterKeyTests
{
    private readonly HighlightContrastFixture _fx;
    public ManageSavedEnterKeyTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Returns whether the app claimed the key.</summary>
    private static bool SimulateEnter(PresentationSource source)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        InputManager.Current.ProcessInput(args);
        return args.Handled;
    }

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static void FocusFor(UIElement target, string what)
    {
        target.Focus();
        Keyboard.Focus(target);
        PumpRender();
        Assert.True(target.IsKeyboardFocused, $"{what} never took keyboard focus");
    }

    public static TheoryData<string> FocusedField() => new() { "name", "password" };

    [Theory, MemberData(nameof(FocusedField))]
    public void EnterAddsThePasswordInsteadOfEscapingToTheDefaultButton(string focused) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var cfg = new Config();
        var vm = new UnlockViewModel(cfg, () => true);
        var window = new ManageSavedWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            window.NewPwLabel.Text = "Northgate Clinic";
            window.NewPwValue.Password = "northgate2024";

            UIElement target = focused == "name" ? window.NewPwLabel : window.NewPwValue;
            FocusFor(target, $"the {focused} box");

            var source = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("no PresentationSource — window never realised");
            var handled = SimulateEnter(source);

            Assert.True(handled,
                $"Enter from the {focused} box was left unhandled, so in the real ShowDialog() " +
                "window it reaches Close's IsDefault — which is the bug: the dialog shuts and " +
                "both typed fields are gone.");

            Assert.Single(vm.Saved);
            Assert.Equal("Northgate Clinic", vm.Saved[0].Label);
            Assert.Equal("northgate2024", vm.Saved[0].Password);

            // Persisted to the config the Unlock window actually reads, not
            // just to the display collection.
            Assert.Single(cfg.SavedPasswords);
            Assert.Equal("Northgate Clinic", cfg.SavedPasswords[0].Label);

            // Ready for the next entry, the same reset the Add button does.
            Assert.Equal("", window.NewPwLabel.Text);
            Assert.Equal("", window.NewPwValue.Password);
        }
        finally { window.Close(); }
    });

    /// <summary>The over-correction guard. A window-level Return binding would
    /// pass the tests above and quietly take Enter away from Close everywhere
    /// else in the dialog — trading one surprise for another. Enter from the
    /// saved-password list must stay unclaimed so the default button still
    /// gets it.</summary>
    [Fact]
    public void EnterOutsideTheEntryFieldsIsLeftForTheDefaultButton() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
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

            window.NewPwLabel.Text = "Northgate Clinic";
            window.NewPwValue.Password = "northgate2024";
            FocusFor(window.SavedList, "the saved-password list");

            var source = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("no PresentationSource — window never realised");
            var handled = SimulateEnter(source);

            Assert.False(handled,
                "Enter from the list was claimed by the app. The Enter fix belongs to the two " +
                "entry fields; claiming it window-wide steals the key from Close's IsDefault " +
                "everywhere else in the dialog.");
            Assert.Empty(vm.Saved);
        }
        finally { window.Close(); }
    });
}
