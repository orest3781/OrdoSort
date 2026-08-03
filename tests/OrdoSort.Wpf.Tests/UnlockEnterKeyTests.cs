using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 8 (audit remediation, 2026-08-02), Step 1: the Unlock
/// window's primary button had neither <c>IsDefault</c> nor a Return
/// <c>KeyBinding</c> on the password box (UnlockWindow.xaml:12-24) — typing a
/// password and pressing Enter did nothing, with no alternate gesture either.
/// The audit called this the sharpest edge it found in an otherwise
/// keyboard-first app. Fixed with <c>IsDefault="True"</c> on the Unlock
/// button.
///
/// This proves the FULL, real behavior end to end — not just that
/// IsDefault="True" is present in markup, which a plain XAML-string
/// assertion could satisfy even if some ancestor intercepted/consumed the
/// key first, or if UnlockCommand's CanExecute gate were wrong. Simulates a
/// genuine keystroke against a real PresentationSource from a shown
/// (off-screen) window — the same <c>PresentationSource.FromVisual</c> +
/// <c>KeyEventArgs</c> construction ProcessingViewImeGuardTests already
/// established in this suite — but handed to
/// <see cref="System.Windows.Input.InputManager.Current"/>.ProcessInput
/// rather than raised directly via <see cref="UIElement.RaiseEvent"/> on the
/// focused element: WPF's own IsDefault plumbing is NOT reachable by a plain
/// tree bubble from wherever focus happens to sit (confirmed empirically —
/// the Button is never an ancestor of the PasswordBox, so a bare
/// KeyDownEvent bubble from the password box never passes through it
/// either way; a first draft of this test that raised PreviewKeyDown then
/// KeyDown directly via RaiseEvent stayed RED even with the fix applied).
/// Handing a single PreviewKeyDown to InputManager instead lets WPF's real
/// input pipeline do its own preview-to-bubble promotion and default-button
/// lookup exactly as it does for a genuine hardware key, which is what
/// actually reaches ButtonBase's native IsDefault handling — there is no
/// app code implementing this to call directly.
///
/// Waits for UnlockCommand's async run via PumpUntilComplete
/// (TriageWindowInitRaceTests' existing helper, copied rather than shared
/// since it's a two-line private static): AsyncRelayCommand.Execute is
/// `async void`, and the continuation after the injected unlocker returns is
/// posted back to this STA thread's dispatcher — a plain blocking wait
/// would deadlock the very thread that continuation needs to run on.</summary>
[Collection(HighlightContrastTests.Name)]
public class UnlockEnterKeyTests
{
    private readonly HighlightContrastFixture _fx;
    public UnlockEnterKeyTests(HighlightContrastFixture fx) => _fx = fx;

    private static void PumpUntilComplete(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
    }

    private static void SimulateEnterKey(UIElement target, PresentationSource source)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        InputManager.Current.ProcessInput(preview);
    }

    [Fact]
    public void EnterInThePasswordBoxInvokesUnlockWithAPasswordEntered() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var invoked = new List<(string Path, string Password)>();
        var vm = new UnlockViewModel(new Config(), () => { },
            unlocker: (path, password) =>
            {
                invoked.Add((path, password));
                return new Unlock.UnlockResult("ok", path, path, InPlace: true);
            },
            fileSize: _ => 0);
        vm.Files.Add(@"C:\inbox\20240101--1111111111.pdf");

        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            // Setting PasswordBox.Password programmatically raises the real
            // PasswordChanged event, exactly like typing — OnPasswordChanged
            // copies it into the view model, same as production.
            window.PwBox.Password = "hunter2";
            window.PwBox.Focus();
            Keyboard.Focus(window.PwBox);

            var source = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("no PresentationSource for the offscreen window");
            SimulateEnterKey(window.PwBox, source);

            PumpUntilComplete(vm.UnlockCommand.Completion);

            var call = Assert.Single(invoked);
            Assert.Equal(@"C:\inbox\20240101--1111111111.pdf", call.Path);
            Assert.Equal("hunter2", call.Password);
            Assert.Equal("1 unlocked", vm.Summary);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Regression guard for the CanExecute gate: with no files
    /// added, UnlockCommand.CanExecute is false (Files.Count == 0), so
    /// the Unlock button is disabled and Enter must NOT invoke it — WPF's
    /// native IsDefault behavior already respects a disabled default button,
    /// but this nails down that OUR CanExecute wiring doesn't accidentally
    /// bypass it.</summary>
    [Fact]
    public void EnterDoesNothingWhenThereAreNoFilesToUnlock() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var invoked = 0;
        var vm = new UnlockViewModel(new Config(), () => { },
            unlocker: (path, password) =>
            {
                invoked++;
                return new Unlock.UnlockResult("ok", path, path, InPlace: true);
            },
            fileSize: _ => 0);
        // deliberately no Files.Add(...)

        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            window.PwBox.Password = "hunter2";
            window.PwBox.Focus();
            Keyboard.Focus(window.PwBox);

            var source = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("no PresentationSource for the offscreen window");
            SimulateEnterKey(window.PwBox, source);

            // Nothing async should even start; a short synchronous check is
            // enough (no PumpUntilComplete needed — Completion stays the
            // already-finished Task.CompletedTask if Execute never ran).
            Assert.Equal(0, invoked);
            Assert.True(vm.UnlockCommand.Completion.IsCompleted);
        }
        finally
        {
            window.Close();
        }
    });
}
