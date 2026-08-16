# Reports Hub Phase 2: Hub Shell + Sources + Turn-around Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The first visible piece of the Reports hub — a non-modal `ReportsWindow` with a sidebar (Turn-around / Sources), the Sources page's upload-feed card with the persisted ignore checklist, and the Turn-around dashboard (hero tile, month grid, by-source matrix, weekly sparkline, set-aside strip with click-to-inspect, Summary/Detail tabs, two-sheet xlsx export, copy-summary) — built on Phase 1's engine.

**Architecture:** Two new Core pieces (`XlsxWriter` — the repo has a reader but no writer — and `TurnaroundExport`/`TurnaroundSummaryText`, so everything assertable is tested in Core). Three view models following the repo's `ObservableObject`/`RelayCommand`/`DebouncedProbe` conventions: `ReportsViewModel` (coordinator: owns the feed, the summary, the ignore set), `SourcesPageViewModel` and `TurnaroundPageViewModel` (display slices + commands). Views are one window plus two page UserControls, themed with the existing `Theme.*` DynamicResources. The Reports menu gains a hub entry and the Turn-around entry re-targets the hub; `TurnaroundWindow`/`ProductionWindow` stay untouched (deleted in Phase 4; the E2E `ReportScenarios` still drive their view models directly).

**Tech Stack:** .NET 8, WPF, xUnit 2.5.3. **No new NuGet packages** — the xlsx writer is hand-written like `XlsxTable`.

**Spec:** `docs/superpowers/specs/2026-08-15-reports-hub-design.md` (Phase 2 of Sequencing; decisions 1, 2, 6, 7, 8, 9, 10; the TAT half of Architecture § View models and views). Phase 1's engine (`DocumentDate`, `IgnoreList`, `UploadReportFeed`, `TurnaroundSummary`) is merged and its interfaces are used exactly as they exist on `main`.

## Global Constraints

- **Culture:** every parse/format through `CultureInfo.InvariantCulture`. **Comparison:** ordinal.
- **PHI:** all test fixtures synthetic; never copy anything from `docs/sample/`.
- **Test gate (per `docs/known-flakes.md`):**
  ```
  dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
  dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~<TestClass>" -v minimal
  ```
  Always read the `Passed!` count — exit 0 with zero tests is a known failure mode. Core suite currently at **704**.
- **Existing contracts joined (checked at planning, binding on implementers):**
  - Any new `Config` collection/string key is added to `Normalize()`'s null-hardening (`Clean(...)` for lists, `??= ""` for strings) AND to `ConfigNewKeysTests`/`ConfigNullKeysTests`.
  - View models use the `DebouncedProbe<T>` pattern for anything that touches the filesystem; apply-callbacks run on the UI thread; `Dispose()` disposes probes.
  - XAML uses only existing theme resources (`Theme.WindowBg/Surface/SurfaceRaised/Border/Text/SubtleText/AccentText/Accent/RowHover/Warning/WarningText/StatusAmber/StatusGreen/StatusRed`) and existing styles (`StatusText`, `CaptionText`, `FieldRow`, `FieldLabel`, `Icon`); `PrimaryButton` is reserved for commit actions — Export is a plain button (see TurnaroundWindow.xaml's own comment).
  - Do not modify `TurnaroundWindow`, `ProductionWindow`, `TurnaroundViewModel`, `ProductionViewModel`, or anything in `tools/OrdoSort.Smoke` — the smoke suite drives those view models and is reworked in Phase 4.
- **UI tasks** (4–5) have no VM unit tests — there is no Wpf test project; the repo verifies view models via the smoke suite (Phase 4 reworks it). Verification for those tasks = clean full-solution build + the reviewer reading the diff + the final task's live run. Everything assertable lives in Core (Tasks 1–2) precisely so this is safe.
- **Commits:** conventional, lowercase, one per task.

---

### Task 1: XlsxWriter

A minimal, valid xlsx writer — the counterpart to `XlsxTable`, hand-written in the same self-contained style. Unlike the test-only zip builders, this one must produce a package **Excel itself opens**: content types, package rels, workbook, workbook rels, and one worksheet part per sheet.

**Files:**
- Create: `src/OrdoSort.Core/XlsxWriter.cs`
- Test: `tests/OrdoSort.Core.Tests/XlsxWriterTests.cs`

**Interfaces:**
- Consumes: nothing new (`ZipArchive`, `XDocument` in tests).
- Produces (Task 2 consumes):
  - `public static class XlsxWriter` (public, unlike the internal reader — the export builder and its tests call it, and Phase 4's production export will too)
  - `public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows)` (nested)
  - `public static void Write(string path, IReadOnlyList<Sheet> sheets)`

Cell mapping: `null` → cell omitted; `int`/`long`/`double`/`decimal` → numeric cell (`<v>` only, no `t` attribute, so Excel treats it as a number); everything else → `ToString()` as an inline string, XML-escaped. Doubles format with `"R"`; no date cells — callers pre-format dates as strings.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/XlsxWriterTests.cs
using System.IO.Compression;
using System.Xml.Linq;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The writer's output must be a real xlsx: readable back through
/// XlsxTable AND structurally complete enough for Excel (content types,
/// package rels, workbook rels — the parts the reader's minimal fallback
/// path tolerates being absent, but Excel does not).</summary>
public class XlsxWriterTests : IDisposable
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Ct =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly string _dir = Directory.CreateTempSubdirectory("ordoxw_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string Write(params XlsxWriter.Sheet[] sheets)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        XlsxWriter.Write(path, sheets);
        return path;
    }

    private static XDocument Part(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var s = zip.GetEntry(entry)!.Open();
        return XDocument.Load(s);
    }

    [Fact]
    public void StringsAndNumbersRoundTripThroughTheReader()
    {
        var path = Write(new XlsxWriter.Sheet("Summary", new object?[][]
        {
            new object?[] { "Label", "Count", "Percent" },
            new object?[] { "0-1 days", 20953, 96.6 },
        }));
        var rows = XlsxTable.Read(path);
        Assert.Equal(new[] { "Label", "Count", "Percent" }, rows[0]);
        Assert.Equal(new[] { "0-1 days", "20953", "96.6" }, rows[1]);
    }

    [Fact]
    public void XmlSpecialCharactersInCellTextSurvive()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { "a<b&c>\"d'" },
        }));
        Assert.Equal("a<b&c>\"d'", XlsxTable.Read(path)[0][0]);
    }

    [Fact]
    public void NullCellsAreOmittedAndLaterColumnsStayPut()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { "A", null, "C" },
        }));
        // XlsxTable back-fills the omitted cell from the r= reference
        Assert.Equal(new[] { "A", "", "C" }, XlsxTable.Read(path)[0]);
    }

    [Fact]
    public void NumericCellsCarryNoTypeAttribute()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { 42, "x" },
        }));
        var cells = Part(path, "xl/worksheets/sheet1.xml")
            .Descendants(Main + "c").ToList();
        Assert.Null(cells[0].Attribute("t"));                       // number
        Assert.Equal("inlineStr", (string?)cells[1].Attribute("t")); // string
    }

    [Fact]
    public void TwoSheetsProduceTwoNamedPartsInOrder()
    {
        var path = Write(
            new XlsxWriter.Sheet("Summary", new object?[][] { new object?[] { "s" } }),
            new XlsxWriter.Sheet("Documents", new object?[][] { new object?[] { "d" } }));

        var names = Part(path, "xl/workbook.xml").Descendants(Main + "sheet")
            .Select(s => (string?)s.Attribute("name")).ToList();
        Assert.Equal(new[] { "Summary", "Documents" }, names);

        Assert.Equal("d", (string)Part(path, "xl/worksheets/sheet2.xml")
            .Descendants(Main + "t").Single());
        // XlsxTable resolves the FIRST sheet through the workbook rels
        Assert.Equal("s", XlsxTable.Read(path)[0][0]);
    }

    /// <summary>The parts Excel refuses to open a package without — the
    /// reader tolerates their absence, so the round-trip test alone can't
    /// prove they exist.</summary>
    [Fact]
    public void PackageCarriesContentTypesAndRels()
    {
        var path = Write(
            new XlsxWriter.Sheet("A", new object?[][] { new object?[] { 1 } }),
            new XlsxWriter.Sheet("B", new object?[][] { new object?[] { 2 } }));
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        Assert.NotNull(zip.GetEntry("xl/_rels/workbook.xml.rels"));
        var overrides = Part(path, "[Content_Types].xml").Descendants(Ct + "Override")
            .Select(o => (string?)o.Attribute("PartName")).ToList();
        Assert.Contains("/xl/workbook.xml", overrides);
        Assert.Contains("/xl/worksheets/sheet1.xml", overrides);
        Assert.Contains("/xl/worksheets/sheet2.xml", overrides);
    }

    [Fact]
    public void ColumnsPastZUseTwoLetterReferences()
    {
        var row = Enumerable.Range(0, 28).Select(i => (object?)$"c{i}").ToArray();
        var path = Write(new XlsxWriter.Sheet("S", new[] { row }));
        Assert.Equal(row.Select(v => (string)v!), XlsxTable.Read(path)[0]);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `XlsxWriter` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/XlsxWriter.cs
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OrdoSort.Core;

/// <summary>A minimal xlsx writer — XlsxTable's counterpart, hand-written in
/// the same self-contained style rather than pulling a package in. Unlike
/// the reader (which tolerates the barest zip a test can build), this must
/// write a package Excel itself opens: [Content_Types].xml, the package
/// rels, xl/workbook.xml, its rels, and one worksheet part per sheet.
/// Strings are inline (no shared-string table — these exports are written
/// once and read by a human, not diffed for size), numbers are real numeric
/// cells so Excel doesn't flag every count with a green triangle. No
/// styling, no column widths, no date cells — callers pre-format dates as
/// strings, which is how every date already reaches the UI anyway.</summary>
public static class XlsxWriter
{
    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows);

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var contentTypes = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        var workbook = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var workbookRels = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (var i = 0; i < sheets.Count; i++)
        {
            var n = i + 1;
            contentTypes.Append(
                $"<Override PartName=\"/xl/worksheets/sheet{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            workbook.Append(
                $"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
            workbookRels.Append(
                $"<Relationship Id=\"rId{n}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
            Entry(zip, $"xl/worksheets/sheet{n}.xml", SheetXml(sheets[i]));
        }

        contentTypes.Append("</Types>");
        workbook.Append("</sheets></workbook>");
        workbookRels.Append("</Relationships>");

        Entry(zip, "[Content_Types].xml", contentTypes.ToString());
        Entry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        Entry(zip, "xl/workbook.xml", workbook.ToString());
        Entry(zip, "xl/_rels/workbook.xml.rels", workbookRels.ToString());
    }

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            var row = sheet.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (value is null) continue;   // omitted; the reader back-fills from r=
                var cellRef = $"{ColumnRef(c)}{r + 1}";
                if (value is int or long or double or decimal)
                {
                    var text = value switch
                    {
                        double d => d.ToString("R", CultureInfo.InvariantCulture),
                        decimal m => m.ToString(CultureInfo.InvariantCulture),
                        long l => l.ToString(CultureInfo.InvariantCulture),
                        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
                    };
                    sb.Append($"<c r=\"{cellRef}\"><v>{text}</v></c>");
                }
                else
                {
                    sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(Escape(value.ToString() ?? ""))
                      .Append("</t></is></c>");
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>0 → "A", 25 → "Z", 26 → "AA" — the inverse of
    /// XlsxTable.ColumnIndex.</summary>
    private static string ColumnRef(int index)
    {
        var s = "";
        for (var i = index; i >= 0; i = i / 26 - 1)
            s = (char)('A' + i % 26) + s;
        return s;
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~XlsxWriterTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/XlsxWriter.cs tests/OrdoSort.Core.Tests/XlsxWriterTests.cs
git commit -m "feat(core): XlsxWriter — the reader's counterpart, a package Excel opens"
```

---

### Task 2: TurnaroundExport and TurnaroundSummaryText

The two-sheet workbook (spec decision 9: sheet 1 the summary figures including every set-aside count, sheet 2 the detail rows) and the clipboard text for Copy summary. All formatting decisions live here, tested — the view model just calls these.

**Files:**
- Create: `src/OrdoSort.Core/TurnaroundExport.cs`
- Test: `tests/OrdoSort.Core.Tests/TurnaroundExportTests.cs`

**Interfaces:**
- Consumes: `XlsxWriter.Write` (Task 1); `TurnaroundSummary.Summary`/`Doc`/`BucketCounts`/`MonthLine`/`SourceLine`/`IgnoredSource`; `UploadReportFeed.LoadReport` — all as they exist on main.
- Produces (Task 4's view model consumes both):
  - `public static class TurnaroundExport`
  - `public static void Write(string path, TurnaroundSummary.Summary summary, UploadReportFeed.LoadReport report, string sourceFolder)`
  - `public static string BuildCopyText(TurnaroundSummary.Summary summary, UploadReportFeed.LoadReport report)`
  - `public static string MonthName(string month)` — `"2026-07"` → `"Jul"` (shared by the view model's month grid and delta chip)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/TurnaroundExportTests.cs
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Export and copy-text are the two surfaces where a formatting
/// slip silently misreports the SLA numbers to leadership — so the exact
/// strings and cell values are pinned here against a small computed summary
/// (built through Compute, not hand-assembled, so these tests break if the
/// engine's shapes drift). 2026-07-06 is a Monday.</summary>
public class TurnaroundExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordotx_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private const string R1 = "20260706-0900-PECF Report.xlsx";

    private static SweptTable.Row Row(string fileName, string source) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FileName"] = fileName, ["SourceType"] = source,
            ["Pagecount"] = "10", ["Destination"] = "MIX",
        }, R1);

    private static readonly TurnaroundSummary.Summary Summary = TurnaroundSummary.Compute(
        new SweptTable.Table(
            new[] { "FileName", "SourceType", "Pagecount", "Destination" },
            new[]
            {
                Row("20260706-A.pdf", "Email"),   // Same day
                Row("20260703-B.pdf", "Email"),   // Fri→Mon = 1
                Row("20260702-C.pdf", "FAX"),     // Thu→Mon = 2
                Row("20260701-D.pdf", "Paper"),   // Wed→Mon = 3+
                Row("07022026 E.pdf", "ECAA"),    // ignored
                Row("20260707-F.pdf", "Email"),   // future-dated
            },
            FilesRead: 1, FileErrors: Array.Empty<string>()),
        new IgnoreList(new[] { "ECAA" }));

    private static readonly UploadReportFeed.LoadReport Report = new(
        FilesFound: 1, Skipped: Array.Empty<string>(),
        FirstUpload: new DateOnly(2026, 7, 6), LastUpload: new DateOnly(2026, 7, 6),
        RowCount: 6);

    [Fact]
    public void MonthNameIsInvariantThreeLetter()
    {
        Assert.Equal("Jul", TurnaroundExport.MonthName("2026-07"));
        Assert.Equal("Dec", TurnaroundExport.MonthName("2026-12"));
    }

    [Fact]
    public void CopyTextCarriesTheHeadlineEveryBucketAndEverySetAside()
    {
        var text = TurnaroundExport.BuildCopyText(Summary, Report);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Turn-around time — 2026-07-06 to 2026-07-06 (1 files, 6 rows)", lines[0]);
        Assert.Equal("0-1 business days: 50.0% (2 of 4) · 2 days: 25.0% (1) · 3+ days: 25.0% (1)", lines[1]);
        Assert.Equal("Jul: 50.0% in 0-1", lines[2]);
        // Email holds A (Same day) and B (1 day) — both inside 0-1, so 100.0%.
        Assert.Equal("By source (0-1 share): Email 100.0% · FAX 0.0% · Paper 0.0%", lines[3]);
        Assert.Equal("Set aside: 0 duplicates · 1 future-dated · 0 without a date · ECAA 1 ignored", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void WorkbookSheetOneCarriesFiguresAndSheetTwoCarriesTheDocuments()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TurnaroundExport.Write(path, Summary, Report, @"\\server\share");

        var summarySheet = XlsxTable.Read(path);   // reads the FIRST sheet
        // Row 0 is the title block; find pinned rows by their labels.
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Source folder" && r[1] == @"\\server\share");
        Assert.Contains(summarySheet, r => r.Count >= 3 && r[0] == "0-1 business days" && r[1] == "2" && r[2] == "50");
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Future-dated" && r[1] == "1");
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Ignored: ECAA" && r[1] == "1");

        // Sheet 2: header + one row per measurable doc, dates pre-formatted.
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
    }

    [Fact]
    public void DetailRowsMatchTheMeasurableDocs()
    {
        var path = Path.Combine(_dir, "d.xlsx");
        TurnaroundExport.Write(path, Summary, Report, "x");
        // Rewrite sheet 2 alone through the writer to read it back via
        // XlsxTable (which reads only the first sheet): instead, assert via
        // the builder's own row source — the Docs list drives sheet 2 1:1.
        Assert.Equal(4, Summary.Docs.Count);
        var detail = TurnaroundExport.DetailRows(Summary);
        Assert.Equal("FileName", detail[0][0]);
        Assert.Equal(5, detail.Count);   // header + 4 docs
        Assert.Contains(detail, r => (string?)r[0] == "20260706-A.pdf" && (string?)r[4] == "2026-07-06"
            && (string?)r[6] == "0" && (string?)r[7] == "Same day");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `TurnaroundExport` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/TurnaroundExport.cs
using System.Globalization;
using System.Text;

namespace OrdoSort.Core;

/// <summary>The Turn-around page's two outward surfaces (spec decision 9):
/// the two-sheet workbook Export writes, and the plain text Copy summary
/// puts on the clipboard for pasting into email. Both include every
/// set-aside count next to the figures it affects, so the denominator can
/// be defended when questioned. All formatting is invariant and pinned by
/// tests — the view model calls these, it never formats a published figure
/// itself.</summary>
public static class TurnaroundExport
{
    /// <summary>"2026-07" → "Jul". Invariant month abbreviations — the same
    /// label the month grid and the delta chip render.</summary>
    public static string MonthName(string month) =>
        DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture)
            .ToString("MMM", CultureInfo.InvariantCulture);

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string N0(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static string BuildCopyText(TurnaroundSummary.Summary summary,
        UploadReportFeed.LoadReport report)
    {
        var o = summary.Overall;
        var sb = new StringBuilder();
        sb.Append("Turn-around time — ")
          .Append(report.FirstUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?")
          .Append(" to ")
          .Append(report.LastUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?")
          .Append($" ({N0(report.FilesFound)} files, {N0(report.RowCount)} rows)").Append('\n');
        sb.Append($"0-1 business days: {F1(o.ZeroToOnePercent)}% ({N0(o.ZeroToOne)} of {N0(o.Total)})")
          .Append($" · 2 days: {F1(o.TwoPercent)}% ({N0(o.TwoDays)})")
          .Append($" · 3+ days: {F1(o.ThreePlusPercent)}% ({N0(o.ThreePlus)})").Append('\n');
        sb.Append(string.Join(" · ", summary.ByMonth.Select(m =>
            $"{MonthName(m.Month)}: {F1(m.Counts.ZeroToOnePercent)}% in 0-1"))).Append('\n');
        sb.Append("By source (0-1 share): ").Append(string.Join(" · ",
            summary.BySource.Select(s => $"{s.SourceType} {F1(s.Counts.ZeroToOnePercent)}%"))).Append('\n');
        sb.Append($"Set aside: {N0(summary.DuplicateRows)} duplicates")
          .Append($" · {N0(summary.FutureDated)} future-dated")
          .Append($" · {N0(summary.NoDate)} without a date");
        foreach (var ig in summary.Ignored)
            sb.Append($" · {ig.Value} {N0(ig.Count)} ignored");
        return sb.ToString();
    }

    public static void Write(string path, TurnaroundSummary.Summary summary,
        UploadReportFeed.LoadReport report, string sourceFolder)
    {
        XlsxWriter.Write(path, new[]
        {
            new XlsxWriter.Sheet("Summary", SummaryRows(summary, report, sourceFolder)),
            new XlsxWriter.Sheet("Documents", DetailRows(summary)),
        });
    }

    private static IReadOnlyList<IReadOnlyList<object?>> SummaryRows(
        TurnaroundSummary.Summary summary, UploadReportFeed.LoadReport report, string sourceFolder)
    {
        var o = summary.Overall;
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Turn-around time" },
            new object?[] { "Source folder", sourceFolder },
            new object?[] { "Files found", report.FilesFound },
            new object?[] { "Files skipped", report.Skipped.Count },
            new object?[] { "Rows", report.RowCount },
            new object?[] { "Upload span",
                $"{report.FirstUpload:yyyy-MM-dd} to {report.LastUpload:yyyy-MM-dd}" },
            Array.Empty<object?>(),
            new object?[] { "Bucket", "Documents", "Percent" },
            new object?[] { "Same day", o.SameDay, Math.Round(o.Total == 0 ? 0 : 100.0 * o.SameDay / o.Total, 2) },
            new object?[] { "1 business day", o.OneDay, Math.Round(o.Total == 0 ? 0 : 100.0 * o.OneDay / o.Total, 2) },
            new object?[] { "2 business days", o.TwoDays, Math.Round(o.TwoPercent, 2) },
            new object?[] { "3+ business days", o.ThreePlus, Math.Round(o.ThreePlusPercent, 2) },
            new object?[] { "0-1 business days", o.ZeroToOne, Math.Round(o.ZeroToOnePercent, 2) },
            new object?[] { "Measurable documents", o.Total },
            Array.Empty<object?>(),
            new object?[] { "Month", "Documents", "0-1 %", "2 days", "3+ days" },
        };
        rows.AddRange(summary.ByMonth.Select(m => (IReadOnlyList<object?>)new object?[]
        {
            MonthName(m.Month), m.Counts.Total, Math.Round(m.Counts.ZeroToOnePercent, 2),
            m.Counts.TwoDays, m.Counts.ThreePlus,
        }));
        rows.Add(Array.Empty<object?>());
        rows.Add(new object?[] { "Source", "Documents", "0-1 %", "2 days", "3+ days" });
        rows.AddRange(summary.BySource.Select(s => (IReadOnlyList<object?>)new object?[]
        {
            s.SourceType, s.Counts.Total, Math.Round(s.Counts.ZeroToOnePercent, 2),
            s.Counts.TwoDays, s.Counts.ThreePlus,
        }));
        rows.Add(Array.Empty<object?>());
        rows.Add(new object?[] { "Set aside", "Count" });
        rows.Add(new object?[] { "Duplicates", summary.DuplicateRows });
        rows.Add(new object?[] { "Future-dated", summary.FutureDated });
        rows.Add(new object?[] { "Without a date", summary.NoDate });
        rows.AddRange(summary.Ignored.Select(ig => (IReadOnlyList<object?>)new object?[]
        {
            $"Ignored: {ig.Value}", ig.Count,
        }));
        return rows;
    }

    /// <summary>Sheet 2's rows — internal-shaped but public so the test can
    /// pin the content without re-reading a second sheet the minimal reader
    /// can't reach. Dates pre-formatted, invariant.</summary>
    public static IReadOnlyList<IReadOnlyList<object?>> DetailRows(TurnaroundSummary.Summary summary)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "FileName", "SourceType", "Pagecount", "Destination",
                "DocDate", "UploadDate", "BusinessDays", "Bucket", "SourceReport" },
        };
        rows.AddRange(summary.Docs.Select(d => (IReadOnlyList<object?>)new object?[]
        {
            d.FileName, d.SourceType, d.Pagecount, d.Destination,
            d.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.UploadDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.BusinessDays.ToString(CultureInfo.InvariantCulture),
            BucketLabel(d.Bucket),
            Path.GetFileName(d.SourceFile),
        }));
        return rows;
    }

    public static string BucketLabel(TurnaroundSummary.Bucket bucket) => bucket switch
    {
        TurnaroundSummary.Bucket.SameDay => "Same day",
        TurnaroundSummary.Bucket.OneDay => "1 day",
        TurnaroundSummary.Bucket.TwoDays => "2 days",
        _ => "3+ days",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~TurnaroundExportTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 4`. If a pinned string differs only in
formatting produced by the code as written (not a wrong number), fix the TEST
to the code only when the code matches this plan verbatim and the value is
correct — otherwise fix the code.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/TurnaroundExport.cs tests/OrdoSort.Core.Tests/TurnaroundExportTests.cs
git commit -m "feat(core): the two-sheet TAT export and the copy-summary text, formats pinned"
```

---

### Task 3: Config key `reports_upload_folder`

The hub's own persisted upload-feed folder (spec § Config). Distinct from the
old window's `tat_report_folder`, which stays untouched until Phase 4.
**This task explicitly joins the two contracts Phase 1's final review caught
being missed:** `Normalize()` null-hardening and the config-key test suites.

**Files:**
- Modify: `src/OrdoSort.Core/Config.cs` — property after `TatIgnoredSources` (~line 121); `Normalize()` line after `TatIgnoredSources = Clean(TatIgnoredSources);` (~line 462)
- Test: modify `tests/OrdoSort.Core.Tests/ConfigNewKeysTests.cs` and `tests/OrdoSort.Core.Tests/ConfigNullKeysTests.cs`

**Interfaces:**
- Produces: `Config.ReportsUploadFolder : string` under JSON key `reports_upload_folder`, default `""`, null-hardened.

- [ ] **Step 1: Write the failing tests**

In `ConfigNewKeysTests.NewKeysRoundTripWithExactJsonNames`, add to the
constructed `Config`: `ReportsUploadFolder = @"\\server\share\CAVO_REPORTS",`
and alongside the sibling assertions:
```csharp
        Assert.Contains("\"reports_upload_folder\"", json);
```
and in the load-back block (mirroring how the test verifies its siblings):
```csharp
        Assert.Equal(@"\\server\share\CAVO_REPORTS", back.ReportsUploadFolder);
```
In `ConfigNullKeysTests`, alongside the sibling null-string checks (same
pattern the file uses for `tat_report_folder`-class keys), add
`"reports_upload_folder": null` to the null-payload JSON and:
```csharp
        Assert.Equal("", cfg.ReportsUploadFolder);
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `ReportsUploadFolder` does not exist.

- [ ] **Step 3: Implement**

In `Config.cs`, directly after the `TatIgnoredSources` property:
```csharp
    [JsonPropertyName("reports_upload_folder")] public string ReportsUploadFolder { get; set; } = "";
```
In `Normalize()`, directly after `TatIgnoredSources = Clean(TatIgnoredSources);`:
```csharp
        ReportsUploadFolder ??= "";
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~Config" -v minimal
```
Expected: all Config tests pass, nonzero count, including the two new
assertions.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/Config.cs tests/OrdoSort.Core.Tests/ConfigNewKeysTests.cs tests/OrdoSort.Core.Tests/ConfigNullKeysTests.cs
git commit -m "feat(core): reports_upload_folder config key, null-hardened from day one"
```

---

### Task 4: The three view models

`ReportsViewModel` (coordinator: owns the feed table, the summary, the ignore
set, and navigation), `SourcesPageViewModel` and `TurnaroundPageViewModel`
(display slices + commands). Loading follows `TurnaroundViewModel`'s exact
`DebouncedProbe` shape; every published figure is formatted by Task 2's Core
code, never here.

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/ReportsViewModel.cs`
- Create: `src/OrdoSort.Wpf/ViewModels/SourcesPageViewModel.cs`
- Create: `src/OrdoSort.Wpf/ViewModels/TurnaroundPageViewModel.cs`

**Interfaces:**
- Consumes: `UploadReportFeed.Load(string) : Result(Table, Report)`;
  `TurnaroundSummary.Compute(SweptTable.Table, IgnoreList) : Summary` (with
  `Docs`, `Overall`, `ByMonth`, `BySource`, `ByWeek`, `Ignored`,
  `DuplicateRowsDetail`, `FutureDatedDetail`, `NoDateDetail`, `IgnoredDetail`
  and computed `DuplicateRows/FutureDated/NoDate`);
  `IgnoreList` (ctor + `Discover`); `TurnaroundExport.Write/BuildCopyText/MonthName/BucketLabel`;
  `Config.ReportsUploadFolder`, `Config.TatIgnoredSources`;
  `ObservableObject`, `RelayCommand`, `DebouncedProbe<T>`, `IWorkScheduler`/`TaskWorkScheduler`, `IDialogService`.
- Produces (Task 5's XAML binds these; names are binding contracts):
  - `ReportsViewModel`: `SourcesPageViewModel Sources`, `TurnaroundPageViewModel Turnaround`, `object CurrentPage`, `int SelectedPageIndex` (0 = Turn-around, 1 = Sources), `string FooterText`, `void ShowPage(int index)`, `void Dispose()`
  - `SourcesPageViewModel`: `string FolderPath`, `string StatusText`, `string SkippedText`, `bool HasSkipped`, `ObservableCollection<IgnoreEntryVm> IgnoreEntries`, `RelayCommand BrowseCommand`, `RelayCommand RefreshCommand`; `IgnoreEntryVm(string Value, int Count)` with `bool IsIncluded { get; set; }`
  - `TurnaroundPageViewModel`: `string HeroPercentText`, `string DeltaChipText`, `bool HasDelta`, `string SameDayText/OneDayText/TwoDaysText/ThreePlusText`, `ObservableCollection<MonthRowVm> MonthRows`, `ObservableCollection<SourceRowVm> SourceRows`, `ObservableCollection<SparkBarVm> SparkBars`, `ObservableCollection<SetAsideChipVm> SetAsideChips`, `int SelectedTabIndex` (0 = Summary, 1 = Detail), `string ContextText`, `ObservableCollection<string> DetailSources`, `string SelectedDetailSource`, `string DetailFilter`, `ObservableCollection<DetailRowVm> DetailRows`, `string DetailCountText`, `bool HasData`, `RelayCommand ExportCommand`, `RelayCommand CopySummaryCommand`, `RelayCommand RefreshCommand`

- [ ] **Step 1: Write the implementation** (no VM test project — see Global
  Constraints; the compile is the red/green here, and Task 6 verifies live)

```csharp
// src/OrdoSort.Wpf/ViewModels/ReportsViewModel.cs
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Reports hub's coordinator: owns the upload feed's loaded
/// table, the ignore set, and the computed summary; the two page view models
/// are display slices over state that lives here. One DebouncedProbe runs
/// both the full reload (folder walk + parse + compute) and the cheap
/// ignore-toggle recompute (compute only, over the cached table) — the same
/// off-thread/apply-on-UI shape TurnaroundViewModel uses, and the same
/// stale-probe protection: a slow load can never overwrite a newer one.</summary>
public sealed class ReportsViewModel : ObservableObject, IDisposable
{
    private readonly Config _cfg;
    private readonly Action? _saveCfg;
    internal readonly IDialogService Dialogs;
    internal readonly IWorkScheduler Scheduler;
    private readonly DebouncedProbe<Snapshot> _probe;

    /// <summary>Everything one load/recompute produces, applied atomically
    /// on the UI thread so no panel ever binds half of one load and half of
    /// another (spec decision 8).</summary>
    internal sealed record Snapshot(UploadReportFeed.Result Feed, TurnaroundSummary.Summary Summary,
        IReadOnlyList<IgnoreList.Entry> IgnoreEntries);

    internal Snapshot? Current { get; private set; }

    public SourcesPageViewModel Sources { get; }
    public TurnaroundPageViewModel Turnaround { get; }

    public ReportsViewModel(Config cfg, IDialogService dialogs, Action? saveCfg,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        int probeDelayMs = 300)
    {
        _cfg = cfg;
        Dialogs = dialogs;
        _saveCfg = saveCfg;
        Scheduler = scheduler ?? new TaskWorkScheduler();
        _probe = new DebouncedProbe<Snapshot>(Scheduler, uiContext, Apply, probeDelayMs);

        Sources = new SourcesPageViewModel(this);
        Turnaround = new TurnaroundPageViewModel(this);
        _currentPage = Turnaround;

        Reload(immediate: true);
    }

    public void Dispose() => _probe.Dispose();

    // ---------------------------------------------------------- navigation
    private object _currentPage;
    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    private int _selectedPageIndex;
    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (!Set(ref _selectedPageIndex, value)) return;
            CurrentPage = value == 1 ? Sources : Turnaround;
        }
    }

    public void ShowPage(int index) => SelectedPageIndex = index;

    private string _footerText = "";
    public string FooterText { get => _footerText; private set => Set(ref _footerText, value); }

    // ------------------------------------------------------------- loading
    internal string Folder
    {
        get => _cfg.ReportsUploadFolder;
        set
        {
            if (_cfg.ReportsUploadFolder == value) return;
            _cfg.ReportsUploadFolder = value;
            _saveCfg?.Invoke();
            Reload(immediate: true);
        }
    }

    /// <summary>Full reload: walk the folder, parse every report, compute.
    /// An empty folder path resolves synchronously to an empty snapshot —
    /// cancelling any in-flight probe (DebouncedProbe.Cancel's documented
    /// contract) so a slow stale load can't repopulate the hub afterwards.</summary>
    internal void Reload(bool immediate = false)
    {
        var folder = _cfg.ReportsUploadFolder;
        var ignored = _cfg.TatIgnoredSources.ToArray();

        if (folder.Length == 0)
        {
            _probe.Cancel();
            Apply(EmptySnapshot(ignored));
            return;
        }
        _probe.Trigger(() => Build(UploadReportFeed.Load(folder), ignored), immediate);
    }

    /// <summary>Ignore-toggle path: recompute over the cached table without
    /// re-walking the share. Falls back to a full reload when nothing is
    /// cached yet.</summary>
    internal void SetIgnored(string value, bool ignored)
    {
        var list = _cfg.TatIgnoredSources;
        if (ignored && !list.Contains(value, StringComparer.Ordinal)) list.Add(value);
        if (!ignored) list.RemoveAll(v => string.Equals(v, value, StringComparison.Ordinal));
        _saveCfg?.Invoke();

        if (Current is not { } current) { Reload(immediate: true); return; }
        var feed = current.Feed;
        var ignoredNow = list.ToArray();
        _probe.Trigger(() => Build(feed, ignoredNow), immediate: true);
    }

    private static Snapshot Build(UploadReportFeed.Result feed, IReadOnlyList<string> ignoredValues)
    {
        var ignore = new IgnoreList(ignoredValues);
        var summary = TurnaroundSummary.Compute(feed.Table, ignore);
        var discovered = ignore.Discover(feed.Table.Rows.Select(r =>
            r.Cells.TryGetValue(TurnaroundSummary.SourceTypeColumn, out var v) ? v : ""));
        return new Snapshot(feed, summary, discovered);
    }

    private static Snapshot EmptySnapshot(IReadOnlyList<string> ignoredValues)
    {
        var empty = new UploadReportFeed.Result(
            new SweptTable.Table(Array.Empty<string>(), Array.Empty<SweptTable.Row>(),
                0, Array.Empty<string>()),
            new UploadReportFeed.LoadReport(0, Array.Empty<string>(), null, null, 0));
        return Build(empty, ignoredValues);
    }

    /// <summary>UI thread only (probe marshal or the empty fast path).</summary>
    private void Apply(Snapshot snapshot)
    {
        Current = snapshot;
        Sources.Apply(snapshot);
        Turnaround.Apply(snapshot);
        FooterText = $"{snapshot.Feed.Report.FilesFound} files · {snapshot.Feed.Report.RowCount} rows";
    }
}
```

```csharp
// src/OrdoSort.Wpf/ViewModels/SourcesPageViewModel.cs
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
```

```csharp
// src/OrdoSort.Wpf/ViewModels/TurnaroundPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
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

    /// <summary>Chip click: land on Detail with that set-aside selected.</summary>
    public void InspectSetAside(string key)
    {
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
            HasDelta = true;
        }
        else { DeltaChipText = ""; HasDelta = false; }

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

    private static DetailRowVm FromRawRow(SweptTable.Row row)
    {
        string Cell(string column) => row.Cells.TryGetValue(column, out var v) ? v : "";
        return new DetailRowVm(
            Cell(TurnaroundSummary.FileNameColumn), Cell(TurnaroundSummary.SourceTypeColumn),
            Cell(TurnaroundSummary.PagecountColumn), Cell(TurnaroundSummary.DestinationColumn),
            "—", "—", "—", "—");
    }

    // ------------------------------------------------------------- commands
    private void CopySummary()
    {
        if (_snapshot is not { } snapshot) return;
        var text = TurnaroundExport.BuildCopyText(snapshot.Summary, snapshot.Feed.Report);
        Clipboard.SetText(text);
        _owner.Dialogs.Info("Summary copied to the clipboard.", "OrdoSort");
    }

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
```

- [ ] **Step 2: Build clean**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **Build succeeded, 0 warnings** from the new files. If
`IWorkScheduler.Run` or `RelayCommand`'s signature differs from this
reference code, adapt to the real signature (read the file) — the interfaces
above were written against main but the real file wins.

- [ ] **Step 3: Run the Core suite (regression only)**

```
dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal
```
Expected: everything passes (these files add no Core changes).

- [ ] **Step 4: Commit**

```
git add src/OrdoSort.Wpf/ViewModels/ReportsViewModel.cs src/OrdoSort.Wpf/ViewModels/SourcesPageViewModel.cs src/OrdoSort.Wpf/ViewModels/TurnaroundPageViewModel.cs
git commit -m "feat(ui): the reports hub's three view models — one snapshot, two page slices"
```

---

### Task 5: Views, window, and menu wiring

`ReportsWindow` (non-modal singleton, sidebar + page host), the two page
UserControls, and the menu: a new "Reports hub…" entry plus the existing
"Turn-around time…" entry re-targeted to the hub. "Production reports…" and
both old windows stay exactly as they are.

**Files:**
- Create: `src/OrdoSort.Wpf/Windows/ReportsWindow.xaml` + `.xaml.cs`
- Create: `src/OrdoSort.Wpf/Views/TurnaroundPageView.xaml` + `.xaml.cs`
- Create: `src/OrdoSort.Wpf/Views/SourcesPageView.xaml` + `.xaml.cs`
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml` (Reports menu block, currently lines 346–353) and `src/OrdoSort.Wpf/MainWindow.xaml.cs` (`OnTurnaroundReport`, currently lines 355–361, plus one new handler and one field)

**Interfaces:**
- Consumes: every Task 4 binding name exactly as listed there; theme
  resources and styles from Global Constraints.
- Produces: `ReportsWindow(ReportsViewModel vm)` with `void ShowPage(int index)`;
  `MainWindow.OnReportsHub` handler.

- [ ] **Step 1: Write the window**

```xml
<!-- src/OrdoSort.Wpf/Windows/ReportsWindow.xaml -->
<Window x:Class="OrdoSort.Wpf.Windows.ReportsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:OrdoSort.Wpf.ViewModels"
        xmlns:views="clr-namespace:OrdoSort.Wpf.Views"
        Title="OrdoSort — Reports" Width="1100" Height="720" MinWidth="900" MinHeight="600"
        WindowStartupLocation="CenterOwner"
        Style="{StaticResource {x:Type Window}}">
    <Window.Resources>
        <DataTemplate DataType="{x:Type vm:TurnaroundPageViewModel}">
            <views:TurnaroundPageView />
        </DataTemplate>
        <DataTemplate DataType="{x:Type vm:SourcesPageViewModel}">
            <views:SourcesPageView />
        </DataTemplate>
    </Window.Resources>
    <DockPanel>
        <!-- sidebar: the hub's page switcher. A ListBox, not a TabControl —
             the pages are peers with their own toolbars, and Phase 4 adds
             Production between them. -->
        <Border DockPanel.Dock="Left" Width="190"
                Background="{DynamicResource Theme.Surface}"
                BorderBrush="{DynamicResource Theme.Border}" BorderThickness="0,0,1,0">
            <DockPanel Margin="0,14">
                <TextBlock DockPanel.Dock="Bottom" Text="{Binding FooterText}"
                           Style="{StaticResource CaptionText}" Margin="14,10,14,0"
                           TextWrapping="Wrap" />
                <StackPanel>
                    <TextBlock Text="Reports" Style="{StaticResource FieldLabel}"
                               FontSize="15" FontWeight="SemiBold" Margin="14,0,14,12" />
                    <ListBox SelectedIndex="{Binding SelectedPageIndex}"
                             BorderThickness="0" Background="Transparent">
                        <ListBoxItem Content="Turn-around" Padding="14,8" />
                        <ListBoxItem Content="Sources" Padding="14,8" />
                    </ListBox>
                </StackPanel>
            </DockPanel>
        </Border>
        <ContentControl Content="{Binding CurrentPage}" Margin="18,14" Focusable="False" />
    </DockPanel>
</Window>
```

```csharp
// src/OrdoSort.Wpf/Windows/ReportsWindow.xaml.cs
using System.Windows;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class ReportsWindow : Window
{
    private readonly ReportsViewModel _vm;

    public ReportsWindow(ReportsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    public void ShowPage(int index) => _vm.ShowPage(index);
}
```

- [ ] **Step 2: Write the Turn-around page**

```xml
<!-- src/OrdoSort.Wpf/Views/TurnaroundPageView.xaml -->
<UserControl x:Class="OrdoSort.Wpf.Views.TurnaroundPageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel>
        <!-- header: title + context, actions right -->
        <DockPanel DockPanel.Dock="Top" Margin="0,0,0,8">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Top">
                <Button Content="Refresh" Command="{Binding RefreshCommand}" Margin="0,0,8,0" />
                <Button Content="Copy summary" Command="{Binding CopySummaryCommand}" Margin="0,0,8,0" />
                <Button Content="Export to spreadsheet…" Command="{Binding ExportCommand}" />
            </StackPanel>
            <StackPanel>
                <TextBlock Text="Turn-around time" FontSize="19" FontWeight="SemiBold" />
                <TextBlock Text="{Binding ContextText}" Style="{StaticResource StatusText}" />
            </StackPanel>
        </DockPanel>

        <TabControl SelectedIndex="{Binding SelectedTabIndex}">
            <TabItem Header="Summary">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="0,10,0,0">
                        <!-- hero + bucket tiles -->
                        <Grid Margin="0,0,0,12">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="1.6*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Background="{DynamicResource Theme.SurfaceRaised}"
                                    BorderBrush="{DynamicResource Theme.Accent}" BorderThickness="1"
                                    CornerRadius="8" Padding="14,10" Margin="0,0,8,0">
                                <StackPanel>
                                    <TextBlock Text="0–1 BUSINESS DAYS" Style="{StaticResource CaptionText}" />
                                    <TextBlock Text="{Binding HeroPercentText}" FontSize="36" FontWeight="SemiBold"
                                               Foreground="{DynamicResource Theme.AccentText}" />
                                    <Border Background="{DynamicResource Theme.Surface}" CornerRadius="10"
                                            Padding="8,2" HorizontalAlignment="Left"
                                            Visibility="{Binding HasDelta, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="{Binding DeltaChipText}" Style="{StaticResource CaptionText}"
                                                   Foreground="{DynamicResource Theme.StatusGreen}" />
                                    </Border>
                                </StackPanel>
                            </Border>
                            <Border Grid.Column="1" Style="{StaticResource ReportTile}">
                                <StackPanel>
                                    <TextBlock Text="SAME DAY" Style="{StaticResource CaptionText}" />
                                    <TextBlock Text="{Binding SameDayText}" FontSize="20" FontWeight="SemiBold" />
                                </StackPanel>
                            </Border>
                            <Border Grid.Column="2" Style="{StaticResource ReportTile}">
                                <StackPanel>
                                    <TextBlock Text="1 DAY" Style="{StaticResource CaptionText}" />
                                    <TextBlock Text="{Binding OneDayText}" FontSize="20" FontWeight="SemiBold" />
                                </StackPanel>
                            </Border>
                            <Border Grid.Column="3" Style="{StaticResource ReportTile}">
                                <StackPanel>
                                    <TextBlock Text="2 DAYS" Style="{StaticResource CaptionText}" />
                                    <TextBlock Text="{Binding TwoDaysText}" FontSize="20" FontWeight="SemiBold" />
                                </StackPanel>
                            </Border>
                            <Border Grid.Column="4" Style="{StaticResource ReportTile}" Margin="8,0,0,0">
                                <StackPanel>
                                    <TextBlock Text="3+ DAYS" Style="{StaticResource CaptionText}" />
                                    <TextBlock Text="{Binding ThreePlusText}" FontSize="20" FontWeight="SemiBold"
                                               Foreground="{DynamicResource Theme.WarningText}" />
                                </StackPanel>
                            </Border>
                        </Grid>

                        <!-- month grid + by-source, side by side -->
                        <Grid Margin="0,0,0,12">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="1.25*" />
                            </Grid.ColumnDefinitions>
                            <GroupBox Header="By month" Margin="0,0,8,0">
                                <DataGrid ItemsSource="{Binding MonthRows}" AutoGenerateColumns="False"
                                          IsReadOnly="True" HeadersVisibility="Column">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="Month" Binding="{Binding Month}" Width="*" />
                                        <DataGridTextColumn Header="0–1 days" Binding="{Binding ZeroToOne}" Width="Auto" />
                                        <DataGridTextColumn Header="docs" Binding="{Binding CountNote}" Width="Auto" />
                                        <DataGridTextColumn Header="2" Binding="{Binding Two}" Width="Auto" />
                                        <DataGridTextColumn Header="3+" Binding="{Binding ThreePlus}" Width="Auto" />
                                    </DataGrid.Columns>
                                </DataGrid>
                            </GroupBox>
                            <GroupBox Grid.Column="1" Header="By source">
                                <DataGrid ItemsSource="{Binding SourceRows}" AutoGenerateColumns="False"
                                          IsReadOnly="True" HeadersVisibility="Column">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="Source" Binding="{Binding Source}" Width="*" />
                                        <DataGridTextColumn Header="Docs" Binding="{Binding Docs}" Width="Auto" />
                                        <DataGridTextColumn Header="0–1" Binding="{Binding ZeroToOne}" Width="Auto" />
                                        <DataGridTextColumn Header="2" Binding="{Binding Two}" Width="Auto" />
                                        <DataGridTextColumn Header="3+" Binding="{Binding ThreePlus}" Width="Auto" />
                                    </DataGrid.Columns>
                                </DataGrid>
                            </GroupBox>
                        </Grid>

                        <!-- weekly spark + set-aside chips -->
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="1.6*" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <GroupBox Header="Weekly · % in 0–1 days" Margin="0,0,8,0">
                                <ItemsControl ItemsSource="{Binding SparkBars}" Height="64" Margin="4">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate>
                                            <UniformGrid Rows="1" />
                                        </ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <!-- a drawn bar, no chart library: height is the
                                                 VM's precomputed fraction of the 60px strip -->
                                            <Border Background="{DynamicResource Theme.Accent}"
                                                    VerticalAlignment="Bottom" Margin="2,0"
                                                    CornerRadius="2,2,0,0"
                                                    Height="{Binding HeightFraction, Converter={StaticResource FractionToHeight}}"
                                                    ToolTip="{Binding Tooltip}" />
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </GroupBox>
                            <GroupBox Grid.Column="1" Header="Set aside — click to inspect">
                                <ItemsControl ItemsSource="{Binding SetAsideChips}" Margin="4">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Button Margin="0,0,6,6" Padding="10,4"
                                                    Click="OnSetAsideChipClick" Tag="{Binding Key}">
                                                <StackPanel Orientation="Horizontal">
                                                    <TextBlock Text="{Binding Label}" Margin="0,0,6,0" />
                                                    <TextBlock Text="{Binding CountText}" FontWeight="SemiBold"
                                                               Foreground="{DynamicResource Theme.StatusAmber}" />
                                                </StackPanel>
                                            </Button>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </GroupBox>
                        </Grid>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>

            <TabItem Header="Detail">
                <DockPanel Margin="0,10,0,0">
                    <Grid DockPanel.Dock="Top" Style="{StaticResource FieldRow}">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="220" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="Rows:" Style="{StaticResource FieldLabel}" />
                        <ComboBox Grid.Column="1" MinWidth="180" Margin="0,0,20,0"
                                  ItemsSource="{Binding DetailSources}"
                                  SelectedItem="{Binding SelectedDetailSource}" />
                        <TextBlock Grid.Column="2" Text="Filter:" Style="{StaticResource FieldLabel}" />
                        <TextBox Grid.Column="3"
                                 Text="{Binding DetailFilter, UpdateSourceTrigger=PropertyChanged}" />
                        <TextBlock Grid.Column="4" Text="{Binding DetailCountText}"
                                   Style="{StaticResource StatusText}" HorizontalAlignment="Right"
                                   VerticalAlignment="Center" />
                    </Grid>
                    <DataGrid ItemsSource="{Binding DetailRows}" AutoGenerateColumns="False"
                              IsReadOnly="True" Margin="0,8,0,0">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="File name" Binding="{Binding FileName}" Width="2*" />
                            <DataGridTextColumn Header="Source" Binding="{Binding SourceType}" Width="Auto" />
                            <DataGridTextColumn Header="Pages" Binding="{Binding Pagecount}" Width="Auto" />
                            <DataGridTextColumn Header="Destination" Binding="{Binding Destination}" Width="Auto" />
                            <DataGridTextColumn Header="Doc date" Binding="{Binding DocDate}" Width="Auto" />
                            <DataGridTextColumn Header="Uploaded" Binding="{Binding UploadDate}" Width="Auto" />
                            <DataGridTextColumn Header="Bus. days" Binding="{Binding Tat}" Width="Auto" />
                            <DataGridTextColumn Header="Bucket" Binding="{Binding Bucket}" Width="Auto" />
                        </DataGrid.Columns>
                    </DataGrid>
                </DockPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</UserControl>
```

```csharp
// src/OrdoSort.Wpf/Views/TurnaroundPageView.xaml.cs
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Views;

public partial class TurnaroundPageView : UserControl
{
    public TurnaroundPageView() => InitializeComponent();

    private void OnSetAsideChipClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TurnaroundPageViewModel vm &&
            sender is FrameworkElement { Tag: string key })
            vm.InspectSetAside(key);
    }
}
```

Two small resources this XAML needs, added where the app's shared resources
live (`src/OrdoSort.Wpf/Theme/Styles.xaml` — follow its existing structure):

```xml
    <!-- ReportTile: the hub's small stat tiles -->
    <Style x:Key="ReportTile" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Theme.Surface}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Theme.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="8" />
        <Setter Property="Padding" Value="14,10" />
        <Setter Property="Margin" Value="0,0,8,0" />
    </Style>
```

and a tiny converter (create `src/OrdoSort.Wpf/Theme/FractionToHeightConverter.cs`,
register it beside the app's existing converters — find where `BoolToVis` and
`TextToVis` are declared and add `FractionToHeight` the same way):

```csharp
using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Theme;

/// <summary>VM spark fractions (0..1) into pixel heights for the drawn
/// weekly bars — 60px strip, no charting dependency (spec decision 10).</summary>
public sealed class FractionToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double fraction ? Math.Max(2.0, fraction * 60.0) : 2.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 3: Write the Sources page**

```xml
<!-- src/OrdoSort.Wpf/Views/SourcesPageView.xaml -->
<UserControl x:Class="OrdoSort.Wpf.Views.SourcesPageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel MaxWidth="680" HorizontalAlignment="Left">
            <TextBlock Text="Sources" FontSize="19" FontWeight="SemiBold" />
            <TextBlock Text="Folders are scanned recursively · choices persist in config"
                       Style="{StaticResource StatusText}" Margin="0,0,0,14" />

            <!-- the upload-feed card; Phase 4 adds the other three -->
            <Border Background="{DynamicResource Theme.Surface}"
                    BorderBrush="{DynamicResource Theme.Border}" BorderThickness="1"
                    CornerRadius="8" Padding="16,12">
                <StackPanel>
                    <DockPanel Margin="0,0,0,8">
                        <TextBlock DockPanel.Dock="Right" Text="feeds Turn-around"
                                   Style="{StaticResource CaptionText}" VerticalAlignment="Center" />
                        <TextBlock Text="Upload reports" FontWeight="SemiBold" />
                    </DockPanel>
                    <DockPanel Margin="0,0,0,8">
                        <Button DockPanel.Dock="Right" Content="Refresh"
                                Command="{Binding RefreshCommand}" Margin="6,0,0,0" />
                        <Button DockPanel.Dock="Right" Content="Browse…"
                                Command="{Binding BrowseCommand}" Margin="6,0,0,0" />
                        <TextBox Text="{Binding FolderPath, UpdateSourceTrigger=LostFocus}" />
                    </DockPanel>
                    <TextBlock Text="{Binding StatusText}" Style="{StaticResource StatusText}"
                               Margin="0,0,0,6" />
                    <TextBlock Text="{Binding SkippedText}"
                               Foreground="{DynamicResource Theme.StatusAmber}"
                               Visibility="{Binding HasSkipped, Converter={StaticResource BoolToVis}}"
                               TextWrapping="Wrap" Margin="0,0,0,6" />
                    <TextBlock Text="SOURCE TYPES — UNCHECK TO SET ASIDE"
                               Style="{StaticResource CaptionText}" Margin="0,6,0,6" />
                    <ItemsControl ItemsSource="{Binding IgnoreEntries}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <CheckBox IsChecked="{Binding IsIncluded}" Margin="0,0,16,6">
                                    <StackPanel Orientation="Horizontal">
                                        <TextBlock Text="{Binding Display}" Margin="0,0,5,0" />
                                        <TextBlock Text="{Binding CountText}"
                                                   Style="{StaticResource CaptionText}" />
                                    </StackPanel>
                                </CheckBox>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </Border>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

```csharp
// src/OrdoSort.Wpf/Views/SourcesPageView.xaml.cs
using System.Windows.Controls;

namespace OrdoSort.Wpf.Views;

public partial class SourcesPageView : UserControl
{
    public SourcesPageView() => InitializeComponent();
}
```

- [ ] **Step 4: Wire the menu**

In `MainWindow.xaml`, the Reports menu becomes (keeping the existing icons'
style; `E9D2` is the Segoe MDL2 "AreaChart" glyph):

```xml
            <MenuItem Header="_Reports">
                <MenuItem Header="_Reports hub…" Click="OnReportsHub">
                    <MenuItem.Icon><TextBlock Style="{StaticResource Icon}" Text="&#xE9D2;" /></MenuItem.Icon>
                </MenuItem>
                <Separator />
                <MenuItem Header="_Turn-around time…" Click="OnTurnaroundReport">
                    <MenuItem.Icon><TextBlock Style="{StaticResource Icon}" Text="&#xE916;" /></MenuItem.Icon>
                </MenuItem>
                <MenuItem Header="_Production reports…" Click="OnProductionReport">
                    <MenuItem.Icon><TextBlock Style="{StaticResource Icon}" Text="&#xE9D9;" /></MenuItem.Icon>
                </MenuItem>
            </MenuItem>
```

In `MainWindow.xaml.cs`, replace `OnTurnaroundReport`'s body and add the
singleton (leave `OnProductionReport` untouched):

```csharp
    // The hub is a non-modal singleton: a dashboard someone keeps open
    // beside their work, not a modal utility. Re-invoking focuses the
    // existing window on the requested page rather than opening a second
    // copy with a second feed load.
    private Windows.ReportsWindow? _reportsWindow;

    private void OpenReportsHub(int pageIndex)
    {
        if (_reportsWindow is { IsLoaded: true })
        {
            _reportsWindow.ShowPage(pageIndex);
            _reportsWindow.Activate();
            return;
        }
        var vm = new ReportsViewModel(Shell.Cfg, Dialogs, Shell.SaveConfigNow,
            uiContext: SynchronizationContext.Current);
        var window = new Windows.ReportsWindow(vm) { Owner = this };
        window.Closed += (_, _) => { vm.Dispose(); _reportsWindow = null; };
        _reportsWindow = window;
        window.ShowPage(pageIndex);
        window.Show();
    }

    private void OnReportsHub(object sender, RoutedEventArgs e) => OpenReportsHub(0);

    private void OnTurnaroundReport(object sender, RoutedEventArgs e) => OpenReportsHub(0);
```

- [ ] **Step 5: Build clean and run the Core suite**

```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal
```
Expected: build succeeds with 0 warnings from the new files; full suite
passes. If a style key or converter name assumed here doesn't exist in
`Styles.xaml`/`App.xaml` (e.g. `BoolToVis` lives elsewhere), find the real
declaration site and follow it — do not invent a parallel resource system.

- [ ] **Step 6: Commit**

```
git add src/OrdoSort.Wpf src/OrdoSort.Wpf/MainWindow.xaml src/OrdoSort.Wpf/MainWindow.xaml.cs
git commit -m "feat(ui): the reports hub window — sidebar, turn-around page, sources page, menu"
```

---

### Task 6: Full gate and live verification

**Files:** none — verification only.

- [ ] **Step 1: Full rebuild gate**

```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal
```
Expected: 0 warnings; `Passed!` count = 704 + 13 new (7 XlsxWriter +
4 export + 2 config assertions live inside existing tests — so the count is
**≥ 715**; read the actual number and record it).

- [ ] **Step 2: Launch check**

Run `dotnet run --project src/OrdoSort.Wpf` (or the controller drives this
live with the user): open Reports → Reports hub; confirm the window opens
non-modally, the sidebar switches pages, Sources accepts a folder and the
checklist appears after load, the Turn-around page renders tiles and grids,
a set-aside chip jumps to Detail with the right rows, Export writes an
`.xlsx` that Excel opens, and Copy summary fills the clipboard. Record what
was checked in the task report. If the app cannot be launched in the
execution environment, say so explicitly — the controller then runs this
checklist with the user instead of marking it done.

- [ ] **Step 3: Commit anything the launch check fixed; report**

---

## After Phase 2

Phase 3 (production feed engines — move logs, scan reports, mailroom) gets
its own plan. Do not start it here. The old `TurnaroundWindow` remains
compiled but unreachable from the menu — deleted in Phase 4 with the E2E
rework.
