using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Zip &amp; unzip tab: one list holding files, folders and
/// archives, and the buttons light from what is in it. Zip folds the whole
/// list into one archive; Extract maps each pending archive to its own
/// sibling folder. They are inverse operations on the same objects, which is
/// why one list serves both and nobody has to pick a mode.
///
/// Every button carries its own count ("Zip 5 items", "Extract 2 zips"), so a
/// mixed list states each action's scope rather than leaving it to be
/// inferred.</summary>
public sealed class ZipExtractViewModel : ZipListViewModel
{
    private readonly IDialogService _dialogs;
    private readonly Func<IReadOnlyList<string>, string?, Zipper.ZipResult> _zipper;
    private readonly Func<string, Zipper.UnzipResult> _extractor;

    public ZipExtractViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
        Func<string, Zipper.UnzipResult>? extractor = null)
        : base(scheduler, uiContext)
    {
        _dialogs = dialogs;
        _zipper = zipper ?? Zipper.CreateZip;
        // No passwords yet — Task 6 threads the window's candidates and its
        // prompt through here; until then a locked zip reports needs_password.
        _extractor = extractor ?? (path => Zipper.Extract(path, Array.Empty<string>(), null));

        ZipCommand = new AsyncRelayCommand(() => ZipAsync(null), () => Rows.Count > 0);
        ZipAsCommand = new AsyncRelayCommand(ZipWithDialogAsync, () => Rows.Count > 0);
        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => PendingZips > 0);
    }

    /// <summary>Anything that exists — a PDF is valid input here, just for
    /// the other button.</summary>
    protected override ISet<string>? Extensions => null;

    protected override string IntakeNoun => "item";

    private int PendingZips => Rows.Count(r => r.IsZip && r.StatusKind == ZipItemRowStatus.Pending);

    protected override void OnRowsChanged()
    {
        Raise(nameof(ZipButtonText));
        Raise(nameof(ExtractButtonText));
        ZipCommand.RaiseCanExecuteChanged();
        ZipAsCommand.RaiseCanExecuteChanged();
        ExtractCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand ZipCommand { get; }
    public AsyncRelayCommand ZipAsCommand { get; }
    public AsyncRelayCommand ExtractCommand { get; }

    /// <summary>Counts the WHOLE list: zipping never excludes anything.</summary>
    public string ZipButtonText => Rows.Count switch
    {
        0 => "Zip",
        1 => "Zip 1 item",
        var n => $"Zip {n} items",
    };

    /// <summary>Counts only the archives a click would actually act on, so a
    /// mixed list cannot overstate this button's reach.</summary>
    public string ExtractButtonText => PendingZips switch
    {
        0 => "Extract",
        1 => "Extract 1 zip",
        var n => $"Extract {n} zips",
    };

    /// <summary>The fold: the whole list into one archive, at the default
    /// location Zipper.CreateZip picks or wherever Save-As sent it. A no-op
    /// on an empty list — the buttons are disabled then anyway, this is the
    /// same belt-and-braces guard every other batch command applies.</summary>
    internal async Task ZipAsync(string? outputPath)
    {
        if (Rows.Count == 0) return;
        var paths = Rows.Select(r => r.Path).ToList();
        var itemCount = paths.Count;
        var result = await Scheduler.Run(() => _zipper(paths, outputPath));
        RunOnUi(() => Status = result.Status == "ok"
            ? $"Created {System.IO.Path.GetFileName(result.Output!)} · {itemCount} item{(itemCount == 1 ? "" : "s")}"
            : result.Message);
    }

    /// <summary>Asks where to save, suggesting Zipper.DefaultName's own pick,
    /// then runs the same path with that answer. A cancelled dialog is a
    /// silent no-op: Status is left exactly as it was.</summary>
    internal async Task ZipWithDialogAsync()
    {
        if (Rows.Count == 0) return;
        var suggested = Zipper.DefaultName(Rows.Select(r => r.Path).ToList());
        var path = _dialogs.AskSaveFile("Zip archive (*.zip)|*.zip", suggested);
        if (path is null) return;
        await ZipAsync(path);
    }

    /// <summary>The map: each pending archive into its own sibling folder.
    /// Loose rows are never passed to the extractor.</summary>
    internal Task ExtractAsync() => RunBatchAsync(
        _extractor,
        r => r.Status,
        (row, r) => row.Apply(r),
        "Extracting",
        new[] { ("ok", "extracted"), ("error", "failed") });
}
