using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Which visual voice a notification-rail item speaks in. See
/// MainWindow.xaml's NoticeItemTemplate for the exact colour pairing each
/// resolves to: Warning is item bg Theme.Warning / text Theme.WarningText
/// (the pairing <c>ThemeTests.EveryTextPairingMeetsWcagAa</c> already
/// enforces); Error is item bg Theme.SurfaceRaised / 1px Theme.Border with a
/// Theme.StatusRedRaised icon — the exact pairing
/// <c>HighlightContrastTests.MainWindowToastIconContrast</c> enforces.</summary>
public enum NoticeKind { Warning, Error }

/// <summary>One row in MainWindow's unified notification rail (Phase 3, Task
/// 3.3) — the single replacement for the old set-aside banner, history-backup
/// banner, and floating alert toast. <see cref="ShellViewModel.Notices"/>
/// derives these fresh from the three sources those surfaces used to read
/// independently (HasDeferred/DeferredAlert, HasHistoryBackupWarning/
/// HistoryBackupWarning, ToastVisible/ToastText/ToastDetail) — this class
/// owns no state of its own beyond what it's given; it is a display/command
/// shell, not a second source of truth.
///
/// Message/Detail are settable (via <see cref="Update"/>) rather than
/// init-only: ShellViewModel.RefreshNotices reuses the SAME NoticeVm
/// instance, and its container's Loaded-triggered entrance animation, for an
/// existing notice whose text merely changed (the deferred count ticking up,
/// a fresh alert's toast text) — matching how the old floating toast already
/// refreshed its text in place without replaying its slide-in. Only a
/// notice that didn't exist a moment ago (a new Key) gets a brand new
/// instance, and therefore a brand new container that plays the entrance.</summary>
public sealed class NoticeVm : ObservableObject
{
    /// <summary>Stable identity for the underlying source ("deferred",
    /// "history-backup", "alert") — never shown; it's how
    /// ShellViewModel.RefreshNotices tells "the same notice, updated text"
    /// from "a different notice" when reconciling the collection.</summary>
    public string Key { get; }

    public NoticeKind Kind { get; }

    private string _message;
    public string Message { get => _message; private set => Set(ref _message, value); }

    private string _detail;
    public string Detail
    {
        get => _detail;
        private set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); }
    }

    public bool HasDetail => _detail.Length > 0;

    public string ActionLabel { get; }
    public RelayCommand ActionCommand { get; }
    public RelayCommand DismissCommand { get; }

    public NoticeVm(string key, NoticeKind kind, string message, string detail,
        string actionLabel, Action action, Action dismiss)
    {
        Key = key;
        Kind = kind;
        _message = message;
        _detail = detail;
        ActionLabel = actionLabel;
        ActionCommand = new RelayCommand(action);
        DismissCommand = new RelayCommand(dismiss);
    }

    /// <summary>Refresh this notice's text in place — see the class doc for
    /// why this exists instead of always constructing a new instance.</summary>
    internal void Update(string message, string detail)
    {
        Message = message;
        Detail = detail;
    }
}
