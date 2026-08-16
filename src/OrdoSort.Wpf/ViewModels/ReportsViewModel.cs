using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Reports hub's coordinator: owns the upload feed's loaded
/// table, the ignore set, and the computed summary; the two page view models
/// are display slices over state that lives here. One DebouncedProbe runs
/// both the full reload (folder walk + parse + compute) and the cheap
/// ignore-toggle recompute (compute only, over the cached table) — the same
/// off-thread/apply-on-UI shape TurnaroundViewModel uses, and the same
/// stale-probe protection: a slow load can never overwrite a newer one.</summary>
public sealed class ReportsViewModel : ObservableObject, IDisposable
{
    private readonly Config _cfg;
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

    public ReportsViewModel(Config cfg, IDialogService dialogs, Action? saveCfg,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        int probeDelayMs = 300)
    {
        _cfg = cfg;
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
        get => _cfg.ReportsUploadFolder;
        set
        {
            if (_cfg.ReportsUploadFolder == value) return;
            _cfg.ReportsUploadFolder = value;
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
        var folder = _cfg.ReportsUploadFolder;
        var ignored = _cfg.TatIgnoredSources.ToArray();

        if (folder.Length == 0)
        {
            _probe.Cancel();
            Apply(EmptySnapshot(ignored));
            return;
        }
        _probe.Trigger(() => Build(UploadReportFeed.Load(folder), ignored), immediate);
    }

    /// <summary>Ignore-toggle path: recompute over the cached table without
    /// re-walking the share. Falls back to a full reload when nothing is
    /// cached yet.</summary>
    internal void SetIgnored(string value, bool ignored)
    {
        var list = _cfg.TatIgnoredSources;
        if (ignored && !list.Contains(value, StringComparer.Ordinal)) list.Add(value);
        if (!ignored) list.RemoveAll(v => string.Equals(v, value, StringComparison.Ordinal));
        _saveCfg?.Invoke();

        if (Current is not { } current) { Reload(immediate: true); return; }
        var feed = current.Feed;
        var ignoredNow = list.ToArray();
        _probe.Trigger(() => Build(feed, ignoredNow), immediate: true);
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
