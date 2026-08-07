using System.Collections.ObjectModel;
using System.IO;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>An editable label-maker client row.</summary>
public sealed class LabelClientVm : ObservableObject
{
    private string _id = "", _destroyDaysText = "30", _nextNumberText = "1";

    /// <summary>Uppercased as typed — the barcode alphabet is A-Z 0-9.</summary>
    public string Id
    {
        get => _id;
        set { if (Set(ref _id, value.ToUpperInvariant().Trim())) Raise(nameof(Summary)); }
    }

    public string DestroyDaysText
    {
        get => _destroyDaysText;
        set { if (Set(ref _destroyDaysText, value)) Raise(nameof(Summary)); }
    }

    public string NextNumberText
    {
        get => _nextNumberText;
        set { if (Set(ref _nextNumberText, value)) Raise(nameof(Summary)); }
    }

    /// <summary>Hover detail for the client list row.</summary>
    public string Summary =>
        $"{DestroyDaysText}-day retention   ·   next label {NextNumberText}";

    public Dictionary<string, System.Text.Json.JsonElement> Extras { get; init; } = new();

    public static LabelClientVm From(LabelClient c) => new()
    {
        Id = c.Id,
        DestroyDaysText = c.DestroyDays.ToString(),
        NextNumberText = c.NextNumber.ToString(),
        Extras = new Dictionary<string, System.Text.Json.JsonElement>(c.Extras),
    };

    public LabelClient ToClient() => new()
    {
        Id = Id,
        DestroyDays = int.TryParse(DestroyDaysText.Trim(), out var d) ? d : 30,
        NextNumber = long.TryParse(NextNumberText.Trim(), out var n) ? n : 1,
        Extras = new Dictionary<string, System.Text.Json.JsonElement>(Extras),
    };
}

/// <summary>Tools → Label maker: print-ready box labels, ten per US-letter
/// sheet. Each client keeps its own destruction offset and running number;
/// generating a batch advances the number and persists it.</summary>
public sealed class LabelMakerViewModel : ObservableObject
{
    private readonly Config _cfg;
    private readonly string _boxLabelsPath;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _today;
    private readonly Action<string> _openFile;
    private readonly IWorkScheduler _scheduler;

    public ObservableCollection<LabelClientVm> Clients { get; } = new();

    // ---------------------------------------------------- merge-Persist state
    // Only clients touched during this window session get written back; every
    // other client's row is whatever the disk currently holds (another
    // station may have advanced its counter while this window was open).
    // Tracked by OBJECT identity (default reference equality — LabelClientVm
    // overrides neither Equals nor GetHashCode), NOT by id string: two rows
    // can share an id (a rename collision, or a pre-existing duplicate — both
    // of which Persist's duplicate-id guard below still refuses to merge, but
    // Problems()/Add don't prevent the transient state from existing), and a
    // string-keyed set would sweep an untouched sibling into the dirty branch
    // right along with the one actually edited.
    private readonly HashSet<LabelClientVm> _dirty = new();
    private readonly HashSet<string> _removedIds = new();

    // _dirty is row-granularity ("something on this client changed"), which
    // is too coarse for NextNumber specifically: editing e.g. retention days
    // dirties the whole row, and the row's NextNumberText is whatever was on
    // screen when the window opened — not necessarily what's on disk now (a
    // peer may have advanced it via Print/SavePdf since). Writing that stale
    // value back would reissue box numbers already on physical boxes. This
    // second, finer set records only "the user actually edited
    // NextNumberText on this row" so Persist can special-case that one field:
    // disk wins UNLESS this set says otherwise, even when the row is
    // otherwise dirty for an unrelated reason. A deliberate edit must still
    // win — this is what lets it.
    private readonly HashSet<LabelClientVm> _numberEdited = new();

    // Set while ClaimNumbers pushes its post-claim number onto the VM: that
    // update is display-only (the store already holds the advanced number),
    // so it must NOT be mistaken for a user edit and dirty the client.
    private bool _suppressDirty;

    public LabelMakerViewModel(Config cfg, string boxLabelsPath, IDialogService dialogs,
        Func<DateTime>? today = null, Action<string>? openFile = null,
        IWorkScheduler? scheduler = null)
    {
        _cfg = cfg;
        _boxLabelsPath = boxLabelsPath;
        _dialogs = dialogs;
        _today = today ?? (() => DateTime.Now);
        _openFile = openFile ?? (p => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }));
        _scheduler = scheduler ?? new TaskWorkScheduler();

        try
        {
            // Legacy inline label_clients (a pre-split config) must not be
            // silently orphaned: if the box-labels file has never been
            // written yet but the config still carries inline clients, seed
            // the store from them BEFORE reading. Without this, opening the
            // window shows an empty roster, closing it persists that empty
            // roster, and Config.Save's bootstrap-only rule means the
            // migration never gets another chance — the inline counters are
            // gone for good the next time anything saves config.json.
            if (!File.Exists(boxLabelsPath) && _cfg.LabelClients.Count > 0)
            {
                BoxLabelStore.Mutate(boxLabelsPath, d =>
                {
                    d.LabelClients = _cfg.LabelClients.Select(c => new LabelClient
                    {
                        Id = c.Id, DestroyDays = c.DestroyDays, NextNumber = c.NextNumber,
                        Extras = c.Extras,
                    }).ToList();
                    return 0;
                });
            }

            var doc = BoxLabelStore.Read(boxLabelsPath);
            _dateStyle = BoxLabels.NormalizeDateStyle(doc.DateStyle);
            foreach (var c in doc.LabelClients)
                Hook(Clients.AddReturn(LabelClientVm.From(c)));
        }
        catch (ConfigException ex)
        {
            // a held or corrupt file at window-open time is not fatal — warn
            // and open with an empty roster rather than throwing into the
            // global handler
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
        }

        AddClientCommand = new RelayCommand(() =>
        {
            var vm = Hook(Clients.AddReturn(new LabelClientVm()));
            Selected = vm;
            RequestIdFocus?.Invoke();   // the id box is the only next step
        });
        RemoveClientCommand = new RelayCommand(() =>
        {
            if (Selected is not { } s) return;
            // the running number is what keeps box numbers unique — removing
            // it is the one destructive act in this window, so it confirms
            // (a just-added blank row goes quietly)
            var pristine = s.Id.Length == 0 && s.NextNumberText.Trim() is "" or "1";
            if (!pristine && !_dialogs.Confirm(
                    $"Remove \"{s.Id}\"?\n\nIts running label number ({s.NextNumberText}) "
                    + "will be lost — re-adding the client starts back at 1.",
                    "OrdoSort — label maker"))
                return;
            Clients.Remove(s);
            if (s.Id.Length > 0) _removedIds.Add(s.Id);   // a blank pristine row was never on disk
            Selected = Clients.FirstOrDefault();
        }, () => Selected is not null);
        ResetNumberCommand = new RelayCommand(
            () => { if (Selected is { } s) s.NextNumberText = "1"; },
            () => Selected is not null);
        PrintCommand = new RelayCommand(Print, () => Selected is not null);
        SavePdfCommand = new RelayCommand(SavePdf, () => Selected is not null);

        Selected = Clients.FirstOrDefault();   // after the commands the setter pokes
    }

    /// <summary>Sends composed sheets to a printer; returns false when the
    /// user cancels the print dialog. Supplied by the window (WPF PrintDialog
    /// + FixedDocument); tests inject a recorder.</summary>
    internal Func<IReadOnlyList<BoxLabels.Item>, string, bool>? PrintSheets { get; set; }

    /// <summary>The window's print path reports failures through the same
    /// dialog service the view model uses.</summary>
    internal IDialogService Dialogs => _dialogs;

    /// <summary>Raised after Add so the view can put the caret in the
    /// client-id box — typing the id is the only sensible next step.</summary>
    public event Action? RequestIdFocus;

    private LabelClientVm Hook(LabelClientVm vm)
    {
        var lastId = vm.Id;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LabelClientVm.Id) && vm.Id != lastId)
            {
                // renaming a tracked client is remove-old + dirty-new: the
                // store may still hold a row keyed by the pre-edit id, and it
                // must not survive alongside the new one after Persist
                if (lastId.Length > 0) _removedIds.Add(lastId);
                lastId = vm.Id;
            }
            if (!_suppressDirty && vm.Id.Length > 0)
            {
                _dirty.Add(vm);
                // only a direct edit of the number box counts — Summary's own
                // change notification (raised alongside it, see
                // LabelClientVm.NextNumberText) must not trip this
                if (e.PropertyName == nameof(LabelClientVm.NextNumberText))
                    _numberEdited.Add(vm);
            }
            RefreshPreview();
        };
        return vm;
    }

    private LabelClientVm? _selected;
    public LabelClientVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            RemoveClientCommand.RaiseCanExecuteChanged();
            ResetNumberCommand.RaiseCanExecuteChanged();
            PrintCommand.RaiseCanExecuteChanged();
            SavePdfCommand.RaiseCanExecuteChanged();
            RefreshPreview();
        }
    }

    // "bars" (default) or "plain" — seeded from the store at ctor, persisted
    // through it immediately on change (config-ish, not counter-ish: unlike
    // the client rows, there is no "disk wins for untouched" story to protect
    // here, so an immediate write is both correct and cheap).
    private string _dateStyle = BoxLabels.DateStyleBars;
    public string DateStyle
    {
        get => _dateStyle;
        set
        {
            if (!Set(ref _dateStyle, value)) return;
            Raise(nameof(DateStyleBars));
            Raise(nameof(DateStylePlain));
            try
            {
                BoxLabelStore.Mutate(_boxLabelsPath, d => { d.DateStyle = value; return 0; });
            }
            catch (ConfigException ex)
            {
                _dialogs.Warn(ex.Message, "OrdoSort — label maker");
            }
        }
    }

    /// <summary>Two-radio pattern (Filing tab's ModeInsert/ModeReplace, etc):
    /// bound with GroupName="DateStyle" in XAML.</summary>
    public bool DateStyleBars
    {
        get => DateStyle == BoxLabels.DateStyleBars;
        set { if (value) DateStyle = BoxLabels.DateStyleBars; }
    }

    public bool DateStylePlain
    {
        get => DateStyle == BoxLabels.DateStylePlain;
        set { if (value) DateStyle = BoxLabels.DateStylePlain; }
    }

    private string _labelCountText = "10";
    public string LabelCountText
    {
        get => _labelCountText;
        set { if (Set(ref _labelCountText, value)) RefreshPreview(); }
    }

    private string _preview = "";
    public string Preview { get => _preview; private set => Set(ref _preview, value); }

    /// <summary>The first label of the batch, rendered live by the window's
    /// preview control; null while the inputs have a problem.</summary>
    private BoxLabels.Item? _previewItem;
    public BoxLabels.Item? PreviewItem { get => _previewItem; private set => Set(ref _previewItem, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RelayCommand AddClientCommand { get; }
    public RelayCommand RemoveClientCommand { get; }
    public RelayCommand ResetNumberCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand SavePdfCommand { get; }

    /// <summary>Everything wrong with the current inputs, one line each.</summary>
    internal List<string> Problems()
    {
        var problems = new List<string>();
        if (Selected is not { } s) { problems.Add("Add a client first."); return problems; }

        var idProblem = BoxLabels.ValidateClientId(s.Id);
        if (idProblem.Length > 0) problems.Add(idProblem);
        if (Clients.Count(c => c.Id == s.Id) > 1)
            problems.Add($"Two clients are both called \"{s.Id}\".");
        if (!int.TryParse(s.DestroyDaysText.Trim(), out var days) || days is < 0 or > 3650)
            problems.Add("Keep days must be a number from 0 to 3650.");
        if (!long.TryParse(s.NextNumberText.Trim(), out var next) || next < 1 || next > BoxLabels.MaxNumber)
            problems.Add($"Next label number must be 1 to {BoxLabels.MaxNumber}.");
        if (!int.TryParse(LabelCountText.Trim(), out var count) || count is < 1 or > 1000)
            problems.Add("Labels to print must be 1 to 1000.");
        else if (long.TryParse(s.NextNumberText.Trim(), out var n)
                 && n >= 1 && n + count - 1 > BoxLabels.MaxNumber)
            problems.Add("That batch would run past label 99999999 — reset the number.");
        return problems;
    }

    private void RefreshPreview()
    {
        if (Selected is not { } s) { Preview = ""; PreviewItem = null; return; }
        var problems = Problems();
        if (problems.Count > 0) { Preview = "⚠ " + problems[0]; PreviewItem = null; return; }
        var created = _today().Date;
        var destroy = created.AddDays(int.Parse(s.DestroyDaysText.Trim()));
        var start = long.Parse(s.NextNumberText.Trim());
        var count = int.Parse(LabelCountText.Trim());
        var sheets = (count + BoxLabels.PerSheet - 1) / BoxLabels.PerSheet;
        PreviewItem = new BoxLabels.Item(BoxLabels.Compose(s.Id, start), created, destroy);
        Preview = count == 1
            ? $"Prints {BoxLabels.Compose(s.Id, start)}   ·   1 sheet"
            : $"Prints {BoxLabels.Compose(s.Id, start)} – {BoxLabels.Compose(s.Id, start + count - 1)}"
              + $"   ·   {sheets} sheet{(sheets == 1 ? "" : "s")}";
    }

    /// <summary>Validated batch for the current inputs, or null after warning.</summary>
    private (List<BoxLabels.Item> Items, LabelClientVm Client, long Start, int Count)? BuildBatch()
    {
        if (Selected is not { } s) return null;
        var problems = Problems();
        if (problems.Count > 0)
        {
            _dialogs.Warn("These need fixing first:\n\n • " + string.Join("\n • ", problems),
                "OrdoSort — label maker");
            return null;
        }
        var start = long.Parse(s.NextNumberText.Trim());
        var count = int.Parse(LabelCountText.Trim());
        var items = BoxLabels.Batch(s.Id, start, count, _today(),
            int.Parse(s.DestroyDaysText.Trim()));
        return (items, s, start, count);
    }

    /// <summary>The file-only half of a claim: reads the FRESH on-disk
    /// counter, refuses a batch that would pass <see cref="BoxLabels.MaxNumber"/>,
    /// and advances it. Touches nothing on the VM, so it is safe to run off
    /// the UI thread (SavePdfAsync offloads it alongside the render). Throws
    /// <see cref="ConfigException"/> on a busy/corrupt file OR a
    /// ceiling-breaking batch — the write never lands in either case.</summary>
    private long ClaimNumbersCore(LabelClientVm client, int count) =>
        BoxLabelStore.Mutate(_boxLabelsPath, doc =>
        {
            var c = doc.LabelClients.FirstOrDefault(x => x.Id == client.Id);
            if (c is null)
            {
                c = client.ToClient();
                doc.LabelClients.Add(c);
            }
            var s = c.NextNumber;
            if (s + count - 1 > BoxLabels.MaxNumber)
                throw new ConfigException(
                    "this batch would pass label 99 999 999 — reset or renumber the client");
            c.NextNumber = s + count;
            return s;
        });

    /// <summary>Push a post-claim number onto the VM without marking the
    /// client dirty — the store already holds the advanced number, so this is
    /// display-only (see the merge-Persist notes on <see cref="_dirty"/>).</summary>
    private void SetClaimedNumber(LabelClientVm client, long value)
    {
        _suppressDirty = true;
        try { client.NextNumberText = value.ToString(); }
        finally { _suppressDirty = false; }
    }

    /// <summary>Claim `count` numbers for `client` from the FRESH on-disk
    /// counter (several stations may be printing). Returns the claimed start,
    /// or null when the file is busy past the retry window or the claim would
    /// pass the ceiling — either way, already warned. UI-thread only: Print()
    /// calls this directly; SavePdfAsync uses <see cref="ClaimNumbersCore"/>
    /// instead so the file work can run off-thread.</summary>
    internal long? ClaimNumbers(LabelClientVm client, int count)
    {
        try
        {
            var start = ClaimNumbersCore(client, count);
            SetClaimedNumber(client, start + count);
            return start;
        }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
            return null;
        }
    }

    /// <summary>Rebuild the batch's items against a freshly-claimed start when
    /// it differs from the stale on-screen number BuildBatch used — the
    /// created/destroy dates carry over unchanged, only the codes shift.</summary>
    private static List<BoxLabels.Item> RebuildFromClaim(
        (List<BoxLabels.Item> Items, LabelClientVm Client, long Start, int Count) b, long claimedStart) =>
        claimedStart == b.Start
            ? b.Items
            : BoxLabels.Batch(b.Client.Id, claimedStart, b.Count, b.Items[0].Created,
                int.Parse(b.Client.DestroyDaysText.Trim()));

    internal void Print()
    {
        if (BuildBatch() is not { } b) return;
        if (PrintSheets is null)
        {
            _dialogs.Warn("Printing isn't available here.", "OrdoSort — label maker");
            return;
        }
        // Claim from the fresh file FIRST: several stations may be printing,
        // so the sheets that actually go out must carry the claimed numbers,
        // not whatever was on screen when this window opened.
        if (ClaimNumbers(b.Client, b.Count) is not { } start) return;   // busy file — already warned
        var items = RebuildFromClaim(b, start);
        if (!PrintSheets(items, $"OrdoSort labels {items[0].Code}")) return;   // cancelled
        var sheets = (b.Count + BoxLabels.PerSheet - 1) / BoxLabels.PerSheet;
        Status = $"Sent {b.Count} label{(b.Count == 1 ? "" : "s")} "
            + $"({sheets} sheet{(sheets == 1 ? "" : "s")}) to the printer.";
    }

    internal void SavePdf() => _ = SavePdfAsync();

    internal async Task SavePdfAsync()
    {
        if (BuildBatch() is not { } b) return;
        var dest = _dialogs.AskSaveFile("PDF files (*.pdf)|*.pdf",
            $"labels_{b.Client.Id}_{b.Start:D8}.pdf");
        if (dest is null) return;
        var dateStyle = _dateStyle;   // read on the UI thread before offloading

        long claimedStart;
        try
        {
            // Claim from the fresh file INSIDE the offload, same reasoning as
            // Print(): the PDF that lands on disk must carry the claimed
            // numbers. The claim is now alongside the render (both are file
            // work, neither belongs on the UI thread) — if the claim throws,
            // the render below never runs, and nothing lands on disk.
            claimedStart = await _scheduler.Run(() =>
            {
                var start = ClaimNumbersCore(b.Client, b.Count);
                var items = RebuildFromClaim(b, start);
                BoxLabels.RenderPdf(dest, items, dateStyle);
                return start;
            });
        }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort — label maker");
            return;
        }

        // back on the UI thread — the await above resumed via the captured
        // SynchronizationContext, same as every other post-offload line in
        // this file, so it is safe to touch the VM here
        SetClaimedNumber(b.Client, claimedStart + b.Count);
        var sheets = (b.Count + BoxLabels.PerSheet - 1) / BoxLabels.PerSheet;
        Status = $"Saved {b.Count} label{(b.Count == 1 ? "" : "s")} "
            + $"({sheets} sheet{(sheets == 1 ? "" : "s")}) — print at 100% scale.";
        try { _openFile(dest); } catch { /* viewer trouble isn't a label problem */ }
    }

    /// <summary>Merge only the clients touched this session back into the
    /// box-labels file — untouched clients are left exactly as the disk holds
    /// them, so a station that advanced someone else's counter while this
    /// window was open (via Print/SavePdf elsewhere, or another station
    /// entirely) is never clobbered by our stale in-memory copy of that row.
    /// A zero-edit close writes nothing at all. Refuses entirely (no partial
    /// write) if two clients on screen share an id: merging by id is
    /// inherently ambiguous under a collision — whichever row this loop
    /// reaches last would silently win and the other's edits (or a
    /// concurrent counter advance under that id) would be discarded with no
    /// warning. Nothing here blocks a duplicate id from existing transiently
    /// (Problems() only ever gates the currently-Selected row), so this is
    /// the one place that must catch it before it reaches disk.
    ///
    /// Within a dirty row, NextNumber gets one further guard beyond "disk
    /// wins for untouched clients": it also loses to the disk for a client
    /// that IS otherwise dirty, unless <see cref="_numberEdited"/> says the
    /// user actually edited NextNumberText. Editing an unrelated field (e.g.
    /// retention days) must not roll a peer's counter advance back to
    /// whatever was on screen when this window opened — that would reissue
    /// numbers already printed on physical boxes. A deliberate edit of the
    /// number itself still lands; this only guards the untouched case.</summary>
    internal void Persist()
    {
        if (_dirty.Count == 0 && _removedIds.Count == 0) return;   // zero-edit close writes nothing

        var duplicate = Clients
            .Where(c => c.Id.Length > 0)
            .GroupBy(c => c.Id)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            _dialogs.Warn($"Two clients share the id \"{duplicate.Key}\" — fix the duplicate "
                + "before closing; nothing was saved.", "OrdoSort — label maker");
            return;
        }

        try
        {
            BoxLabelStore.Mutate(_boxLabelsPath, doc =>
            {
                doc.LabelClients.RemoveAll(c => _removedIds.Contains(c.Id));
                foreach (var vm in Clients)
                {
                    if (!_dirty.Contains(vm)) continue;        // untouched: disk wins
                    var fresh = doc.LabelClients.FirstOrDefault(c => c.Id == vm.Id);
                    if (fresh is null) doc.LabelClients.Add(vm.ToClient());
                    else
                    {
                        var edited = vm.ToClient();
                        fresh.DestroyDays = edited.DestroyDays;
                        // NextNumber: disk wins unless the user actually
                        // edited the number box on this row — see the
                        // Persist doc comment and _numberEdited above.
                        if (_numberEdited.Contains(vm)) fresh.NextNumber = edited.NextNumber;
                        fresh.Extras = edited.Extras;
                    }
                }
                return 0;
            });
            _dirty.Clear(); _removedIds.Clear(); _numberEdited.Clear();
        }
        catch (ConfigException ex) { _dialogs.Warn(ex.Message, "OrdoSort — label maker"); }
    }
}

file static class CollectionExtensions
{
    public static T AddReturn<T>(this ObservableCollection<T> list, T item)
    {
        list.Add(item);
        return item;
    }
}
