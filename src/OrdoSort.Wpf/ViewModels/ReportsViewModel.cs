using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Reports hub's coordinator: owns the upload feed's loaded
/// table, the ignore set, and the computed summary; the two page view models
/// are display slices over state that lives here. One DebouncedProbe runs
/// every reload — the initial load, a folder change, and an ignore toggle
/// (see <see cref="SetIgnored"/>'s own doc comment for why that one is a
/// full walk too, not a cheap recompute) — the same off-thread/apply-on-UI
/// shape TurnaroundViewModel uses, and the same stale-probe protection: a
/// slow load can never overwrite a newer one.</summary>
public sealed class ReportsViewModel : ObservableObject, IDisposable
{
    // Func<Config>, not a captured Config (I1 fix, 2026-08-16 fix wave):
    // ShellViewModel.ApplySettingsAsync replaces its own `_cfg` field
    // wholesale on every settings save (see that method's own `_cfg = cfg;`)
    // rather than mutating the object in place. The hub is a non-modal
    // singleton (MainWindow.OpenReportsHub's own doc comment) that can sit
    // open across a settings save, so a Config captured once at construction
    // goes stale the moment Settings saves -- every Folder/TatIgnoredSources
    // read after that point sees the OLD object, and every write this class
    // makes lands on it too, silently discarded once the shell moves on to
    // the new one. Reading through _getCfg() at the point of use instead
    // means every access always sees whichever Config the shell currently
    // owns. MainWindow.OpenReportsHub passes `() => Shell.Cfg`.
    private readonly Func<Config> _getCfg;
    private readonly Action? _saveCfg;
    internal readonly IDialogService Dialogs;
    internal readonly IWorkScheduler Scheduler;
    private readonly DebouncedProbe<Snapshot> _probe;

    /// <summary>Everything one load/recompute produces, applied atomically
    /// on the UI thread so no panel ever binds half of one load and half of
    /// another (spec decision 8).</summary>
    internal sealed record Snapshot(UploadReportFeed.Result Feed, TurnaroundSummary.Summary Summary,
        IReadOnlyList<IgnoreList.Entry> IgnoreEntries);

    internal Snapshot? Current { get; private set; }

    public SourcesPageViewModel Sources { get; }
    public TurnaroundPageViewModel Turnaround { get; }

    public ReportsViewModel(Func<Config> getCfg, IDialogService dialogs, Action? saveCfg,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        int probeDelayMs = 300)
    {
        _getCfg = getCfg;
        Dialogs = dialogs;
        _saveCfg = saveCfg;
        Scheduler = scheduler ?? new TaskWorkScheduler();
        _probe = new DebouncedProbe<Snapshot>(Scheduler, uiContext, Apply, probeDelayMs);

        Sources = new SourcesPageViewModel(this);
        Turnaround = new TurnaroundPageViewModel(this);
        _currentPage = Turnaround;

        Reload(immediate: true);
    }

    public void Dispose() => _probe.Dispose();

    // ---------------------------------------------------------- navigation
    private object _currentPage;
    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    private int _selectedPageIndex;
    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (!Set(ref _selectedPageIndex, value)) return;
            CurrentPage = value == 1 ? Sources : Turnaround;
        }
    }

    public void ShowPage(int index) => SelectedPageIndex = index;

    private string _footerText = "";
    public string FooterText { get => _footerText; private set => Set(ref _footerText, value); }

    // ------------------------------------------------------------- loading
    internal string Folder
    {
        get => _getCfg().ReportsUploadFolder;
        set
        {
            var cfg = _getCfg();
            if (cfg.ReportsUploadFolder == value) return;
            cfg.ReportsUploadFolder = value;
            _saveCfg?.Invoke();
            Reload(immediate: true);
        }
    }

    /// <summary>Full reload: walk the folder, parse every report, compute.
    /// An empty folder path resolves synchronously to an empty snapshot —
    /// cancelling any in-flight probe (DebouncedProbe.Cancel's documented
    /// contract) so a slow stale load can't repopulate the hub afterwards.</summary>
    internal void Reload(bool immediate = false)
    {
        var cfg = _getCfg();
        var folder = cfg.ReportsUploadFolder;
        var ignored = cfg.TatIgnoredSources.ToArray();

        if (folder.Length == 0)
        {
            _probe.Cancel();
            Apply(EmptySnapshot(ignored));
            return;
        }
        _probe.Trigger(() => Build(UploadReportFeed.Load(folder), ignored), immediate);
    }

    /// <summary>Ignore-toggle path (I2 fix, 2026-08-16 fix wave): persists the
    /// list change, then runs a full <see cref="Reload"/> — the same single
    /// code path every other change to the hub's state goes through. The
    /// previous shape recomputed over the CACHED table (<c>Current.Feed</c>)
    /// through the same shared <see cref="DebouncedProbe{T}"/> — cheap, but
    /// racy by construction: a toggle fired while a fresh folder walk was
    /// already in flight got a NEWER probe generation than that walk (the
    /// probe's own "newest trigger wins" rule, see DebouncedProbe's class
    /// doc), so the toggle's stale-cache recompute would win the race and
    /// silently bury the fresher walk's result once it landed. Routing
    /// through Reload closes that: both the in-flight walk AND the toggle's
    /// own reload are now full walks of the CURRENT folder, so whichever one
    /// the probe's generation guard lets through is never stale data —
    /// accepted cost is a folder walk per toggle instead of a cheap
    /// recompute. See ReportsHubCoordinatorTests' own regression test next
    /// to ClearingTheFolderCancelsAnInFlightLoad for the race this
    /// closes.</summary>
    internal void SetIgnored(string value, bool ignored)
    {
        var list = _getCfg().TatIgnoredSources;
        if (ignored && !list.Contains(value, StringComparer.Ordinal)) list.Add(value);
        if (!ignored) list.RemoveAll(v => string.Equals(v, value, StringComparison.Ordinal));
        _saveCfg?.Invoke();

        Reload(immediate: true);
    }

    private static Snapshot Build(UploadReportFeed.Result feed, IReadOnlyList<string> ignoredValues)
    {
        var ignore = new IgnoreList(ignoredValues);
        var summary = TurnaroundSummary.Compute(feed.Table, ignore);
        var discovered = ignore.Discover(feed.Table.Rows.Select(r =>
            r.Cells.TryGetValue(TurnaroundSummary.SourceTypeColumn, out var v) ? v : ""));
        return new Snapshot(feed, summary, discovered);
    }

    private static Snapshot EmptySnapshot(IReadOnlyList<string> ignoredValues)
    {
        var empty = new UploadReportFeed.Result(
            new SweptTable.Table(Array.Empty<string>(), Array.Empty<SweptTable.Row>(),
                0, Array.Empty<string>()),
            new UploadReportFeed.LoadReport(0, Array.Empty<string>(), null, null, 0));
        return Build(empty, ignoredValues);
    }

    /// <summary>UI thread only (probe marshal or the empty fast path).</summary>
    private void Apply(Snapshot snapshot)
    {
        Current = snapshot;
        Sources.Apply(snapshot);
        Turnaround.Apply(snapshot);
        FooterText = $"{snapshot.Feed.Report.FilesFound.ToString("N0", CultureInfo.InvariantCulture)} files · " +
            $"{snapshot.Feed.Report.RowCount.ToString("N0", CultureInfo.InvariantCulture)} rows";
    }
}
