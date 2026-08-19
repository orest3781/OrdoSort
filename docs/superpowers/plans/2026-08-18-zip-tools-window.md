# Zip tools window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `ZipWindow`, `UnzipWindow` and `ZipMergeWindow` with one two-tab `ZipToolsWindow`, deleting the duplication the three share rather than relocating it.

**Architecture:** Two tabs, each owning its own list. *Zip & unzip* takes files, folders and archives and lights its buttons from the contents; *Merge PDFs* takes archives only. Both tabs' shared machinery — intake, dedupe, remove, clear, notes, the cancellable batch runner — lives in one abstract `ZipListViewModel`, with two small subclasses adding only their own commands. The Core engines (`Zipper`, `ZipMerge`) are not touched.

**Tech Stack:** .NET 8 WPF, xUnit, `ObservableObject`/`RelayCommand`/`AsyncRelayCommand` from `OrdoSort.Wpf.Mvvm`, `IWorkScheduler` for off-thread work.

**Spec:** `docs/superpowers/specs/2026-08-18-archive-window-merge-design.md`

## Global Constraints

- **Build before test, always with `-p:Deterministic=false`.** Smart App Control on this machine blocks freshly-built test assemblies by hash otherwise. Build: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`. Test: `dotnet test tests/OrdoSort.Wpf.Tests --no-build --nologo`.
- **`dotnet test` can exit 0 having run ZERO tests.** Never trust the exit code alone — confirm the `Passed!  - Failed: 0, Passed: N` line, and that N is what you expected.
- Baseline at plan time: **608 Core + 1726 WPF tests green.**
- The three Core engines are **out of scope**: no edits to `src/OrdoSort.Core/Zipper.cs`, `src/OrdoSort.Core/ZipMerge.cs`, or their test files.
- Spacing in any XAML you touch must sit on the canon scale **2/4/6/8/10/12/14/16** (documented above `FieldRow` in `Theme/Styles.xaml`). Dialog root gutter is `14`.
- Any `TextBlock` carrying prose must not sit in a horizontal `StackPanel` — that measures children at infinite width and silently defeats wrapping and trimming. `TextWrapCoverageTests` enforces this and will fail the build.
- Commit after every task. Never leave a commit with a red build.

---

### Task 1: `ZipItemRow` and the shared `ZipListViewModel` base

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `ZipItemRowStatus { Pending, Ok, NoPdfs, Error }`; `ZipItemRow` with `Path`, `Kind`, `IsZip`, `Display`, `StatusKind`, `Note`, `Output`, `static string KindOf(string path)`, `internal void Apply(Zipper.UnzipResult)`, `internal void Apply(ZipMerge.MergeResult)`; `abstract class ZipListViewModel` with `Rows`, `AddNote`, `Status`, `ClearCommand`, `Task AddPaths(IEnumerable<string>)`, `void RemoveSelected(IList)`, `void Cancel()`, `protected Task RunBatchAsync<TResult>(...)`, `protected abstract ISet<string>? Extensions`, `protected abstract string IntakeNoun`, `protected virtual void OnRowsChanged()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`:

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>ZipItemRow is the union of the three row types the zip tools
/// used to carry one each (PathRow, UnzipRow, ZipRow). These pin the parts
/// that were behaviour rather than plumbing: how a path becomes a Kind, and
/// what each engine result turns into on the row.</summary>
public class ZipItemRowTests
{
    [Theory]
    [InlineData(@"C:\in\a.pdf", "file")]
    [InlineData(@"C:\in\a.ZIP", "zip")]
    [InlineData(@"C:\in\a.zip", "zip")]
    public void KindOfReadsTheExtensionForAnythingThatIsNotADirectory(string path, string expected) =>
        Assert.Equal(expected, ZipItemRow.KindOf(path));

    [Fact]
    public void KindOfCallsAnExistingDirectoryAFolder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.Equal("folder", ZipItemRow.KindOf(dir)); }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void IsZipDrivesOffKindNotTheExtension() =>
        Assert.True(new ZipItemRow(@"C:\in\a.zip", "zip").IsZip);

    /// <summary>A folder row shows the folder's OWN name; Path.GetFileName
    /// would return "" for a trailing separator, which is why PathRow used
    /// DirectoryInfo.Name and this keeps doing so.</summary>
    [Fact]
    public void DisplayUsesTheFolderNameForAFolderRow() =>
        Assert.Equal("scans", new ZipItemRow(@"C:\in\scans\", "folder").Display);

    [Fact]
    public void DisplayUsesTheFileNameForEverythingElse() =>
        Assert.Equal("a.pdf", new ZipItemRow(@"C:\in\a.pdf", "file").Display);

    [Fact]
    public void ApplyingAnOkExtractShowsTheOutputFolder()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "ok", @"C:\in\a"));
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal("→ a", row.Note);
        Assert.Equal(@"C:\in\a", row.Output);
    }

    [Fact]
    public void ApplyingAFailedExtractShowsTheMessageVerbatim()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "error", null, "not a valid zip archive"));
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Equal("not a valid zip archive", row.Note);
    }

    [Fact]
    public void ApplyingAnOkMergeCountsThePdfs()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new ZipMerge.MergeResult(@"C:\in\a.zip", "ok", @"C:\in\a.pdf", PdfCount: 3));
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal("→ a.pdf (3 PDFs)", row.Note);
    }

    [Fact]
    public void ApplyingAMergeWithNoPdfsIsItsOwnStatus()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new ZipMerge.MergeResult(@"C:\in\a.zip", "no_pdfs", Message: "no PDFs inside"));
        Assert.Equal(ZipItemRowStatus.NoPdfs, row.StatusKind);
        Assert.Equal("no PDFs inside", row.Note);
    }

    /// <summary>Singular/plural on the PDF count — ZipRow got this right and
    /// the union must not lose it.</summary>
    [Fact]
    public void OnePdfIsNotPluralised()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new ZipMerge.MergeResult(@"C:\in\a.zip", "ok", @"C:\in\a.pdf", PdfCount: 1));
        Assert.Equal("→ a.pdf (1 PDF)", row.Note);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`
Expected: FAIL — `error CS0246: The type or namespace name 'ZipItemRow' could not be found`.

- [ ] **Step 3: Write `ZipListViewModel.cs`**

Create `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs`. Model the doc-comment voice on the file it replaces (`UnzipViewModel.cs`) — comments state constraints and cite the measured reason, they do not narrate.

```csharp
using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a row's operation ended in. The union of the two enums the
/// zip tools carried one each (UnzipRowStatus, ZipRowStatus): NoPdfs is
/// reachable only from a merge, and stays Pending-shaped for every other
/// operation rather than being modelled per-tab.</summary>
public enum ZipItemRowStatus { Pending, Ok, NoPdfs, Error }

/// <summary>One listed source: a loose file, a whole folder, or an archive.
/// The union of PathRow (Kind/Display), UnzipRow and ZipRow (the status,
/// note and output a batch operation writes back). Kind is a plain string
/// tag rather than an enum for the same reason PathRow's was — nothing
/// switches on it but its own grid column and <see cref="IsZip"/>.</summary>
public sealed class ZipItemRow : ObservableObject
{
    public string Path { get; }
    public string Kind { get; }

    /// <summary>Drives which actions a tab can offer for this row.</summary>
    public bool IsZip => Kind == "zip";

    /// <summary>The file name for a file or archive row; the folder's OWN
    /// name for a folder row — DirectoryInfo.Name handles a trailing
    /// separator correctly where a bare Path.GetFileName returns "".</summary>
    public string Display => Kind == "folder"
        ? new DirectoryInfo(Path).Name
        : System.IO.Path.GetFileName(Path);

    public ZipItemRow(string path, string kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Classifies a path the one way both tabs agree on. Checked in
    /// this order deliberately: a directory named "x.zip" is a folder.</summary>
    public static string KindOf(string path) =>
        Directory.Exists(path) ? "folder"
        : System.IO.Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "zip"
        : "file";

    private ZipItemRowStatus _statusKind = ZipItemRowStatus.Pending;
    public ZipItemRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>"" while Pending; the operation's own message on a failure;
    /// a short result line on success.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _output;
    public string? Output { get => _output; private set => Set(ref _output, value); }

    internal void Apply(Zipper.UnzipResult result)
    {
        StatusKind = result.Status == "ok" ? ZipItemRowStatus.Ok : ZipItemRowStatus.Error;
        Output = result.OutputFolder;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.OutputFolder!)}"
            : result.Message;
    }

    internal void Apply(ZipMerge.MergeResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "no_pdfs" => ZipItemRowStatus.NoPdfs,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.Output;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.Output!)} ({result.PdfCount} PDF{(result.PdfCount == 1 ? "" : "s")})"
            : result.Message;
    }
}

/// <summary>Everything the two zip-tool tabs share: the list, intake and its
/// dedupe, selection removal, Clear, the add note, the status line, and the
/// sequential cancellable batch runner. Each tab owns its OWN instance, so
/// nothing here is shared state between them — extracting on one tab has no
/// bearing on merging on the other, which is the whole reason the tabs have
/// separate lists.
///
/// Sequential rather than parallel, and cancelled BETWEEN items rather than
/// mid-item: each operation writes a folder or a document, so running
/// several at once buys contention rather than speed, and a half-written
/// output is worse than a late one. Both rules are inherited verbatim from
/// the two batch tools this replaces.</summary>
public abstract class ZipListViewModel : ObservableObject
{
    protected readonly IWorkScheduler Scheduler;
    protected readonly SynchronizationContext? UiContext;

    // Cancelled once, from the window's OnClosed: a closed window must not
    // keep working invisibly.
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ZipItemRow> Rows { get; } = new();

    protected ZipListViewModel(IWorkScheduler? scheduler, SynchronizationContext? uiContext)
    {
        Scheduler = scheduler ?? new TaskWorkScheduler();
        UiContext = uiContext;

        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            OnRowsChanged();
        });

        Rows.CollectionChanged += (_, _) => OnRowsChanged();
    }

    /// <summary>Which extensions this tab accepts, in Intake's shape
    /// (dot-less, lowercase); null means anything that exists, files and
    /// folders alike.</summary>
    protected abstract ISet<string>? Extensions { get; }

    /// <summary>The noun Intake's own note builder uses — "item" where a tab
    /// takes anything, "zip" where it takes archives only.</summary>
    protected abstract string IntakeNoun { get; }

    /// <summary>Raised whenever the list changes so a subclass can refresh
    /// its own button texts and command enablement.</summary>
    protected virtual void OnRowsChanged() { }

    public RelayCommand ClearCommand { get; }

    /// <summary>Feedback for the last AddPaths call ("2 added · 1 ignored…");
    /// blank when it added something with nothing to complain about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress during a batch, then its verdict; or a single
    /// verdict for a one-shot operation. One line per tab.</summary>
    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    /// <summary>Called by drag-drop and the Add buttons. Existence checks run
    /// off-thread: a big drop from a slow share must not stall the UI thread
    /// one File.Exists at a time.</summary>
    public async Task AddPaths(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = Rows.Select(r => r.Path).ToList();
        var extensions = Extensions;

        var (offThread, kinds) = await Scheduler.Run(() =>
        {
            var taken = extensions is null
                ? Intake.Add(already, candidates, exists: p => File.Exists(p) || Directory.Exists(p))
                : Intake.Add(already, candidates, extensions, File.Exists);
            var kind = taken.Files.ToDictionary(
                p => p, ZipItemRow.KindOf, StringComparer.OrdinalIgnoreCase);
            return (taken, kind);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — otherwise a second drop landing mid-await duplicates rows.
        var settled = Intake.Add(Rows.Select(r => r.Path), offThread.Files);
        foreach (var p in settled.Files) Rows.Add(new ZipItemRow(p, kinds[p]));

        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note(IntakeNoun);
    }

    /// <summary>Removes exactly the rows the window's grid selection holds.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<ZipItemRow>().ToList())
            Rows.Remove(item);
    }

    /// <summary>Runs one operation over every still-Pending ZIP row, one at a
    /// time. Extract and Merge are this method with a different operation —
    /// the duplication the two batch tools used to carry a copy of each.
    ///
    /// Only Pending rows run: a row that already finished is left exactly as
    /// it is, and re-adding the archive (a fresh Pending row) is how a failed
    /// one is retried.
    ///
    /// <paramref name="clauses"/> are matched against each result's own
    /// status string, in order; a status matching none of them counts toward
    /// the LAST clause, which is how "error" and anything unrecognized share
    /// a bucket.</summary>
    protected async Task RunBatchAsync<TResult>(
        Func<string, TResult> operation,
        Func<TResult, string> statusOf,
        Action<ZipItemRow, TResult> apply,
        string progressVerb,
        IReadOnlyList<(string Status, string Label)> clauses)
    {
        var pending = Rows.Where(r => r.IsZip && r.StatusKind == ZipItemRowStatus.Pending).ToList();
        if (pending.Count == 0) return;   // nothing new — re-add to retry

        var token = _cts.Token;
        var counts = new int[clauses.Count];

        for (var i = 0; i < pending.Count; i++)
        {
            // Checked BETWEEN items, never mid-item: a half-written output is
            // worse than a late one.
            if (token.IsCancellationRequested) break;

            var row = pending[i];
            Status = $"{progressVerb} {i + 1} of {pending.Count}…";
            var result = await Scheduler.Run(() => operation(row.Path));

            // Tallied from the result's OWN status rather than from the row
            // after applying it: the apply may be marshalled onto the UI
            // thread and has not necessarily landed yet.
            var status = statusOf(result);
            var slot = clauses.ToList().FindIndex(c => c.Status == status);
            counts[slot >= 0 ? slot : clauses.Count - 1]++;

            ApplyOnUi(row, result, apply);
        }

        var parts = new List<string>();
        for (var i = 0; i < clauses.Count; i++)
            if (counts[i] > 0) parts.Add($"{counts[i]} {clauses[i].Label}");
        Status = string.Join(" · ", parts);
    }

    /// <summary>Marshals onto UiContext when one is set — a raw thread-pool
    /// continuation has no synchronization context of its own to inherit.</summary>
    protected void ApplyOnUi<TResult>(ZipItemRow row, TResult result, Action<ZipItemRow, TResult> apply)
    {
        if (UiContext is null) apply(row, result);
        else UiContext.Post(_ => apply(row, result), null);
    }

    /// <summary>Marshals a context-free action onto UiContext, for the
    /// one-shot operations that write Status rather than a row.</summary>
    protected void RunOnUi(Action action)
    {
        if (UiContext is null) action();
        else UiContext.Post(_ => action(), null);
    }

    /// <summary>Stops any not-yet-started item from starting; one already
    /// under way finishes.</summary>
    public void Cancel() => _cts.Cancel();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q` then
`dotnet test tests/OrdoSort.Wpf.Tests --no-build --nologo --filter "FullyQualifiedName~ZipItemRowTests"`
Expected: `Passed!  - Failed: 0, Passed: 11`

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs
git commit -m "feat(zip): one row type and one list base for the zip tools"
```

---

### Task 2: `ZipExtractViewModel` — the Zip & unzip tab

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs`
- Read for porting: `tests/OrdoSort.Wpf.Tests/ZipViewModelTests.cs` (11 facts), `tests/OrdoSort.Wpf.Tests/UnzipViewModelTests.cs` (13 facts)

**Interfaces:**
- Consumes: `ZipListViewModel`, `ZipItemRow`, `ZipItemRowStatus` from Task 1.
- Produces: `ZipExtractViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null, Func<string, Zipper.UnzipResult>? extractor = null)` with `ZipCommand`, `ZipAsCommand`, `ExtractCommand`, `ZipButtonText`, `ExtractButtonText`, `internal Task ZipAsync(string? outputPath)`, `internal Task ZipWithDialogAsync()`, `internal Task ExtractAsync()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs`. **Port every fact** from `ZipViewModelTests.cs` and `UnzipViewModelTests.cs` using this rename table, then add the new facts below:

| Old | New |
|---|---|
| `new ZipViewModel(...)` / `new UnzipViewModel(...)` | `new ZipExtractViewModel(dialogs, scheduler, uiContext, zipper, extractor)` |
| `vm.CreateCommand` / `CreateAsync` | `vm.ZipCommand` / `ZipAsync` |
| `vm.CreateAsCommand` / `CreateWithDialogAsync` | `vm.ZipAsCommand` / `ZipWithDialogAsync` |
| `vm.AddFilesAsync(...)` | `vm.AddPaths(...)` |
| `vm.Summary` | `vm.Status` |
| `PathRow` / `UnzipRow` | `ZipItemRow` |
| `UnzipRowStatus.X` | `ZipItemRowStatus.X` |

**Do not port** `UnzipViewModelTests`' non-zip-rejection fact (the one asserting a dropped `.pdf` is refused with "isn't a zip"). This tab accepts it by design; its Merge-tab counterpart survives in Task 3.

Then add these new facts:

```csharp
    /// <summary>The counts are the contract: a mixed list must report its two
    /// scopes independently, so neither button can be misread about what it
    /// will touch.</summary>
    [Fact]
    public async Task AMixedListCountsItemsAndZipsSeparately()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { pdf, zip });

        Assert.Equal("Zip 2 items", vm.ZipButtonText);
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);
        Assert.True(vm.ZipCommand.CanExecute(null));
        Assert.True(vm.ExtractCommand.CanExecute(null));
    }

    /// <summary>An archive is still a file, so Zip never excludes anything —
    /// bundling archives together is a real thing people do.</summary>
    [Fact]
    public async Task ZipIsEnabledByAnyNonEmptyListButExtractNeedsAZip()
    {
        using var dir = new TempDir();
        var vm = MakeVm();

        await vm.AddPaths(new[] { dir.File("a.pdf") });

        Assert.True(vm.ZipCommand.CanExecute(null));
        Assert.False(vm.ExtractCommand.CanExecute(null));
        Assert.Equal("Extract", vm.ExtractButtonText);
    }

    /// <summary>Extract must leave the loose files in a mixed list alone —
    /// the extractor is never even asked about them.</summary>
    [Fact]
    public async Task ExtractTouchesOnlyTheZipRows()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var asked = new List<string>();
        var vm = MakeVm(extractor: p =>
        {
            asked.Add(p);
            return new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "b"));
        });

        await vm.AddPaths(new[] { pdf, zip });
        await vm.ExtractAsync();

        Assert.Equal(new[] { zip }, asked);
        Assert.Equal(ZipItemRowStatus.Pending, vm.Rows.Single(r => r.Path == pdf).StatusKind);
    }

    /// <summary>A folder is a legitimate zip source and must not be mistaken
    /// for an archive because of its name.</summary>
    [Fact]
    public async Task AFolderNamedLikeAnArchiveIsStillAFolder()
    {
        using var dir = new TempDir();
        var folder = dir.Dir("bundle.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { folder });

        Assert.Equal("folder", vm.Rows.Single().Kind);
        Assert.False(vm.ExtractCommand.CanExecute(null));
    }
```

`MakeVm` and the `TempDir` helper: copy the existing ones from `ZipViewModelTests.cs` verbatim, widening `MakeVm` to take an optional `extractor` as well as the existing `dialogs` and `zipper`. If `ZipViewModelTests` has no `Dir()` helper on its temp-directory type, add one:

```csharp
    public string Dir(string name)
    {
        var p = Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`
Expected: FAIL — `error CS0246: The type or namespace name 'ZipExtractViewModel' could not be found`.

- [ ] **Step 3: Write `ZipExtractViewModel.cs`**

```csharp
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
        _extractor = extractor ?? Zipper.Extract;

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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q` then
`dotnet test tests/OrdoSort.Wpf.Tests --no-build --nologo --filter "FullyQualifiedName~ZipExtractViewModelTests"`
Expected: `Passed!` with 27 tests (11 ported from Zip + 12 ported from Unzip + 4 new).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs
git commit -m "feat(zip): the Zip and unzip tab chooses its action from the list"
```

---

### Task 3: `MergePdfsViewModel` — the Merge PDFs tab

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`
- Read for porting: `tests/OrdoSort.Wpf.Tests/ZipMergeViewModelTests.cs` (13 facts)

**Interfaces:**
- Consumes: `ZipListViewModel`, `ZipItemRow`, `ZipItemRowStatus` from Task 1.
- Produces: `MergePdfsViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, Func<string, ZipMerge.MergeResult>? merger = null)` with `MergeCommand`, `MergeButtonText`, `internal Task MergeAsync()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`. Port **all 13 facts** from `ZipMergeViewModelTests.cs` using the same rename table as Task 2 (`new ZipMergeViewModel(...)` → `new MergePdfsViewModel(...)`, `AddFilesAsync` → `AddPaths`, `Summary` → `Status`, `ZipRow` → `ZipItemRow`, `ZipRowStatus` → `ZipItemRowStatus`). **Keep** the non-zip-rejection fact — it is still correct on a tab that can only merge.

Add one new fact pinning the tabs' independence:

```csharp
    /// <summary>The two tabs' lists never interact — that separation is the
    /// whole reason Merge PDFs has its own tab rather than being a third
    /// button beside Extract.</summary>
    [Fact]
    public async Task ItsListIsIndependentOfTheZipAndUnzipTab()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var merge = MakeVm();
        var zipExtract = new ZipExtractViewModel(new FakeDialogs(), new InlineWorkScheduler(),
            extractor: p => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")));

        await merge.AddPaths(new[] { zip });
        await zipExtract.AddPaths(new[] { zip });
        await zipExtract.ExtractAsync();

        Assert.Equal(ZipItemRowStatus.Pending, merge.Rows.Single().StatusKind);
        Assert.True(merge.MergeCommand.CanExecute(null));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`
Expected: FAIL — `error CS0246: The type or namespace name 'MergePdfsViewModel' could not be found`.

- [ ] **Step 3: Write `MergePdfsViewModel.cs`**

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Merge PDFs tab: drop archives, and every PDF inside each one
/// is merged (natural-sorted by entry path) into a single &lt;zipname&gt;.pdf
/// beside it. Its own tab and its own list because it is a different job
/// wearing a zip costume — it consumes archives and produces a document —
/// and because separate lists mean extracting an archive on the other tab
/// has no bearing on merging it here.</summary>
public sealed class MergePdfsViewModel : ZipListViewModel
{
    private readonly Func<string, ZipMerge.MergeResult> _merger;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase).</summary>
    private static readonly ISet<string> Zips = new HashSet<string> { "zip" };

    public MergePdfsViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<string, ZipMerge.MergeResult>? merger = null)
        : base(scheduler, uiContext)
    {
        // dialogs is accepted for ctor-shape consistency with the sibling
        // tab; nothing here needs it, so there is no field to keep it in.
        _ = dialogs;
        _merger = merger ?? ZipMerge.MergeZip;

        MergeCommand = new AsyncRelayCommand(MergeAsync, () => Rows.Count > 0);
    }

    /// <summary>Archives only: this tab has nothing to offer anything else,
    /// so "that isn't a zip" is still the honest answer here.</summary>
    protected override ISet<string>? Extensions => Zips;

    protected override string IntakeNoun => "zip";

    protected override void OnRowsChanged()
    {
        Raise(nameof(MergeButtonText));
        MergeCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand MergeCommand { get; }

    /// <summary>Reflects the TOTAL row count, matching MergeCommand's own
    /// CanExecute: re-clicking after everything has run is harmless.</summary>
    public string MergeButtonText => Rows.Count switch
    {
        0 => "Merge",
        1 => "Merge 1 zip",
        var n => $"Merge {n} zips",
    };

    internal Task MergeAsync() => RunBatchAsync(
        _merger,
        r => r.Status,
        (row, r) => row.Apply(r),
        "Merging",
        new[] { ("ok", "merged"), ("no_pdfs", "had no PDFs"), ("error", "failed") });
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q` then
`dotnet test tests/OrdoSort.Wpf.Tests --no-build --nologo --filter "FullyQualifiedName~MergePdfsViewModelTests"`
Expected: `Passed!` with 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs
git commit -m "feat(zip): Merge PDFs becomes its own tab view model"
```

---

### Task 4: `ZipToolsWindow`

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs`
- Create: `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml`
- Create: `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs`
- Read as the model to copy: `src/OrdoSort.Wpf/Windows/UnzipWindow.xaml` (grid, columns, empty state), `src/OrdoSort.Wpf/Windows/ZipWindow.xaml` (Kind column, toolbar), `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (TabControl idiom)

**Interfaces:**
- Consumes: `ZipExtractViewModel` (Task 2), `MergePdfsViewModel` (Task 3).
- Produces: `ZipToolsViewModel(IDialogService dialogs, SynchronizationContext? uiContext = null)` exposing `ZipExtract` and `MergePdfs`; `ZipToolsWindow(ZipToolsViewModel vm)`.

- [ ] **Step 1: Write the shell view model**

Create `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs`:

```csharp
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The window's DataContext: one view model per tab, each owning
/// its own list. Deliberately holds no state of its own — the tabs do not
/// coordinate, which is what keeps one tab's results from affecting the
/// other's.</summary>
public sealed class ZipToolsViewModel
{
    public ZipExtractViewModel ZipExtract { get; }
    public MergePdfsViewModel MergePdfs { get; }

    public ZipToolsViewModel(IDialogService dialogs, SynchronizationContext? uiContext = null)
    {
        ZipExtract = new ZipExtractViewModel(dialogs, uiContext: uiContext);
        MergePdfs = new MergePdfsViewModel(dialogs, uiContext: uiContext);
    }

    public void Cancel()
    {
        ZipExtract.Cancel();
        MergePdfs.Cancel();
    }
}
```

- [ ] **Step 2: Write the window XAML**

Create `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml`. Copy `UnzipWindow.xaml`'s grid wholesale for each tab — its `DataGrid`, `RowStyle`, `ResultColumn` (with the `Error → StatusRed` trigger and, on the Merge tab, `NoPdfs → StatusAmber`), and the overlaid empty state — and copy the `Kind` column from `ZipWindow.xaml:119-134` into the Zip & unzip tab only. Both tabs' `Width="*"` first column keeps its `MinWidth="180"`, which `DataGridSizingCoverageTests.EveryStarColumnDeclaresItsOwnFloor` requires.

Skeleton (grids elided — copy them from the two sources named above):

```xml
<Window x:Class="OrdoSort.Wpf.Windows.ZipToolsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="OrdoSort — Zip and unzip" Width="700" Height="520" MinWidth="580" MinHeight="420"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False" AllowDrop="True"
        DragOver="OnDragOver" Drop="OnDrop"
        Style="{StaticResource {x:Type Window}}">
    <DockPanel Margin="14">
        <DockPanel DockPanel.Dock="Bottom" Margin="0,10,0,0">
            <Button DockPanel.Dock="Right" Content="Close" Width="96" IsCancel="True" />
            <!-- Footer swaps with the tab: each tab's own actions and its own
                 status line, so neither can act on the other's list. -->
            <Grid>
                <StackPanel Orientation="Horizontal"
                            Visibility="{Binding IsSelected, ElementName=ZipExtractTab,
                                         Converter={StaticResource BoolToVis}}">
                    <Button Command="{Binding ZipExtract.ZipCommand}"
                            Style="{StaticResource PrimaryButton}" MinWidth="110" Margin="0,0,8,0"
                            AutomationProperties.Name="{Binding ZipExtract.ZipButtonText}">
                        <TextBlock Text="{Binding ZipExtract.ZipButtonText}"
                                   Style="{StaticResource PrimaryButtonLabel}" />
                    </Button>
                    <Button Content="Zip to…" Command="{Binding ZipExtract.ZipAsCommand}" Margin="0,0,8,0" />
                    <Button Command="{Binding ZipExtract.ExtractCommand}" MinWidth="120" Margin="0,0,10,0"
                            AutomationProperties.Name="{Binding ZipExtract.ExtractButtonText}">
                        <TextBlock Text="{Binding ZipExtract.ExtractButtonText}" />
                    </Button>
                    <TextBlock Text="{Binding ZipExtract.Status}" VerticalAlignment="Center"
                               Style="{StaticResource StatusText}" MaxWidth="360" />
                </StackPanel>
                <StackPanel Orientation="Horizontal"
                            Visibility="{Binding IsSelected, ElementName=MergePdfsTab,
                                         Converter={StaticResource BoolToVis}}">
                    <Button Command="{Binding MergePdfs.MergeCommand}"
                            Style="{StaticResource PrimaryButton}" MinWidth="120" Margin="0,0,10,0"
                            AutomationProperties.Name="{Binding MergePdfs.MergeButtonText}">
                        <TextBlock Text="{Binding MergePdfs.MergeButtonText}"
                                   Style="{StaticResource PrimaryButtonLabel}" />
                    </Button>
                    <TextBlock Text="{Binding MergePdfs.Status}" VerticalAlignment="Center"
                               Style="{StaticResource StatusText}" MaxWidth="360" />
                </StackPanel>
            </Grid>
        </DockPanel>

        <TabControl x:Name="Tabs">
            <TabItem x:Name="ZipExtractTab" Header="_Zip &amp; unzip"
                     AutomationProperties.Name="Zip and unzip"
                     DataContext="{Binding ZipExtract}">
                <!-- toolbar + ItemsGrid + empty state, Margin="16,8" -->
            </TabItem>
            <TabItem x:Name="MergePdfsTab" Header="_Merge PDFs"
                     AutomationProperties.Name="Merge PDFs"
                     DataContext="{Binding MergePdfs}">
                <!-- toolbar + ZipsGrid + empty state, Margin="16,8" -->
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

Toolbar for the Zip & unzip tab (Zip's, the superset):

```xml
<DockPanel Margin="0,0,0,10">
    <TextBlock DockPanel.Dock="Right" Text="{Binding AddNote}"
               Style="{StaticResource CaptionText}" VerticalAlignment="Center"
               MaxWidth="240" TextTrimming="CharacterEllipsis" />
    <WrapPanel>
        <Button Content="Add files…" Click="OnAddFiles" Margin="0,0,6,4" />
        <Button Content="Add folder…" Click="OnAddFolder" Margin="0,0,6,4" />
        <Button Content="Remove selected" Click="OnRemoveSelected" Margin="0,0,6,4" />
        <Button Content="Clear" Command="{Binding ClearCommand}" Margin="0,0,10,4" />
    </WrapPanel>
</DockPanel>
```

The Merge PDFs toolbar is the same with "Add zips…" / "Remove selected" / "Clear" and `Click="OnAddZips"` / `Click="OnRemoveSelectedMerge"`.

Empty states: Zip & unzip gets "Drag files, folders or zips anywhere on this window, or press Add…"; Merge PDFs keeps "Drag zips anywhere on this window, or press Add zips…". Both use `Style="{StaticResource EmptyStateText}"` with the `ZeroToVis` binding on `Rows.Count`.

- [ ] **Step 3: Write the code-behind**

Create `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class ZipToolsWindow : Window
{
    private readonly ZipToolsViewModel _vm;

    public ZipToolsWindow(ZipToolsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ItemsGrid, ItemsResultColumn);
        DataGridColumnCap.Track(ZipsGrid, ZipsResultColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.ZipExtract.AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _ = _vm.ZipExtract.AddPaths(new[] { dlg.FolderName });
    }

    private void OnAddZips(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip archives (*.zip)|*.zip", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _ = _vm.MergePdfs.AddPaths(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.ZipExtract.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnRemoveSelectedMerge(object sender, RoutedEventArgs e) =>
        _vm.MergePdfs.RemoveSelected(ZipsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A drop lands on whichever tab is showing — the tab is the
    /// statement of intent, so routing anywhere else would silently put the
    /// files in a list the person is not looking at.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (ReferenceEquals(Tabs.SelectedItem, MergePdfsTab)) _ = _vm.MergePdfs.AddPaths(paths);
        else _ = _vm.ZipExtract.AddPaths(paths);
    }

    /// <summary>A closed window must not keep working invisibly: the work is
    /// async and owned by the view models rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
```

- [ ] **Step 4: Verify it builds and renders**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`
Expected: 0 errors.

Then add the window to `WindowOverflowTests.Registry()` (see Task 5 for the full registry edit — add just this entry now so the layout is proved before the cutover):

```csharp
        ["ZipToolsWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new ZipToolsViewModel(new FakeDialogs());
            return (new ZipToolsWindow(vm), null);
        }, ProbeEveryTab: true),
```

Run: `dotnet test tests/OrdoSort.Wpf.Tests --no-build --nologo --filter "FullyQualifiedName~WindowOverflowTests"`
Expected: `Passed!` — no element escapes either tab at either size.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs
git commit -m "feat(zip): one window, two tabs"
```

---

### Task 5: Cut over — menu, deletions, registries, end-to-end

This task is deliberately atomic: the moment the old windows are deleted, every registry naming them fails, so they move together or the build is red. Do all steps, then commit once.

**Files:**
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml:336-344`, `src/OrdoSort.Wpf/MainWindow.xaml.cs:343-353`
- Delete: `src/OrdoSort.Wpf/ViewModels/{Zip,Unzip,ZipMerge}ViewModel.cs`, `src/OrdoSort.Wpf/Windows/{Zip,Unzip,ZipMerge}Window.xaml`, `….xaml.cs`, `tests/OrdoSort.Wpf.Tests/{Zip,Unzip,ZipMerge}ViewModelTests.cs`
- Modify: `tests/OrdoSort.Wpf.Tests/DataGridWindowCoverageTests.cs:68-74,133-140`, `DataGridSizingCoverageTests.cs:57-77`, `AutoFitColumnTests.cs:1075-1250`, `DataGridSelectionContrastTests.cs:138-316`, `DataGridNoteColourTests.cs:259-390`
- Modify: `tools/OrdoSort.Smoke/E2E/Scenarios/{Zip,Unzip,ZipMerge}Scenarios.cs`, `ScenarioKit.cs:25-29`

**Interfaces:**
- Consumes: `ZipToolsWindow`, `ZipToolsViewModel` from Task 4.
- Produces: no new API.

- [ ] **Step 1: Replace the three menu entries with one**

In `MainWindow.xaml`, delete the three `MenuItem`s at 336-344 and put one in their place:

```xml
                <MenuItem Header="_Zip and unzip…" Click="OnZipTools">
                    <MenuItem.Icon><TextBlock Style="{StaticResource Icon}" Text="&#xE8B7;" /></MenuItem.Icon>
                </MenuItem>
```

In `MainWindow.xaml.cs`, delete `OnZipMerge`, `OnZip` and `OnUnzip`, and add:

```csharp
    private void OnZipTools(object sender, RoutedEventArgs e) =>
        new Windows.ZipToolsWindow(new ZipToolsViewModel(Dialogs, SynchronizationContext.Current))
            { Owner = this }.ShowDialog();
```

- [ ] **Step 2: Delete the superseded source and tests**

```bash
git rm src/OrdoSort.Wpf/ViewModels/ZipViewModel.cs src/OrdoSort.Wpf/ViewModels/UnzipViewModel.cs src/OrdoSort.Wpf/ViewModels/ZipMergeViewModel.cs
git rm src/OrdoSort.Wpf/Windows/ZipWindow.xaml src/OrdoSort.Wpf/Windows/ZipWindow.xaml.cs
git rm src/OrdoSort.Wpf/Windows/UnzipWindow.xaml src/OrdoSort.Wpf/Windows/UnzipWindow.xaml.cs
git rm src/OrdoSort.Wpf/Windows/ZipMergeWindow.xaml src/OrdoSort.Wpf/Windows/ZipMergeWindow.xaml.cs
git rm tests/OrdoSort.Wpf.Tests/ZipViewModelTests.cs tests/OrdoSort.Wpf.Tests/UnzipViewModelTests.cs tests/OrdoSort.Wpf.Tests/ZipMergeViewModelTests.cs
```

- [ ] **Step 3: Update the registries**

`DataGridWindowCoverageTests.cs` — in `CoveredWindows`, replace `"ZipWindow"`, `"ZipMergeWindow"`, `"UnzipWindow"` with `"ZipToolsWindow"`. Update the doc comment at 133-140: it names nine grid windows individually, and there are now seven (BulkRename, FilenameList, History, MatchMerge, PageCounts, Triage, ZipTools). The `>= 8` floor counts ALL window types (16 → 14) and is unaffected.

`DataGridSizingCoverageTests.cs` — remove `"ZipWindow"` from `KnownUncovered` and its reason line; remove `"ZipMergeWindow"` and `"UnzipWindow"` from `SizingCovered` and add `"ZipToolsWindow"`. The `>= 10` floor also counts all window types and is unaffected.

`AutoFitColumnTests.cs` — replace `BuildZipMergeWindow` and `BuildUnzipWindow` with one builder that selects the tab under test:

```csharp
    private static (ZipToolsWindow win, DataGrid grid) BuildZipToolsWindow(
        bool mergeTab, string resultValue, int rowCount)
    {
        var vm = new ZipToolsViewModel(new FakeDialogs());
        var list = mergeTab ? (ZipListViewModel)vm.MergePdfs : vm.ZipExtract;
        for (var i = 0; i < rowCount; i++)
        {
            var row = new ZipItemRow($@"C:\in\file{i}.zip", "zip");
            if (mergeTab) row.Apply(new ZipMerge.MergeResult(row.Path, "error", Message: resultValue));
            else row.Apply(new Zipper.UnzipResult(row.Path, "error", null, resultValue));
            list.Rows.Add(row);
        }
        var win = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        // a TabControl realises only the selected tab's content
        win.Tabs.SelectedIndex = mergeTab ? 1 : 0;
        win.UpdateLayout();
        PumpRender();
        win.UpdateLayout();
        return (win, mergeTab ? win.ZipsGrid : win.ItemsGrid);
    }
```

Keep the `0.45` share constants; retarget the six facts onto the two tabs (short value measures narrow, long value stops at the cap with ellipsis and tooltip, no horizontal scrollbar at MinWidth — three per tab).

`DataGridSelectionContrastTests.cs` — replace the three builders with one that takes a `mergeTab` flag and selects the tab (same shape as above); the six theories become two, each asserting every column's foreground clears 4.5:1 on `Accent` (selected) and `Surface` (unselected) for its tab.

`DataGridNoteColourTests.cs` — the two assert helpers become one taking the tab flag. The Error fact runs against both tabs; the NoPdfs fact runs against the Merge tab only. Both resolve the cell by header string `"Result"`, which is unchanged.

- [ ] **Step 4: Re-point the end-to-end scenarios**

In `ZipScenarios.cs`, `UnzipScenarios.cs` and `ZipMergeScenarios.cs`, keep every scenario and every `Surface` string exactly as they are — `e2e.bat zip` and the exact-match-first filter depend on them. Change only the construction: `new ZipViewModel(...)` and `new UnzipViewModel(...)` become `vm.ZipExtract` off a `ZipToolsViewModel`, `new ZipMergeViewModel(...)` becomes `vm.MergePdfs`, and each scenario that opens a window opens `ZipToolsWindow` and sets `win.Tabs.SelectedIndex` (0 for Zip and Unzip, 1 for Zip merge) before driving it. `vm.Summary` becomes `vm.Status`; `AddFilesAsync`/`AddPaths` both become `AddPaths`.

In `ScenarioKit.cs:25-29`, the class doc names three intake methods as the reason `Added()` was removed — there is now one, `AddPaths`. Reword that sentence.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false --nologo -v q`
Expected: 0 errors.

Run: `dotnet test OrdoSort.sln --no-build --nologo`
Expected: `Passed!` on both assemblies. Core stays at 608. WPF lands near 1726 — the three deleted suites (37 facts) are replaced by 41 (11 row + 27 zip/extract + 14 merge, minus the one retired rejection fact), and the registry consolidations remove a few theory cases.

Confirm the counts rather than trusting the exit code.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(zip)!: three zip windows become one, with two tabs"
```

---

### Task 6: Documentation

**Files:**
- Modify: `README.md:105`
- Modify: `e2e.bat:3-6`

**Interfaces:**
- Consumes: the shipped window from Task 5.
- Produces: nothing.

- [ ] **Step 1: Update the README feature list**

Line ~105 currently reads:

> Also *Filename list*, *PDF page counts*, *List reformatter*, *Merge PDFs from zip*, *Zip* and *Unzip*.

Replace the three zip names with the one tool:

> Also *Filename list*, *PDF page counts*, *List reformatter*, and *Zip and unzip* — which also merges the PDFs inside an archive, on its own tab.

Leave the `e2e.bat zip` note (~151) alone: the three end-to-end surfaces survive, so it is still accurate.

- [ ] **Step 2: Update the e2e.bat header**

The header comment says the runner drives "14 surfaces". The surface count is unchanged — the three zip surfaces still exist — so verify the number is still right and leave it if so. Only correct it if `E2ERunner.AllScenarios()` gained or lost a group.

- [ ] **Step 3: Verify no stale references remain in shipping code**

```bash
grep -rn "ZipWindow\|UnzipWindow\|ZipMergeWindow\|ZipViewModel\|UnzipViewModel\|ZipMergeViewModel" src/ tests/ tools/ README.md e2e.bat
```

Expected: no hits. Older specs, plans and audits under `docs/superpowers/` legitimately name them as historical record and are excluded from this check.

- [ ] **Step 4: Commit**

```bash
git add README.md e2e.bat
git commit -m "docs: one zip tool in the feature list"
```

---

## Self-Review

**Spec coverage.** Every spec section maps to a task: behaviour and enablement → Tasks 2 and 3 (button texts, `PendingZips`, the new facts); permissive intake and the retired fact → Task 2; the Merge tab keeping its rejection → Task 3; `ZipItemRow`'s field union and both `Apply` overloads → Task 1; the base class and shared batch runner → Task 1; the two subclasses → Tasks 2 and 3; window, tabs, sizes, empty states, `DataGridColumnCap` → Task 4; menu, deletions, six registries, end-to-end → Task 5; docs → Task 6.

**Type consistency.** `ZipItemRow`/`ZipItemRowStatus`/`ZipListViewModel`/`ZipExtractViewModel`/`MergePdfsViewModel`/`ZipToolsViewModel`/`ZipToolsWindow` are used identically in every task. `AddPaths` is the single intake name throughout (no `AddFilesAsync` survives). `Status` is the single line name (no `Summary` survives). Grid names `ItemsGrid`/`ZipsGrid` and column names `ItemsResultColumn`/`ZipsResultColumn` are introduced in Task 4 and consumed unchanged in Task 5.

**One deliberate deviation from the plan format.** Tasks 2 and 3 port 37 existing test facts rather than reproducing them inline; the plan gives the exact source files and a complete rename table instead. Reproducing them verbatim would add roughly 700 lines to this document without adding information — the source is in the repo and the executor reads it. Every *new* fact is written out in full.
