# Merging documents, spreadsheets, slides, images and text with PDFs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the Merge PDFs window accepts Word documents, spreadsheets, slide decks, images, text files and CSVs beside PDFs and zips, converts each to PDF pages, and merges them into the same output — loose and inside archives — with a row of per-type toggles saying which take part.

**Architecture:** Core gains a byte-in/byte-out `IDocumentConverter` contract, a pure paginator, and two Office-free converters (tables, text). Wpf ships an image converter over WPF's own decoders and an Office converter over late-bound COM, composed into a chain that prefers Office and never silently downgrades. `PdfMerge` asks the chain to turn any non-PDF into pages, then feeds the result through the same `AddPdf` a real PDF takes. Toggles narrow which types are selected into a unit, in the window and inside archives alike.

**Tech Stack:** .NET 8, PdfSharp 6.1.1 and WPF's `BitmapDecoder` (both already present), late-bound COM via `Type.GetTypeFromProgID`. **No new packages.**

**Spec:** `docs/superpowers/specs/2026-08-30-office-documents-in-merge-design.md` — read it, including its Amendment section; it carries the decisions, the type table and the rejected alternatives this plan argues from.

**Branch:** create `feature/office-docs-in-merge` off `main`. Do not work on `main`.

## Global Constraints

- **Check command** (repo root, before every commit; BOTH `Passed!` lines must appear, totals at or above baseline — Core **750**, Wpf **2014**):
  ```
  dotnet build OrdoSort.sln -t:Rebuild -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  - **Smart App Control wears three disguises** (`0x800711C7`): an assembly silently `Skipping`ed with **no `Passed!` line**; `FileLoadException … blocked this file` surfacing *inside* individual tests, which reads as real failures; and the block hopping to a different assembly after each rebuild. `Directory.Build.targets`' non-determinism does not reliably clear it. What works: delete every `bin`/`obj` under `src/`, `tests/`, `tools/`, build once, run Core; then `dotnet build tests/OrdoSort.Wpf.Tests -t:Rebuild` and run Wpf alone. Two runs covering both assemblies on the same sources is acceptable evidence — say so in the report. **A missing `Passed!` line is a FAILED check.**
  - `XamlParseException` burst → `dotnet build-server shutdown`, rebuild, rerun. MSB3027 → kill the stale `testhost.exe`.
  - Known intermittent `HeaderLayoutTests` stall → rerun with `--blame-hang --blame-hang-timeout 180s --blame-hang-dump-type none` and READ THE TOTAL.
  - Known flakes in `docs/known-flakes.md`: `BulkRenameBatchTests.UndoHandsTheFileWorkToTheSchedulerInsteadOfDoingItOnTheClick`, `BulkRenameProbeTests.ADiscreteToggleResolvesWithoutWaitingTheFullDebounceWindow`. Re-run in isolation, report, never chase or weaken.
  - Run every test command in the **foreground**. Do not background a test run; nothing will notify you.
- **The type groups, exactly** (the spec's table is the authority; this is the same list for quick reference):
  | Group | Extensions |
  |---|---|
  | `pdf` | pdf |
  | `zip` | zip |
  | `word` | docx, doc, docm, rtf, odt, htm, html |
  | `excel` | xlsx, xls, xlsm, ods, csv, tsv |
  | `powerpoint` | pptx, ppt |
  | `images` | jpg, jpeg, png, tif, tiff, bmp, gif |
  | `text` | txt, log, md, json |
- **Revert-proof:** every behavioural fact must fail for a VALUE reason when its named production line is broken. Break it, see the failure, restore, record the message. A fact that passes with the fix removed is a defect in the fact.
- **Never throws:** `PdfMerge` promises every failure comes back as a `MergeResult`. `IDocumentConverter` implementations inherit that promise — a converter that throws is a defect.
- **The in-repo converters never prompt.** Only the Office path participates in the password contract; the others have no decryptor, so a protected file reports `error` naming the reason rather than raising a prompt that cannot help.
- **No silent downgrade.** If Office handles a type and fails, that failure stands — the chain must not fall through to a lesser in-repo rendering.
- No new NuGet packages. No changes to `Theme/Styles.xaml`. Do not touch TriageWindow, FilenameListWindow, or the Zip and unzip window.
- C# style per the repo: XML doc comments explaining WHY on public/internal surfaces; `_camelCase` private fields; no single-letter names except loop indices.
- Every commit carries these trailer lines after a blank line:
  ```
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
  ```
  Use a here-doc or a temp message file so multi-line messages survive the shell; verify with `git log -1 --format=%B`. Commit messages explain WHY.

## File structure

| File | Responsibility |
|---|---|
| `src/OrdoSort.Core/DocumentConverter.cs` (new) | `IDocumentConverter`, `ConversionResult`, and the group→extension table. |
| `src/OrdoSort.Core/TablePages.cs` (new) | Pure pagination: table + page geometry → column groups and row ranges. No PdfSharp. |
| `src/OrdoSort.Core/TableToPdf.cs` (new) | csv/tsv/xlsx via the existing readers. |
| `src/OrdoSort.Core/TextToPdf.cs` (new) | txt/log/md/json — the same paginator, one column. |
| `src/OrdoSort.Core/XlsxTable.cs` | Gains a `Stream` overload so bytes are read without a temp file. |
| `src/OrdoSort.Core/PdfMerge.cs` | Both merge paths take a converter and an enabled-type set. |
| `src/OrdoSort.Wpf/Services/ImageToPdf.cs` (new) | Images via `BitmapDecoder`; multi-frame TIFF → a page per frame. |
| `src/OrdoSort.Wpf/Services/OfficeConverter.cs` (new) | Word, Excel and PowerPoint over late-bound COM; temp-file and process discipline. |
| `src/OrdoSort.Wpf/Services/ConverterChain.cs` (new) | Office first; no silent downgrade. |
| `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` | Accepted types, toggles, converter composition, probe-on-add. |
| `src/OrdoSort.Wpf/ViewModels/ZipItemRow.cs` | `KindOf` learns the new kinds; `IsIncluded`. |
| `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml` | The toggle row. |
| `src/OrdoSort.Core/Config.cs` | `merge_types` persistence. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs` | Converted document, image, and a type switched off. |

---

### Task 1: Spike — prove the COM assumptions before building on them

**This task's output is an answer, not code.** Everything it writes is throwaway and deleted before it reports. If either assumption fails, STOP and report rather than proceeding.

**Files:** create and then delete `S:\tmp\office-spike\` (a console app targeting `net8.0-windows`).

- [ ] **Step 1: Build fixtures with Office itself**

```powershell
$w = New-Object -ComObject Word.Application; $w.Visible = $false
$d = $w.Documents.Add(); $d.Content.Text = "Spike fixture. Second line."
$d.SaveAs2("S:\tmp\office-spike\plain.docx"); $d.Close()
$p = $w.Documents.Add(); $p.Content.Text = "Protected fixture."
$p.SaveAs2("S:\tmp\office-spike\locked.docx", [Type]::Missing, $false, "secret"); $p.Close()
$w.Quit()
$pp = New-Object -ComObject PowerPoint.Application
$pres = $pp.Presentations.Add($false)
$null = $pres.Slides.Add(1, 11)
$pres.SaveAs("S:\tmp\office-spike\deck.pptx"); $pres.Close(); $pp.Quit()
```
Confirm all three exist and that `locked.docx` really asks for a password when opened by hand.

- [ ] **Step 2: Convert the plain document, late-bound**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

var type = Type.GetTypeFromProgID("Word.Application")
    ?? throw new InvalidOperationException("Word.Application is not registered");
var sw = Stopwatch.StartNew();
dynamic app = Activator.CreateInstance(type)!;
Console.WriteLine($"word cold start: {sw.ElapsedMilliseconds} ms");
app.Visible = false; app.DisplayAlerts = 0; app.AutomationSecurity = 3;

sw.Restart();
dynamic doc = app.Documents.Open(@"S:\tmp\office-spike\plain.docx",
    ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
    PasswordDocument: "an-unlikely-sentinel-3f9c", Visible: false);
doc.ExportAsFixedFormat(@"S:\tmp\office-spike\plain.pdf", 17);   // wdExportFormatPDF
doc.Close(false);
Console.WriteLine($"convert: {sw.ElapsedMilliseconds} ms, " +
    $"bytes: {new FileInfo(@"S:\tmp\office-spike\plain.pdf").Length}");
```

**The question this answers:** does a *sentinel* password break the open of an UNprotected document? It must not — Word is expected to ignore `PasswordDocument` when the file needs none. If this open fails, the sentinel approach is wrong and the design changes.

- [ ] **Step 3: Confirm the sentinel fails fast on the protected one**

```csharp
sw.Restart();
try
{
    dynamic locked = app.Documents.Open(@"S:\tmp\office-spike\locked.docx",
        ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
        PasswordDocument: "an-unlikely-sentinel-3f9c", Visible: false);
    Console.WriteLine("OPENED WITH THE SENTINEL — the design's premise is wrong");
    locked.Close(false);
}
catch (COMException ex)
{
    Console.WriteLine($"refused in {sw.ElapsedMilliseconds} ms: 0x{ex.HResult:X8} {ex.Message}");
}
```
**Record the HRESULT** — Task 6 needs it to tell "wrong password" from "corrupt file". If this HANGS (no output for 30s) the sentinel does not work: kill it, report, stop. Then confirm `PasswordDocument: "secret"` opens it.

- [ ] **Step 4: PowerPoint through the same shape**

```csharp
var ppType = Type.GetTypeFromProgID("PowerPoint.Application")!;
sw.Restart();
dynamic pp = Activator.CreateInstance(ppType)!;
Console.WriteLine($"powerpoint cold start: {sw.ElapsedMilliseconds} ms");
// PowerPoint refuses Visible=false in some builds — record what happens.
try { pp.Visible = false; } catch (Exception ex) { Console.WriteLine($"pp.Visible=false refused: {ex.Message}"); }
dynamic pres = pp.Presentations.Open(@"S:\tmp\office-spike\deck.pptx",
    ReadOnly: true, Untitled: false, WithWindow: false);
pres.ExportAsFixedFormat(@"S:\tmp\office-spike\deck.pdf", 2);   // ppFixedFormatTypePDF
pres.Close();
Console.WriteLine($"deck bytes: {new FileInfo(@"S:\tmp\office-spike\deck.pdf").Length}");
```
PowerPoint historically objects to a hidden application window. **Whatever it does, record it** — Task 6's adapter has to live with the answer.

- [ ] **Step 5: Prove the processes can be cleaned up**

```csharp
int pid = 0;
GetWindowThreadProcessId((IntPtr)app.Hwnd, out pid);
app.Quit(); Marshal.FinalReleaseComObject(app);
pp.Quit(); Marshal.FinalReleaseComObject(pp);
GC.Collect(); GC.WaitForPendingFinalizers();
Thread.Sleep(1500);
Console.WriteLine($"word {pid} still running: {Process.GetProcesses().Any(p => p.Id == pid)}");
foreach (var name in new[] { "WINWORD", "EXCEL", "POWERPNT" })
    Console.WriteLine($"{name}: {Process.GetProcessesByName(name).Length}");

[DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
```

- [ ] **Step 6: Report, and delete everything**

```bash
rm -rf "S:/tmp/office-spike"
```
Report: cold-start ms per app, per-convert ms, the sentinel's behaviour on both documents, the wrong-password HRESULT, whether PowerPoint accepted a hidden window, and whether any Office process survived. **Nothing is committed.**

---

### Task 2: The contract, the type table, and pagination as a pure function

**Files:**
- Create: `src/OrdoSort.Core/DocumentConverter.cs`, `src/OrdoSort.Core/TablePages.cs`
- Test: `tests/OrdoSort.Core.Tests/TablePagesTests.cs`, `tests/OrdoSort.Core.Tests/MergeTypesTests.cs`

**Interfaces:**
- Produces: `IDocumentConverter`, `ConversionResult`, `MergeTypes` (Tasks 3–9); `TablePages.Paginate`, `TablePage` (Tasks 3, 5).

- [ ] **Step 1: Write the contract and the group table**

`src/OrdoSort.Core/DocumentConverter.cs`:

```csharp
namespace OrdoSort.Core;

/// <summary>Turns a document that is not a PDF into one, so
/// <see cref="PdfMerge"/> can merge it like any other.
///
/// Bytes in, bytes out, deliberately: PdfMerge already buffers every source
/// in memory, and its ZipSlip immunity rests on the rule that a zip entry's
/// own name never reaches a filesystem API. An implementation that needs a
/// real file on disk (Office can only open one) writes a temp file under a
/// name IT generates — never the entry's — and deletes it.
///
/// Implementations inherit PdfMerge's promise: never throw. Every failure
/// comes back as a <see cref="ConversionResult"/>.</summary>
public interface IDocumentConverter
{
    /// <param name="extension">Dot-less and lowercase, as Intake produces.</param>
    bool Handles(string extension);

    /// <summary>Convert, asking for a password the way the rest of the app
    /// does. <paramref name="candidates"/> are tried before
    /// <paramref name="ask"/> is called at all; a null ask means "never
    /// prompt".</summary>
    ConversionResult ToPdf(byte[] source, string displayName,
                           IReadOnlyList<string> candidates,
                           Func<PasswordRequest, string?>? ask);
}

/// <summary><see cref="Status"/> is "ok" | "needs_password" | "unsupported"
/// | "error". "unsupported" is a converter-internal signal meaning "not
/// mine" — what lets a chain fall through to the next implementation. It is
/// never a user-facing outcome: when NOTHING handles a type, the merge
/// reports "error" naming why.</summary>
public sealed record ConversionResult(string Status, byte[]? Pdf,
                                      string Message = "", string? Item = null);

/// <summary>The file types the merge window can take, grouped the way the
/// user switches them on and off. Groups rather than extensions are what is
/// stored and toggled, so adding an extension to a group later needs no
/// config migration.</summary>
public static class MergeTypes
{
    public const string Pdf = "pdf", Zip = "zip", Word = "word",
        Excel = "excel", PowerPoint = "powerpoint", Images = "images", Text = "text";

    private static readonly Dictionary<string, string[]> ByGroup = new(StringComparer.OrdinalIgnoreCase)
    {
        [Pdf] = ["pdf"],
        [Zip] = ["zip"],
        [Word] = ["docx", "doc", "docm", "rtf", "odt", "htm", "html"],
        [Excel] = ["xlsx", "xls", "xlsm", "ods", "csv", "tsv"],
        [PowerPoint] = ["pptx", "ppt"],
        [Images] = ["jpg", "jpeg", "png", "tif", "tiff", "bmp", "gif"],
        [Text] = ["txt", "log", "md", "json"],
    };

    /// <summary>Every group, in the order the window shows them.</summary>
    public static IReadOnlyList<string> AllGroups { get; } =
        [Pdf, Zip, Word, Excel, PowerPoint, Images, Text];

    public static IReadOnlyList<string> ExtensionsOf(string group) =>
        ByGroup.TryGetValue(group, out var list) ? list : Array.Empty<string>();

    /// <summary>The group a file belongs to, or null when this window cannot
    /// merge it at all (an .exe, a .mp4) — which is a refusal at intake, not
    /// a toggle.</summary>
    public static string? GroupOf(string extension)
    {
        foreach (var (group, extensions) in ByGroup)
            if (extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return group;
        return null;
    }

    /// <summary>Every extension of every group — the set Intake accepts.</summary>
    public static ISet<string> AllExtensions { get; } =
        new HashSet<string>(ByGroup.Values.SelectMany(e => e), StringComparer.OrdinalIgnoreCase);

    /// <summary>Round-trips the enabled groups through config's existing
    /// comma-list convention (see Config's monitored-folder "filetypes").
    /// Unknown names are dropped rather than failing: a config written by a
    /// later version must not break an earlier one.</summary>
    public static string Save(IEnumerable<string> groups) => string.Join(",", groups);

    public static ISet<string> Load(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? new HashSet<string>(AllGroups, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(g => ByGroup.ContainsKey(g)),
                StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Write `MergeTypesTests`**

```csharp
public class MergeTypesTests
{
    [Theory]
    [InlineData("pdf", "pdf")] [InlineData("docx", "word")] [InlineData("rtf", "word")]
    [InlineData("csv", "excel")] [InlineData("xlsx", "excel")] [InlineData("pptx", "powerpoint")]
    [InlineData("TIF", "images")] [InlineData("json", "text")]
    public void EveryHandledExtensionKnowsItsGroup(string extension, string group) =>
        Assert.Equal(group, MergeTypes.GroupOf(extension));

    [Theory]
    [InlineData("exe")] [InlineData("mp4")] [InlineData("")]
    public void AForeignTypeHasNoGroup(string extension) =>
        Assert.Null(MergeTypes.GroupOf(extension));

    [Fact]
    public void NoExtensionBelongsToTwoGroups()
    {
        var all = MergeTypes.AllGroups.SelectMany(MergeTypes.ExtensionsOf).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NothingStoredMeansEverythingIsOn() =>
        Assert.Equal(MergeTypes.AllGroups.OrderBy(g => g),
                     MergeTypes.Load(null).OrderBy(g => g));

    [Fact]
    public void TheEnabledSetSurvivesARoundTrip()
    {
        var chosen = new[] { MergeTypes.Pdf, MergeTypes.Images };
        Assert.Equal(chosen.OrderBy(g => g),
                     MergeTypes.Load(MergeTypes.Save(chosen)).OrderBy(g => g));
    }

    [Fact]
    public void AGroupNameFromALaterVersionIsIgnoredRatherThanBreakingTheLoad() =>
        Assert.Equal(new[] { "pdf" }, MergeTypes.Load("pdf,hologram"));

    [Fact]
    public void EverythingOffIsDistinguishableFromNothingStored() =>
        Assert.Empty(MergeTypes.Load("  "));   // NOTE: see step 3
}
```

**Ruling the implementer must apply:** the last fact and `NothingStoredMeansEverythingIsOn` conflict — `"  "` is whitespace, which `IsNullOrWhiteSpace` treats as "nothing stored". "Everything off" must be storable and distinguishable from "never set", or a user who unticks every box gets everything back on restart. Resolve it by storing a sentinel: `Save` of an empty set writes `"none"`, and `Load("none")` returns an empty set. Add that to `MergeTypes` and to the two facts. Record the decision in the report.

- [ ] **Step 3: Write the failing pagination tests**

`tests/OrdoSort.Core.Tests/TablePagesTests.cs` — `measure` is injected so these are exact arithmetic, not font-dependent:

```csharp
public class TablePagesTests
{
    private static readonly Func<string, double> Measure = s => s.Length;

    private static List<List<string>> Table(params string[][] rows) =>
        rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void ASmallTableIsOnePageWithColumnsSizedToTheirWidestCell()
    {
        var pages = TablePages.Paginate(Table(["id", "name"], ["1", "Alice"], ["2", "Bo"]),
            pageWidth: 100, pageHeight: 100, rowHeight: 10, Measure);
        var page = Assert.Single(pages);
        Assert.Equal(new[] { 0, 1 }, page.Columns);
        Assert.Equal(new[] { 2.0, 5.0 }, page.Widths);
        Assert.Equal(new[] { 1, 2 }, page.Rows);
    }

    [Fact]
    public void ATableTallerThanThePageSplitsWithEveryRowAppearingExactlyOnce()
    {
        var rows = new List<List<string>> { ["h"] };
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        var pages = TablePages.Paginate(rows, 100, 100, 10, Measure);
        Assert.Equal(3, pages.Count);              // 9 body rows a page
        Assert.Equal(Enumerable.Range(1, 20), pages.SelectMany(p => p.Rows));
    }

    [Fact]
    public void EveryPageCarriesTheHeaderRow()
    {
        var rows = new List<List<string>> { ["h"] };
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        Assert.All(TablePages.Paginate(rows, 100, 100, 10, Measure), p => Assert.Equal(0, p.HeaderRow));
    }

    [Fact]
    public void ATableWiderThanThePageSplitsIntoColumnGroups()
    {
        var wide = new string('x', 30);
        var pages = TablePages.Paginate(Table([wide, wide, wide, wide], [wide, wide, wide, wide]),
            100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pages[0].Columns);
        Assert.Equal(new[] { 3 }, pages[1].Columns);
    }

    [Fact]
    public void ATableBothTooTallAndTooWideGivesAPagePerGroupPerRowRange()
    {
        var wide = new string('x', 30);
        var rows = new List<List<string>> { [wide, wide, wide, wide] };
        for (var i = 0; i < 20; i++) rows.Add([wide, wide, wide, wide]);
        Assert.Equal(6, TablePages.Paginate(rows, 100, 100, 10, Measure).Count);
    }

    [Fact]
    public void AColumnWiderThanThePageStillGetsAPageToItself()
    {
        // The guard against an infinite loop: a group is never empty.
        var huge = new string('x', 500);
        var pages = TablePages.Paginate(Table([huge, "b"], [huge, "b"]), 100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0 }, pages[0].Columns);
    }

    [Fact]
    public void RaggedRowsArePaddedRatherThanCrashing()
    {
        var pages = TablePages.Paginate(Table(["a", "b", "c"], ["1"], ["1", "2", "3"]),
            1000, 1000, 10, Measure);
        Assert.Equal(3, Assert.Single(pages).Columns.Count);
    }

    [Fact]
    public void AnEmptyTableProducesNoPages() =>
        Assert.Empty(TablePages.Paginate(new List<List<string>>(), 100, 100, 10, Measure));

    [Fact]
    public void AHeaderOnlyTableStillProducesOnePage() =>
        Assert.Empty(Assert.Single(TablePages.Paginate(Table(["a", "b"]), 100, 100, 10, Measure)).Rows);
}
```

- [ ] **Step 4: Run them to see them fail** — `dotnet build tests/OrdoSort.Core.Tests -v quiet`, compile error.

- [ ] **Step 5: Write the paginator**

`src/OrdoSort.Core/TablePages.cs`:

```csharp
namespace OrdoSort.Core;

/// <summary>One page of a table: which source columns it shows, how wide
/// each is, and which source rows fall on it. <see cref="HeaderRow"/> is
/// repeated at the top of every page — a page of values with no headings is
/// unreadable.</summary>
public sealed record TablePage(
    IReadOnlyList<int> Columns, IReadOnlyList<double> Widths,
    IReadOnlyList<int> Rows, int HeaderRow = 0);

/// <summary>Laying a table onto pages, as arithmetic — no PdfSharp, no
/// fonts, no I/O, so it can be checked exhaustively. The same compose/draw
/// seam <see cref="BoxLabels.ComposeDrawing"/> uses: this decides, the
/// renderer draws.</summary>
public static class TablePages
{
    /// <summary>Split <paramref name="table"/> into pages that fit
    /// <paramref name="pageWidth"/> x <paramref name="pageHeight"/>.
    ///
    /// Columns are sized to their widest cell, measured by
    /// <paramref name="measure"/> — injected so a test can use character
    /// counts and a renderer can use real text metrics. Columns that do not
    /// all fit are split into consecutive groups, each repeated down the
    /// table's rows, so a wide sheet reads across then onward rather than
    /// being silently cropped. A single column too wide for the page still
    /// takes a page of its own: a group is never empty, which is also what
    /// stops this looping forever.</summary>
    public static IReadOnlyList<TablePage> Paginate(
        IReadOnlyList<IReadOnlyList<string>> table,
        double pageWidth, double pageHeight, double rowHeight,
        Func<string, double> measure)
    {
        if (table.Count == 0) return Array.Empty<TablePage>();
        var columnCount = table.Max(r => r.Count);
        if (columnCount == 0) return Array.Empty<TablePage>();

        var widths = new double[columnCount];
        foreach (var row in table)
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], measure(row[c] ?? ""));

        var groups = new List<List<int>>();
        var current = new List<int>();
        var used = 0.0;
        for (var c = 0; c < columnCount; c++)
        {
            if (current.Count > 0 && used + widths[c] > pageWidth)
            {
                groups.Add(current);
                current = new List<int>();
                used = 0;
            }
            current.Add(c);
            used += widths[c];
        }
        groups.Add(current);

        // One header row is repeated on every page, so the body gets what is
        // left of the height.
        var bodyRowsPerPage = Math.Max(1, (int)(pageHeight / rowHeight) - 1);
        var bodyRows = Enumerable.Range(1, table.Count - 1).ToList();

        var pages = new List<TablePage>();
        foreach (var group in groups)
        {
            var groupWidths = group.Select(c => widths[c]).ToList();
            if (bodyRows.Count == 0)
            {
                pages.Add(new TablePage(group, groupWidths, Array.Empty<int>()));
                continue;
            }
            for (var start = 0; start < bodyRows.Count; start += bodyRowsPerPage)
                pages.Add(new TablePage(group, groupWidths,
                    bodyRows.Skip(start).Take(bodyRowsPerPage).ToList()));
        }
        return pages;
    }
}
```

- [ ] **Step 6: Run both suites** — `--filter "FullyQualifiedName~TablePagesTests|FullyQualifiedName~MergeTypesTests"`, all green.

- [ ] **Step 7: Revert-proof**

Break `if (current.Count > 0 && …)` to `if (used + widths[c] > pageWidth)` → `AColumnWiderThanThePageStillGetsAPageToItself` fails. Restore. Break `Math.Max(1, …)` → the tall-table facts fail. Restore. Break `GroupOf` to return the first group unconditionally → `EveryHandledExtensionKnowsItsGroup` fails. Restore. Record all three messages.

- [ ] **Step 8: Full check, then commit**

```
feat(convert): the converter contract, the type groups, and pagination as arithmetic
```
Explain WHY in the body: bytes-in/bytes-out preserves PdfMerge's ZipSlip rule; groups rather than extensions are stored so a later version can add an extension without a config migration; pagination is split from drawing so the awkward cases are checkable with a calculator.

---

### Task 3: `TableToPdf` and `TextToPdf` — the Office-free converters

**Files:**
- Modify: `src/OrdoSort.Core/XlsxTable.cs` (add a `Stream` overload)
- Create: `src/OrdoSort.Core/TableToPdf.cs`, `src/OrdoSort.Core/TextToPdf.cs`
- Test: `tests/OrdoSort.Core.Tests/TableToPdfTests.cs`, `tests/OrdoSort.Core.Tests/TextToPdfTests.cs`

**Interfaces:** consumes `IDocumentConverter`, `ConversionResult`, `TablePages.Paginate` (Task 2) and `Csv.Parse`/`Csv.ReadText`/`XlsxTable.Read` (existing, internal, same assembly). Produces `TableToPdf` and `TextToPdf`, public with parameterless constructors.

- [ ] **Step 1: Give `XlsxTable` a stream overload**

Keep `Read(path)` as the thin wrapper so every existing caller is untouched:

```csharp
    internal static List<List<string>> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>The same reader over an open stream — what lets a converter
    /// read an xlsx out of a zip entry's bytes without writing a temp
    /// file.</summary>
    internal static List<List<string>> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        // ... the existing body, unchanged, against `zip` ...
    }
```
`ZipFile.OpenRead` already returns a `ZipArchive`, so the body needs no other change.

- [ ] **Step 2: Write the failing tests**

`TableToPdfTests` — the round trip and, more importantly, the refusals:

```csharp
public class TableToPdfTests
{
    private static readonly TableToPdf Converter = new();

    private static int PageCountOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    [Theory]
    [InlineData("csv", true)] [InlineData("tsv", true)] [InlineData("xlsx", true)]
    [InlineData("docx", false)] [InlineData("pdf", false)] [InlineData("png", false)]
    public void HandlesOnlyWhatItCanRead(string extension, bool handled) =>
        Assert.Equal(handled, Converter.Handles(extension));

    [Fact]
    public void ACsvBecomesAReadablePdf()
    {
        var r = Converter.ToPdf("id,name\n1,Alice\n2,Bo\n"u8.ToArray(),
            "people.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, PageCountOf(r.Pdf!));
    }

    [Fact]
    public void AQuotedFieldWithACommaAndANewlineSurvives()
    {
        var r = Converter.ToPdf("id,note\n1,\"Smith, John\nsecond line\"\n"u8.ToArray(),
            "notes.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
    }

    [Fact]
    public void ALongCsvRunsToSeveralPages()
    {
        var rows = new List<string> { "id,name" };
        for (var i = 0; i < 500; i++) rows.Add($"{i},Name {i}");
        var r = Converter.ToPdf(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", rows)),
            "long.csv", Array.Empty<string>(), null);
        Assert.True(PageCountOf(r.Pdf!) > 5, $"500 rows fitted on {PageCountOf(r.Pdf!)} page(s)");
    }

    [Fact]
    public void AnEmptyFileIsAnErrorNotAnEmptyPdf()
    {
        var r = Converter.ToPdf(Array.Empty<byte>(), "empty.csv", Array.Empty<string>(), null);
        Assert.Equal("error", r.Status);
        Assert.Contains("nothing", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWordDocumentIsNotItsToConvert() =>
        Assert.Equal("unsupported",
            Converter.ToPdf([1, 2, 3], "letter.docx", Array.Empty<string>(), null).Status);

    [Fact]
    public void AProtectedSpreadsheetSaysSoRatherThanAskingForAPasswordItCannotUse()
    {
        // An encrypted xlsx is an OLE compound file, not a zip — the reader
        // cannot open it, and no password would help HERE, so this must not
        // come back as needs_password (which would prompt for nothing).
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var asked = false;
        var r = Converter.ToPdf(ole, "locked.xlsx", ["hunter2"], _ => { asked = true; return "x"; });
        Assert.Equal("error", r.Status);
        Assert.False(asked, "the fallback must never prompt — it has no decryptor");
        Assert.Contains("Excel", r.Message);
    }

    [Fact]
    public void GarbageComesBackAsAnErrorRatherThanThrowing() =>
        Assert.Equal("error",
            Converter.ToPdf([0xFF, 0xFE, 0x00], "junk.xlsx", Array.Empty<string>(), null).Status);
}
```

`TextToPdfTests` — the same shape, plus the two that matter for text:

```csharp
    [Fact]
    public void AVeryLongLineWrapsRatherThanRunningOffThePage()
    {
        var line = new string('w', 5000);
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(line),
            "wide.txt", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.True(PageCountOf(r.Pdf!) >= 1);
        // The paginator is column-based, so a single 5000-char "column" must
        // not produce 5000 points of width: the renderer hard-wraps first.
    }

    [Fact]
    public void ALongTextFileRunsToSeveralPages()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"line {i}"));
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(text),
            "long.log", Array.Empty<string>(), null);
        Assert.True(PageCountOf(r.Pdf!) > 5);
    }
```

- [ ] **Step 3: Run to see them fail.**

- [ ] **Step 4: Write `TableToPdf`**

```csharp
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core;

/// <summary>CSV, TSV and XLSX to PDF with nothing installed — the fallback
/// for a PC without Office. It reads with the same readers the roster loader
/// uses and draws a plain table: accurate values, not the spreadsheet's own
/// look. A workbook's LATER SHEETS ARE NOT INCLUDED — XlsxTable returns the
/// first worksheet only — which is why the caller says so in the row's note
/// rather than letting a sheet disappear quietly.
///
/// It never prompts. There is no decryptor here, so a password could not be
/// used even if one were typed; a protected file reports the reason instead
/// of raising a prompt that cannot help.</summary>
public sealed class TableToPdf : IDocumentConverter
{
    internal const double PageWidthPt = 792;    // Letter landscape: a table is
    internal const double PageHeightPt = 612;   // wider than it is tall
    internal const double MarginPt = 36;
    internal const double FontSizePt = 9;
    internal const double RowHeightPt = 14;
    private const double CellPaddingPt = 6;

    public bool Handles(string extension) =>
        extension is "csv" or "tsv" or "xlsx"
        || extension.Equals("csv", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("tsv", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("xlsx", StringComparison.OrdinalIgnoreCase);

    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't a spreadsheet or CSV");

        List<List<string>> table;
        try
        {
            table = extension == "xlsx"
                ? XlsxTable.Read(new MemoryStream(source))
                : Csv.Parse(Csv.ReadText(source));
        }
        catch (Exception ex)
        {
            // An encrypted xlsx is an OLE compound file rather than a zip, so
            // it lands here too — and no password would help, since nothing
            // in this class can decrypt one.
            return new("error", null,
                $"couldn't read it without Excel installed: {ex.Message}", displayName);
        }

        if (table.Count == 0 || table.All(r => r.Count == 0))
            return new("error", null, "there was nothing in it to merge", displayName);

        try { return new("ok", Render(table)); }
        catch (Exception ex) { return new("error", null, $"couldn't lay it out: {ex.Message}", displayName); }
    }

    /// <summary>Shared with <see cref="TextToPdf"/>: paginate with real text
    /// metrics, then draw. Internal so both converters lay out identically
    /// rather than drifting apart.</summary>
    internal static byte[] Render(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var font = new XFont("Segoe UI", FontSizePt);
        var headerFont = new XFont("Segoe UI", FontSizePt, XFontStyleEx.Bold);

        using var scratch = new PdfDocument();
        using var scratchGfx = XGraphics.FromPdfPage(scratch.AddPage());
        double Measure(string text) => scratchGfx.MeasureString(text ?? "", font).Width + CellPaddingPt;

        var pages = TablePages.Paginate(table, PageWidthPt - 2 * MarginPt,
            PageHeightPt - 2 * MarginPt, RowHeightPt, Measure);

        using var document = new PdfDocument();
        foreach (var layout in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(PageWidthPt);
            page.Height = XUnit.FromPoint(PageHeightPt);
            using var gfx = XGraphics.FromPdfPage(page);
            var y = MarginPt;
            DrawRow(gfx, layout, table[layout.HeaderRow], headerFont, y);
            y += RowHeightPt;
            foreach (var rowIndex in layout.Rows)
            {
                DrawRow(gfx, layout, table[rowIndex], font, y);
                y += RowHeightPt;
            }
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static void DrawRow(XGraphics gfx, TablePage layout,
        IReadOnlyList<string> row, XFont font, double y)
    {
        var x = MarginPt;
        for (var i = 0; i < layout.Columns.Count; i++)
        {
            var column = layout.Columns[i];
            // Ragged rows are ordinary in a CSV: a row with fewer fields than
            // the header draws blanks, it does not fail the merge.
            var text = column < row.Count ? row[column] ?? "" : "";
            gfx.DrawString(text, font, XBrushes.Black,
                new XRect(x, y, layout.Widths[i], RowHeightPt), XStringFormats.CenterLeft);
            x += layout.Widths[i];
        }
    }
}
```
**Simplify `Handles`** — the `is` pattern already covers it; the extra `Equals` calls in the draft above are redundant. Write it as a single case-insensitive check and say so in the report.

- [ ] **Step 5: Write `TextToPdf`**

Same shape, but a text file is a one-column table with **no header** — every line is content. Two consequences to handle rather than inherit:

- `TablePages` repeats row 0 on every page. For text that would repeat the first line, which is wrong. Pass the lines with a synthetic empty first row, then draw `HeaderRow` as blank — or add an `IReadOnlyList<TablePage> Paginate(..., bool repeatHeader)` overload. **Choose one, and say which and why in the report;** the second is cleaner and the fact `EveryPageCarriesTheHeaderRow` must keep passing for tables either way.
- A single very long line would make one enormous column. Hard-wrap each line to the page width **before** paginating, using the same `MeasureString`, so `AVeryLongLineWrapsRatherThanRunningOffThePage` passes.

Portrait Letter (612 × 792) suits text; landscape suits tables. Keep the constants separate.

- [ ] **Step 6: Run both suites, then the full check.**

- [ ] **Step 7: Revert-proof**

Make the protected-xlsx path return `needs_password` → `AProtectedSpreadsheet…` fails. Restore. Remove the hard-wrap from `TextToPdf` → `AVeryLongLineWraps…` fails. Restore. Record both.

- [ ] **Step 8: Commit** — `feat(convert): spreadsheets, CSV and text to PDF with nothing installed`.

---

### Task 4: `PdfMerge` routes non-PDFs through a converter, and honours the enabled types

**Files:**
- Modify: `src/OrdoSort.Core/PdfMerge.cs`
- Test: `tests/OrdoSort.Core.Tests/PdfMergeTests.cs` (add to the existing class)

**Interfaces:**
- Consumes: `IDocumentConverter`, `MergeTypes` (Task 2).
- Produces: `MergeZip(zipPath, candidates, ask, converter, includeTypes)` and `MergeFiles(paths, outputPath, candidates, ask, converter, includeTypes)` — TWO optional trailing parameters, both defaulting to null, so every existing caller and test compiles unchanged. `includeTypes` null means "every type the converter handles".

- [ ] **Step 1: Write the failing tests**

Add a fake converter and the rules. **Helper names are this class's own, verified:** `_dir` (created in the constructor, deleted in `Dispose`), `MakePdfBytes(pageCount, widthPt)`, `MakePdfFile(name, widthPt)`, `MakeZip(name, params (string, byte[])[])`, `NoPasswords`, `NeverAsked`, and `PdfReader.Open(…, PdfDocumentOpenMode.Import)` for counting. Do **not** introduce a `TempDir` type — this class does not use one.

```csharp
    /// <summary>Stands in for Office: deterministic, and able to produce each
    /// outcome the real one can. The PDF it returns is a real one-page
    /// document, so the merge path is exercised rather than mocked.</summary>
    private sealed class FakeConverter : IDocumentConverter
    {
        public string Status = "ok";
        public int Calls;
        public readonly List<string> Seen = new();
        public bool Handles(string extension) => extension is "docx" or "xlsx" or "csv" or "png";
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
        {
            Calls++;
            Seen.Add(displayName);
            return Status switch
            {
                "ok" => new("ok", MakePdfBytes(1)),
                "needs_password" => new("needs_password", null, "needs a password", displayName),
                _ => new("error", null, "couldn't convert it", displayName),
            };
        }
    }

    private string MakeDocFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static int PageCountOf(string path)
    {
        using var merged = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return merged.PageCount;
    }

    [Fact]
    public void ALooseWordDocumentIsConvertedAndMergedWithThePdfs()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var converter = new FakeConverter();
        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked, converter);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, converter.Calls);
        Assert.Equal(2, PageCountOf(r.Output!));
    }

    [Fact]
    public void AConversionThatNeedsAPasswordFailsTheWholeUnitAndNamesTheDocument()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked,
            new FakeConverter { Status = "needs_password" });
        Assert.Equal("needs_password", r.Status);
        Assert.Equal(doc, r.Item);
        Assert.Equal(new[] { pdf }, Directory.GetFiles(_dir, "*.pdf"));   // nothing written
    }

    [Fact]
    public void AFailedConversionFailsTheWholeUnitRatherThanDroppingTheDocument()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked,
            new FakeConverter { Status = "error" });
        Assert.Equal("error", r.Status);
        Assert.Equal(doc, r.Item);
    }

    [Fact]
    public void WithNoConverterANonPdfIsAClearErrorRatherThanASilentSkip()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked);
        Assert.Equal("error", r.Status);
        Assert.Equal(doc, r.Item);
        Assert.Contains("can't be converted", r.Message);
    }

    [Fact]
    public void DocumentsInsideAZipAreConvertedToo()
    {
        var zip = MakeZip("mixed.zip", ("a.pdf", MakePdfBytes(1)), ("b.docx", new byte[] { 1, 2, 3 }));
        var r = PdfMerge.MergeZip(zip, NoPasswords, NeverAsked, new FakeConverter());
        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        Assert.Equal(0, r.SkippedEntries);
    }

    [Fact]
    public void AZipOfOnlyDocumentsWithNoConverterStillReportsNothingToMerge()
    {
        var zip = MakeZip("docs.zip", ("a.docx", new byte[] { 1, 2, 3 }));
        var r = PdfMerge.MergeZip(zip, NoPasswords, NeverAsked);
        Assert.Equal("no_pdfs", r.Status);
        Assert.Equal("nothing to merge inside", r.Message);
    }

    [Fact]
    public void ConvertedDocumentsTakeTheirPlaceInTheSameNaturalSort()
    {
        var ten = MakePdfFile("10.pdf", widthPt: 110);
        var two = MakeDocFile("2.docx");
        var one = MakePdfFile("1.pdf", widthPt: 101);
        var converter = new FakeConverter();
        PdfMerge.MergeFiles(new[] { ten, two, one }, null, NoPasswords, NeverAsked, converter);
        Assert.Equal(new[] { "2.docx" }, converter.Seen);
    }

    // ------------------------------------------------- the enabled-type set

    [Fact]
    public void ATypeSwitchedOffIsNotConvertedEvenThoughTheConverterHandlesIt()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var converter = new FakeConverter();
        var onlyPdfs = new HashSet<string> { MergeTypes.Pdf };
        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked, converter, onlyPdfs);
        Assert.Equal("ok", r.Status);
        Assert.Equal(0, converter.Calls);
        Assert.Equal(1, PageCountOf(r.Output!));      // the PDF alone
    }

    [Fact]
    public void EntriesOfASwitchedOffTypeAreSkippedInsideAZipAndCounted()
    {
        var zip = MakeZip("mixed.zip", ("a.pdf", MakePdfBytes(1)), ("b.docx", new byte[] { 1, 2, 3 }));
        var onlyPdfs = new HashSet<string> { MergeTypes.Pdf };
        var r = PdfMerge.MergeZip(zip, NoPasswords, NeverAsked, new FakeConverter(), onlyPdfs);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, r.PdfCount);
        Assert.Equal(1, r.SkippedEntries);   // so "empty" and "filtered" stay distinguishable
    }

    [Fact]
    public void SwitchingPdfsOffLeavesAZipWithNothingToMerge()
    {
        var zip = MakeZip("pdfs.zip", ("a.pdf", MakePdfBytes(1)));
        var r = PdfMerge.MergeZip(zip, NoPasswords, NeverAsked, new FakeConverter(),
            new HashSet<string> { MergeTypes.Word });
        Assert.Equal("no_pdfs", r.Status);
    }
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Thread both parameters through**

1. Add `IDocumentConverter? converter = null, ISet<string>? includeTypes = null` to all four entry points, passing down to the `*Core` methods.
2. One predicate both paths use:

```csharp
    /// <summary>A PDF, or something the converter offers to turn into one —
    /// and in both cases only when the user has that type switched on.
    /// <paramref name="includeTypes"/> null means every type is on.</summary>
    private static bool IsMergeable(string name, IDocumentConverter? converter, ISet<string>? includeTypes)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var group = MergeTypes.GroupOf(extension);
        if (group is null) return false;
        if (includeTypes is not null && !includeTypes.Contains(group)) return false;
        return extension == "pdf" || (converter is not null && converter.Handles(extension));
    }
```
3. In `MergeZipCore`, replace the `pdfEntries` filter with `IsMergeable(entry.Name, converter, includeTypes)`, rename the local to `mergeable` throughout (sort, `PdfCount`), and change the empty message to `"nothing to merge inside"`.
4. In `MergeFilesCore`, skip paths that fail `IsMergeable` **before** the read — a switched-off file is not an error, it simply is not in the unit. If that leaves nothing, return the existing `"nothing to merge"` error.
5. Add the shared conversion routine beside `AddPdf`:

```csharp
    /// <summary>PDF bytes for a source that may not be a PDF: passed straight
    /// through when it already is one, otherwise handed to the converter with
    /// the caller's own passwords and prompt. Returns the failure to report,
    /// or null with <paramref name="bytes"/> replaced by the converted
    /// document. A type nothing can convert is an ERROR, not a silent skip —
    /// a merge that quietly omitted a document looks identical to a complete
    /// one until somebody notices it is missing. (A type the user switched
    /// OFF never reaches here; it is filtered out of the unit instead.)</summary>
    private static MergeResult? AsPdfBytes(ref byte[] bytes, string displayName, string itemKey,
        IDocumentConverter? converter, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask)
    {
        if (displayName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;

        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (converter is null || !converter.Handles(extension))
            return new("", "error", Item: itemKey,
                Message: $"{displayName} can't be converted on this PC");

        var converted = converter.ToPdf(bytes, displayName, candidates, ask);
        switch (converted.Status)
        {
            case "ok" when converted.Pdf is not null:
                bytes = converted.Pdf;
                return null;
            case "needs_password":
                return new("", "needs_password", Item: itemKey,
                    Message: converted.Message.Length > 0 ? converted.Message : "needs a password");
            default:
                return new("", "error", Item: itemKey,
                    Message: converted.Message.Length > 0 ? converted.Message : "couldn't convert it");
        }
    }
```
6. Call it in both loops right after the bytes are read and before `AddPdf`, returning `unconverted with { Source = … }`.
7. Update the class doc comment — it says "Merge PDFs into one document". Say what it does now, keeping every existing paragraph (ZipSlip, fail-whole, memory) and noting that a converted document's bytes join the same buffered set.

- [ ] **Step 4: Run the tests, then the full check.**

- [ ] **Step 5: Revert-proof**

Return `null` from `AsPdfBytes`'s "nothing handles it" branch → `WithNoConverterANonPdfIsAClearError…` fails and the merge silently produces a short document, which is the defect the fact exists to catch. Restore. Drop the `includeTypes` check from `IsMergeable` → both toggle facts fail. Restore. Record both.

- [ ] **Step 6: Commit** — `feat(merge): documents that aren't PDFs go through a converter, and only switched-on types take part`.

---

### Task 5: `ImageToPdf` — scans and photos, no Office involved

**Files:**
- Create: `src/OrdoSort.Wpf/Services/ImageToPdf.cs`
- Test: `tests/OrdoSort.Wpf.Tests/ImageToPdfTests.cs`

**Interfaces:** consumes `IDocumentConverter`; produces `ImageToPdf`, public with a parameterless constructor.

This is the highest-value converter for a filing app and the easiest to get right: no COM, no external process, no password path, and WPF already ships the decoders.

- [ ] **Step 1: Write the failing tests**

Fixtures are generated in-process with WPF's own encoders, so nothing is checked in:

```csharp
/// <summary>Images need no Office, so unlike the Office adapter these are
/// ordinary hermetic tests. Fixtures are encoded in-process by WPF itself.</summary>
[Collection(HighlightContrastTests.Name)]
public class ImageToPdfTests
{
    private readonly HighlightContrastFixture _fx;
    public ImageToPdfTests(HighlightContrastFixture fx) => _fx = fx;

    private static byte[] Png(int width, int height, double dpi = 96)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)200);
        var source = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] MultiPageTiff(int frames)
    {
        var encoder = new TiffBitmapEncoder();
        for (var i = 0; i < frames; i++)
        {
            var stride = 8 * 4;
            var pixels = new byte[stride * 8];
            var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            encoder.Frames.Add(BitmapFrame.Create(source));
        }
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (int Pages, double Width, double Height) Read(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.InformationOnly);
        return (doc.PageCount, doc.Pages[0].Width.Point, doc.Pages[0].Height.Point);
    }

    [Theory]
    [InlineData("png", true)] [InlineData("jpg", true)] [InlineData("TIFF", true)]
    [InlineData("bmp", true)] [InlineData("gif", true)]
    [InlineData("docx", false)] [InlineData("pdf", false)]
    public void HandlesTheImageTypesAndNothingElse(string extension, bool handled) =>
        _fx.Invoke(() => Assert.Equal(handled, new ImageToPdf().Handles(extension)));

    [Fact]
    public void AnImageBecomesOnePage() => _fx.Invoke(() =>
    {
        var r = new ImageToPdf().ToPdf(Png(100, 100), "shot.png", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, Read(r.Pdf!).Pages);
    });

    [Fact]
    public void AMultiPageTiffBecomesOnePagePerFrame() => _fx.Invoke(() =>
    {
        // The reason images live in the Wpf layer at all: WPF's decoder
        // exposes every frame, and a multi-page TIFF is what a sheet-feed
        // scanner produces.
        var r = new ImageToPdf().ToPdf(MultiPageTiff(4), "scan.tif", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(4, Read(r.Pdf!).Pages);
    });

    [Fact]
    public void AScanAtItsOwnDpiComesOutAtItsTrueSize() => _fx.Invoke(() =>
    {
        // 1700 x 2200 at 200 DPI is exactly 8.5 x 11 inches — 612 x 792 pt.
        var r = new ImageToPdf().ToPdf(Png(1700, 2200, dpi: 200), "scan.png", Array.Empty<string>(), null);
        var (_, width, height) = Read(r.Pdf!);
        Assert.Equal(612, width, 1);
        Assert.Equal(792, height, 1);
    });

    [Fact]
    public void APhotoWithMeaninglessDpiIsFittedToLetterInsteadOfAnAbsurdPage() => _fx.Invoke(() =>
    {
        // 4000 x 3000 at 72 DPI would be 55 x 41 inches. Fit instead.
        var r = new ImageToPdf().ToPdf(Png(4000, 3000, dpi: 72), "photo.jpg", Array.Empty<string>(), null);
        var (_, width, height) = Read(r.Pdf!);
        Assert.True(width <= 792 + 1 && height <= 792 + 1, $"page came out {width} x {height} pt");
        Assert.True(width > height, "a landscape photo should get a landscape page");
    });

    [Fact]
    public void ACorruptImageIsAnErrorNotAThrow() => _fx.Invoke(() =>
    {
        var r = new ImageToPdf().ToPdf([0, 1, 2, 3], "broken.png", Array.Empty<string>(), null);
        Assert.Equal("error", r.Status);
    });

    [Fact]
    public void ItNeverPrompts() => _fx.Invoke(() =>
    {
        var asked = false;
        new ImageToPdf().ToPdf([0, 1, 2], "x.png", ["pw"], _ => { asked = true; return "pw"; });
        Assert.False(asked);
    });
}
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Write the converter**

Requirements, each with a fact above:

- `Handles` → the `images` group's extensions, case-insensitively (use `MergeTypes.ExtensionsOf(MergeTypes.Images)` rather than a second list — one source of truth).
- Decode with `BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)`; **`OnLoad` matters** — it reads everything up front so the stream can be closed, and avoids a file handle outliving the call.
- One page per `decoder.Frames[i]`.
- Page size per frame: physical size is `PixelWidth / DpiX` inches. Accept it when **every side lands between 1 and 30 inches**; otherwise fit inside Letter (612 × 792) preserving aspect, choosing landscape when the frame is wider than tall. Put that rule and its two example numbers (the 200-DPI scan, the 72-DPI photo) in the doc comment.
- Draw with `XImage.FromStream` onto the page filling it exactly. Re-encode each frame to PNG in memory first (`PngBitmapEncoder`) so PdfSharp gets a format it certainly reads, regardless of the source codec.
- Never prompt, never throw — wrap everything and return `error`.
- STA: `BitmapDecoder` needs no STA thread for decoding, but the tests run inside the shared fixture anyway; note in the doc comment that the converter is called from the merge's worker thread and must not touch UI objects beyond these decoders.

- [ ] **Step 4: Run the tests, then the full check.**

- [ ] **Step 5: Revert-proof**

Force the DPI branch off (always fit to Letter) → `AScanAtItsOwnDpiComesOutAtItsTrueSize` fails with the fitted size. Restore. Take only `Frames[0]` → `AMultiPageTiffBecomesOnePagePerFrame` fails at 1 page. Restore. Record both.

- [ ] **Step 6: Commit** — `feat(convert): scans and photos become pages, multi-page TIFF included`.

---

### Task 6: `OfficeConverter` — Word, Excel and PowerPoint over late-bound COM

**Files:**
- Create: `src/OrdoSort.Wpf/Services/OfficeConverter.cs`
- Test: `tests/OrdoSort.Wpf.Tests/OfficeConverterTests.cs`

**Task 1's measured findings — build against these, do not re-derive them:**

| Measured | Value |
|---|---|
| Word cold start / convert | 705–805 ms / 600–725 ms per file |
| PowerPoint cold start | 475–529 ms; open 91 ms; `SaveAs` 270 ms |
| Wrong password, **Word** | `0x800A1520` — refused in 43–51 ms, no hang |
| Wrong password, **Excel** | `0x800A03EC` — refused in 85–91 ms. **Different from Word's; the adapter needs both.** |
| Sentinel on an *unprotected* file | ignored, opens normally — Word and Excel both |
| PowerPoint export | `ExportAsFixedFormat` **fails** across six calling conventions. Use `Presentation.SaveAs(path, 32)` (`ppSaveAsPDF`) — worked first try, even with `ReadOnly: true`. |
| PowerPoint `Visible = false` | **refused** every run (COMException). Open with `WithWindow: false` instead and leave the app as it is. |
| Excel `FitToPagesTall = false` | accepted directly, no `Type.Missing` fallback needed |
| Excel whole-workbook export | both worksheets present (the PDF's own `/Pages /Count` read 4) |
| Natural exit after `Quit()` | PowerPoint ~20–30 s, Excel **over 2 minutes** — neither exits inside any practical grace period |

**THE SAFETY FINDING, which shapes this task more than anything else.** `Activator.CreateInstance` on these ProgIDs can **silently attach to the user's already-running Office session** — Word, Excel and PowerPoint are single-instance COM servers. Task 1 proved this rather than inferring it: a simulated user session holding unsaved work was reused by our own call, and a name-based kill then destroyed it.

So the adapter must know **whether it started an instance or borrowed one**, by diffing the process list immediately before and after `CreateInstance`, and behave differently:

- **Borrowed** (no new PID appeared): never `Quit()`, never force-kill, and **save and restore every app-global flag it changes** — setting `DisplayAlerts = false` on the user's own Word suppresses *their* save prompts, and `Visible = false` hides *their* window. Close only the document this class opened.
- **Ours** (a new PID appeared): `Quit()`, `Marshal.FinalReleaseComObject`, then force-kill **that PID** after a 3–5 s grace period, because neither app exits naturally in any practical window.
- **Kill by name is forbidden outright**, in any code path, including hang recovery.

A fact must pin this: with a pre-existing instance running, the converter completes and that instance is **still alive afterwards**. If the test cannot start a second instance to simulate the user's (single-instance COM makes that awkward), assert the decision function instead — given a before/after PID pair, does it choose kill or leave? — and say in the report which route you took.

- [ ] **Step 1: Write the tests (they skip without Office)**

`SkippableFact` is **not** referenced in this repo and Global Constraints forbid new packages, so write the guard as an early `return` with a comment saying why, plus one always-running fact asserting `IsAvailable` is a bool so the class is never empty. **Report which route you took.**

Every fact runs under a hard timeout — the failure being guarded against is a HANG, and a hanging test wedges the run rather than failing it:

```csharp
    private static void WithTimeout(TimeSpan limit, Action body)
    {
        var task = Task.Run(body);
        Assert.True(task.Wait(limit),
            $"timed out after {limit.TotalSeconds}s — a modal Office dialog is the likely cause");
        if (task.IsFaulted) throw task.Exception!.InnerException!;
    }
```

Facts: a real `.docx` converts to pages; a real `.pptx` converts; a **protected** document returns `needs_password` rather than hanging; the right password opens it; every worksheet of a two-sheet workbook is included; and **no `WINWORD`/`EXCEL`/`POWERPNT` process survives**, by comparing PIDs before and after. Fixtures are built by Office itself once per run, cached in a static, under a GUID temp folder deleted at the end.

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Write the converter**

```csharp
/// <summary>Word, Excel and PowerPoint, driven over LATE-BOUND COM — no
/// interop package, nothing needed at build time, no coupling to an Office
/// generation.
///
/// Three hazards this class exists to contain, each measured before it was
/// written (the plan's Task 1 spike):
///
/// 1. THE HANG. Documents.Open, Workbooks.Open and Presentations.Open each
///    take a password. Pass none for a protected file and Office raises a
///    MODAL DIALOG on a hidden window: the call never returns and the run is
///    wedged. So a password is ALWAYS passed — a deliberate sentinel when we
///    have no candidate — and Office throws instead, which is what becomes
///    "needs_password".
/// 2. ORPHANED PROCESSES. Every Application is quit and released in a
///    finally, with a kill-by-PID net. One instance per CONVERTER, not per
///    file: cold start dominates the cost.
/// 3. TEMP FILES. Office can only open a real file. Names are generated
///    here, never taken from a zip entry (that is what keeps PdfMerge's
///    ZipSlip rule true), and deleted in a finally — these are clients'
///    documents.</summary>
public sealed class OfficeConverter : IDocumentConverter, IDisposable
```

- `IsAvailable` → per app, not all-or-nothing: Word may be present without PowerPoint. `Handles(extension)` maps the extension to its group and returns whether *that* app is registered.
- `ToPdf` → write `source` to `%TEMP%/<guid>/<guid>.<ext>`; resolve the password through `Passwords.Resolve(candidates, ask, displayName, inside: null, tryWith: …)` so saved passwords are tried before any prompt, exactly as zips and PDFs do; convert; read the bytes back; delete the temp folder in a `finally`.
- `TryOpen` returns `PasswordTry.Opened`/`WrongPassword`/`Unreadable`, mapping the spike's HRESULT to `WrongPassword`.
- Sentinel: a constant like `"\u0001ordosort-no-password\u0001"` when the candidate is null or empty.
- Setup: `DisplayAlerts = 0`, `AutomationSecurity = 3`, and `Visible = false` for Word and Excel only — **PowerPoint refuses it** (measured), so open with `WithWindow: false` there and leave the app as it is. On a BORROWED instance, read each flag first and restore it afterwards.
- Word: `ExportAsFixedFormat(outPath, 17)`. Excel: per-worksheet `Zoom = false; FitToPagesWide = 1; FitToPagesTall = false;` then `ExportAsFixedFormat(0, outPath)` on the workbook. PowerPoint: **`SaveAs(outPath, 32)`** — `ExportAsFixedFormat` does not work there (Task 1 measured six failing conventions).
- `Dispose` quits **only apps it started**, releases them, and force-kills those diffed PIDs after a 3-5s grace period. A borrowed instance is left running with its flags restored.

- [ ] **Step 4: Run the tests, then the full check.** Record the end-to-end timing — it is the number that tells the owner whether a ten-file merge is acceptable.

- [ ] **Step 5: Revert-proof**

Replace the sentinel with `Type.Missing` → the protected-document fact must TIME OUT (its guard firing is the proof the sentinel is load-bearing). Restore immediately. Remove `Quit()` from `Dispose` → the process fact fails. Restore. Record both.

- [ ] **Step 6: Commit** — `feat(convert): Word, Excel and PowerPoint over late-bound COM`.

---

### Task 7: The toggle row

**Files:**
- Modify: `src/OrdoSort.Core/Config.cs`, `src/OrdoSort.Wpf/ViewModels/ZipItemRow.cs`, `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`, `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml`
- Test: `tests/OrdoSort.Wpf.Tests/MergeTypeToggleTests.cs`

**Interfaces:** consumes `MergeTypes` (Task 2) and `PdfMerge`'s `includeTypes` (Task 4).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task ARowOfASwitchedOffTypeIsListedButNotIncluded()
    {
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath(), PdfPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        var word = vm.Rows.Single(r => r.Kind == "word");
        Assert.False(word.IsIncluded);
        Assert.False(word.IsRunnable);
        Assert.Contains("not included", word.Note);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);   // the PDF only
    }

    [Fact]
    public async Task SwitchingTheTypeBackOnIncludesTheRowsAlreadyInTheListWithoutReAdding()
    {
        // The whole reason exclusion is a live property rather than a status.
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        Assert.False(vm.Rows.Single().IsIncluded);
        vm.SetTypeEnabled(MergeTypes.Word, true);
        Assert.True(vm.Rows.Single().IsIncluded);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);
    }

    [Fact]
    public async Task TogglingRaisesPropertyChangedOnTheRowsSoTheGridRepaints()
    {
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath()]);
        var row = vm.Rows.Single();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        Assert.Contains(nameof(ZipItemRow.IsIncluded), raised);
    }

    [Fact]
    public void TheChoiceIsSavedAndComesBack()
    {
        var config = new Config();
        var vm = NewViewModel(config);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        vm.SetTypeEnabled(MergeTypes.Images, false);
        var reopened = NewViewModel(config);
        Assert.False(reopened.IsTypeEnabled(MergeTypes.Word));
        Assert.False(reopened.IsTypeEnabled(MergeTypes.Images));
        Assert.True(reopened.IsTypeEnabled(MergeTypes.Pdf));
    }

    [Fact]
    public void UntickingEverythingStaysUntickedAfterAReopen()
    {
        // The "everything off" sentinel from Task 2 - without it an empty
        // stored value reads as "never set" and everything comes back on.
        var config = new Config();
        var vm = NewViewModel(config);
        foreach (var group in MergeTypes.AllGroups) vm.SetTypeEnabled(group, false);
        Assert.All(MergeTypes.AllGroups, g => Assert.False(NewViewModel(config).IsTypeEnabled(g)));
    }

    [Fact]
    public async Task AnExcludedRowIsNotSelectedIntoTheRun()
    {
        var vm = NewViewModel(zipMerger: …, fileMerger: RecordingMerger);
        await vm.AddPaths([DocxPath(), PdfPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        await vm.MergeAsync(null);
        Assert.DoesNotContain(RecordingMerger.PathsSeen, p => p.EndsWith(".docx"));
    }
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement**

- `Config`: a `merge_types` string property with the same `JsonPropertyName` style as its neighbours, defaulting to `""` (never set → everything on).
- `ZipItemRow`: `IsIncluded` (settable by the view model, raising `PropertyChanged`), folded into `IsRunnable`, and a note when excluded. `KindOf` maps every group's extensions to its group name so the Kind column reads `word`/`excel`/`powerpoint`/`image`/`text` alongside `pdf`/`zip`/`folder`.
- `MergePdfsViewModel`: `IsTypeEnabled(group)` / `SetTypeEnabled(group, bool)`; a `TypeToggles` collection the XAML binds to; on change, persist through config and re-evaluate `IsIncluded` on every row, then `OnRowsChanged()`. Pass the enabled set into `PdfMerge` via the lambda capture (**do not change the `_zipMerger`/`_fileMerger` delegate signatures** — existing tests inject through them).
- `MergePdfsWindow.xaml`: a `WrapPanel` of `CheckBox`es above the grid, bound to `TypeToggles`, each with `AutomationProperties.Name`. Follow the window's existing button-row idiom; a `WrapPanel` because seven checkboxes will not fit the 580px minimum width on one line at the largest font preset — the same reasoning the file already records for its button row.

- [ ] **Step 4: Run the Wpf suites, then the full check.**

- [ ] **Step 5: Revert-proof**

Make `SetTypeEnabled` skip the per-row re-evaluation → `SwitchingTheTypeBackOnIncludes…` fails. Restore. Make `Save` write `""` for an empty set → `UntickingEverythingStaysUnticked…` fails. Restore. Record both.

- [ ] **Step 6: Commit** — `feat(merge): per-type toggles, remembered between sessions`.

---

### Task 8: Wiring the window together

**Files:** `src/OrdoSort.Wpf/Services/ConverterChain.cs` (new), `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`, tests alongside.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void TheChainPrefersOfficeAndDoesNotDowngradeWhenItFails()
    {
        // Office present and failing must NOT fall through to the table
        // renderer: a lesser rendering of a document the user believes
        // converted properly is worse than a clear failure.
        var office = new StubConverter("xlsx") { Status = "error" };
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        var result = new ConverterChain(office, fallback).ToPdf([1], "a.xlsx", Array.Empty<string>(), null);
        Assert.Equal("error", result.Status);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void TheChainUsesTheFallbackWhenOfficeDoesNotHandleTheTypeAtAll()
    {
        var office = new StubConverter(handles: null);      // Office absent
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        Assert.Equal("ok", new ConverterChain(office, fallback)
            .ToPdf([1], "a.xlsx", Array.Empty<string>(), null).Status);
    }

    [Fact]
    public async Task ADocumentNothingCanConvertIsMarkedWhenItIsDropped()
    {
        // The probe that already runs on add is what tells you at DROP time,
        // not after a long run.
        var vm = NewViewModel(converter: new StubConverter(handles: null));
        await vm.AddPaths([DocxPath()]);
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Contains("Word", row.Note);
    }

    [Fact]
    public async Task AForeignTypeIsStillRefusedAtIntake()
    {
        var vm = NewViewModel();
        await vm.AddPaths([ExePath()]);
        Assert.Empty(vm.Rows);
    }
```

- [ ] **Step 2: Implement**

- `ConverterChain` — `Handles` true when any link handles; `ToPdf` asks the **first link that `Handles`** and returns its result whatever it is; only a link that does not handle the type at all falls through. This is the no-silent-downgrade rule.
- `MergePdfsViewModel`:
  - `Extensions` becomes `MergeTypes.AllExtensions`; `IntakeNoun` becomes "PDF, document, image or zip".
  - an optional `IDocumentConverter? converter = null` constructor parameter, defaulting to `new ConverterChain(new OfficeConverter(), new ImageToPdf(), new TableToPdf(), new TextToPdf())`.
  - **Sweep every `IsPdf` use in this class.** The loose-unit selection and `RunnableLoosePdfs` currently filter on `IsPdf`; they must include every non-zip included row, or a dropped `.docx` is listed and never merged — a no-op that looks like a bug. This is the likeliest miss in the whole plan.
  - `Probe`: for a row whose type is enabled but whose converter does not handle it, return `(Error, "<app> isn't installed, so this can't be converted")`.

- [ ] **Step 3: Run everything, revert-proof (remove a group from `AllExtensions` → the acceptance fact fails; make the chain fall through on failure → the downgrade fact fails), commit** — `feat(merge): the window takes documents, images and text, not just PDFs and zips`.

---

### Task 9: E2E scenarios and the docs

**Files:** `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs`, `README.md`, `CONTEXT.md`

- [ ] **Step 1: Add three scenarios**, following the file's existing shape and using the deterministic in-repo converters (never Office) so they stay reproducible:

```csharp
        new Scenario(Surface, "a spreadsheet merges with the PDFs", "clean", SheetWithPdfs),
        new Scenario(Surface, "an image merges with the PDFs", "clean", ImageWithPdfs),
        new Scenario(Surface, "a type switched off is listed but not merged", "awkward", TypeSwitchedOff),
```

- [ ] **Step 2: Run them** — `dotnet run --project tools/OrdoSort.Smoke -- e2e zipmerge`, expect `E2E PASS` with the count up by three.

- [ ] **Step 3: Update the docs**

- `README.md` — the Merge PDFs entry says which types it takes, that Office is used when installed, and that the toggles choose what merges.
- `CONTEXT.md` — add `IDocumentConverter`, `MergeTypes`, `TablePages`, `TableToPdf`, `TextToPdf`, `ImageToPdf`, `OfficeConverter` and `ConverterChain`, with one line each on where they live and why the split follows the `net8.0` / `net8.0-windows` line.

- [ ] **Step 4: Full check, then commit** — `docs(convert): the merge window's file types and toggles`.

---

## Self-review (done while writing)

- **Spec coverage:** contract and groups → T2; Office-free converters → T3; conversion and the enabled-type filter in both merge paths → T4; images → T5; Office incl. PowerPoint, with the hang/orphan/temp rules → T6; toggles, persistence and live exclusion → T7; chain, intake, probe-on-add → T8; E2E and docs → T9; the spike → T1.
- **Placeholders:** none — every step carries its code or its exact edit. Four things are deliberately deferred to a measurement rather than guessed, and each says so in place: the wrong-password HRESULT (T1 → T6), whether PowerPoint tolerates a hidden window (T1 → T6), whether `SkippableFact` exists (T6 Step 1, both routes given), and the header-repetition choice for text (T3 Step 5, two options with the criterion).
- **A conflict I planted deliberately and resolved:** T2's `EverythingOffIsDistinguishableFromNothingStored` contradicts `NothingStoredMeansEverythingIsOn`. It is called out in the step with the ruling (a `"none"` sentinel), because a user who unticks every box and gets everything back after a restart is a real defect and the naïve implementation has it.
- **Type consistency:** `IDocumentConverter.Handles/ToPdf` and `ConversionResult(Status, Pdf, Message, Item)` are identical across T3, T5, T6, T8. `TablePages.Paginate(table, pageWidth, pageHeight, rowHeight, measure) → IReadOnlyList<TablePage>` matches between T2, T3. `MergeTypes.GroupOf/ExtensionsOf/AllExtensions/Load/Save` are used the same way in T2, T4, T5, T7, T8. `PdfMerge`'s two new parameters are optional and trailing in every overload, so no existing caller changes.
- **Known risk, stated rather than hidden:** T8's `IsPdf` sweep. A `.docx` accepted into the list but never selected into a unit would merge nothing and look like a no-op. The task names it as the likeliest miss in the plan; the acceptance fact catches it.
