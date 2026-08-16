using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One checklist row on the upload-feed card: a SourceType value
/// found in the data, its raw row count, and whether it is currently
/// included. Toggling routes through the coordinator, which persists the
/// ignore list and recomputes (spec decision 7).</summary>
public sealed class IgnoreEntryVm : ObservableObject
{
    private readonly ReportsViewModel _owner;
    public string Value { get; }
    public int Count { get; }
    public string CountText { get; }

    /// <summary>What the checkbox shows — a blank SourceType is a real,
    /// toggleable value but must not render as an empty label.</summary>
    public string Display => Value.Length == 0 ? "(blank)" : Value;

    private bool _isIncluded;
    public bool IsIncluded
    {
        get => _isIncluded;
        set { if (Set(ref _isIncluded, value)) _owner.SetIgnored(Value, ignored: !value); }
    }

    public IgnoreEntryVm(ReportsViewModel owner, IgnoreList.Entry entry)
    {
        _owner = owner;
        Value = entry.Value;
        Count = entry.Count;
        CountText = entry.Count.ToString("N0", CultureInfo.InvariantCulture);
        _isIncluded = !entry.Ignored;
    }
}

/// <summary>The Sources page: this phase, one card — the upload feed. Path,
/// browse, refresh, found-file status, skipped-file list (never silently
/// dropped — spec decision 6), and the ignore checklist. Blank values
/// display as "(blank)" but toggle by their real "" value.</summary>
public sealed class SourcesPageViewModel : ObservableObject
{
    private readonly ReportsViewModel _owner;

    public SourcesPageViewModel(ReportsViewModel owner) => _owner = owner;

    public string FolderPath
    {
        get => _owner.Folder;
        set { _owner.Folder = value; Raise(nameof(FolderPath)); }
    }

    private string _statusText = "No folder chosen";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _skippedText = "";
    public string SkippedText { get => _skippedText; private set => Set(ref _skippedText, value); }

    private bool _hasSkipped;
    public bool HasSkipped { get => _hasSkipped; private set => Set(ref _hasSkipped, value); }

    public ObservableCollection<IgnoreEntryVm> IgnoreEntries { get; } = new();

    public RelayCommand BrowseCommand => _browse ??= new RelayCommand(() =>
    {
        if (_owner.Dialogs.BrowseFolder(FolderPath.Length == 0 ? null : FolderPath) is { } folder)
            FolderPath = folder;
    });
    private RelayCommand? _browse;

    public RelayCommand RefreshCommand => _refresh ??= new RelayCommand(() => _owner.Reload(immediate: true));
    private RelayCommand? _refresh;

    internal void Apply(ReportsViewModel.Snapshot snapshot)
    {
        Raise(nameof(FolderPath));
        var r = snapshot.Feed.Report;
        StatusText = FolderPath.Length == 0
            ? "No folder chosen — browse to your upload reports"
            : $"{r.FilesFound} files · {r.RowCount.ToString("N0", CultureInfo.InvariantCulture)} rows · " +
              (r.FirstUpload is { } f && r.LastUpload is { } l
                  ? $"{f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {l.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                  : "no dated reports found");
        HasSkipped = r.Skipped.Count > 0;
        SkippedText = HasSkipped
            ? $"{r.Skipped.Count} skipped — {r.Skipped[0]}" : "";

        IgnoreEntries.Clear();
        foreach (var entry in snapshot.IgnoreEntries)
            IgnoreEntries.Add(new IgnoreEntryVm(_owner, entry));
    }
}
