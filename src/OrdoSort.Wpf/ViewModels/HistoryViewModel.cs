using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One displayed audit row.</summary>
public sealed record HistoryRow(
    string When, string Original, string FiledAs, string Name,
    string Route, bool Reverted)
{
    public static HistoryRow From(IReadOnlyDictionary<string, object> r)
    {
        var whenRaw = r["ts_utc"] as string ?? "";
        var when = DateTime.TryParse(whenRaw, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : whenRaw;
        return new HistoryRow(
            when,
            r["original_name"] as string ?? "",
            r["new_name"] as string ?? "",
            r["name_entered"] as string ?? "",
            r["route_label"] as string ?? "",
            Convert.ToInt64(r["reverted"]) != 0);
    }

    public bool Matches(string filter) =>
        Original.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || FiledAs.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Route.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The in-app audit viewer — newest first, lazy 500-row load with
/// Show all, live substring filter, CSV export. Every SQLite touch runs off
/// the UI thread: the history DB can live on a share where a single query
/// (or a busy_timeout wait) takes seconds, and filtering must stay pure
/// in-memory — no query per keystroke.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    public const int InitialLoad = 500;

    private readonly History _history;
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;
    private long _total;
    private bool _showedAll;

    /// <summary>Every currently-loaded row (unfiltered) — replaced wholesale
    /// only by <see cref="LoadAsync"/> (startup, Show all), never by typing
    /// in Find. <see cref="RowsView"/> is the filtered view the grid actually
    /// binds to.</summary>
    public ObservableCollection<HistoryRow> Rows { get; } = new();

    /// <summary>The live filtered view over <see cref="Rows"/>. Typing in
    /// Find re-evaluates the filter predicate against the SAME HistoryRow
    /// instances already sitting in <see cref="Rows"/> — no Clear()/Add(), no
    /// re-materialised list. History is the app's one unbounded-growth
    /// collection, so a per-keystroke rebuild of the whole list is the
    /// instance that matters; this is why filtering is a view over the data,
    /// not a rewrite of it.</summary>
    public ICollectionView RowsView { get; }

    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ExportCommand { get; }

    public HistoryViewModel(History history, IDialogService dialogs,
        IWorkScheduler? scheduler = null)
    {
        _history = history;
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => _filter.Length == 0 || (o is HistoryRow r && r.Matches(_filter));
        ShowAllCommand = new RelayCommand(() => _ = LoadAsync(all: true), () => !_showedAll && !IsBusy);
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
        _ = LoadAsync(all: false);
    }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) ApplyFilter(); }
    }

    private string _footerText = "";
    public string FooterText { get => _footerText; private set => Set(ref _footerText, value); }

    /// <summary>No filings recorded at all — distinct from <see cref="NoMatches"/>
    /// (filings exist, but none match the current search) so the empty-state
    /// message can tell a genuinely empty history apart from a too-narrow
    /// search.</summary>
    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    private bool _noMatches;
    public bool NoMatches { get => _noMatches; private set => Set(ref _noMatches, value); }

    /// <summary>True from the moment a load is handed to the scheduler until
    /// its rows have landed. The window shows a Loading line on it, and Show
    /// all is parked on it — a second click mid-load used to start a second
    /// full query (UX-06).</summary>
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            // Both empty-state lines and the Loading line live in the same
            // Grid cell (HistoryWindow.xaml), so a filter that had zero
            // matches right before Show all was clicked left "No filings
            // match your search." rendered on top of "Loading…" for the
            // whole load. ApplyFilter recomputes both correctly once the
            // load lands; clearing them here just stops the stale line
            // showing while it's in flight.
            if (value) { IsEmpty = false; NoMatches = false; }
            ShowAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanShowAll => !_showedAll;

    internal async Task LoadAsync(bool all)
    {
        var history = _history;
        IsBusy = true;
        try
        {
            var (rows, total) = await _scheduler.Run(() =>
            {
                var loaded = (all ? history.Rows() : history.Rows(InitialLoad))
                    .Select(HistoryRow.From).ToList();
                return (loaded, (long)history.Count());
            });
            _total = total;
            _showedAll = all || rows.Count < InitialLoad;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }
        catch (Exception ex) when (IsDatabaseOrFileError(ex))
        {
            // Every caller discards this Task (the constructor and Show all
            // both fire and forget it), so an exception left in it reaches
            // nobody: a busy or locked DB on a share used to leave an empty
            // grid with no word of why. Say so where the rows would be, and
            // out loud.
            FooterText = "Couldn't load the history: " + ex.Message;
            _dialogs.Warn("Couldn't load the history: " + ex.Message, "OrdoSort");
            return;
        }
        finally
        {
            IsBusy = false;
        }
        ShowAllCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanShowAll));
        ApplyFilter();
    }

    /// <summary>Pure in-memory, and — unlike the old Rows.Clear()/Add()
    /// approach — no rebuild of the underlying collection: RowsView.Refresh()
    /// only re-runs the Filter predicate over the SAME HistoryRow instances
    /// already in Rows, so a keystroke costs a predicate scan, never a
    /// collection rebuild.</summary>
    private void ApplyFilter()
    {
        RowsView.Refresh();
        var visibleCount = RowsView.Cast<HistoryRow>().Count();
        IsEmpty = Rows.Count == 0;
        NoMatches = !IsEmpty && visibleCount == 0;

        FooterText = _showedAll
            ? $"{visibleCount} of {_total} filings shown"
            : $"Showing the latest {visibleCount} of {_total} filings";
    }

    internal async Task ExportAsync()
    {
        var dest = _dialogs.AskSaveFile("Spreadsheet files (*.csv)|*.csv", "ordosort_history.csv");
        if (dest is null) return;
        try
        {
            var history = _history;
            var count = await _scheduler.Run(() => history.ExportCsv(dest));
            _dialogs.Info($"Exported {count} rows to {dest}", "OrdoSort");
        }
        catch (Exception ex) when (IsDatabaseOrFileError(ex))
        {
            _dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort");
        }
    }

    /// <summary>The failures a history DB on a share really produces: the
    /// file side (IO, access) and the SQLite side (busy or locked past the
    /// busy timeout, a dropped share mid-query). ExportCommand discards the
    /// ExportAsync Task just as LoadAsync's callers do, so a SqliteException
    /// left out of this filter vanished without a word.</summary>
    private static bool IsDatabaseOrFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException;
}
