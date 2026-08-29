using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Zip and unzip window: one list holding files, folders and
/// archives, and the buttons light from what is in it. Zip folds the whole
/// list into one archive; Extract maps each runnable archive to its own
/// sibling folder. They are inverse operations on the same objects, which is
/// why one list serves both and nobody has to pick a mode.
///
/// Every button carries its own count ("Zip 5 items", "Extract 2 zips"), so a
/// mixed list states each action's scope rather than leaving it to be
/// inferred. A locked archive is probed as it is added (the saved passwords
/// only — see the base class) and, at Extract time, opened with the
/// window's candidates or the prompt; a skipped prompt leaves it runnable.</summary>
public sealed class ZipExtractViewModel : ZipListViewModel
{
    private readonly Func<IReadOnlyList<string>, string?, Zipper.ZipResult> _zipper;
    private readonly Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult> _extractor;
    private readonly Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult> _zipProbe;

    public ZipExtractViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult>? extractor = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null)
        : base(dialogs, savedPasswords, scheduler, uiContext)
    {
        _zipper = zipper ?? Zipper.CreateZip;
        _extractor = extractor ?? Zipper.Extract;
        _zipProbe = zipProbe ?? Zipper.Probe;

        ZipCommand = new AsyncRelayCommand(() => ZipAsync(null), () => Rows.Count > 0);
        ZipAsCommand = new AsyncRelayCommand(ZipWithDialogAsync, () => Rows.Count > 0);
        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => RunnableZips > 0);
    }

    /// <summary>Anything that exists — a PDF is valid input here, just for
    /// the other button.</summary>
    protected override ISet<string>? Extensions => null;

    protected override string IntakeNoun => "item";

    private int RunnableZips => Rows.Count(r => r.IsZip && r.IsRunnable);

    /// <summary>Archives only: a loose file or a folder needs nothing said
    /// about it before Zip folds it in.</summary>
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) =>
        row.IsZip ? FromZipProbe(_zipProbe(row.Path, savedPasswords)) : null;

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

    /// <summary>Counts only the archives a click would actually act on —
    /// pending ones and ones still waiting for a password — so a mixed list
    /// cannot overstate this button's reach.</summary>
    public string ExtractButtonText => RunnableZips switch
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
        var path = Dialogs.AskSaveFile("Zip archive (*.zip)|*.zip", suggested);
        if (path is null) return;
        await ZipAsync(path);
    }

    /// <summary>The map: each runnable archive into its own sibling folder,
    /// one unit per row. Loose rows are never passed to the extractor. The
    /// candidates and the prompt are the base class's; the extractor asks
    /// only for what none of the candidates opens.</summary>
    internal Task ExtractAsync() => RunBatchAsync(
        Rows.Where(r => r.IsZip && r.IsRunnable)
            .Select(row => new Unit<Zipper.UnzipResult>(new[] { row },
                candidates => _extractor(row.Path, candidates, AskPassword)))
            .ToList(),
        r => r.Status,
        (rows, r) => rows[0].Apply(r),
        "Extracting",
        new[]
        {
            new TallyClause("ok", "extracted"),
            new TallyClause("needs_password", "needs a password", "need a password"),
            new TallyClause("error", "failed"),
        });
}
