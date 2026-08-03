using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
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

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static void SimulateEnterKey(UIElement target, PresentationSource source)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        InputManager.Current.ProcessInput(preview);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static UnlockWindow OffScreen(UnlockViewModel vm) => new(vm)
    {
        Left = -20000, Top = 0, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    /// <summary>Runs one real batch through the Enter gesture so the save
    /// offer is up, and hands back the live "Save password as" TextBox beside
    /// its Save button (UnlockWindow.xaml). Every step is the production path:
    /// the banner only appears when a TYPED password unlocked something new.</summary>
    private static TextBox ArrangeSaveOffer(
        UnlockWindow window, UnlockViewModel vm, List<(string Path, string Password)> invoked)
    {
        window.Show();
        window.UpdateLayout();

        window.PwBox.Password = "hunter2";
        window.PwBox.Focus();
        Keyboard.Focus(window.PwBox);

        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("no PresentationSource for the offscreen window");
        SimulateEnterKey(window.PwBox, source);
        PumpUntilComplete(vm.UnlockCommand.Completion);

        Assert.Single(invoked);
        Assert.True(vm.SaveBannerVisible,
            "the save offer never appeared — this test's arrangement is broken, not the fix");

        window.UpdateLayout();
        PumpRender();
        window.UpdateLayout();

        return Descendants<TextBox>(window)
                .FirstOrDefault(t => AutomationProperties.GetName(t) == "Save password as")
            ?? throw new InvalidOperationException(
                "no \"Save password as\" TextBox realized under UnlockWindow");
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

    /// <summary>The cost of making the Unlock button the default: the save
    /// offer's own "Save password as" box (UnlockWindow.xaml) sits beside a
    /// Save button, and Enter there — the obvious gesture — fired the DEFAULT
    /// button instead. <see cref="UnlockViewModel.UnlockAsync"/> calls
    /// ResetBanner at the start of every run, so the offer being answered was
    /// destroyed and nothing was saved. Measured against the build that had
    /// IsDefault but no key binding on the box:
    /// <code>
    /// unlocker invocations: 1     (the arrangement run; Enter re-ran it -> 2 expected below)
    /// SaveBannerVisible now: False
    /// SaveBannerName now:    ""
    /// saved entries:         0
    /// </code>
    /// Before IsDefault existed this was a harmless no-op, which is why the
    /// regression arrived with the fix rather than before it.</summary>
    [Fact]
    public void EnterInTheSavePasswordBoxSavesInsteadOfRerunningTheBatch() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var cfg = new Config();
        var configSaves = 0;
        var invoked = new List<(string Path, string Password)>();
        var vm = new UnlockViewModel(cfg, () => configSaves++,
            unlocker: (path, password) =>
            {
                invoked.Add((path, password));
                return new Unlock.UnlockResult("ok", path, path, InPlace: true);
            },
            fileSize: _ => 0);
        vm.Files.Add(@"C:\inbox\20240101--1111111111.pdf");

        var window = OffScreen(vm);
        try
        {
            var saveAs = ArrangeSaveOffer(window, vm, invoked);

            // typed into the box, not poked into the view model: the binding
            // is UpdateSourceTrigger=PropertyChanged, and SaveBannerCommand's
            // CanExecute gate depends on it having arrived
            saveAs.Text = "Acme scans";
            Assert.Equal("Acme scans", vm.SaveBannerName);

            saveAs.Focus();
            Keyboard.Focus(saveAs);
            Assert.True(saveAs.IsKeyboardFocused, "the save-as box never took keyboard focus");

            var source = PresentationSource.FromVisual(window)!;
            SimulateEnterKey(saveAs, source);
            PumpUntilComplete(vm.UnlockCommand.Completion);

            var entry = Assert.Single(cfg.SavedPasswords);
            Assert.Equal("Acme scans", entry.Label);
            Assert.Equal("hunter2", PasswordVault.Reveal(entry.Password));
            Assert.Equal(1, configSaves);
            Assert.Single(vm.Saved);

            // and the batch did NOT re-run: still just the arrangement call
            Assert.Single(invoked);
            // the offer was answered, not discarded
            Assert.False(vm.SaveBannerVisible);
            Assert.Equal("", vm.SaveBannerName);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The blank-name corner, which is why the handler marks Enter
    /// handled UNCONDITIONALLY rather than only when the save command can
    /// run. A CanExecute-gated <c>KeyBinding</c> would leave this case
    /// falling straight back through to the default button — re-running the
    /// whole batch because someone pressed Enter before typing a label.</summary>
    [Fact]
    public void EnterInTheSavePasswordBoxWithNoLabelYetDoesNotRerunTheBatch() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var cfg = new Config();
        var invoked = new List<(string Path, string Password)>();
        var vm = new UnlockViewModel(cfg, () => { },
            unlocker: (path, password) =>
            {
                invoked.Add((path, password));
                return new Unlock.UnlockResult("ok", path, path, InPlace: true);
            },
            fileSize: _ => 0);
        vm.Files.Add(@"C:\inbox\20240101--1111111111.pdf");

        var window = OffScreen(vm);
        try
        {
            var saveAs = ArrangeSaveOffer(window, vm, invoked);
            Assert.Equal("", saveAs.Text);   // deliberately nothing typed
            Assert.False(vm.SaveBannerCommand.CanExecute(null));

            saveAs.Focus();
            Keyboard.Focus(saveAs);
            Assert.True(saveAs.IsKeyboardFocused, "the save-as box never took keyboard focus");

            var source = PresentationSource.FromVisual(window)!;
            SimulateEnterKey(saveAs, source);
            PumpUntilComplete(vm.UnlockCommand.Completion);

            Assert.Single(invoked);                 // the batch did not re-run
            Assert.Empty(cfg.SavedPasswords);       // and nothing was saved
            Assert.True(vm.SaveBannerVisible,       // the offer is still on the table
                "the save offer was thrown away by an Enter that did nothing else");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The other half, and the guard against over-fixing: Enter
    /// ANYWHERE ELSE in the window must still run the batch, including while
    /// the save offer is up. A fix that swallowed Return window-wide, or
    /// dropped IsDefault, would pass the test above and fail this one.</summary>
    [Fact]
    public void EnterOutsideTheSaveBoxStillUnlocksWhileTheOfferIsUp() => _fx.Invoke(() =>
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

        var window = OffScreen(vm);
        try
        {
            var saveAs = ArrangeSaveOffer(window, vm, invoked);
            saveAs.Text = "Acme scans";

            // focus back in the password box, offer still up
            window.PwBox.Focus();
            Keyboard.Focus(window.PwBox);
            Assert.True(window.PwBox.IsKeyboardFocused, "the password box never took keyboard focus back");
            Assert.True(vm.SaveBannerVisible, "the offer went away before the second Enter");

            var source = PresentationSource.FromVisual(window)!;
            SimulateEnterKey(window.PwBox, source);
            PumpUntilComplete(vm.UnlockCommand.Completion);

            Assert.Equal(2, invoked.Count);
            Assert.Equal("1 unlocked", vm.Summary);
        }
        finally
        {
            window.Close();
        }
    });
}
