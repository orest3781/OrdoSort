using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Merge PDFs window: drop PDFs, zips, and (Task 8) Word,
/// Excel, PowerPoint, image and text files, and one PDF comes out per
/// source — every mergeable entry inside a zip into &lt;zipname&gt;.pdf
/// beside it, and every loose file in the list into one file beside the
/// first of them, converting whatever isn't already a PDF along the way.
/// Its own window and its own list because it is a different job wearing a
/// zip costume — it consumes archives and documents and produces a
/// document — and because a separate list means extracting an archive in
/// the other window has no bearing on merging it here.
///
/// Units (see the base class): each runnable zip row is a unit of one; the
/// runnable loose documents — PDFs and, since Task 8, everything else this
/// window converts too — are one unit of many, run last. Fail-whole applies
/// per unit: one document nobody can open merges nothing from its unit, and
/// the rows it held back say so (<see cref="ApplyToUnit"/>).</summary>
public sealed class MergePdfsViewModel : ZipListViewModel, IDisposable
{
    private readonly Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _zipMerger;
    private readonly Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _fileMerger;
    private readonly Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult> _zipProbe;
    private readonly Func<string, IReadOnlyList<string>, Unlock.ProbeResult> _pdfProbe;

    /// <summary>Turns a non-PDF row into pages for both the probe (does
    /// SOMETHING handle this switched-on type at all?) and the two mergers
    /// below. Defaults to the real chain — Office first, then the three
    /// converters that need nothing installed — so opening this window in
    /// production always gets real conversion without a caller having to
    /// wire anything; a test supplies its own stand-in instead.</summary>
    private readonly IDocumentConverter _converter;

    private readonly Config _cfg;
    private readonly Action? _saveConfig;

    /// <summary>Which MergeTypes groups are currently switched on — seeded
    /// from config at construction and the single source of truth
    /// SetTypeEnabled/IsTypeEnabled/IsRowIncluded all read and write; _cfg.
    /// MergeTypes is kept in lockstep with it purely for persistence (see
    /// SetTypeEnabled), never read back out of _cfg mid-session.</summary>
    private readonly HashSet<string> _enabledTypes;

    /// <summary>Display labels for the toggle row's checkboxes. A small,
    /// deliberately separate mapping rather than titlecasing MergeTypes'
    /// own lowercase group constants: "PDF" and "PowerPoint" are not what
    /// titlecasing "pdf"/"powerpoint" would produce.</summary>
    private static readonly IReadOnlyDictionary<string, string> GroupLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MergeTypes.Pdf] = "PDF",
            [MergeTypes.Zip] = "Zip",
            [MergeTypes.Word] = "Word",
            [MergeTypes.Excel] = "Excel",
            [MergeTypes.PowerPoint] = "PowerPoint",
            [MergeTypes.Images] = "Images",
            [MergeTypes.Text] = "Text",
        };

    public MergePdfsViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null,
        Config? config = null, Action? saveConfig = null,
        IDocumentConverter? converter = null)
        : base(dialogs, savedPasswords, scheduler, uiContext)
    {
        // Task 8: the machinery Tasks 2-7 built (the four converters, the
        // toggle row, the enabled-type set) had nothing calling it yet.
        // Office first — best fidelity when installed — then the three
        // converters that need nothing installed at all; see ConverterChain
        // for the no-silent-downgrade rule that ordering depends on.
        _converter = converter ?? new ConverterChain(
            new OfficeConverter(), new ImageToPdf(), new TableToPdf(), new TextToPdf());

        // Fix round (review finding 6): a bare method group cannot omit a
        // delegate's optional parameters at all — a method group conversion
        // is only defined against candidates whose EVERY optional parameter
        // has a corresponding one in the target delegate (ECMA-334 §10.8).
        // `_zipMerger = PdfMerge.MergeZip;` would fail identically as a
        // plain assignment; ?? was never the discriminator, and the CS0019
        // this produced is just the symptom of no conversion existing at
        // all now that MergeZip/MergeFiles carry two extra optional
        // parameters (converter, includeTypes). The lambda spells out the
        // exact three/four-argument shape this field actually needs.
        // includeTypes is `_enabledTypes` itself, not a snapshot taken here:
        // the lambda reads the FIELD at invocation time (well after this
        // constructor returns), so a toggle flipped between AddPaths and
        // MergeAsync is honoured. Task 7 review finding 1: this was
        // previously left at the PdfMerge default (null = every type on),
        // which let a merge reach past the toggles entirely — switch PDF
        // off, leave Zip on, and PDFs inside an included zip still merged,
        // because the archive's OWN contents were never filtered by
        // anything but MergeTypes.AllExtensions at intake. `converter` is
        // `_converter` itself for the same reason — read live, not
        // snapshotted, though in practice it never changes after
        // construction.
        _zipMerger = zipMerger ?? ((path, mergeCandidates, ask) =>
            PdfMerge.MergeZip(path, mergeCandidates, ask, converter: _converter, includeTypes: _enabledTypes));
        _fileMerger = fileMerger ?? ((paths, outputPath, mergeCandidates, ask) =>
            PdfMerge.MergeFiles(paths, outputPath, mergeCandidates, ask, converter: _converter, includeTypes: _enabledTypes));
        _zipProbe = zipProbe ?? Zipper.Probe;
        _pdfProbe = pdfProbe ?? Unlock.ProbeReadiness;

        _cfg = config ?? new Config();
        _saveConfig = saveConfig;
        // MergeTypes.Load("") — an unconfigured _cfg.MergeTypes — is every
        // group on, matching the "never touched a toggle" default a fresh
        // window has to show.
        _enabledTypes = new HashSet<string>(MergeTypes.Load(_cfg.MergeTypes), StringComparer.OrdinalIgnoreCase);
        TypeToggles = MergeTypes.AllGroups
            .Select(g => new MergeTypeToggle(this, g, GroupLabels.TryGetValue(g, out var label) ? label : g))
            .ToList();

        MergeCommand = new AsyncRelayCommand(() => MergeAsync(null), () => RunnableRows > 0);
        MergeToCommand = new AsyncRelayCommand(MergeToAsync, () => RunnableLooseDocuments > 0);
    }

    /// <summary>Every document, image, text file and zip this window can
    /// merge (Task 8 wires the converter that actually turns a non-PDF into
    /// pages) — widened from PDF/zip alone so the per-type toggle row below
    /// has something to switch off. Intake stays permissive on purpose: it
    /// is the toggle set, not the accepted extension set, that decides what
    /// merges — see MergePdfsWindow.xaml's toggle row and IsRowIncluded.
    /// Anything outside every MergeTypes group is still refused by intake
    /// with its usual note.</summary>
    protected override ISet<string>? Extensions => MergeTypes.AllExtensions;

    protected override string IntakeNoun => "PDF, document, image or zip";

    private int RunnableRows => Rows.Count(r => r.IsRunnable);

    /// <summary>Every runnable row that belongs to the LOOSE unit — the
    /// group PdfMerge.MergeFiles/DefaultName treat as "everything not
    /// inside its own zip". Renamed from "RunnableLoosePdfs" (Task 8): the
    /// old name and its IsPdf-only filter were the likeliest miss in the
    /// whole plan — a lone runnable .docx used to count as zero here, so
    /// "Merge to…" (and MergeAsync's own loose-unit selection, the same
    /// widening) silently ignored the one item the button and MergeButtonText
    /// both claimed was ready. !IsZip, not IsPdf, is the correct test: every
    /// row that ISN'T its own zip unit belongs to the loose group, whatever
    /// its own type.</summary>
    private int RunnableLooseDocuments => Rows.Count(r => !r.IsZip && r.IsRunnable);

    /// <summary>One checkbox in the toggle row: a MergeTypes group's current
    /// on/off state, bound two-way so ticking or unticking calls straight
    /// back into <see cref="SetTypeEnabled"/>. A thin wrapper rather than a
    /// plain bool because the XAML needs something to bind Content and
    /// AutomationProperties.Name to alongside IsChecked, and because the
    /// state has to be re-announced to WPF whenever SetTypeEnabled is called
    /// by a route other than this checkbox's own binding — the tests call it
    /// directly.</summary>
    public sealed class MergeTypeToggle : ObservableObject
    {
        private readonly MergePdfsViewModel _owner;
        public string Group { get; }
        public string Label { get; }

        internal MergeTypeToggle(MergePdfsViewModel owner, string group, string label)
        {
            _owner = owner;
            Group = group;
            Label = label;
        }

        public bool IsEnabled
        {
            get => _owner.IsTypeEnabled(Group);
            set => _owner.SetTypeEnabled(Group, value);
        }

        internal void RaiseIsEnabledChanged() => Raise(nameof(IsEnabled));
    }

    /// <summary>The window's toggle row, one entry per MergeTypes.AllGroups,
    /// in that group's own display order. Built once at construction — the
    /// GROUPS never change at runtime, only which of them are switched on,
    /// which each MergeTypeToggle reads live off this view model rather than
    /// caching.</summary>
    public IReadOnlyList<MergeTypeToggle> TypeToggles { get; }

    public bool IsTypeEnabled(string group) => _enabledTypes.Contains(group);

    /// <summary>Switches one MergeTypes group on or off. Persists the whole
    /// set through config immediately via MergeTypes.Save — which writes its
    /// own NoneStored sentinel for an empty set rather than "", the one
    /// thing that lets unticking every type survive a reopen as "everything
    /// off" instead of reading back as "never configured" and defaulting to
    /// everything on again (MergeTypes.Load's own contract) — then folds the
    /// new set into every row already in the list by calling OnRowsChanged,
    /// which is also what re-raises PropertyChanged on the ROW so the grid
    /// repaints and refreshes MergeButtonText/the commands' CanExecute the
    /// same way any other row change does. This is the whole reason a
    /// switched-off type's rows stay listed rather than being removed: the
    /// exact same rows rejoin a run the instant the type is switched back
    /// on, with no re-add.</summary>
    public void SetTypeEnabled(string group, bool enabled)
    {
        if (enabled) _enabledTypes.Add(group); else _enabledTypes.Remove(group);
        _cfg.MergeTypes = MergeTypes.Save(_enabledTypes);
        _saveConfig?.Invoke();

        foreach (var toggle in TypeToggles) toggle.RaiseIsEnabledChanged();
        OnRowsChanged();
    }

    /// <summary>Whether the CURRENT toggle set includes this row's own type.
    /// An extension no MergeTypes group recognizes at all is never
    /// excludable by any toggle — the same rule PdfMerge.IsSwitchedOff
    /// applies at merge time, kept in step here so a row's grid state never
    /// disagrees with what a run would actually do with it. A folder row
    /// (Extensions never lets one into THIS window, but the check is honest
    /// either way) has no extension and so is never excluded by this either.</summary>
    private bool IsRowIncluded(ZipItemRow row) =>
        MergeTypes.GroupOf(ZipItemRow.ExtensionOf(row.Path)) is not { } group || _enabledTypes.Contains(group);

    /// <summary>Zips at archive level, loose PDFs through Unlock's own
    /// probe. PDFs INSIDE a zip are not probed here — that would read every
    /// archive fully twice over a share — and are asked for during the run.
    ///
    /// Every OTHER row Task 7 lets in — word/excel/powerpoint/image/text —
    /// gets a different question: not "is it locked" (none of these five
    /// groups have a password concept this window checks up front — Office
    /// documents ARE askable, but only during the run itself, the same as a
    /// PDF inside a zip), but "does anything on this PC even claim this
    /// type at all". A .docx on a machine without Word is the headline case:
    /// without this, it would sit Pending, show as an enabled "Merge 1
    /// item", and then merge nothing on click — found out only after
    /// clicking Merge instead of the moment it's dropped. Only checked while
    /// the type is switched ON: an excluded row's Note is already masked
    /// (ZipItemRow.IsIncluded) and it never joins a run either way, so
    /// probing it here would produce a verdict nobody can see or act on
    /// until the type is switched back on — at which point THIS probe has
    /// already finished and would never run again for it.</summary>
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords)
    {
        if (row.IsZip) return FromZipProbe(_zipProbe(row.Path, savedPasswords));
        if (row.IsPdf) return FromPdfProbe(_pdfProbe(row.Path, savedPasswords));

        var extension = ZipItemRow.ExtensionOf(row.Path);
        var group = MergeTypes.GroupOf(extension);
        if (group is null || !_enabledTypes.Contains(group) || _converter.Handles(extension)) return null;

        var appName = GroupLabels.TryGetValue(group, out var label) ? label : group;
        return (ZipItemRowStatus.Error, $"{appName} isn't installed, so this can't be converted");
    }

    /// <summary>Already the one place re-run after every list change (an
    /// add, Clear, a run finishing) as well as every SetTypeEnabled call —
    /// so re-evaluating IsIncluded here, rather than separately in
    /// SetTypeEnabled, covers a file dropped while its type is ALREADY
    /// switched off (excluded immediately, not just from the next unrelated
    /// toggle) with the same one pass that covers a toggle flip. Every row's
    /// setter is a no-op unless its own IsIncluded actually changes, so
    /// looping every row on every call costs nothing observable.</summary>
    protected override void OnRowsChanged()
    {
        foreach (var row in Rows) row.IsIncluded = IsRowIncluded(row);

        Raise(nameof(MergeButtonText));
        MergeCommand.RaiseCanExecuteChanged();
        MergeToCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand MergeCommand { get; }
    public AsyncRelayCommand MergeToCommand { get; }

    /// <summary>Counts every runnable row — a zip or a loose document alike
    /// — matching MergeCommand's own CanExecute.</summary>
    public string MergeButtonText => RunnableRows switch
    {
        0 => "Merge",
        1 => "Merge 1 item",
        var n => $"Merge {n} items",
    };

    /// <summary>Merge to…: a Save-As for the loose-document output only —
    /// zips already have a natural name and place. "Loose" now means every
    /// runnable row that isn't its own zip (Task 8): a Word document or an
    /// image sitting loose in the list is exactly as much a candidate for a
    /// chosen output name as a loose PDF always was — there is no reason
    /// Save-As should offer to merge PDFs alone when the button beside it
    /// merges everything. Suggests PdfMerge.DefaultName's own pick; a
    /// cancelled dialog is a silent no-op.</summary>
    internal async Task MergeToAsync()
    {
        var loose = Rows.Where(r => !r.IsZip && r.IsRunnable).Select(r => r.Path).ToList();
        if (loose.Count == 0) return;
        var path = Dialogs.AskSaveFile("PDF (*.pdf)|*.pdf", PdfMerge.DefaultName(loose));
        if (path is null) return;
        await MergeAsync(path);
    }

    /// <summary>Zips first, one unit each, then the loose group as one unit
    /// — runnable rows only. <paramref name="outputPath"/> reaches the loose
    /// group alone. The candidates and the prompt are the base class's; a
    /// merger asks only for what none of the candidates opens.
    ///
    /// The loose group's own selection is !IsZip, not IsPdf (Task 8): before
    /// this, a list holding only a runnable .docx counted as 1 in
    /// MergeButtonText/RunnableRows (which never used IsPdf) but selected
    /// ZERO units here — an enabled "Merge 1 item" button that merged
    /// nothing on click, silently, because the row was neither a zip nor a
    /// literal PDF. The revert-proof fact for this file is exactly that
    /// scenario: a lone non-PDF document, run for real, through the real
    /// PdfMerge.MergeFiles.</summary>
    internal Task MergeAsync(string? outputPath)
    {
        var units = new List<Unit<PdfMerge.MergeResult>>();
        foreach (var row in Rows.Where(r => r.IsZip && r.IsRunnable))
        {
            var zipRow = row;
            units.Add(new Unit<PdfMerge.MergeResult>(new[] { zipRow },
                candidates => _zipMerger(zipRow.Path, candidates, AskPassword)));
        }
        var loose = Rows.Where(r => !r.IsZip && r.IsRunnable).ToList();
        if (loose.Count > 0)
        {
            var paths = loose.Select(r => r.Path).ToList();
            units.Add(new Unit<PdfMerge.MergeResult>(loose,
                candidates => _fileMerger(paths, outputPath, candidates, AskPassword)));
        }
        return RunBatchAsync(units, r => r.Status, ApplyToUnit, "Merging",
            new[]
            {
                new TallyClause("ok", "merged"),
                new TallyClause("no_pdfs", "had no PDFs"),
                new TallyClause("needs_password", "needs a password", "need a password"),
                new TallyClause("error", "failed"),
            });
    }

    /// <summary>Disposes the converter this window was built with, if it
    /// needs disposing at all. The default chain's OfficeConverter link is
    /// the one that does: an Office session it STARTED gets Quit() and
    /// killed here; one it BORROWED (the user's own already-open Word or
    /// Excel) gets its DisplayAlerts/Visible/AutomationSecurity flags
    /// restored. A failure to restore a borrowed session's flags is recorded
    /// in OfficeConverter.RestorationWarnings — a channel nothing else in
    /// this feature reads — so any warning it collects is folded into
    /// Status here, the last place this window can still say something to
    /// whoever is looking at it before it closes.
    ///
    /// Called from MergePdfsWindow.OnClosed, alongside Cancel(); harmless to
    /// call from a test that never opens a real window (an OfficeConverter
    /// that never converted anything disposes as a handful of null checks),
    /// and harmless to call twice (OfficeConverter.Dispose is idempotent).
    /// A converter supplied from the outside is disposed too, exactly the
    /// same way, if it happens to implement IDisposable — this class does
    /// not know or care which converter it was given, only whether disposing
    /// it is possible.</summary>
    public void Dispose()
    {
        if (_converter is not IDisposable disposable) return;
        disposable.Dispose();

        var warnings = (_converter as ConverterChain)?.RestorationWarnings ?? Array.Empty<string>();
        if (warnings.Count == 0) return;
        var warningText = string.Join("; ", warnings);
        Status = Status.Length > 0 ? $"{Status} · {warningText}" : warningText;
    }

    /// <summary>One result, every row of the unit. A unit of one, or any
    /// success, is the row's own Apply. A failed group is fail-whole: the
    /// culprit (MergeResult.Item, a full path) takes the result — a runnable
    /// NeedsPassword, or an Error — and every other row stays Pending with a
    /// note naming what held it back, so the next run picks them up again
    /// once the culprit is opened or removed. A group failure with no
    /// culprit (the save itself failed) leaves every row Pending with the
    /// message.</summary>
    private static void ApplyToUnit(IReadOnlyList<ZipItemRow> rows, PdfMerge.MergeResult result)
    {
        if (rows.Count == 1 || result.Status == "ok")
        {
            foreach (var row in rows) row.Apply(result);
            return;
        }
        if (result.Item is null)
        {
            foreach (var row in rows) row.Mark(ZipItemRowStatus.Pending, result.Message);
            return;
        }
        var culpritName = System.IO.Path.GetFileName(result.Item);
        var reason = result.Status == "needs_password" ? "needs a password" : "couldn't be read";
        foreach (var row in rows)
        {
            if (string.Equals(row.Path, result.Item, StringComparison.OrdinalIgnoreCase)) row.Apply(result);
            else row.Mark(ZipItemRowStatus.Pending, $"not merged — {culpritName} {reason}");
        }
    }
}
