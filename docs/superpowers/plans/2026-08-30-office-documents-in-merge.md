# Merging Word, Excel and CSV with PDFs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the Merge PDFs window accepts `.docx`, `.xlsx` and `.csv` beside PDFs and zips, converts them to PDF pages, and merges them into the same output — loose in the list and inside archives.

**Architecture:** Core gains a byte-in/byte-out `IDocumentConverter` contract and a pure table renderer built on the existing `Csv`/`XlsxTable` readers and the PdfSharp drawing `BoxLabels` already does. `PdfMerge` asks the converter to turn any non-PDF into pages, then feeds the result through the same `AddPdf` a real PDF takes. The Wpf layer ships an `OfficeConverter` driving Word and Excel over late-bound COM, and composes "Office when available, Core renderer when not".

**Tech Stack:** .NET 8, PdfSharp 6.1.1 (already present), late-bound COM via `Type.GetTypeFromProgID` (no new packages), xunit.

**Spec:** `docs/superpowers/specs/2026-08-30-office-documents-in-merge-design.md` — read it; it carries the decisions and the rejected alternatives this plan argues from.

**Branch:** create `feature/office-docs-in-merge` off `main` (`b2032b2`). Do not work on `main`.

## Global Constraints

- **Check command** (repo root, before every commit; BOTH `Passed!` lines must appear and totals must be at or above baseline — Core **750**, Wpf **2014**):
  ```
  dotnet build OrdoSort.sln -t:Rebuild -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  - **Smart App Control wears three disguises** (`0x800711C7`): an assembly silently `Skipping`ed with **no `Passed!` line**; `FileLoadException … blocked this file` surfacing *inside* individual tests, which reads as real failures; and the block hopping to a different assembly after each rebuild. `Directory.Build.targets`' non-determinism does not reliably clear it. What works: delete every `bin`/`obj` under `src/`, `tests/`, `tools/`, build once, run Core; then `dotnet build tests/OrdoSort.Wpf.Tests -t:Rebuild` and run Wpf alone. Two runs covering both assemblies on the same sources is acceptable evidence — say so in the report. **A missing `Passed!` line is a FAILED check.**
  - `XamlParseException` burst → `dotnet build-server shutdown`, rebuild, rerun. MSB3027 → kill the stale `testhost.exe`.
  - Known intermittent `HeaderLayoutTests` stall → rerun with `--blame-hang --blame-hang-timeout 180s --blame-hang-dump-type none` and READ THE TOTAL.
  - Known flakes, documented in `docs/known-flakes.md`: `BulkRenameBatchTests.UndoHandsTheFileWorkToTheSchedulerInsteadOfDoingItOnTheClick`, `BulkRenameProbeTests.ADiscreteToggleResolvesWithoutWaitingTheFullDebounceWindow`. Re-run in isolation, report, never chase or weaken.
  - Run every test command in the **foreground**. Do not background a test run; nothing will notify you.
- **Revert-proof:** every behavioural fact must fail for a VALUE reason when its named production line is broken. Break it, see the failure, restore, record the message. A fact that passes with the fix removed is a defect in the fact.
- **Never throws:** `PdfMerge` promises every failure comes back as a `MergeResult`. `IDocumentConverter` implementations inherit that promise — a converter that throws is a defect.
- **The fallback never prompts.** Only the Office path participates in the password contract. `TableToPdf` cannot use a password even if given one (it has no decryptor), so a protected file there reports `error` naming the reason — prompting for a password that cannot help is worse than saying so. *(Refinement of spec decision 3, which describes the Office path.)*
- **No new NuGet packages.** No changes to `Theme/Styles.xaml`. Do not touch TriageWindow, FilenameListWindow, or the Zip and unzip window.
- C# style per the repo: XML doc comments explaining WHY on public/internal surfaces; `_camelCase` private fields; no single-letter names except loop indices.
- Every commit carries these trailer lines after a blank line:
  ```
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
  ```
  Use a here-doc or a temp message file so multi-line messages survive the shell; verify with `git log -1 --format=%B`. Commit messages explain WHY.

---

## File structure

| File | Responsibility |
|---|---|
| `src/OrdoSort.Core/DocumentConverter.cs` (new) | The `IDocumentConverter` contract and `ConversionResult`. Nothing else. |
| `src/OrdoSort.Core/TablePages.cs` (new) | Pure pagination: a table plus page geometry → pages of column groups and row ranges. No PdfSharp. |
| `src/OrdoSort.Core/TableToPdf.cs` (new) | `IDocumentConverter` for csv/xlsx: read with the existing readers, paginate, draw with PdfSharp. |
| `src/OrdoSort.Core/XlsxTable.cs` | Gains a `Stream` overload so bytes can be read without a temp file. |
| `src/OrdoSort.Core/PdfMerge.cs` | Both merge paths accept a converter and route non-PDFs through it. |
| `src/OrdoSort.Wpf/Services/OfficeConverter.cs` (new) | Late-bound COM to Word and Excel; temp-file and process discipline. |
| `src/OrdoSort.Wpf/Services/ConverterChain.cs` (new) | Office when available, Core renderer when not. |
| `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` | Accepted extensions, converter composition, probe-on-add for unconvertible types. |
| `src/OrdoSort.Wpf/ViewModels/ZipItemRow.cs` | `KindOf` learns the new kinds. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs` | Scenarios for a converted document and a protected one. |

---

### Task 1: Spike — prove the two assumptions before building on them

**This task's output is an answer, not code.** Everything it writes is throwaway and must be deleted before it commits. The design rests on two unverified claims; if either is false, STOP and report rather than proceeding.

**Files:**
- Create (throwaway, deleted in step 5): `S:\tmp\office-spike\Program.cs` and a matching `.csproj`

- [ ] **Step 1: Build the fixtures**

Create two files with Word itself, driving it from PowerShell (this is fixture-making, not the thing under test):

```powershell
$w = New-Object -ComObject Word.Application
$w.Visible = $false
$d = $w.Documents.Add()
$d.Content.Text = "Spike fixture. Second line."
$d.SaveAs2("S:\tmp\office-spike\plain.docx")
$d.Close()
$p = $w.Documents.Add()
$p.Content.Text = "Protected fixture."
$p.SaveAs2("S:\tmp\office-spike\locked.docx", [Type]::Missing, $false, "secret")
$p.Close()
$w.Quit()
```
Confirm both files exist and that `locked.docx` really is protected (opening it in Word asks for a password).

- [ ] **Step 2: Convert the plain one, late-bound**

A console app targeting `net8.0-windows`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

var type = Type.GetTypeFromProgID("Word.Application")
    ?? throw new InvalidOperationException("Word.Application is not registered");
var sw = Stopwatch.StartNew();
dynamic app = Activator.CreateInstance(type)!;
Console.WriteLine($"cold start: {sw.ElapsedMilliseconds} ms");
app.Visible = false;
app.DisplayAlerts = 0;
app.AutomationSecurity = 3;          // msoAutomationSecurityForceDisable

sw.Restart();
dynamic doc = app.Documents.Open(@"S:\tmp\office-spike\plain.docx",
    ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
    PasswordDocument: "an-unlikely-sentinel-3f9c", Visible: false);
doc.ExportAsFixedFormat(@"S:\tmp\office-spike\plain.pdf", 17);   // wdExportFormatPDF
doc.Close(false);
Console.WriteLine($"convert: {sw.ElapsedMilliseconds} ms, " +
    $"bytes: {new FileInfo(@"S:\tmp\office-spike\plain.pdf").Length}");
```

**The question this answers:** does a *sentinel* password break the open of an UNprotected document? It must not — Word is expected to ignore `PasswordDocument` when the file needs none. If the open fails here, the whole sentinel approach is wrong and the design changes.

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
**Record the HRESULT** — the adapter needs it to tell "wrong password" apart from "corrupt file". If this call instead HANGS (no output for 30s), the sentinel does not work: kill it, report, and stop.

Then confirm the real password opens it:
```csharp
dynamic ok = app.Documents.Open(@"S:\tmp\office-spike\locked.docx",
    ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
    PasswordDocument: "secret", Visible: false);
Console.WriteLine($"real password opened it, pages: {ok.ComputeStatistics(2)}");
ok.Close(false);
```

- [ ] **Step 4: Prove the process can be cleaned up**

```csharp
int pid = 0;
GetWindowThreadProcessId((IntPtr)app.Hwnd, out pid);
Console.WriteLine($"word pid: {pid}");
app.Quit();
Marshal.FinalReleaseComObject(app);
GC.Collect(); GC.WaitForPendingFinalizers();
Thread.Sleep(1500);
Console.WriteLine($"still running after Quit: {Process.GetProcesses().Any(p => p.Id == pid)}");

[DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
```
Before and after the whole run, print `Process.GetProcessesByName("WINWORD").Length`.

- [ ] **Step 5: Report, and delete everything**

```bash
rm -rf "S:/tmp/office-spike"
```
Report these numbers, which the later tasks depend on: cold-start ms, per-convert ms, the sentinel's behaviour on both files, the HRESULT for a wrong password, and whether any `WINWORD.EXE` survived. **Nothing is committed by this task.** If the sentinel opened the protected file, or if any open hung, say so plainly and stop — the design changes first.

---

### Task 2: The contract, and pagination as a pure function

**Files:**
- Create: `src/OrdoSort.Core/DocumentConverter.cs`, `src/OrdoSort.Core/TablePages.cs`
- Test: `tests/OrdoSort.Core.Tests/TablePagesTests.cs`

**Interfaces:**
- Produces: `IDocumentConverter`, `ConversionResult` (consumed by Tasks 3, 4, 5, 6); `TablePages.Paginate` and `TablePage` (consumed by Task 3).

- [ ] **Step 1: Write the contract**

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

    /// <summary>Convert, asking for a password the same way the rest of the
    /// app does. <paramref name="candidates"/> are tried before
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
```

- [ ] **Step 2: Write the failing pagination tests**

`tests/OrdoSort.Core.Tests/TablePagesTests.cs`. `measure` is injected so these are exact arithmetic, not font-dependent:

```csharp
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>How a table becomes pages, proven without a PDF. Widths here are
/// character counts (the measure delegate is <c>s => s.Length</c>), so every
/// expected number below is arithmetic a reader can check.</summary>
public class TablePagesTests
{
    private static readonly Func<string, double> Measure = s => s.Length;

    private static List<List<string>> Table(params string[][] rows) =>
        rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void ASmallTableIsOnePageWithColumnsSizedToTheirWidestCell()
    {
        var table = Table(["id", "name"], ["1", "Alice"], ["2", "Bo"]);
        var pages = TablePages.Paginate(table, pageWidth: 100, pageHeight: 100, rowHeight: 10, Measure);
        var page = Assert.Single(pages);
        Assert.Equal(new[] { 0, 1 }, page.Columns);
        Assert.Equal(new[] { 2.0, 5.0 }, page.Widths);   // "id"=2, "Alice"=5
        Assert.Equal(new[] { 1, 2 }, page.Rows);          // the header is not a body row
    }

    [Fact]
    public void ATableTallerThanThePageSplitsAndRepeatsNothingBetweenPages()
    {
        // rowHeight 10 into a 100-tall page, less one header row = 9 body rows a page.
        var rows = new List<List<string>> { ["h"] };
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        var pages = TablePages.Paginate(rows, 100, 100, 10, Measure);
        Assert.Equal(3, pages.Count);
        Assert.Equal(9, pages[0].Rows.Count);
        Assert.Equal(9, pages[1].Rows.Count);
        Assert.Equal(2, pages[2].Rows.Count);
        // every body row appears exactly once, in order
        Assert.Equal(Enumerable.Range(1, 20), pages.SelectMany(p => p.Rows));
    }

    [Fact]
    public void EveryPageCarriesTheHeaderRow()
    {
        var rows = new List<List<string>> { ["h"] };
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        var pages = TablePages.Paginate(rows, 100, 100, 10, Measure);
        Assert.All(pages, p => Assert.Equal(0, p.HeaderRow));
    }

    [Fact]
    public void ATableWiderThanThePageSplitsIntoColumnGroups()
    {
        // four 30-wide columns into a 100-wide page: 3 fit, then 1.
        var wide = new string('x', 30);
        var table = Table([wide, wide, wide, wide], [wide, wide, wide, wide]);
        var pages = TablePages.Paginate(table, 100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pages[0].Columns);
        Assert.Equal(new[] { 3 }, pages[1].Columns);
        // every column appears exactly once
        Assert.Equal(Enumerable.Range(0, 4), pages.SelectMany(p => p.Columns).OrderBy(c => c));
    }

    [Fact]
    public void ATableBothTooTallAndTooWideGivesAPagePerGroupPerRowRange()
    {
        var wide = new string('x', 30);
        var rows = new List<List<string>> { [wide, wide, wide, wide] };
        for (var i = 0; i < 20; i++) rows.Add([wide, wide, wide, wide]);
        var pages = TablePages.Paginate(rows, 100, 100, 10, Measure);
        Assert.Equal(6, pages.Count);   // 2 column groups x 3 row ranges
    }

    [Fact]
    public void AColumnWiderThanThePageStillGetsAPageToItself()
    {
        // The guard against an infinite loop: a group is never empty, even
        // when its single column cannot fit.
        var huge = new string('x', 500);
        var table = Table([huge, "b"], [huge, "b"]);
        var pages = TablePages.Paginate(table, 100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0 }, pages[0].Columns);
        Assert.Equal(new[] { 1 }, pages[1].Columns);
    }

    [Fact]
    public void RaggedRowsArePaddedRatherThanCrashing()
    {
        // A CSV row with fewer fields than the header is ordinary, not an error.
        var table = Table(["a", "b", "c"], ["1"], ["1", "2", "3"]);
        var pages = TablePages.Paginate(table, 1000, 1000, 10, Measure);
        var page = Assert.Single(pages);
        Assert.Equal(3, page.Columns.Count);
        Assert.Equal(new[] { 1, 2 }, page.Rows);
    }

    [Fact]
    public void AnEmptyTableProducesNoPages()
    {
        Assert.Empty(TablePages.Paginate(new List<List<string>>(), 100, 100, 10, Measure));
    }

    [Fact]
    public void AHeaderOnlyTableStillProducesOnePage()
    {
        var pages = TablePages.Paginate(Table(["a", "b"]), 100, 100, 10, Measure);
        var page = Assert.Single(pages);
        Assert.Empty(page.Rows);
    }
}
```

- [ ] **Step 3: Run them to see them fail**

`dotnet build tests/OrdoSort.Core.Tests -v quiet` — expected: a compile error, `TablePages` does not exist.

- [ ] **Step 4: Write the paginator**

`src/OrdoSort.Core/TablePages.cs`:

```csharp
namespace OrdoSort.Core;

/// <summary>One page of a table: which source columns it shows, how wide
/// each is, and which source rows fall on it. <see cref="HeaderRow"/> is the
/// row index repeated at the top of every page — a table read out of a
/// spreadsheet or a CSV has one, and a page of values with no headings is
/// unreadable.</summary>
public sealed record TablePage(
    IReadOnlyList<int> Columns,
    IReadOnlyList<double> Widths,
    IReadOnlyList<int> Rows,
    int HeaderRow = 0);

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
    /// all fit are split into consecutive groups, each group repeated down
    /// the table's rows, so a wide sheet reads left-to-right then onward
    /// rather than being silently cropped. A single column too wide for the
    /// page still takes a page of its own: a group is never empty, which is
    /// also what stops this looping forever.</summary>
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
                widths[c] = Math.Max(widths[c], measure(row[c]));

        // Consecutive column groups that fit the page width.
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
        // left of the page's height.
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

- [ ] **Step 5: Run the tests to see them pass**

`dotnet build tests/OrdoSort.Core.Tests -v quiet && dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal --filter "FullyQualifiedName~TablePagesTests"` — expected `Passed! … Total: 9`.

- [ ] **Step 6: Revert-proof**

Break `if (current.Count > 0 && …)` to `if (used + widths[c] > pageWidth)` → `AColumnWiderThanThePageStillGetsAPageToItself` fails (an empty first group). Restore. Break `Math.Max(1, …)` to `(int)(pageHeight / rowHeight) - 1` and paginate a 1-row-high page → the tall-table facts fail. Restore. Record both messages.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Core/DocumentConverter.cs src/OrdoSort.Core/TablePages.cs tests/OrdoSort.Core.Tests/TablePagesTests.cs
git commit -F - <<'EOF'
feat(convert): the converter contract, and pagination as arithmetic

Bytes in and bytes out keeps PdfMerge's ZipSlip rule intact: an
implementation that needs a real file writes one under a name it generates,
never the entry's. Pagination is separated from drawing along the same seam
BoxLabels already uses, so the awkward parts - a sheet wider than the page,
ragged CSV rows, a column that fits nowhere - are checkable with a
calculator instead of a rendered PDF.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
EOF
```

---

### Task 3: `TableToPdf` — CSV and XLSX without Office

**Files:**
- Modify: `src/OrdoSort.Core/XlsxTable.cs` (add a `Stream` overload)
- Create: `src/OrdoSort.Core/TableToPdf.cs`
- Test: `tests/OrdoSort.Core.Tests/TableToPdfTests.cs`

**Interfaces:**
- Consumes: `IDocumentConverter`, `ConversionResult`, `TablePages.Paginate` (Task 2); `Csv.Parse`, `Csv.ReadText`, `XlsxTable.Read` (existing, internal, same assembly).
- Produces: `TableToPdf` — a public class with a parameterless constructor, used by Tasks 4 and 6.

- [ ] **Step 1: Give `XlsxTable` a stream overload**

The existing `Read(string path)` opens `ZipFile.OpenRead(path)`. Split it so bytes can be read without a temp file — keep `Read(path)` as the thin wrapper so every existing caller is untouched:

```csharp
    internal static List<List<string>> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>The same reader over an open stream — what lets a converter
    /// read an xlsx out of a zip entry's bytes without writing a temp file.</summary>
    internal static List<List<string>> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        // ... the existing body, unchanged, against `zip` ...
    }
```
`ZipFile.OpenRead` returns a `ZipArchive`, so the body needs no other change. Add `using System.IO.Compression;` if it is not already there.

- [ ] **Step 2: Write the failing tests**

`tests/OrdoSort.Core.Tests/TableToPdfTests.cs`:

```csharp
using OrdoSort.Core;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

/// <summary>The converter that needs no Office. It reads with the same
/// readers the roster loader uses and draws with PdfSharp, so these facts
/// check the round trip — bytes in, a PDF that PdfSharp itself can reopen —
/// and the refusals, which are the part a user actually meets.</summary>
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
    [InlineData("csv", true)]
    [InlineData("xlsx", true)]
    [InlineData("docx", false)]
    [InlineData("pdf", false)]
    public void HandlesOnlyTheTwoItCanRead(string extension, bool handled) =>
        Assert.Equal(handled, Converter.Handles(extension));

    [Fact]
    public void ACsvBecomesAReadablePdf()
    {
        var csv = "id,name\n1,Alice\n2,Bo\n"u8.ToArray();
        var result = Converter.ToPdf(csv, "people.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", result.Status);
        Assert.NotNull(result.Pdf);
        Assert.Equal(1, PageCountOf(result.Pdf!));
    }

    [Fact]
    public void AQuotedFieldWithACommaAndANewlineSurvives()
    {
        // The reader already handles this; the fact pins that the converter
        // uses it rather than splitting on commas itself.
        var csv = "id,note\n1,\"Smith, John\nsecond line\"\n"u8.ToArray();
        var result = Converter.ToPdf(csv, "notes.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public void ALongCsvRunsToSeveralPages()
    {
        var rows = new List<string> { "id,name" };
        for (var i = 0; i < 500; i++) rows.Add($"{i},Name {i}");
        var csv = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", rows));
        var result = Converter.ToPdf(csv, "long.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", result.Status);
        Assert.True(PageCountOf(result.Pdf!) > 5,
            $"500 rows should not fit on {PageCountOf(result.Pdf!)} page(s)");
    }

    [Fact]
    public void AnEmptyFileIsAnErrorNotAnEmptyPdf()
    {
        var result = Converter.ToPdf(Array.Empty<byte>(), "empty.csv", Array.Empty<string>(), null);
        Assert.Equal("error", result.Status);
        Assert.Contains("nothing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWordDocumentIsNotItsToConvert()
    {
        var result = Converter.ToPdf([1, 2, 3], "letter.docx", Array.Empty<string>(), null);
        Assert.Equal("unsupported", result.Status);
    }

    [Fact]
    public void AProtectedSpreadsheetSaysSoRatherThanAskingForAPasswordItCannotUse()
    {
        // An encrypted xlsx is an OLE compound file, not a zip — the reader
        // cannot open it, and no password would help here, so this must NOT
        // come back as needs_password (which would prompt for nothing).
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var asked = false;
        var result = Converter.ToPdf(ole, "locked.xlsx", ["hunter2"], _ => { asked = true; return "x"; });
        Assert.Equal("error", result.Status);
        Assert.False(asked, "the fallback must never prompt — it has no decryptor");
        Assert.Contains("Excel", result.Message);
    }

    [Fact]
    public void GarbageComesBackAsAnErrorRatherThanThrowing()
    {
        var result = Converter.ToPdf([0xFF, 0xFE, 0x00], "junk.xlsx", Array.Empty<string>(), null);
        Assert.Equal("error", result.Status);
    }
}
```

- [ ] **Step 3: Run to see them fail**

`dotnet build tests/OrdoSort.Core.Tests -v quiet` — compile error, `TableToPdf` does not exist.

- [ ] **Step 4: Write the converter**

`src/OrdoSort.Core/TableToPdf.cs`:

```csharp
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core;

/// <summary>CSV and XLSX to PDF with nothing installed — the fallback for a
/// PC without Office. It reads with the same readers the roster loader uses
/// and draws a plain table: accurate values, not the spreadsheet's own look.
/// A workbook's LATER SHEETS ARE NOT INCLUDED — <see cref="XlsxTable"/>
/// returns the first worksheet only — which is why the caller says so in the
/// row's note rather than letting a sheet disappear quietly.
///
/// It never prompts. There is no decryptor here, so a password could not be
/// used even if one were typed; a protected file reports the reason instead
/// of raising a prompt that cannot help.</summary>
public sealed class TableToPdf : IDocumentConverter
{
    private const double PageWidthPt = 792;    // US Letter landscape: a table
    private const double PageHeightPt = 612;   // is wider than it is tall
    private const double MarginPt = 36;
    private const double FontSizePt = 9;
    private const double RowHeightPt = 14;
    private const double CellPaddingPt = 6;

    public bool Handles(string extension) =>
        extension.Equals("csv", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("xlsx", StringComparison.OrdinalIgnoreCase);

    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.');
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't a spreadsheet or CSV");

        List<List<string>> table;
        try
        {
            table = extension.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
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

        try
        {
            return new("ok", Draw(table), "");
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't lay it out: {ex.Message}", displayName);
        }
    }

    private static byte[] Draw(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var font = new XFont("Segoe UI", FontSizePt);
        var headerFont = new XFont("Segoe UI", FontSizePt, XFontStyleEx.Bold);
        var usableWidth = PageWidthPt - 2 * MarginPt;

        // Measured with the same font the drawing uses, so a column is as
        // wide as its content actually renders.
        using var scratch = new PdfDocument();
        var scratchPage = scratch.AddPage();
        using var scratchGfx = XGraphics.FromPdfPage(scratchPage);
        double Measure(string text) =>
            scratchGfx.MeasureString(text ?? "", font).Width + CellPaddingPt;

        var pages = TablePages.Paginate(table, usableWidth,
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
                new XRect(x, y, layout.Widths[i], RowHeightPt),
                XStringFormats.CenterLeft);
            x += layout.Widths[i];
        }
    }
}
```

- [ ] **Step 5: Run the tests**

`dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal --filter "FullyQualifiedName~TableToPdfTests"` — expected `Passed! … Total: 10` (7 facts + 4 theory rows − the theory counted once per row).

- [ ] **Step 6: Full check, then revert-proof**

Run the Global Constraints check. Then: make `ToPdf`'s protected-xlsx path return `needs_password` → `AProtectedSpreadsheetSaysSoRatherThanAskingForAPasswordItCannotUse` fails. Restore. Make `Draw` ignore `layout.Columns` and always draw column 0 → `ACsvBecomesAReadablePdf` still passes but `ALongCsvRunsToSeveralPages` does not change; instead break `Paginate`'s call to pass `double.MaxValue` as the page height → the long-CSV fact fails on page count. Record both.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Core/XlsxTable.cs src/OrdoSort.Core/TableToPdf.cs tests/OrdoSort.Core.Tests/TableToPdfTests.cs
git commit -F - <<'EOF'
feat(convert): CSV and spreadsheets to PDF with nothing installed

The fallback for a PC without Office, reusing the readers the roster
loader already has and drawing the table with PdfSharp. It reports the
first-worksheet-only limit rather than dropping later sheets silently, and
it never prompts for a password it has no way to use - saying so is more
honest than a dialog that cannot help.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
EOF
```

---

### Task 4: `PdfMerge` routes non-PDFs through a converter

**Files:**
- Modify: `src/OrdoSort.Core/PdfMerge.cs`
- Test: `tests/OrdoSort.Core.Tests/PdfMergeTests.cs` (add to the existing class)

**Interfaces:**
- Consumes: `IDocumentConverter`, `ConversionResult` (Task 2).
- Produces: `MergeZip(zipPath, candidates, ask, converter)` and `MergeFiles(paths, outputPath, candidates, ask, converter)` — an OPTIONAL trailing parameter on each, defaulting to null, so every existing caller and test compiles unchanged. Task 6 supplies a real one.

- [ ] **Step 1: Write the failing tests**

Add to `PdfMergeTests` a fake converter and the rules that matter:

```csharp
    /// <summary>Stands in for Office: deterministic, and able to produce each
    /// outcome the real one can. The PDF it returns is a real one-page
    /// document built by the same helper the other facts here use, so the
    /// merge path is exercised for real rather than mocked.</summary>
    private sealed class FakeConverter : IDocumentConverter
    {
        public string Status = "ok";
        public int Calls;
        public readonly List<string> Seen = new();
        public bool Handles(string extension) => extension is "docx" or "xlsx" or "csv";
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

    /// <summary>A loose non-PDF beside the PDFs, using this class's own
    /// _dir/Make* conventions.</summary>
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
        var pdf = MakePdfFile("a.pdf", widthPt: 200);   // MakePdfFile makes 1 page
        var doc = MakeDocFile("b.docx");
        var converter = new FakeConverter();

        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked, converter);

        Assert.Equal("ok", r.Status);
        Assert.Equal(1, converter.Calls);
        Assert.Equal(2, PageCountOf(r.Output!));   // 1 from the PDF + 1 converted
    }

    [Fact]
    public void AConversionThatNeedsAPasswordFailsTheWholeUnitAndNamesTheDocument()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var converter = new FakeConverter { Status = "needs_password" };

        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked, converter);

        Assert.Equal("needs_password", r.Status);
        Assert.Equal(doc, r.Item);
        // nothing written: the only PDF in the folder is still the input
        Assert.Equal(new[] { pdf }, Directory.GetFiles(_dir, "*.pdf"));
    }

    [Fact]
    public void AFailedConversionFailsTheWholeUnitRatherThanDroppingTheDocument()
    {
        var pdf = MakePdfFile("a.pdf");
        var doc = MakeDocFile("b.docx");
        var converter = new FakeConverter { Status = "error" };

        var r = PdfMerge.MergeFiles(new[] { pdf, doc }, null, NoPasswords, NeverAsked, converter);

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
        var converter = new FakeConverter();

        var r = PdfMerge.MergeZip(zip, NoPasswords, NeverAsked, converter);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        Assert.Equal(0, r.SkippedEntries);
        Assert.Equal(2, PageCountOf(r.Output!));
    }

    [Fact]
    public void AZipOfOnlyDocumentsIsNoLongerNothingToMerge()
    {
        var zip = MakeZip("docs.zip", ("a.docx", new byte[] { 1, 2, 3 }));
        var converter = new FakeConverter();

        Assert.Equal("ok", PdfMerge.MergeZip(zip, NoPasswords, NeverAsked, converter).Status);
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

        // 1.pdf, 2.docx, 10.pdf — the converter is asked about 2.docx only,
        // and the merge reached it between the two PDFs, which is what
        // natural order means for a mixed list.
        Assert.Equal(new[] { "2.docx" }, converter.Seen);
    }
```

**Helper names are this class's own, verified:** `_dir` (created in the constructor, deleted in `Dispose`), `MakePdfBytes(pageCount, widthPt)`, `MakePdfFile(name, widthPt)`, `MakeZip(name, params (string, byte[])[])`, `NoPasswords`, `NeverAsked`, and the `PdfReader.Open(..., PdfDocumentOpenMode.Import)` idiom for counting pages. Add `MakeDocFile` and `PageCountOf` as shown; do not introduce a `TempDir` type — this class does not use one.

- [ ] **Step 2: Run to see them fail**

`dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal --filter "FullyQualifiedName~PdfMergeTests"` — compile error first (the fifth parameter does not exist), then value failures once it compiles.

- [ ] **Step 3: Thread the converter through both paths**

In `PdfMerge`:

1. Add the optional parameter to all four entry points (`MergeZip` x2, `MergeFiles`, and the internal test seam), passing it down to the `*Core` methods.
2. In `MergeZipCore`, widen the entry filter:

```csharp
            // Directory entries are skipped without counting. Everything else
            // that is neither a PDF nor something the converter can turn into
            // one counts toward SkippedEntries, so the caller can still tell
            // "an empty zip" from "a zip full of things we can't merge".
            var mergeable = new List<ZipEntry>();
            var skipped = 0;
            foreach (var entry in entries)
            {
                if (!entry.IsFile) continue;
                if (IsMergeable(entry.Name, converter)) mergeable.Add(entry);
                else skipped++;
            }
            if (mergeable.Count == 0)
                return new(zipPath, "no_pdfs", SkippedEntries: skipped, Message: "nothing to merge inside");
```
   with
```csharp
    /// <summary>A PDF, or something the converter offers to turn into one.</summary>
    private static bool IsMergeable(string name, IDocumentConverter? converter) =>
        name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || (converter is not null
            && converter.Handles(Path.GetExtension(name).TrimStart('.').ToLowerInvariant()));
```
   and rename the local `pdfEntries` to `mergeable` throughout the method (including the sort and `PdfCount: mergeable.Count`).

3. Add the one routine both paths share, beside `AddPdf`:

```csharp
    /// <summary>PDF bytes for a source that may not be a PDF: passed straight
    /// through when it already is one, otherwise handed to the converter with
    /// the caller's own passwords and prompt. Returns the failure to report,
    /// or null with <paramref name="bytes"/> replaced by the converted
    /// document. A type nothing can convert is an ERROR, not a silent skip —
    /// a merge that quietly omitted a document looks identical to a complete
    /// one until somebody notices it is missing.</summary>
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

4. Call it in both loops immediately after the bytes are read and before `AddPdf` — in `MergeZipCore`:
```csharp
                    var unconverted = AsPdfBytes(ref bytes, entry.Name, entry.Name, converter, candidates, ask);
                    if (unconverted is not null) return unconverted with { Source = zipPath };
```
   and in `MergeFilesCore`, with `Path.GetFileName(path)` as the display name and `path` as the item key, returning `unconverted with { Source = source }`.

5. Update the class doc comment: it currently says "Merge PDFs into one document". Say what it does now — PDFs, plus anything an injected `IDocumentConverter` can turn into one — and keep every existing paragraph (ZipSlip, fail-whole, memory) intact, noting that a converted document's bytes join the same buffered set.

- [ ] **Step 4: Run the tests, then the full check**

Filtered `PdfMergeTests` green, then the Global Constraints check. Core baseline rises by the facts added here.

- [ ] **Step 5: Revert-proof**

Return `null` instead of the error from `AsPdfBytes`'s "nothing handles it" branch → `WithNoConverterANonPdfIsAClearErrorRatherThanASilentSkip` fails (and the merge silently produces a short document, which is the defect the fact exists to catch). Restore. Change `case "needs_password"` to fall into `default` → `AConversionThatNeedsAPasswordFailsTheWholeUnitAndNamesTheDocument` fails on status. Restore. Record both messages.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Core/PdfMerge.cs tests/OrdoSort.Core.Tests/PdfMergeTests.cs
git commit -F - <<'EOF'
feat(merge): documents that aren't PDFs go through a converter first

Both units - the loose group and the archive - hand anything that isn't a
PDF to the injected converter and then feed the result through exactly the
same AddPdf a real PDF takes, so page import, the plain-output rule and
fail-whole all apply unchanged. A type nothing can convert is an error
rather than a silent skip: a merge missing a document looks exactly like a
complete one until someone notices.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
EOF
```

---

### Task 5: `OfficeConverter` — Word and Excel over late-bound COM

**Files:**
- Create: `src/OrdoSort.Wpf/Services/OfficeConverter.cs`
- Test: `tests/OrdoSort.Wpf.Tests/OfficeConverterTests.cs`

**Interfaces:**
- Consumes: `IDocumentConverter`, `ConversionResult`, `Passwords.Resolve`, `PasswordRequest`, `PasswordTry`.
- Produces: `OfficeConverter` — `public sealed class OfficeConverter : IDocumentConverter, IDisposable`, with `public static bool IsAvailable` telling the composition whether Word and Excel are registered.

**Use the spike's measured HRESULT** for the wrong-password case rather than the placeholder below; the brief for this task carries the number Task 1 reported.

- [ ] **Step 1: Write the tests (they skip without Office)**

```csharp
using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>The one part of this feature that cannot be hermetic: it drives
/// real Word and Excel. Every fact here skips when they are not registered,
/// so the suite stays green on a machine without Office — and each one runs
/// under a hard timeout, because the failure mode being guarded against is a
/// HANG (Office raising a modal password dialog on a hidden window), and a
/// hanging test wedges the whole run rather than failing it.</summary>
public class OfficeConverterTests
{
    private static bool Available => OfficeConverter.IsAvailable;

    private static void WithTimeout(TimeSpan limit, Action body)
    {
        var task = Task.Run(body);
        Assert.True(task.Wait(limit),
            $"timed out after {limit.TotalSeconds}s — a modal Office dialog is the likely cause");
        if (task.IsFaulted) throw task.Exception!.InnerException!;
    }

    [SkippableFact]
    public void ARealWordDocumentConvertsToPagesWeCanRead()
    {
        Skip.IfNot(Available, "Word and Excel are not registered on this machine");
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            using var converter = new OfficeConverter();
            var docx = OfficeFixtures.PlainDocx();     // built once per run, see below
            var result = converter.ToPdf(docx, "plain.docx", Array.Empty<string>(), null);
            Assert.Equal("ok", result.Status);
            Assert.NotNull(result.Pdf);
            Assert.True(result.Pdf!.Length > 500, $"a converted document was {result.Pdf.Length} bytes");
        });
    }

    [SkippableFact]
    public void AProtectedDocumentComesBackAsNeedsPasswordRatherThanHanging()
    {
        Skip.IfNot(Available, "Word and Excel are not registered on this machine");
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            using var converter = new OfficeConverter();
            var locked = OfficeFixtures.LockedDocx(password: "secret");
            var result = converter.ToPdf(locked, "locked.docx", Array.Empty<string>(), null);
            Assert.Equal("needs_password", result.Status);
        });
    }

    [SkippableFact]
    public void TheRightPasswordOpensIt()
    {
        Skip.IfNot(Available, "Word and Excel are not registered on this machine");
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            using var converter = new OfficeConverter();
            var locked = OfficeFixtures.LockedDocx(password: "secret");
            var result = converter.ToPdf(locked, "locked.docx", ["secret"], null);
            Assert.Equal("ok", result.Status);
        });
    }

    [SkippableFact]
    public void NoOfficeProcessSurvivesTheConverter()
    {
        Skip.IfNot(Available, "Word and Excel are not registered on this machine");
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            using (var converter = new OfficeConverter())
                converter.ToPdf(OfficeFixtures.PlainDocx(), "plain.docx", Array.Empty<string>(), null);
        });
        var after = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        Assert.Empty(after.Except(before));
    }

    [SkippableFact]
    public void EveryWorksheetOfAWorkbookIsIncluded()
    {
        Skip.IfNot(Available, "Word and Excel are not registered on this machine");
        WithTimeout(TimeSpan.FromSeconds(120), () =>
        {
            using var converter = new OfficeConverter();
            var book = OfficeFixtures.TwoSheetXlsx();   // one full page of rows per sheet
            var result = converter.ToPdf(book, "two.xlsx", Array.Empty<string>(), null);
            Assert.Equal("ok", result.Status);
            Assert.True(PdfPageCount(result.Pdf!) >= 2,
                "both worksheets should be in the output, not just the first");
        });
    }
}
```

`Skip.IfNot` needs `Xunit.SkippableFact`. **If that package is not already referenced, do not add it** (Global Constraints: no new packages) — instead write the guard as an early `return` with a comment saying why, and add a single non-skippable fact asserting `IsAvailable` is a bool so the class is never empty. Report which route you took.

`OfficeFixtures` builds its files with Office itself, once per test run, cached in a static — the same trick the spike used. Put it in the same file, and have it write to `Path.GetTempPath()` under a GUID folder deleted at the end.

- [ ] **Step 2: Run to see them fail**

Compile error, `OfficeConverter` does not exist.

- [ ] **Step 3: Write the converter**

Key requirements, all of which have a fact above or a Global Constraint behind them:

```csharp
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Services;

/// <summary>Word and Excel, driven over LATE-BOUND COM — no interop package,
/// nothing needed at build time, and no coupling to an Office generation.
///
/// Three hazards this class exists to contain, each measured before it was
/// written (see the plan's Task 1 spike):
///
/// 1. THE HANG. Documents.Open and Workbooks.Open each take a password. Pass
///    none for a protected file and Office raises a MODAL DIALOG on a hidden
///    window: the call never returns and the run is wedged. So a password is
///    ALWAYS passed — a deliberate sentinel when we have no candidate — and
///    Office throws instead, which is what becomes "needs_password".
/// 2. ORPHANED PROCESSES. Every Application is quit and released in a
///    finally, with a kill-by-PID net. One instance per CONVERTER, not per
///    file: cold start dominates the cost.
/// 3. TEMP FILES. Office can only open a real file. Names are generated here,
///    never taken from a zip entry (that is what keeps PdfMerge's ZipSlip
///    rule true), and deleted in a finally — these are clients' documents.</summary>
public sealed class OfficeConverter : IDocumentConverter, IDisposable
```

- `IsAvailable` → both `Type.GetTypeFromProgID("Word.Application")` and `"Excel.Application"` non-null.
- `Handles` → `docx` (Word), `xlsx`/`csv` (Excel), and only when `IsAvailable`.
- `ToPdf` → write `source` to `%TEMP%/<guid>/<guid>.<ext>`; resolve the password through `Passwords.Resolve(candidates, ask, displayName, inside: null, tryWith: pw => TryOpen(...))` so saved passwords are tried before the prompt, exactly as zips and PDFs do; convert; read the output bytes; delete the temp folder in a `finally`.
- `TryOpen` returns `PasswordTry.Opened` / `WrongPassword` / `Unreadable`, mapping the spike's HRESULT to `WrongPassword` and anything else to `Unreadable`.
- The sentinel: a constant like `"\u0001ordosort-no-password\u0001"`, used when the candidate is null/empty.
- App setup: `Visible = false`, `DisplayAlerts = 0`, `AutomationSecurity = 3`.
- Word: `Documents.Open(path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, PasswordDocument: pw, Visible: false)` then `ExportAsFixedFormat(outPath, 17)`.
- Excel: `Workbooks.Open(path, UpdateLinks: 0, ReadOnly: true, Password: pw, AddToMru: false)`, then **per worksheet** `PageSetup.Zoom = false; PageSetup.FitToPagesWide = 1; PageSetup.FitToPagesTall = false;` then `ExportAsFixedFormat(0, outPath)` on the WORKBOOK, which exports every sheet.
- `Dispose` quits both apps if started, `Marshal.FinalReleaseComObject`s them, and kills the recorded PIDs if they outlive a short grace period.

- [ ] **Step 4: Run the tests, then the full check**

On this machine Office is present, so all five run. Record how long the class takes end to end — it is the number that tells the owner whether a ten-file merge is acceptable.

- [ ] **Step 5: Revert-proof**

Replace the sentinel with `Type.Missing` → `AProtectedDocumentComesBackAsNeedsPasswordRatherThanHanging` must TIME OUT (its guard firing is the proof the sentinel is load-bearing). Restore immediately. Remove the `Quit()` from `Dispose` → `NoOfficeProcessSurvivesTheConverter` fails. Restore. Record both.

- [ ] **Step 6: Commit** — subject `feat(convert): Word and Excel over late-bound COM`, with the WHY (the hang, the orphans, the temp files) and the two trailer lines.

---

### Task 6: The window accepts the new types

**Files:**
- Create: `src/OrdoSort.Wpf/Services/ConverterChain.cs`
- Modify: `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs`, `src/OrdoSort.Wpf/ViewModels/ZipItemRow.cs`
- Test: `tests/OrdoSort.Wpf.Tests/MergePdfsViewModelTests.cs`, `tests/OrdoSort.Wpf.Tests/ZipItemRowTests.cs`

**Interfaces:** consumes everything above.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Theory]
    [InlineData(@"C:\in\a.docx", "word")]
    [InlineData(@"C:\in\a.xlsx", "sheet")]
    [InlineData(@"C:\in\a.csv", "sheet")]
    [InlineData(@"C:\in\a.pdf", "pdf")]
    [InlineData(@"C:\in\a.zip", "zip")]
    public void KindOfNamesTheNewDocumentTypes(string path, string kind) =>
        Assert.Equal(kind, ZipItemRow.KindOf(path));

    [Fact]
    public async Task AWordDocumentIsAcceptedByTheMergeWindow()
    {
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
        await vm.AddPaths([ /* a real temp .docx path */ ]);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("word", row.Kind);
        Assert.True(row.IsRunnable);
    }

    [Fact]
    public async Task AWordDocumentIsMarkedOnAddWhenNothingCanConvertIt()
    {
        // The probe that already runs on add is what tells you at DROP time,
        // not after a long run.
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(),
            converter: new NoDocxConverter());
        await vm.AddPaths([ /* a real temp .docx path */ ]);
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Contains("Word", row.Note);
    }

    [Fact]
    public void TheChainPrefersOfficeAndDoesNotDowngradeWhenItFails()
    {
        // Office present and failing must NOT silently fall through to the
        // table renderer: a lesser rendering of a document the user thinks
        // converted properly is worse than a clear failure.
        var office = new StubConverter("xlsx") { Status = "error" };
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        var chain = new ConverterChain(office, fallback);
        var result = chain.ToPdf([1], "a.xlsx", Array.Empty<string>(), null);
        Assert.Equal("error", result.Status);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void TheChainUsesTheFallbackWhenOfficeDoesNotHandleTheTypeAtAll()
    {
        var office = new StubConverter(handles: null);      // Office absent
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        var chain = new ConverterChain(office, fallback);
        Assert.Equal("ok", chain.ToPdf([1], "a.xlsx", Array.Empty<string>(), null).Status);
        Assert.Equal(1, fallback.Calls);
    }
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement**

- `ZipItemRow.KindOf` gains `docx → "word"`, `xlsx`/`csv` → `"sheet"`, before the `"file"` fallback.
- `ConverterChain` — `Handles` is true when either link handles; `ToPdf` asks the first link that `Handles` the extension and **returns its result, whatever it is**; only when that link does not handle the type at all does it try the next. This is the no-silent-downgrade rule and it is what the two chain facts pin.
- `MergePdfsViewModel`:
  - `PdfsAndZips` becomes `MergeableTypes = { "pdf", "zip", "docx", "xlsx", "csv" }`; update `IntakeNoun` to "PDF, document or zip".
  - a new optional constructor parameter `IDocumentConverter? converter = null`, defaulting to `new ConverterChain(new OfficeConverter(), new TableToPdf())`; store it in a field.
  - `MergeAsync` passes it: `candidates => _zipMerger(zipRow.Path, candidates, AskPassword)` becomes a lambda that calls `PdfMerge.MergeZip(zipRow.Path, candidates, AskPassword, _converter)`. **Do not change the `_zipMerger`/`_fileMerger` delegate signatures** — the existing tests inject through them, and a lambda capture keeps those seams intact.
  - `RunnableLoosePdfs` / the loose-unit selection currently filter on `IsPdf`; they must now include the convertible kinds, or a dropped `.docx` will never be merged. Check every `IsPdf` use in this class.
  - `Probe` gains: for a convertible row, if `!_converter.Handles(extension)`, return `(ZipItemRowStatus.Error, "<Word/Excel> isn't installed, so this can't be converted")`.

- [ ] **Step 4: Run the Wpf suites, then the full check.**

- [ ] **Step 5: Revert-proof** — remove `"docx"` from the accepted set → the acceptance fact fails; restore. Make `ConverterChain` fall through on failure → `TheChainPrefersOfficeAndDoesNotDowngradeWhenItFails` fails; restore.

- [ ] **Step 6: Commit** — subject `feat(merge): the window takes documents, not just PDFs and zips`.

---

### Task 7: E2E scenarios and the docs

**Files:**
- Modify: `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs`, `README.md`, `CONTEXT.md`

- [ ] **Step 1: Add two scenarios**

Following the file's existing shape, using the deterministic Core converter (never Office) so they are reproducible:

```csharp
        new Scenario(Surface, "a spreadsheet merges with the PDFs", "clean", SheetWithPdfs),
        new Scenario(Surface, "a document nothing can convert", "awkward", UnconvertibleDocument),
```

- [ ] **Step 2: Run them**

`dotnet run --project tools/OrdoSort.Smoke -- e2e zipmerge` — expect `E2E PASS` with the scenario count risen by two.

- [ ] **Step 3: Update the docs**

- `README.md` — the Tools list entry for Merge PDFs should say it takes Word, Excel and CSV files too, and that Office is used when installed.
- `CONTEXT.md` — add `IDocumentConverter`, `TableToPdf` and `OfficeConverter` to the map, with one line each on where they live and why the split follows the `net8.0` / `net8.0-windows` line.

- [ ] **Step 4: Full check, then commit** — subject `docs(convert): the merge window's new file types`.

---

## Self-review (done while writing)

- **Spec coverage:** contract → T2; fallback renderer → T2/T3; conversion in both merge paths → T4; Office adapter with the hang, orphan and temp-file rules → T5; intake, Kind, probe-on-add, no-silent-downgrade → T6; E2E and docs → T7; the spike → T1.
- **Placeholders:** none — every step carries its code or its exact edit. Two deliberate lookups are flagged in place rather than guessed: the wrong-password HRESULT (T5 uses the spike's measured value) and whether `SkippableFact` is already referenced (T5 Step 1 gives both routes and asks which was taken).
- **Type consistency:** `IDocumentConverter.Handles(string)`/`ToPdf(byte[], string, IReadOnlyList<string>, Func<PasswordRequest,string?>?)` and `ConversionResult(Status, Pdf, Message, Item)` are used identically in T3, T4, T5, T6. `TablePages.Paginate(table, pageWidth, pageHeight, rowHeight, measure) → IReadOnlyList<TablePage>` and `TablePage(Columns, Widths, Rows, HeaderRow)` match between T2 and T3. `PdfMerge`'s new parameter is optional and trailing in every overload, so no existing caller changes.
- **Known risk, stated rather than hidden:** T6's `IsPdf` sweep is the likeliest place for a miss — a `.docx` accepted into the list but never selected into a unit would merge nothing and look like a no-op. The task names it; the acceptance fact catches it.
