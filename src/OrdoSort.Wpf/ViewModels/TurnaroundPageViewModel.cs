using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

public sealed record MonthRowVm(string Month, string ZeroToOne, string CountNote, string Two, string ThreePlus);
public sealed record SourceRowVm(string Source, string Docs, string ZeroToOne, string Two, string ThreePlus);
public sealed record SparkBarVm(double HeightFraction, string Tooltip);
public sealed record DetailRowVm(string FileName, string SourceType, string Pagecount,
    string Destination, string DocDate, string UploadDate, string Tat, string Bucket);

/// <summary>One set-aside chip: label + count, clicking jumps to the Detail
/// tab filtered to exactly the rows behind the number (spec decision 2 —
/// the counts are defensible because the rows are one click away).</summary>
public sealed record SetAsideChipVm(string Key, string Label, string CountText);

/// <summary>The Turn-around page: hero tile, bucket tiles, month grid,
/// by-source matrix, weekly spark bars, set-aside chips, and the Detail tab
/// with its row-source selector and inline filter. Pure display over the
/// coordinator's snapshot — every published figure is formatted by
/// TurnaroundExport (tested) or is a plain count.</summary>
public sealed class TurnaroundPageViewModel : ObservableObject
{
    internal const string SourceMeasurable = "Measurable documents";
    internal const string SourceDuplicates = "Duplicates";
    internal const string SourceFutureDated = "Future-dated";
    internal const string SourceNoDate = "Without a date";
    internal const string SourceIgnored = "Ignored";

    private readonly ReportsViewModel _owner;
    private ReportsViewModel.Snapshot? _snapshot;

    public TurnaroundPageViewModel(ReportsViewModel owner)
    {
        _owner = owner;
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
        CopySummaryCommand = new RelayCommand(CopySummary);
        RefreshCommand = new RelayCommand(() => _owner.Reload(immediate: true));
    }

    public RelayCommand ExportCommand { get; }
    public RelayCommand CopySummaryCommand { get; }
    public RelayCommand RefreshCommand { get; }

    // ----------------------------------------------------------- summary tab
    private string _heroPercentText = "—";
    public string HeroPercentText { get => _heroPercentText; private set => Set(ref _heroPercentText, value); }

    private string _deltaChipText = "";
    public string DeltaChipText { get => _deltaChipText; private set => Set(ref _deltaChipText, value); }

    /// <summary>I4 fix: whether the delta chip's arrow is ▲ (improving) or ▼
    /// (worsening) — TurnaroundPageView.xaml's chip used to hard-code
    /// Theme.StatusGreen regardless of direction, so a worsening ▼ delta
    /// still rendered green. Set alongside DeltaChipText.</summary>
    private bool _deltaIsPositive;
    public bool DeltaIsPositive { get => _deltaIsPositive; private set => Set(ref _deltaIsPositive, value); }

    private bool _hasDelta;
    public bool HasDelta { get => _hasDelta; private set => Set(ref _hasDelta, value); }

    private string _sameDayText = "—", _oneDayText = "—", _twoDaysText = "—", _threePlusText = "—";
    public string SameDayText { get => _sameDayText; private set => Set(ref _sameDayText, value); }
    public string OneDayText { get => _oneDayText; private set => Set(ref _oneDayText, value); }
    public string TwoDaysText { get => _twoDaysText; private set => Set(ref _twoDaysText, value); }
    public string ThreePlusText { get => _threePlusText; private set => Set(ref _threePlusText, value); }

    private string _contextText = "";
    public string ContextText { get => _contextText; private set => Set(ref _contextText, value); }

    private bool _hasData;
    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

    public ObservableCollection<MonthRowVm> MonthRows { get; } = new();
    public ObservableCollection<SourceRowVm> SourceRows { get; } = new();
    public ObservableCollection<SparkBarVm> SparkBars { get; } = new();
    public ObservableCollection<SetAsideChipVm> SetAsideChips { get; } = new();

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

    /// <summary>Chip click: land on Detail with that set-aside selected.
    /// Directed fold (b): a filter typed on a PREVIOUS visit to Detail must
    /// not silently narrow the rows a chip click promised — clearing it
    /// first keeps the chip's own count and the rows actually shown in
    /// agreement.</summary>
    public void InspectSetAside(string key)
    {
        DetailFilter = "";
        SelectedDetailSource = key;
        SelectedTabIndex = 1;
    }

    // ------------------------------------------------------------ detail tab
    public ObservableCollection<string> DetailSources { get; } = new()
        { SourceMeasurable, SourceDuplicates, SourceFutureDated, SourceNoDate, SourceIgnored };

    private string _selectedDetailSource = SourceMeasurable;
    public string SelectedDetailSource
    {
        get => _selectedDetailSource;
        set
        {
            if (value is null) return;   // WPF Selector null-push, same guard as TurnaroundViewModel
            if (Set(ref _selectedDetailSource, value)) RebuildDetail();
        }
    }

    private string _detailFilter = "";
    public string DetailFilter
    {
        get => _detailFilter;
        set { if (Set(ref _detailFilter, value)) RebuildDetail(); }
    }

    public ObservableCollection<DetailRowVm> DetailRows { get; } = new();

    private string _detailCountText = "";
    public string DetailCountText { get => _detailCountText; private set => Set(ref _detailCountText, value); }

    // -------------------------------------------------------------- rebuild
    internal void Apply(ReportsViewModel.Snapshot snapshot)
    {
        _snapshot = snapshot;
        var s = snapshot.Summary;
        var o = s.Overall;
        HasData = o.Total > 0;

        HeroPercentText = HasData
            ? o.ZeroToOnePercent.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
        SameDayText = o.SameDay.ToString("N0", CultureInfo.InvariantCulture);
        OneDayText = o.OneDay.ToString("N0", CultureInfo.InvariantCulture);
        TwoDaysText = o.TwoDays.ToString("N0", CultureInfo.InvariantCulture);
        ThreePlusText = o.ThreePlus.ToString("N0", CultureInfo.InvariantCulture);

        var r = snapshot.Feed.Report;
        ContextText = r.FirstUpload is { } f && r.LastUpload is { } l
            ? $"Upload reports · {f.ToString("MMM d", CultureInfo.InvariantCulture)} – {l.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} · {r.FilesFound} files · {r.RowCount.ToString("N0", CultureInfo.InvariantCulture)} rows"
            : "Upload reports · no data loaded — set the folder on the Sources page";

        // Month-over-month delta on the 0-1 share, latest vs previous.
        if (s.ByMonth.Count >= 2)
        {
            var prev = s.ByMonth[^2];
            var last = s.ByMonth[^1];
            var delta = last.Counts.ZeroToOnePercent - prev.Counts.ZeroToOnePercent;
            var arrow = delta >= 0 ? "▲" : "▼";
            DeltaChipText = $"{arrow} {(delta >= 0 ? "+" : "−")}{Math.Abs(delta).ToString("F1", CultureInfo.InvariantCulture)} pt vs {TurnaroundExport.MonthName(prev.Month)}";
            DeltaIsPositive = delta >= 0;
            HasDelta = true;
        }
        else { DeltaChipText = ""; DeltaIsPositive = false; HasDelta = false; }

        MonthRows.Clear();
        foreach (var m in s.ByMonth)
            MonthRows.Add(new MonthRowVm(
                TurnaroundExport.MonthName(m.Month),
                m.Counts.ZeroToOnePercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
                m.Counts.ZeroToOne.ToString("N0", CultureInfo.InvariantCulture),
                m.Counts.TwoDays.ToString("N0", CultureInfo.InvariantCulture),
                m.Counts.ThreePlus.ToString("N0", CultureInfo.InvariantCulture)));

        SourceRows.Clear();
        foreach (var src in s.BySource)
            SourceRows.Add(new SourceRowVm(
                src.SourceType.Length == 0 ? "(blank)" : src.SourceType,
                src.Counts.Total.ToString("N0", CultureInfo.InvariantCulture),
                src.Counts.ZeroToOnePercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
                src.Counts.TwoDays.ToString("N0", CultureInfo.InvariantCulture),
                src.Counts.ThreePlus.ToString("N0", CultureInfo.InvariantCulture)));

        // Spark bars: 0-1 share per ISO week, scaled so the worst week still
        // draws (0.2 floor) and the best fills the strip.
        SparkBars.Clear();
        if (s.ByWeek.Count > 0)
        {
            var values = s.ByWeek.Select(w => w.Counts.ZeroToOnePercent).ToList();
            var min = values.Min();
            var max = values.Max();
            var span = max - min;
            foreach (var w in s.ByWeek)
            {
                var fraction = span < 0.001 ? 1.0
                    : 0.2 + 0.8 * (w.Counts.ZeroToOnePercent - min) / span;
                SparkBars.Add(new SparkBarVm(fraction,
                    $"{w.Week}: {w.Counts.ZeroToOnePercent.ToString("F1", CultureInfo.InvariantCulture)}% in 0-1 ({w.Counts.Total.ToString("N0", CultureInfo.InvariantCulture)} docs)"));
            }
        }

        SetAsideChips.Clear();
        SetAsideChips.Add(new SetAsideChipVm(SourceDuplicates, "Duplicates",
            s.DuplicateRows.ToString("N0", CultureInfo.InvariantCulture)));
        SetAsideChips.Add(new SetAsideChipVm(SourceFutureDated, "Future-dated",
            s.FutureDated.ToString("N0", CultureInfo.InvariantCulture)));
        SetAsideChips.Add(new SetAsideChipVm(SourceNoDate, "No date",
            s.NoDate.ToString("N0", CultureInfo.InvariantCulture)));
        foreach (var ig in s.Ignored)
            SetAsideChips.Add(new SetAsideChipVm(SourceIgnored,
                $"{(ig.Value.Length == 0 ? "(blank)" : ig.Value)} ignored",
                ig.Count.ToString("N0", CultureInfo.InvariantCulture)));

        RebuildDetail();
    }

    private void RebuildDetail()
    {
        DetailRows.Clear();
        if (_snapshot is not { } snapshot) { DetailCountText = ""; return; }
        var s = snapshot.Summary;

        IEnumerable<DetailRowVm> rows = SelectedDetailSource switch
        {
            SourceDuplicates => s.DuplicateRowsDetail.Select(FromRawRow),
            SourceFutureDated => s.FutureDatedDetail.Select(FromRawRow),
            SourceNoDate => s.NoDateDetail.Select(FromRawRow),
            SourceIgnored => s.IgnoredDetail.Select(FromRawRow),
            _ => s.Docs.Select(d => new DetailRowVm(
                d.FileName, d.SourceType, d.Pagecount, d.Destination,
                d.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.UploadDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.BusinessDays.ToString(CultureInfo.InvariantCulture),
                TurnaroundExport.BucketLabel(d.Bucket))),
        };

        var filter = DetailFilter.Trim();
        if (filter.Length > 0)
            rows = rows.Where(r =>
                r.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.SourceType.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var row in rows) DetailRows.Add(row);
        DetailCountText = $"{DetailRows.Count.ToString("N0", CultureInfo.InvariantCulture)} rows · {SelectedDetailSource.ToLowerInvariant()}";
    }

    /// <summary>Directed fold (d): a set-aside row (duplicate/future-dated/
    /// no-date/ignored) still knows its own doc date and upload date — only
    /// TAT and Bucket genuinely depend on the computed-Docs pipeline this raw
    /// row was set aside FROM, so only those two stay "—". DocDate comes
    /// from the same DocumentDate.Parse the hub's own pipeline uses;
    /// UploadDate from the source report's own filename, same as every
    /// measurable row's UploadDate. Both render yyyy-MM-dd invariant, "—"
    /// only when the row's own filename genuinely doesn't parse.</summary>
    private static DetailRowVm FromRawRow(SweptTable.Row row)
    {
        string Cell(string column) => row.Cells.TryGetValue(column, out var v) ? v : "";
        var fileName = Cell(TurnaroundSummary.FileNameColumn);
        var docDate = DocumentDate.Parse(fileName);
        var uploadDate = TurnaroundTime.UploadTimeFromReportName(row.SourceFile);
        return new DetailRowVm(
            fileName, Cell(TurnaroundSummary.SourceTypeColumn),
            Cell(TurnaroundSummary.PagecountColumn), Cell(TurnaroundSummary.DestinationColumn),
            docDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—",
            uploadDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—",
            "—", "—");
    }

    // ------------------------------------------------------------- commands
    /// <summary>Raised with the text to copy when Copy Summary is invoked.
    /// CLIPBOARD RULE: System.Windows.Clipboard must never appear in this
    /// class — the real repo enforces that in every other view model that
    /// copies to the clipboard (PageCountsViewModel, FilenameListViewModel,
    /// ListReformatViewModel all carry this exact comment: "Clipboard is a
    /// WPF/COM type and must never appear in this class"). The brief's
    /// reference code called Clipboard.SetText directly here; adapted to
    /// match the established convention instead. The window's code-behind
    /// (Task 5) should subscribe to this event, perform Clipboard.SetText in
    /// a try/catch(COMException), and report back via
    /// NoteCopied()/NoteClipboardBusy() — the same pattern those three
    /// windows already use.</summary>
    public event Action<string>? CopyTextRequested;

    private void CopySummary()
    {
        if (_snapshot is not { } snapshot) return;
        var text = TurnaroundExport.BuildCopyText(snapshot.Summary, snapshot.Feed.Report);
        CopyTextRequested?.Invoke(text);
    }

    /// <summary>Set by the window's code-behind after Clipboard.SetText
    /// succeeds.</summary>
    public void NoteCopied() => _owner.Dialogs.Info("Summary copied to the clipboard.", "OrdoSort");

    /// <summary>Set by the window's code-behind when Clipboard.SetText
    /// throws COMException — the clipboard is a shared, single-owner OS
    /// resource another app can be holding for a moment; this just says so
    /// rather than losing the failure silently.</summary>
    public void NoteClipboardBusy() => _owner.Dialogs.Warn("Clipboard busy — try again.", "OrdoSort");

    internal async Task ExportAsync()
    {
        if (_snapshot is not { } snapshot) return;
        var suggested = $"turnaround-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.xlsx";
        var dest = _owner.Dialogs.AskSaveFile("Excel workbook (*.xlsx)|*.xlsx", suggested);
        if (dest is null) return;
        var (summary, report, folder) = (snapshot.Summary, snapshot.Feed.Report, _owner.Folder);
        try
        {
            await _owner.Scheduler.Run(() =>
            {
                TurnaroundExport.Write(dest, summary, report, folder);
                return true;
            });
            _owner.Dialogs.Info($"Exported {summary.Docs.Count} documents to {dest}", "OrdoSort");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.Dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort");
        }
    }
}
