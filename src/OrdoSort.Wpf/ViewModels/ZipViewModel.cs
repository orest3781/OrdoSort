using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One dropped/browsed source — a loose file or a whole folder.
/// Kind is a plain "file"/"folder" tag (shown in its own grid column) rather
/// than an enum: there is no third state and nothing downstream switches on
/// it besides that column, unlike ZipRowStatus/UnzipRowStatus which model a
/// row's own lifecycle.</summary>
public sealed class PathRow : ObservableObject
{
    public string Path { get; }
    public string Kind { get; }

    /// <summary>The file name for a file row; the folder's OWN name (not its
    /// full path) for a folder row — DirectoryInfo.Name handles a trailing
    /// separator correctly where a bare Path.GetFileName would return "".</summary>
    public string Display => Kind == "folder" ? new DirectoryInfo(Path).Name : System.IO.Path.GetFileName(Path);

    public PathRow(string path, string kind)
    {
        Path = path;
        Kind = kind;
    }
}

/// <summary>Zip: drop or browse files and/or folders, then build one .zip —
/// either at the default location Zipper.CreateZip itself picks (beside the
/// first item) or wherever a Save-As dialog sends it. The sources-list shape
/// (Rows, AddPaths, RemoveSelected, ClearCommand) mirrors
/// FilenameListViewModel's own mixed file/folder intake; unlike that tool
/// there's no filesystem probe to debounce here — AddPaths just checks
/// File.Exists/Directory.Exists once per path and is done, so no
/// DebouncedProbe is needed.</summary>
public sealed class ZipViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<IReadOnlyList<string>, string?, Zipper.ZipResult> _zipper;

    public ObservableCollection<PathRow> Rows { get; } = new();

    public ZipViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _uiContext = uiContext;
        _zipper = zipper ?? Zipper.CreateZip;

        CreateCommand = new AsyncRelayCommand(() => CreateAsync(null), () => Rows.Count > 0);
        CreateAsCommand = new AsyncRelayCommand(CreateWithDialogAsync, () => Rows.Count > 0);
        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            Raise(nameof(ZipButtonText));
            CreateCommand.RaiseCanExecuteChanged();
            CreateAsCommand.RaiseCanExecuteChanged();
        });

        Rows.CollectionChanged += (_, _) =>
        {
            Raise(nameof(ZipButtonText));
            CreateCommand.RaiseCanExecuteChanged();
            CreateAsCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Feedback for the last AddPaths call ("2 added · 1 ignored…");
    /// blank when it added something with nothing to complain about — same
    /// shape as ZipMergeViewModel.AddNote.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>The last create attempt's verdict: "Created photos.zip · 3
    /// items" on success, the failure message verbatim on error. Blank until
    /// a create has actually run.</summary>
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>"Zip 3 items" / "Zip 1 item" / "Zip" for an empty list —
    /// bound to the Zip button's content, same pattern as
    /// ZipMergeViewModel.MergeButtonText.</summary>
    public string ZipButtonText => Rows.Count switch
    {
        0 => "Zip",
        1 => "Zip 1 item",
        var n => $"Zip {n} items",
    };

    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand CreateAsCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Called by drag-drop and both Add buttons. Files and folders
    /// alike, deduped on full path (OrdinalIgnoreCase — Windows paths) and
    /// existence-checked off-thread — same reasoning as
    /// ZipMergeViewModel.AddFilesAsync: a big drop from a slow share must
    /// not stall the UI thread one File.Exists/Directory.Exists at a time.</summary>
    public async Task AddPaths(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = new HashSet<string>(Rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var (keep, ignored) = await _scheduler.Run(() =>
        {
            var keepList = new List<(string Path, string Kind)>();
            var ignoredCount = 0;
            var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
            foreach (var p in candidates)
            {
                if (!seen.Add(p)) { ignoredCount++; continue; }
                if (File.Exists(p)) keepList.Add((p, "file"));
                else if (Directory.Exists(p)) keepList.Add((p, "folder"));
                else ignoredCount++;
            }
            return (keepList, ignoredCount);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — the same second-drop race ZipMergeViewModel.AddFilesAsync
        // guards against.
        var live = new HashSet<string>(Rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (p, kind) in keep)
            if (live.Add(p))
            {
                Rows.Add(new PathRow(p, kind));
                added++;
            }
        ignored += keep.Count - added;

        AddNote = added == 0 && ignored > 0
            ? $"nothing added — {ignored} item{(ignored == 1 ? " doesn't exist" : "s don't exist")} (or already listed)"
            : ignored > 0
                ? $"{added} added · {ignored} ignored (missing, or already listed)"
                : "";
    }

    /// <summary>Removes exactly the rows the window's grid selection holds —
    /// same shape as ZipMergeViewModel.RemoveSelected.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<PathRow>().ToList())
            Rows.Remove(item);
    }

    /// <summary>Shared execution behind both commands: CreateCommand calls
    /// this with null (the default-location path Zipper.CreateZip itself
    /// picks); CreateWithDialogAsync calls this with whatever the Save-As
    /// dialog returned. A no-op on an empty list — the buttons are disabled
    /// then anyway, this is just the same belt-and-braces guard every other
    /// batch command in this app applies at the top of its run.</summary>
    internal async Task CreateAsync(string? outputPath)
    {
        if (Rows.Count == 0) return;
        var paths = Rows.Select(r => r.Path).ToList();
        var itemCount = paths.Count;
        var result = await _scheduler.Run(() => _zipper(paths, outputPath));
        ApplyResult(result, itemCount);
    }

    /// <summary>CreateAsCommand's execute delegate: asks where to save
    /// (suggesting Zipper.DefaultName's own pick as the file name), then
    /// runs the same CreateAsync path with that answer. A cancelled dialog
    /// is a silent no-op — Status is left exactly as it was, same as
    /// FilenameListViewModel.SaveAsync's own null-path early return.</summary>
    internal async Task CreateWithDialogAsync()
    {
        if (Rows.Count == 0) return;
        var suggested = Zipper.DefaultName(Rows.Select(r => r.Path).ToList());
        var path = _dialogs.AskSaveFile("Zip archive (*.zip)|*.zip", suggested);
        if (path is null) return;
        await CreateAsync(path);
    }

    /// <summary>Marshals onto _uiContext when one is set, same shape as
    /// ZipMergeViewModel.ApplyResult — a raw thread-pool continuation has no
    /// synchronization context of its own to inherit.</summary>
    private void ApplyResult(Zipper.ZipResult result, int itemCount)
    {
        void Apply()
        {
            Status = result.Status == "ok"
                ? $"Created {Path.GetFileName(result.Output!)} · {itemCount} item{(itemCount == 1 ? "" : "s")}"
                : result.Message;
        }
        if (_uiContext is null) Apply();
        else _uiContext.Post(_ => Apply(), null);
    }
}
