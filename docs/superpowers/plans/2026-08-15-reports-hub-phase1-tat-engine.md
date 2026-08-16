# Reports Hub Phase 1: TAT Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Core computation layer for the Turn-around dashboard — filename date parsing for all three conventions, the persisted ignore list, the recursive PECF feed reader, and the business-day summary aggregates — fully tested, no UI change.

**Architecture:** Four new UI-free classes in `OrdoSort.Core`, built on the existing `SweptTable`/`Csv.ReadTable` folder loader and `TurnaroundTime.UploadTimeFromReportName`. `DocumentDate` parses document dates off FileName cells; `IgnoreList` handles set-aside sources with per-value counts; `UploadReportFeed` finds and loads `*-PECF Report.xlsx` recursively with a load report; `TurnaroundSummary` dedupes, classifies into business-day buckets, and aggregates (overall, by month, by source, by ISO week, set-asides).

**Tech Stack:** .NET 8, xUnit 2.5.3. **No new NuGet packages** — this repo hand-writes its readers (see `XlsxTable.cs` header comment).

**Spec:** `docs/superpowers/specs/2026-08-15-reports-hub-design.md` (Phase 1 of its Sequencing section; data rules 1–5; the TAT half of Architecture § Core).

## Global Constraints

- **Culture:** every date parse/format goes through `CultureInfo.InvariantCulture` — `CultureInvariantDatesTests` pins this policy repo-wide.
- **Comparison:** ordinal everywhere (`StringComparer.Ordinal`); values are never trimmed, case-folded, or normalized (SweptTable's stance).
- **Batch loads never throw:** per-file failures become report entries (the `SweptTable.FileErrors` pattern).
- **PHI:** all test fixtures are synthetic. NEVER copy rows, filenames, or values from `docs/sample/` into code, fixtures, or commits. Synthetic patient-ish names must be obvious fakes (`DOE,JANE`).
- **Test gate (per `docs/known-flakes.md` — Smart App Control blocks fresh test assemblies by hash):**
  ```
  dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
  dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~<TestClass>" -v minimal
  ```
  `-p:Deterministic=false` is load-bearing. **Always read the `Passed!` line and its count** — exit code 0 with zero tests run is a known failure mode, not a pass.
- **Commits:** conventional style, lowercase (`feat(core): …`, `test(core): …`), one commit per task.
- Do not modify `TurnaroundTime.cs` — the old Turn-around window still uses it until Phase 4. `DocumentDate` supersedes `TurnaroundTime.ExtractDocDate` for the hub only.

---

### Task 1: DocumentDate

The three filename date conventions in one tested place (spec rule 1).

**Files:**
- Create: `src/OrdoSort.Core/DocumentDate.cs`
- Test: `tests/OrdoSort.Core.Tests/DocumentDateTests.cs`

**Interfaces:**
- Consumes: nothing new (`Path`, `DateOnly`, `GeneratedRegex`).
- Produces: `public static partial class DocumentDate` with
  `public static DateOnly? Parse(string filenameCell)` — Task 5 calls this.

Parsing contract, in precedence order (first matching *shape* wins; a shape
whose digits aren't a real calendar date returns null rather than trying the
next shape — except the space form, which has a documented fallback):

1. `^(\d{8})-` → `yyyyMMdd` ("20260722-DOE,JANE [048962880].PDF")
2. `^(\d{2}\.\d{2}\.\d{4})\s` → `MM.dd.yyyy` ("07.15.2026 DOE JANE 123.PDF" — an ECAA form)
3. `^(\d{8})\s` → `MMddyyyy`, falling back to `yyyyMMdd` if the month half
   is impossible ("07152026 DOE JANE 123.PDF" — the other ECAA form; the
   fallback covers "20260101 x.pdf" where digits 1–2 can't be a month).
   The two readings almost never both parse (a `20xx` year prefix is an
   invalid month; a valid month prefix is an invalid `1xxx`-era date);
   MMddyyyy-first is the documented tiebreak.

Cells sometimes carry a full path — `Path.GetFileName` runs first, mirroring
`TurnaroundTime.ExtractDocDate`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/DocumentDateTests.cs
using System.Globalization;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>DocumentDate is the hub's one place for the three filename date
/// conventions (spec rule 1). Pure string→DateOnly?, no disk.</summary>
public class DocumentDateTests
{
    private static void UnderCulture(string culture, Action body)
    {
        var prev = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo(culture); body(); }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void DashFormParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 22),
            DocumentDate.Parse("20260722-DOE,JANE [048962880].PDF"));
    }

    [Fact]
    public void DashFormAsAFullPathStillParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 22),
            DocumentDate.Parse(@"C:\inbox\20260722-DOE,JANE.PDF"));
    }

    [Fact]
    public void DashFormWithImpossibleDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("20261332-X.PDF"));
    }

    [Fact]
    public void DottedFormParses()
    {
        Assert.Equal(new DateOnly(2026, 7, 15),
            DocumentDate.Parse("07.15.2026 DOE JANE 123456789_ABC.PDF"));
    }

    [Fact]
    public void DottedFormWithImpossibleDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("13.45.2026 DOE JANE 123.PDF"));
    }

    [Fact]
    public void SpaceFormParsesAsMonthFirst()
    {
        Assert.Equal(new DateOnly(2026, 7, 15),
            DocumentDate.Parse("07152026 DOE JANE 123456789_ABC.PDF"));
    }

    /// <summary>"20260101 " can't be MMddyyyy (month 20) — the documented
    /// fallback reads it as yyyyMMdd instead of losing the date.</summary>
    [Fact]
    public void SpaceFormFallsBackToYearFirst()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), DocumentDate.Parse("20260101 X.PDF"));
    }

    [Fact]
    public void SpaceFormImpossibleUnderBothReadingsIsNull()
    {
        Assert.Null(DocumentDate.Parse("99999999 X.PDF"));
    }

    [Fact]
    public void NoLeadingDateIsNull()
    {
        Assert.Null(DocumentDate.Parse("DOE,JANE [048962880].PDF"));
        Assert.Null(DocumentDate.Parse(""));
    }

    /// <summary>The dotted form is exactly the shape a culture-sensitive
    /// parse would mangle — pin invariance the way the repo's
    /// CultureInvariantDatesTests does.</summary>
    [Fact]
    public void DottedFormIsCultureInvariant()
    {
        UnderCulture("de-DE", () =>
            Assert.Equal(new DateOnly(2026, 7, 15),
                DocumentDate.Parse("07.15.2026 DOE JANE 123.PDF")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
```
Expected: **build FAILS** — `DocumentDate` does not exist. (In a compiled
language the red step is the compile error.)

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/DocumentDate.cs
using System.Globalization;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>The document's own date, read off the front of its FileName cell
/// — the hub's single home for all three conventions found in live PECF
/// exports (spec rule 1): "20260722-…" (the standard form),
/// "07.15.2026 …" and "07152026 …" (the two ECAA forms — parsed rather than
/// rejected so that re-including ECAA later yields real dates, not a wall of
/// exclusions). Anything else is null: a name with no recoverable date is
/// counted and shown, never guessed (spec rule 5). Supersedes
/// TurnaroundTime.ExtractDocDate for the hub; the old window keeps the old
/// method until Phase 4 retires it.</summary>
public static partial class DocumentDate
{
    [GeneratedRegex(@"^(\d{8})-")]
    private static partial Regex DashForm();

    [GeneratedRegex(@"^(\d{2}\.\d{2}\.\d{4})\s")]
    private static partial Regex DottedForm();

    [GeneratedRegex(@"^(\d{8})\s")]
    private static partial Regex SpaceForm();

    /// <summary>Cells sometimes carry a full path rather than a bare name,
    /// so Path.GetFileName runs first regardless — mirroring
    /// TurnaroundTime.ExtractDocDate. First matching shape wins; only the
    /// space form has a second reading (MMddyyyy, then yyyyMMdd — a 20xx
    /// prefix is an impossible month, a valid month prefix is an impossible
    /// year, so the two readings essentially never both parse).</summary>
    public static DateOnly? Parse(string filenameCell)
    {
        var name = Path.GetFileName(filenameCell);

        var dash = DashForm().Match(name);
        if (dash.Success) return TryExact(dash.Groups[1].Value, "yyyyMMdd");

        var dotted = DottedForm().Match(name);
        if (dotted.Success) return TryExact(dotted.Groups[1].Value, "MM.dd.yyyy");

        var space = SpaceForm().Match(name);
        if (space.Success)
            return TryExact(space.Groups[1].Value, "MMddyyyy")
                ?? TryExact(space.Groups[1].Value, "yyyyMMdd");

        return null;
    }

    private static DateOnly? TryExact(string text, string format) =>
        DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~DocumentDateTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 10` — **verify the count is 10**, not 0.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/DocumentDate.cs tests/OrdoSort.Core.Tests/DocumentDateTests.cs
git commit -m "feat(core): DocumentDate — the three filename date conventions, one tested place"
```

---

### Task 2: IgnoreList and its config key

The persisted set-aside mechanism behind the Sources-page checklist (spec
decision 7): membership, discovered values with counts, config round-trip.

**Files:**
- Create: `src/OrdoSort.Core/IgnoreList.cs`
- Modify: `src/OrdoSort.Core/Config.cs` — add one property directly after
  `TatThresholdDays` (currently line 120)
- Test: `tests/OrdoSort.Core.Tests/IgnoreListTests.cs`

**Interfaces:**
- Consumes: `Config.Save(Config, string)` / `Config.Load(string)` (existing).
- Produces (Task 5 and the regression test consume the first two):
  - `public sealed class IgnoreList` — ctor `IgnoreList(IEnumerable<string> ignoredValues)`
  - `public bool IsIgnored(string value)` — ordinal
  - `public IReadOnlyList<string> Ignored { get; }` — distinct, first-seen order
  - `public sealed record Entry(string Value, int Count, bool Ignored)` (nested in `IgnoreList`)
  - `public IReadOnlyList<Entry> Discover(IEnumerable<string> values)` — distinct values with counts, ordered count-descending then value-ordinal
  - `Config.TatIgnoredSources : List<string>` under JSON key `tat_ignored_sources`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/IgnoreListTests.cs
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>IgnoreList backs both dashboards' set-aside checklists (spec
/// decision 7): membership is ordinal — never case-folded, matching the
/// repo's no-normalization stance — and the persisted list must round-trip
/// through Config so a restart can't silently re-include a value.</summary>
public class IgnoreListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoign_" + Guid.NewGuid());
    public IgnoreListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void MembershipIsOrdinal()
    {
        var list = new IgnoreList(new[] { "ECAA" });
        Assert.True(list.IsIgnored("ECAA"));
        Assert.False(list.IsIgnored("ecaa"));   // a different value, not a different casing
        Assert.False(list.IsIgnored("Email"));
    }

    [Fact]
    public void AnEmptyListIgnoresNothing()
    {
        var list = new IgnoreList(Array.Empty<string>());
        Assert.False(list.IsIgnored("ECAA"));
        Assert.Empty(list.Ignored);
    }

    [Fact]
    public void DuplicateIgnoredValuesCollapseFirstSeenOrder()
    {
        var list = new IgnoreList(new[] { "ECAA", "PORTAL", "ECAA" });
        Assert.Equal(new[] { "ECAA", "PORTAL" }, list.Ignored);
    }

    [Fact]
    public void DiscoverCountsAndFlagsEveryDistinctValue()
    {
        var list = new IgnoreList(new[] { "ECAA" });
        var entries = list.Discover(new[] { "Email", "FAX", "Email", "ECAA", "Email" });
        Assert.Equal(new[]
        {
            new IgnoreList.Entry("Email", 3, false),
            new IgnoreList.Entry("ECAA", 1, true),
            new IgnoreList.Entry("FAX", 1, false),
        }, entries);   // count descending, then ordinal — "ECAA" < "FAX"
    }

    [Fact]
    public void TatIgnoredSourcesRoundTripsThroughConfigWithItsExactJsonName()
    {
        var cfg = new Config { TatIgnoredSources = { "ECAA", "PORTAL" } };
        var path = Path.Combine(_dir, "t.json");
        Config.Save(cfg, path);
        Assert.Contains("\"tat_ignored_sources\"", File.ReadAllText(path));
        var back = Config.Load(path);
        Assert.Equal(new[] { "ECAA", "PORTAL" }, back.TatIgnoredSources);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `IgnoreList` and `TatIgnoredSources` don't exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/IgnoreList.cs
namespace OrdoSort.Core;

/// <summary>The set-aside rule shared by both dashboards (spec decision 7):
/// some values in the source data belong to processes a report doesn't cover
/// (ECAA today; others later), so instead of a hard-coded rule per value,
/// the set of values discovered in the loaded data becomes a checklist and
/// unchecking one removes it from every figure — while its count stays on
/// screen, so absent data and deliberately excluded data are never confused.
/// Membership is ordinal, never normalized. The list itself persists as
/// Config.TatIgnoredSources (and, in Phase 3, ProductionIgnoredCategories).</summary>
public sealed class IgnoreList
{
    private readonly HashSet<string> _ignored;

    /// <summary>Distinct ignored values, first-seen order — exactly what
    /// gets written back to config.</summary>
    public IReadOnlyList<string> Ignored { get; }

    public IgnoreList(IEnumerable<string> ignoredValues)
    {
        _ignored = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var value in ignoredValues)
            if (_ignored.Add(value)) ordered.Add(value);
        Ignored = ordered;
    }

    public bool IsIgnored(string value) => _ignored.Contains(value);

    /// <summary>One checklist row: a value seen in the data, how often, and
    /// whether it's currently set aside.</summary>
    public sealed record Entry(string Value, int Count, bool Ignored);

    /// <summary>Every distinct value in the data with its count — the
    /// checklist the Sources page renders. Count descending so the values
    /// that matter most sit on top, ordinal tiebreak so the order never
    /// depends on CurrentCulture.</summary>
    public IReadOnlyList<Entry> Discover(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new Entry(g.Key, g.Count(), IsIgnored(g.Key)))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Value, StringComparer.Ordinal)
            .ToList();
}
```

In `src/OrdoSort.Core/Config.cs`, directly after the `TatThresholdDays`
property (line 120), add:

```csharp
    [JsonPropertyName("tat_ignored_sources")] public List<string> TatIgnoredSources { get; set; } = new();
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~IgnoreListTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 5`.

Also run the existing config suites — the new key must not disturb them:
```
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~Config" -v minimal
```
Expected: all pass, nonzero count.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/IgnoreList.cs src/OrdoSort.Core/Config.cs tests/OrdoSort.Core.Tests/IgnoreListTests.cs
git commit -m "feat(core): IgnoreList with per-value counts, persisted as tat_ignored_sources"
```

---

### Task 3: UploadReportFeed

Finds `<YYYYMMDD>-<HHMM>-PECF Report.xlsx` recursively, loads via
`SweptTable`, returns the table plus a load report (spec feed 1 and the
Architecture § Core "load report" contract). Never throws.

**Files:**
- Create: `src/OrdoSort.Core/UploadReportFeed.cs`
- Test: `tests/OrdoSort.Core.Tests/UploadReportFeedTests.cs`

**Interfaces:**
- Consumes: `SweptTable.Load(IReadOnlyList<string>)` → `SweptTable.Table`
  (`Headers`, `Rows`, `FilesRead`, `FileErrors`);
  `TurnaroundTime.UploadTimeFromReportName(string) : DateTime?`.
- Produces (Phase 2's Sources page consumes `LoadReport`; Task 5's caller
  passes `Result.Table` to `TurnaroundSummary.Compute`):
  - `public static partial class UploadReportFeed`
  - `public sealed record LoadReport(int FilesFound, IReadOnlyList<string> Skipped, DateOnly? FirstUpload, DateOnly? LastUpload, int RowCount)` (nested)
  - `public sealed record Result(SweptTable.Table Table, LoadReport Report)` (nested)
  - `public static bool IsReportFile(string path)`
  - `public static IReadOnlyList<string> FindFiles(string root)` — recursive, filtered, filename-ordinal sort
  - `public static Result Load(string root)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/UploadReportFeedTests.cs
using System.IO.Compression;
using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>UploadReportFeed walks a folder for PECF reports. Tests build
/// real minimal workbooks with ZipArchive (the XlsxTableTests technique) so
/// nothing depends on Excel — and everything in them is synthetic; live
/// sample data never enters a fixture (spec: PHI stance).</summary>
public class UploadReportFeedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordofeed_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>A one-sheet workbook of inline strings. Cell text must not
    /// contain &, &lt; or &gt; — fixture data here never does.</summary>
    private string WriteXlsx(string relativePath, string[][] rows)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder(
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
                sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{rows[r][c]}</t></is></c>");
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), Encoding.UTF8);
        w.Write(sb.ToString());
        return path;
    }

    private static string[][] Rows(params string[] fileNames) =>
        new[] { new[] { "FileName", "SourceType" } }
            .Concat(fileNames.Select(f => new[] { f, "Email" }))
            .ToArray();

    [Fact]
    public void OnlyExactReportNamesMatch()
    {
        Assert.True(UploadReportFeed.IsReportFile(@"x\20260701-1042-PECF Report.xlsx"));
        Assert.True(UploadReportFeed.IsReportFile("20260701-1042-pecf report.XLSX"));   // case-insensitive
        Assert.False(UploadReportFeed.IsReportFile("summary.xlsx"));
        Assert.False(UploadReportFeed.IsReportFile("20260701-PECF Report.xlsx"));            // no time half
        Assert.False(UploadReportFeed.IsReportFile("20260701-1042-PECF Report - Copy.xlsx")); // suffixed
    }

    [Fact]
    public void FindsReportsInDatedSubfoldersSortedByName()
    {
        WriteXlsx(@"20260706\20260706-0941-PECF Report.xlsx", Rows("20260706-A.pdf"));
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-B.pdf"));
        WriteXlsx("20260707-1001-PECF Report.xlsx", Rows("20260707-C.pdf"));   // root level counts too
        WriteXlsx(@"20260701\notes.xlsx", Rows("ignored.pdf"));                // filtered out

        var files = UploadReportFeed.FindFiles(_dir);
        Assert.Equal(new[]
        {
            "20260701-1042-PECF Report.xlsx",
            "20260706-0941-PECF Report.xlsx",
            "20260707-1001-PECF Report.xlsx",
        }, files.Select(Path.GetFileName));
    }

    [Fact]
    public void LoadReportsCountsSpanAndRows()
    {
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-A.pdf", "20260701-B.pdf"));
        WriteXlsx(@"20260710\20260710-0939-PECF Report.xlsx", Rows("20260710-C.pdf"));

        var result = UploadReportFeed.Load(_dir);
        Assert.Equal(2, result.Report.FilesFound);
        Assert.Empty(result.Report.Skipped);
        Assert.Equal(new DateOnly(2026, 7, 1), result.Report.FirstUpload);
        Assert.Equal(new DateOnly(2026, 7, 10), result.Report.LastUpload);
        Assert.Equal(3, result.Report.RowCount);
        Assert.Equal(3, result.Table.Rows.Count);
    }

    [Fact]
    public void ACorruptFileIsSkippedAndNamedWhileTheRestLoads()
    {
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-A.pdf"));
        var corrupt = Path.Combine(_dir, "20260702-0900-PECF Report.xlsx");
        File.WriteAllText(corrupt, "not a zip");   // matches the name filter, fails to read

        var result = UploadReportFeed.Load(_dir);
        Assert.Equal(2, result.Report.FilesFound);
        Assert.Single(result.Report.Skipped);
        Assert.Contains("20260702-0900-PECF Report.xlsx", result.Report.Skipped[0]);
        Assert.Equal(1, result.Report.RowCount);   // the good file still loaded
    }

    [Fact]
    public void AMissingRootIsAnEmptyResultWithANote()
    {
        var result = UploadReportFeed.Load(Path.Combine(_dir, "nope"));
        Assert.Equal(0, result.Report.FilesFound);
        Assert.Single(result.Report.Skipped);
        Assert.Contains("nope", result.Report.Skipped[0]);
        Assert.Equal(0, result.Report.RowCount);
        Assert.Empty(result.Table.Rows);
        Assert.Null(result.Report.FirstUpload);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `UploadReportFeed` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/UploadReportFeed.cs
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>The Turn-around dashboard's one source: a folder of
/// "YYYYMMDD-HHMM-PECF Report.xlsx" exports, in dated subfolders or not —
/// scanning is always recursive (spec feed 1). Load never throws: a missing
/// root or an unreadable file becomes a Skipped entry in the LoadReport, the
/// SweptTable.FileErrors pattern, because a batch of report files is exactly
/// the place one bad file must not take the rest down. The name filter is
/// deliberately exact — a folder full of hand-saved copies ("… - Copy.xlsx")
/// and unrelated workbooks must not leak rows into the SLA numbers.</summary>
public static partial class UploadReportFeed
{
    // The full report-name shape, anchored both ends: date, time, the fixed
    // suffix. IgnoreCase covers hand-renamed extensions (.XLSX) and casing.
    [GeneratedRegex(@"^\d{8}-\d{4}-PECF Report\.xlsx$", RegexOptions.IgnoreCase)]
    private static partial Regex ReportNameRegex();

    /// <summary>What the Sources page shows for this feed: how much was
    /// found, what was skipped and why, the upload-date span, the row count.</summary>
    public sealed record LoadReport(int FilesFound, IReadOnlyList<string> Skipped,
        DateOnly? FirstUpload, DateOnly? LastUpload, int RowCount);

    public sealed record Result(SweptTable.Table Table, LoadReport Report);

    public static bool IsReportFile(string path) =>
        ReportNameRegex().IsMatch(Path.GetFileName(path));

    /// <summary>Every matching file under root, recursively, sorted by bare
    /// filename ordinal (the YYYYMMDD-HHMM prefix makes that chronological)
    /// with the full path as tiebreak, so load order — and therefore
    /// "earliest report wins" dedupe downstream — never depends on
    /// filesystem enumeration order.</summary>
    public static IReadOnlyList<string> FindFiles(string root) =>
        Directory.EnumerateFiles(root, "*.xlsx", SearchOption.AllDirectories)
            .Where(IsReportFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static Result Load(string root)
    {
        IReadOnlyList<string> files;
        try
        {
            files = FindFiles(root);
        }
        catch (Exception ex)   // missing root, access denied — one note, empty result
        {
            var empty = new SweptTable.Table(Array.Empty<string>(),
                Array.Empty<SweptTable.Row>(), 0, Array.Empty<string>());
            return new Result(empty, new LoadReport(0, new[] { $"{root}: {ex.Message}" },
                null, null, 0));
        }

        var table = SweptTable.Load(files);
        var uploads = files
            .Select(TurnaroundTime.UploadTimeFromReportName)
            .Where(u => u is not null)
            .Select(u => DateOnly.FromDateTime(u!.Value))
            .ToList();

        return new Result(table, new LoadReport(files.Count, table.FileErrors,
            uploads.Count == 0 ? null : uploads.Min(),
            uploads.Count == 0 ? null : uploads.Max(),
            table.Rows.Count));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~UploadReportFeedTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/UploadReportFeed.cs tests/OrdoSort.Core.Tests/UploadReportFeedTests.cs
git commit -m "feat(core): UploadReportFeed — recursive PECF discovery with a load report"
```

---

### Task 4: TurnaroundSummary — types, business-day counter, classifier

The pure pieces of spec rule 3: `BusinessDaysBetween` (the workbook's TAT
column, verified equal to weekday-count-in-`[docDate, uploadDate)` on
23,565 of 23,672 live rows) and the four-bucket classifier.

**Files:**
- Create: `src/OrdoSort.Core/TurnaroundSummary.cs`
- Test: `tests/OrdoSort.Core.Tests/TurnaroundSummaryTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (Task 5 fills in `Compute`; Phase 2's view model consumes all of it):
  - `public static class TurnaroundSummary` with column constants
    `FileNameColumn = "FileName"`, `SourceTypeColumn = "SourceType"`,
    `PagecountColumn = "Pagecount"`, `DestinationColumn = "Destination"`
  - `public enum Bucket { SameDay, OneDay, TwoDays, ThreePlus }` (nested)
  - `public sealed record Doc(string FileName, string SourceType, string Pagecount, string Destination, DateOnly DocDate, DateOnly UploadDate, int BusinessDays, Bucket Bucket, string SourceFile)`
  - `public sealed record BucketCounts(int SameDay, int OneDay, int TwoDays, int ThreePlus)` with computed `Total`, `ZeroToOne`, `ZeroToOnePercent`, `TwoPercent`, `ThreePlusPercent`
  - `public sealed record IgnoredSource(string Value, int Count)`
  - `public sealed record MonthLine(string Month, BucketCounts Counts)` — `Month` is `"yyyy-MM"`
  - `public sealed record SourceLine(string SourceType, BucketCounts Counts)`
  - `public sealed record WeekLine(string Week, BucketCounts Counts)` — `Week` is `"yyyy-Www"` (ISO)
  - `public sealed record Summary(IReadOnlyList<Doc> Docs, BucketCounts Overall, IReadOnlyList<MonthLine> ByMonth, IReadOnlyList<SourceLine> BySource, IReadOnlyList<WeekLine> ByWeek, IReadOnlyList<IgnoredSource> Ignored, int DuplicateRows, int FutureDated, int NoDate)`
  - `public static int BusinessDaysBetween(DateOnly from, DateOnly to)`
  - `public static Bucket Classify(int businessDays)`

- [ ] **Step 1: Write the failing tests**

Calendar anchors used below (2026): Jul 1 = Wednesday, Jul 3 = Friday,
Jul 4/5 = weekend, Jul 6 = Monday.

```csharp
// tests/OrdoSort.Core.Tests/TurnaroundSummaryTests.cs
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The business-day counter is the workbook's TAT column: the
/// number of weekdays in [docDate, uploadDate) — numpy busday_count
/// semantics, verified against 23,565 of the 23,672 live rows before this
/// was built (spec decision 3). All fixture dates are synthetic; 2026-07-06
/// is a Monday.</summary>
public class TurnaroundSummaryTests
{
    private static readonly DateOnly Wed = new(2026, 7, 1);
    private static readonly DateOnly Thu = new(2026, 7, 2);
    private static readonly DateOnly Fri = new(2026, 7, 3);
    private static readonly DateOnly Sat = new(2026, 7, 4);
    private static readonly DateOnly Sun = new(2026, 7, 5);
    private static readonly DateOnly Mon = new(2026, 7, 6);

    [Fact]
    public void SameDayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Mon, Mon));

    [Fact]
    public void NextWeekdayIsOne() =>
        Assert.Equal(1, TurnaroundSummary.BusinessDaysBetween(Wed, Thu));

    [Fact]
    public void FridayToMondaySkipsTheWeekend() =>
        Assert.Equal(1, TurnaroundSummary.BusinessDaysBetween(Fri, Mon));

    [Fact]
    public void SaturdayToMondayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Sat, Mon));

    [Fact]
    public void SundayToMondayIsZero() =>
        Assert.Equal(0, TurnaroundSummary.BusinessDaysBetween(Sun, Mon));

    [Fact]
    public void AFullWeekIsFive() =>
        Assert.Equal(5, TurnaroundSummary.BusinessDaysBetween(Mon, Mon.AddDays(7)));

    [Fact]
    public void ReversedDatesCountNegative() =>
        Assert.Equal(-1, TurnaroundSummary.BusinessDaysBetween(Mon, Fri));

    [Theory]
    [InlineData(0, TurnaroundSummary.Bucket.SameDay)]
    [InlineData(1, TurnaroundSummary.Bucket.OneDay)]
    [InlineData(2, TurnaroundSummary.Bucket.TwoDays)]
    [InlineData(3, TurnaroundSummary.Bucket.ThreePlus)]
    [InlineData(9, TurnaroundSummary.Bucket.ThreePlus)]
    public void BucketsMatchTheWorkbookColumns(int days, TurnaroundSummary.Bucket expected) =>
        Assert.Equal(expected, TurnaroundSummary.Classify(days));

    [Fact]
    public void BucketCountsComputeRollupAndPercentages()
    {
        var counts = new TurnaroundSummary.BucketCounts(SameDay: 3, OneDay: 2, TwoDays: 2, ThreePlus: 2);
        Assert.Equal(9, counts.Total);
        Assert.Equal(5, counts.ZeroToOne);
        Assert.Equal(55.56, counts.ZeroToOnePercent, 2);
        Assert.Equal(22.22, counts.TwoPercent, 2);
        Assert.Equal(22.22, counts.ThreePlusPercent, 2);
    }

    [Fact]
    public void EmptyBucketCountsHaveZeroPercentagesNotNaN()
    {
        var counts = new TurnaroundSummary.BucketCounts(0, 0, 0, 0);
        Assert.Equal(0, counts.Total);
        Assert.Equal(0.0, counts.ZeroToOnePercent);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `TurnaroundSummary` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/OrdoSort.Core/TurnaroundSummary.cs
using System.Globalization;

namespace OrdoSort.Core;

/// <summary>The Turn-around dashboard's engine (spec rules 1–5): dedupe by
/// filename with the earliest report winning, set ignored sources aside with
/// counts, classify every measurable document into the workbook's
/// business-day buckets, and aggregate — overall, by month, by source, by
/// ISO week. Headline metric is business days, superseding the 08-11 spec's
/// calendar-day decision: the workbook this mimics computes
/// busday_count(FileDate, UploadDate), verified on 23,565 of 23,672 live
/// rows. Pure computation on SweptTable rows; nothing here touches disk.</summary>
public static class TurnaroundSummary
{
    // The PECF export's fixed layout (spec feed 1). SweptTable's union rows
    // make a missing column read as "", never throw.
    public const string FileNameColumn = "FileName";
    public const string SourceTypeColumn = "SourceType";
    public const string PagecountColumn = "Pagecount";
    public const string DestinationColumn = "Destination";

    public enum Bucket { SameDay, OneDay, TwoDays, ThreePlus }

    /// <summary>One measurable, deduplicated, non-ignored document.</summary>
    public sealed record Doc(string FileName, string SourceType, string Pagecount,
        string Destination, DateOnly DocDate, DateOnly UploadDate, int BusinessDays,
        Bucket Bucket, string SourceFile);

    /// <summary>The four bucket counts plus the derived figures every panel
    /// renders. Percentages of an empty population read 0, not NaN — an
    /// empty month must render as dashes, not poison a binding.</summary>
    public sealed record BucketCounts(int SameDay, int OneDay, int TwoDays, int ThreePlus)
    {
        public int Total => SameDay + OneDay + TwoDays + ThreePlus;
        public int ZeroToOne => SameDay + OneDay;
        public double ZeroToOnePercent => Total == 0 ? 0 : 100.0 * ZeroToOne / Total;
        public double TwoPercent => Total == 0 ? 0 : 100.0 * TwoDays / Total;
        public double ThreePlusPercent => Total == 0 ? 0 : 100.0 * ThreePlus / Total;
    }

    public sealed record IgnoredSource(string Value, int Count);
    public sealed record MonthLine(string Month, BucketCounts Counts);     // "2026-07"
    public sealed record SourceLine(string SourceType, BucketCounts Counts);
    public sealed record WeekLine(string Week, BucketCounts Counts);       // "2026-W28"

    public sealed record Summary(
        IReadOnlyList<Doc> Docs,
        BucketCounts Overall,
        IReadOnlyList<MonthLine> ByMonth,
        IReadOnlyList<SourceLine> BySource,
        IReadOnlyList<WeekLine> ByWeek,
        IReadOnlyList<IgnoredSource> Ignored,
        int DuplicateRows,
        int FutureDated,
        int NoDate);

    /// <summary>Weekdays in [from, to) — numpy busday_count semantics, which
    /// is what the workbook's TAT column is. Saturday-dated work uploaded
    /// Monday reads 0: no business day passed. Reversed dates count
    /// negative, but Compute never classifies those — future-dated documents
    /// are excluded by calendar comparison first (spec rule 4). The walk is
    /// linear in the gap; real gaps are days, not decades.</summary>
    public static int BusinessDaysBetween(DateOnly from, DateOnly to)
    {
        if (to < from) return -BusinessDaysBetween(to, from);
        var days = 0;
        for (var d = from; d < to; d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days++;
        return days;
    }

    /// <summary>The workbook's four Turnaround values by their business-day
    /// count. Callers guarantee non-negative input (see BusinessDaysBetween).</summary>
    public static Bucket Classify(int businessDays) => businessDays switch
    {
        0 => Bucket.SameDay,
        1 => Bucket.OneDay,
        2 => Bucket.TwoDays,
        _ => Bucket.ThreePlus,
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~TurnaroundSummaryTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 14` (9 facts + 5 theory cases).

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/TurnaroundSummary.cs tests/OrdoSort.Core.Tests/TurnaroundSummaryTests.cs
git commit -m "feat(core): TurnaroundSummary types, business-day counter, bucket classifier"
```

---

### Task 5: TurnaroundSummary.Compute

The full pipeline over a `SweptTable.Table`, in this exact order (the order
the verified reference figures were derived in): **(1)** sort rows earliest
report first → **(2)** dedupe by FileName, blanks never merged → **(3)** set
ignored sources aside with per-value counts → **(4)** parse dates, count
no-date rows → **(5)** exclude future-dated by calendar comparison →
**(6)** classify and aggregate.

**Files:**
- Modify: `src/OrdoSort.Core/TurnaroundSummary.cs` (append `Compute` and two private helpers)
- Test: `tests/OrdoSort.Core.Tests/TurnaroundSummaryComputeTests.cs`

**Interfaces:**
- Consumes: `SweptTable.Row`/`.Table` (constructed directly — record
  constructors are public), `DocumentDate.Parse`,
  `TurnaroundTime.UploadTimeFromReportName`, `IgnoreList.IsIgnored`,
  everything Task 4 defined.
- Produces: `public static Summary Compute(SweptTable.Table table, IgnoreList ignoredSources)` — Task 6 and Phase 2 consume this.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Core.Tests/TurnaroundSummaryComputeTests.cs
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Compute is pure computation on SweptTable rows, so these tests
/// build rows directly rather than round-tripping through disk — the same
/// stance TurnaroundTimeTests takes. Report filenames are the upload clock:
/// "20260706-0900-PECF Report.xlsx" uploads on Monday 2026-07-06.</summary>
public class TurnaroundSummaryComputeTests
{
    private const string R1 = "20260706-0900-PECF Report.xlsx";   // Mon Jul 6
    private const string R2 = "20260803-0900-PECF Report.xlsx";   // Mon Aug 3

    private static readonly string[] Headers =
        { "FileName", "SourceType", "Pagecount", "Destination" };

    private static SweptTable.Row Row(string report, string fileName,
        string sourceType = "Email", string pages = "10", string dest = "MIX") =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FileName"] = fileName, ["SourceType"] = sourceType,
            ["Pagecount"] = pages, ["Destination"] = dest,
        }, report);

    private static SweptTable.Table Table(params SweptTable.Row[] rows) =>
        new(Headers, rows, FilesRead: 1, FileErrors: Array.Empty<string>());

    private static readonly IgnoreList NoIgnores = new(Array.Empty<string>());

    [Fact]
    public void ADocumentInTwoReportsCountsOnceAndTheEarliestUploadWins()
    {
        // Listed in R2's rows first — input order must not decide the winner.
        var summary = TurnaroundSummary.Compute(Table(
            Row(R2, "20260706-A.pdf"),
            Row(R1, "20260706-A.pdf")), NoIgnores);

        Assert.Equal(1, summary.DuplicateRows);
        var doc = Assert.Single(summary.Docs);
        Assert.Equal(new DateOnly(2026, 7, 6), doc.UploadDate);   // R1, the earlier report
        Assert.Equal(TurnaroundSummary.Bucket.SameDay, doc.Bucket);
    }

    [Fact]
    public void BlankFileNamesAreNeverMergedWithEachOther()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, ""), Row(R1, "")), NoIgnores);

        Assert.Equal(0, summary.DuplicateRows);
        Assert.Equal(2, summary.NoDate);   // both count, neither is a "duplicate"
    }

    [Fact]
    public void IgnoredSourcesAreSetAsideWholeWithPerValueCounts()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf"),
            Row(R1, "07022026 B.pdf", sourceType: "ECAA"),
            Row(R1, "07.03.2026 C.pdf", sourceType: "ECAA")), new IgnoreList(new[] { "ECAA" }));

        var ignored = Assert.Single(summary.Ignored);
        Assert.Equal(new TurnaroundSummary.IgnoredSource("ECAA", 2), ignored);
        Assert.Single(summary.Docs);                       // only the Email doc measures
        Assert.Equal(100.0, summary.Overall.ZeroToOnePercent);   // percentages over the remainder
    }

    [Fact]
    public void WithoutTheIgnoreListEcaaDatesStillParse()
    {
        // Re-including ECAA later must yield real dates (spec rule 1).
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "07022026 B.pdf", sourceType: "ECAA")), NoIgnores);

        var doc = Assert.Single(summary.Docs);
        Assert.Equal(new DateOnly(2026, 7, 2), doc.DocDate);   // Thu → Mon = 2 business days
        Assert.Equal(TurnaroundSummary.Bucket.TwoDays, doc.Bucket);
    }

    [Fact]
    public void FutureDatedDocumentsAreExcludedAndCountedNeverCoerced()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260707-A.pdf"),      // dated the day after its upload
            Row(R1, "20260706-B.pdf")), NoIgnores);

        Assert.Equal(1, summary.FutureDated);
        Assert.Single(summary.Docs);
    }

    [Fact]
    public void UndatedNamesAreExcludedAndCountedNeverGuessed()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "NODATE.pdf"),
            Row(R1, "20260706-B.pdf")), NoIgnores);

        Assert.Equal(1, summary.NoDate);
        Assert.Single(summary.Docs);
    }

    [Fact]
    public void AggregatesGroupByUploadMonthSourceAndIsoWeek()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf", sourceType: "Email"),
            Row(R1, "20260703-B.pdf", sourceType: "FAX"),     // Fri → Mon = 1
            Row(R2, "20260803-C.pdf", sourceType: "Email")), NoIgnores);

        Assert.Equal(new[] { "2026-07", "2026-08" }, summary.ByMonth.Select(m => m.Month));
        Assert.Equal(2, summary.ByMonth[0].Counts.Total);
        Assert.Equal(1, summary.ByMonth[1].Counts.Total);

        // Source order: count descending, then ordinal.
        Assert.Equal(new[] { "Email", "FAX" }, summary.BySource.Select(s => s.SourceType));
        Assert.Equal(1, summary.BySource[1].Counts.OneDay);

        Assert.Equal(2, summary.ByWeek.Count);
        Assert.All(summary.ByWeek, w => Assert.Matches(@"^\d{4}-W\d{2}$", w.Week));
        Assert.Equal(2, summary.ByWeek[0].Counts.Total);   // both R1 docs upload the same week
    }

    [Fact]
    public void SourceTypesAreNeverCaseFolded()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf", sourceType: "Email"),
            Row(R1, "20260706-B.pdf", sourceType: "EMAIL")), NoIgnores);

        Assert.Equal(2, summary.BySource.Count);   // two values, reported as found
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build OrdoSort.sln -p:Deterministic=false -v minimal`
Expected: **build FAILS** — `Compute` does not exist.

- [ ] **Step 3: Write the implementation** (append inside `TurnaroundSummary`)

```csharp
    /// <summary>The whole pipeline, in the order the spec's verified
    /// reference figures were derived: sort by upload time so the earliest
    /// report wins dedupe; dedupe by FileName (blank names never merge —
    /// each blank row still counts, under NoDate); set ignored sources
    /// aside whole, counted per value; then dates — a row missing either
    /// date is NoDate, a document dated after its upload is FutureDated
    /// (calendar comparison, spec rule 4 — never coerced, never classified);
    /// everything left is measurable and aggregates four ways.</summary>
    public static Summary Compute(SweptTable.Table table, IgnoreList ignoredSources)
    {
        // 1. Earliest report first; original index as tiebreak keeps this stable.
        var ordered = table.Rows
            .Select((row, i) => (Row: row,
                Upload: TurnaroundTime.UploadTimeFromReportName(row.SourceFile), Index: i))
            .OrderBy(r => r.Upload ?? DateTime.MaxValue)
            .ThenBy(r => r.Index)
            .ToList();

        // 2. Dedupe by FileName cell, ordinal. Blank names are not identities.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateRows = 0;
        var kept = new List<(SweptTable.Row Row, DateTime? Upload)>();
        foreach (var (row, upload, _) in ordered)
        {
            var name = Cell(row, FileNameColumn);
            if (name.Length > 0 && !seen.Add(name)) { duplicateRows++; continue; }
            kept.Add((row, upload));
        }

        // 3. Ignored sources: set aside whole, counted per value.
        var ignoredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var live = new List<(SweptTable.Row Row, DateTime? Upload)>();
        foreach (var item in kept)
        {
            var source = Cell(item.Row, SourceTypeColumn);
            if (ignoredSources.IsIgnored(source))
                ignoredCounts[source] = ignoredCounts.GetValueOrDefault(source) + 1;
            else live.Add(item);
        }

        // 4–6. Dates, exclusions, classification.
        var docs = new List<Doc>();
        var noDate = 0;
        var futureDated = 0;
        foreach (var (row, upload) in live)
        {
            var fileName = Cell(row, FileNameColumn);
            var docDate = DocumentDate.Parse(fileName);
            if (docDate is null || upload is null) { noDate++; continue; }

            var uploadDate = DateOnly.FromDateTime(upload.Value);
            if (uploadDate < docDate.Value) { futureDated++; continue; }

            var busDays = BusinessDaysBetween(docDate.Value, uploadDate);
            docs.Add(new Doc(fileName, Cell(row, SourceTypeColumn),
                Cell(row, PagecountColumn), Cell(row, DestinationColumn),
                docDate.Value, uploadDate, busDays, Classify(busDays), row.SourceFile));
        }

        return new Summary(
            docs,
            CountBuckets(docs),
            docs.GroupBy(d => d.UploadDate.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MonthLine(g.Key, CountBuckets(g.ToList())))
                .ToList(),
            docs.GroupBy(d => d.SourceType, StringComparer.Ordinal)
                .Select(g => new SourceLine(g.Key, CountBuckets(g.ToList())))
                .OrderByDescending(s => s.Counts.Total)
                .ThenBy(s => s.SourceType, StringComparer.Ordinal)
                .ToList(),
            docs.GroupBy(d =>
                {
                    var date = d.UploadDate.ToDateTime(TimeOnly.MinValue);
                    return (Year: ISOWeek.GetYear(date), Week: ISOWeek.GetWeekOfYear(date));
                })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Week)
                .Select(g => new WeekLine(
                    $"{g.Key.Year.ToString(CultureInfo.InvariantCulture)}-W{g.Key.Week.ToString("00", CultureInfo.InvariantCulture)}",
                    CountBuckets(g.ToList())))
                .ToList(),
            ignoredCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new IgnoredSource(kv.Key, kv.Value))
                .ToList(),
            duplicateRows, futureDated, noDate);
    }

    private static string Cell(SweptTable.Row row, string column) =>
        row.Cells.TryGetValue(column, out var value) ? value : "";

    private static BucketCounts CountBuckets(IReadOnlyList<Doc> docs) => new(
        docs.Count(d => d.Bucket == Bucket.SameDay),
        docs.Count(d => d.Bucket == Bucket.OneDay),
        docs.Count(d => d.Bucket == Bucket.TwoDays),
        docs.Count(d => d.Bucket == Bucket.ThreePlus));
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~TurnaroundSummary" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 22` (Task 4's 14 + these 8).

- [ ] **Step 5: Commit**

```
git add src/OrdoSort.Core/TurnaroundSummary.cs tests/OrdoSort.Core.Tests/TurnaroundSummaryComputeTests.cs
git commit -m "feat(core): TurnaroundSummary.Compute — dedupe, set-asides, business-day aggregates"
```

---

### Task 6: Regression fixture and the full gate

A miniature end-to-end dataset shaped like the spec's verified reference
figures — two report files on disk, every rule exercised at once, exact
expected numbers — then the full-suite gate.

**Files:**
- Test: `tests/OrdoSort.Core.Tests/TurnaroundRegressionTests.cs`

**Interfaces:**
- Consumes: `UploadReportFeed.Load`, `TurnaroundSummary.Compute`,
  `IgnoreList` — the whole Phase 1 surface, together.
- Produces: nothing new — this pins the pipeline's numbers.

The fixture (all synthetic; 2026-07-06 and 2026-08-03 are Mondays):

| Report (upload) | FileName | Source | Expected fate |
|---|---|---|---|
| R1 (Mon Jul 6) | `20260706-A.pdf` | Email | SameDay |
| R1 | `20260703-B.pdf` | Email | OneDay (Fri→Mon) |
| R1 | `20260702-C.pdf` | FAX | TwoDays (Thu→Mon) |
| R1 | `20260701-D.pdf` | Paper | ThreePlus (Wed→Mon = 3) |
| R1 | `20260704-E.pdf` | CD | SameDay (Sat→Mon = 0) |
| R1 | `07022026 F.pdf` | ECAA | ignored |
| R1 | `07.03.2026 G.pdf` | ECAA | ignored |
| R1 | `20260707-H.pdf` | Email | future-dated |
| R1 | `NODATE.pdf` | Email | no date |
| R2 (Mon Aug 3) | `20260706-A.pdf` | Email | duplicate (R1 wins) |
| R2 | `20260803-I.pdf` | Email | SameDay |
| R2 | `20260731-J.pdf` | FAX | OneDay (Fri→Mon) |
| R2 | `20260730-K.pdf` | CD | TwoDays (Thu→Mon) |
| R2 | `20260724-L.pdf` | Paper | ThreePlus (Fri Jul 24→Mon Aug 3 = 6) |

Expected: 9 measurable docs — Overall `(3, 2, 2, 2)`, ZeroToOne 5 = 55.56%;
`ByMonth` = 2026-07 `(2,1,1,1)`, 2026-08 `(1,1,1,1)`; `BySource` = Email(3),
CD(2), FAX(2), Paper(2) in that order; DuplicateRows 1, Ignored ECAA 2,
FutureDated 1, NoDate 1.

- [ ] **Step 1: Write the test**

```csharp
// tests/OrdoSort.Core.Tests/TurnaroundRegressionTests.cs
using System.IO.Compression;
using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The whole Phase 1 pipeline over real files on disk — the
/// miniature regression fixture the spec calls for: same shapes as the
/// verified live figures, small enough to check by hand, entirely
/// synthetic. If a rule regresses (dedupe order, a date convention, the
/// business-day counter, an exclusion), one of these exact numbers moves.</summary>
public class TurnaroundRegressionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoreg_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private void WriteReport(string relativePath, string[][] dataRows)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new[] { new[] { "FileName", "SourceType", "Pagecount", "Destination" } }
            .Concat(dataRows).ToArray();
        var sb = new StringBuilder(
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
                sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{rows[r][c]}</t></is></c>");
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), Encoding.UTF8);
        w.Write(sb.ToString());
    }

    private static string[] Doc(string name, string source) => new[] { name, source, "10", "MIX" };

    [Fact]
    public void TheMiniatureLiveShapeComputesItsExactFigures()
    {
        WriteReport(@"20260706\20260706-0900-PECF Report.xlsx", new[]
        {
            Doc("20260706-A.pdf", "Email"),
            Doc("20260703-B.pdf", "Email"),
            Doc("20260702-C.pdf", "FAX"),
            Doc("20260701-D.pdf", "Paper"),
            Doc("20260704-E.pdf", "CD"),
            Doc("07022026 F.pdf", "ECAA"),
            Doc("07.03.2026 G.pdf", "ECAA"),
            Doc("20260707-H.pdf", "Email"),
            Doc("NODATE.pdf", "Email"),
        });
        WriteReport(@"20260803\20260803-0900-PECF Report.xlsx", new[]
        {
            Doc("20260706-A.pdf", "Email"),   // duplicate — the July report wins
            Doc("20260803-I.pdf", "Email"),
            Doc("20260731-J.pdf", "FAX"),
            Doc("20260730-K.pdf", "CD"),
            Doc("20260724-L.pdf", "Paper"),
        });

        var feed = UploadReportFeed.Load(_dir);
        Assert.Equal(2, feed.Report.FilesFound);
        Assert.Empty(feed.Report.Skipped);
        Assert.Equal(14, feed.Report.RowCount);

        var summary = TurnaroundSummary.Compute(feed.Table, new IgnoreList(new[] { "ECAA" }));

        Assert.Equal(9, summary.Docs.Count);
        Assert.Equal(new TurnaroundSummary.BucketCounts(3, 2, 2, 2), summary.Overall);
        Assert.Equal(55.56, summary.Overall.ZeroToOnePercent, 2);

        Assert.Equal(new[] { "2026-07", "2026-08" }, summary.ByMonth.Select(m => m.Month));
        Assert.Equal(new TurnaroundSummary.BucketCounts(2, 1, 1, 1), summary.ByMonth[0].Counts);
        Assert.Equal(new TurnaroundSummary.BucketCounts(1, 1, 1, 1), summary.ByMonth[1].Counts);

        Assert.Equal(new[] { "Email", "CD", "FAX", "Paper" },
            summary.BySource.Select(s => s.SourceType));
        Assert.Equal(3, summary.BySource[0].Counts.Total);

        Assert.Equal(1, summary.DuplicateRows);
        Assert.Equal(new TurnaroundSummary.IgnoredSource("ECAA", 2), Assert.Single(summary.Ignored));
        Assert.Equal(1, summary.FutureDated);
        Assert.Equal(1, summary.NoDate);
    }
}
```

- [ ] **Step 2: Run the new test**

Run:
```
dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build --filter "FullyQualifiedName~TurnaroundRegressionTests" -v minimal
```
Expected: `Passed! - Failed: 0, Passed: 1`. (This test is expected to pass
immediately — it pins behavior Tasks 1–5 built. If it fails, a rule above
was implemented wrong: debug the rule, not the fixture.)

- [ ] **Step 3: Run the full gate**

Run:
```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Core.Tests --no-build -v minimal
```
Expected: build succeeds with 0 warnings from the new files; the whole Core
suite passes. **Read the `Passed!` count** and confirm it is ≥ 43 higher
than before this plan started (10 + 5 + 5 + 14 + 8 + 1 new tests) — an exit
code of 0 with a shrunken count means the assembly was blocked, not passing.

- [ ] **Step 4: Commit**

```
git add tests/OrdoSort.Core.Tests/TurnaroundRegressionTests.cs
git commit -m "test(core): the miniature TAT regression fixture — every rule, exact figures"
```

---

## After Phase 1

Phase 2 (hub shell + Sources page + Turn-around page) gets its own plan once
this one lands — its view models consume `UploadReportFeed.Result` and
`TurnaroundSummary.Summary` exactly as defined here. Do not start Phase 2
work inside this plan's tasks.
