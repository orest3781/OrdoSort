# Filename list → manifest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn *Filename list* from a one-column list of names into a curated, column-configurable manifest that copies and exports exactly what is on screen.

**Architecture:** `FilenameList.Build` returns fully-populated `FileRow`s instead of strings, so column visibility is a UI concern and toggling a column never re-reads the disk. Clipboard/export text generation moves out of the window's code-behind into two pure Core functions (`ToText`, `ToCsv`) carrying the one formatting rule. The view model holds `_allRows` from the last build plus a visible projection (exclusions, name filter, sort direction) that Copy, Save and the counts line all read.

**Tech Stack:** C# 12 / .NET 8 (`net8.0` Core, `net8.0-windows` WPF), WPF, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-filename-list-upgrade-design.md`

## Global Constraints

- **Never throws.** `FilenameList.Build`'s contract is that a bad path produces `Ignored`/`Error`, never an exception. Every `FileInfo` read is individually guarded.
- **`Size` is a raw byte count. `Modified` is `yyyy-MM-dd HH:mm`, `CultureInfo.InvariantCulture`.** Never a formatted size, never a local short date.
- **Line separator is `Environment.NewLine`** in both `ToText` and `ToCsv` — `FilenameListTests.ToTextJoinsWithEnvironmentNewLine` pins this and the behaviour must not change.
- **Column order is fixed:** `#`, `Name`, `Size`, `Modified`, `Folder`, `Full path`. Header strings are exactly those.
- **Name is always emitted and is not a flag.** Table shape iff `(cols & ~Columns.Number) != Columns.None`.
- **Path comparison is `OrdinalIgnoreCase`** (Windows paths), matching `Intake.Add`'s existing dedupe.
- **CLIPBOARD RULE:** `System.Windows.Clipboard` appears only in `FilenameListWindow.xaml.cs`, never in a view model — the headless MTA tests cannot touch it.
- **Build and test commands** (Smart App Control blocks test assemblies by hash otherwise — see `docs/known-flakes.md`):
  ```
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Always read the `Passed!` line and its count.** Exit code 0 is not evidence anything ran.
- **A running `OrdoSort.exe` locks `src/OrdoSort.Wpf/bin/`.** Do not kill it — the app moves files. Build around it with `-p:BaseOutputPath=bin-agent/` (forward slash; a trailing backslash makes MSBuild report "Only one project can be specified") and pass the same flag to `dotnet test --no-build`.
- **The WPF test host cannot exit on this machine** — it dies in WebView2 teardown with `Failed to unregister class Chrome_WidgetWin_0`. Tests pass first. A run that prints a `Passed!` count and *then* aborts has told you the truth; a run that prints nothing has not.

---

### Task 1: `FileRow` replaces the string list

**Files:**
- Modify: `src/OrdoSort.Core/FilenameList.cs`
- Test: `tests/OrdoSort.Core.Tests/FilenameListTests.cs`

**Interfaces:**
- Consumes: `Intake.Expand(paths, recursive, filetypes)` → `.Files` (full paths), `.Ignored`, `.Error`; `NaturalSort.Instance`.
- Produces: `FilenameList.FileRow(string Name, long? Size, DateTime? Modified, string Folder, string FullPath)`; `Listing.Rows`; a temporary `Listing.Names` shim deleted in Task 6.

`Folder` is left `""` by this task — Task 2 fills it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/OrdoSort.Core.Tests/FilenameListTests.cs`:

```csharp
[Fact]
public void EachRowCarriesItsSizeAndFullPath()
{
    var path = Touch("report.pdf");
    File.WriteAllText(path, new string('x', 1234));

    var listing = FilenameList.Build(new[] { _dir },
        new FilenameList.Options(Recursive: false, IncludeExtension: true));

    var row = Assert.Single(listing.Rows);
    Assert.Equal("report.pdf", row.Name);
    Assert.Equal(1234L, row.Size);   // long, not int — Size is long?
    Assert.Equal(path, row.FullPath);
}

[Fact]
public void ModifiedIsTheFilesLastWriteTime()
{
    var path = Touch("report.pdf");
    var when = new DateTime(2026, 3, 4, 14, 22, 0, DateTimeKind.Local);
    File.SetLastWriteTime(path, when);

    var listing = FilenameList.Build(new[] { _dir },
        new FilenameList.Options(Recursive: false, IncludeExtension: true));

    Assert.Equal(when, Assert.Single(listing.Rows).Modified);
}

/// <summary>Build never throws, and a file that vanished between the walk and
/// the stat is reported as unknown rather than as 0 bytes — the row itself
/// stays, because it really was there in the walk.</summary>
[Fact]
public void AFileThatDisappearsAfterTheWalkHasNullSizeAndModified()
{
    var path = Touch("gone.pdf");
    var listing = FilenameList.Build(new[] { path },
        new FilenameList.Options(Recursive: false, IncludeExtension: true),
        stat: _ => throw new FileNotFoundException());

    var row = Assert.Single(listing.Rows);
    Assert.Equal("gone.pdf", row.Name);
    Assert.Null(row.Size);
    Assert.Null(row.Modified);
}

[Fact]
public void RowsStayInNaturalOrderByName()
{
    Touch("item2.pdf"); Touch("item10.pdf"); Touch("item1.pdf");

    var listing = FilenameList.Build(new[] { _dir },
        new FilenameList.Options(Recursive: false, IncludeExtension: true));

    Assert.Equal(new[] { "item1.pdf", "item2.pdf", "item10.pdf" },
        listing.Rows.Select(r => r.Name).ToArray());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo`

Expected: FAIL to compile — `'Listing' does not contain a definition for 'Rows'`, and `Build` takes no `stat` argument.

- [ ] **Step 3: Write the implementation**

In `src/OrdoSort.Core/FilenameList.cs`:

```csharp
public sealed record FileRow(
    string Name,
    long? Size,
    DateTime? Modified,
    string Folder,
    string FullPath);

public sealed record Listing(IReadOnlyList<FileRow> Rows, int Ignored, string Error = "")
{
    /// <summary>TEMPORARY shim so FilenameListViewModel keeps compiling while
    /// the layers migrate one task at a time. Deleted in Task 6, once the view
    /// model reads Rows directly.</summary>
    public IReadOnlyList<string> Names => Rows.Select(r => r.Name).ToList();
}

/// <summary>The per-file metadata read, injectable so a test can force the
/// failure that is otherwise a race: a file enumerated by Intake.Expand can be
/// gone, locked or access-denied by the time this runs. Production passes null
/// and gets the real FileInfo.</summary>
public static Listing Build(IReadOnlyList<string> paths, Options opt,
    Func<string, (long Size, DateTime Modified)>? stat = null)
{
    var expanded = Intake.Expand(paths, opt.Recursive, FolderMonitor.ParseFiletypes(opt.ExtensionFilter));
    stat ??= p => { var fi = new FileInfo(p); return (fi.Length, fi.LastWriteTime); };

    var rows = new List<FileRow>(expanded.Files.Count);
    foreach (var file in expanded.Files)
    {
        long? size = null;
        DateTime? modified = null;
        try
        {
            var (s, m) = stat(file);
            size = s;
            modified = m;
        }
        catch (Exception)
        {
            // gone, locked or denied since the walk — an unknown value, not a
            // reason to drop the row or to throw out of a never-throws method
        }

        rows.Add(new FileRow(
            opt.IncludeExtension ? Path.GetFileName(file) : Path.GetFileNameWithoutExtension(file),
            size, modified, "", file));
    }

    // Intake sorts by full PATH; re-sort on the NAME this list actually shows.
    rows.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));
    return new Listing(rows, expanded.Ignored, expanded.Error);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --filter "FullyQualifiedName~FilenameListTests" --nologo
```

Expected: `Passed!`, with every pre-existing `FilenameListTests` case still green — they use `listing.Names`, which the shim keeps working.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/FilenameList.cs tests/OrdoSort.Core.Tests/FilenameListTests.cs
git commit -m "feat(core): the filename listing carries rows, not bare names"
```

---

### Task 2: `Folder`, relative to the root it came from

**Files:**
- Modify: `src/OrdoSort.Core/FilenameList.cs`
- Test: `tests/OrdoSort.Core.Tests/FilenameListTests.cs`

**Interfaces:**
- Consumes: `FileRow` from Task 1.
- Produces: `FileRow.Folder` populated — the directory relative to the longest root that prefixes the file; `""` for a file at the root or added individually.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void AFileAtTheRootHasNoFolder()
{
    Touch("report.pdf");
    var listing = FilenameList.Build(new[] { _dir },
        new FilenameList.Options(Recursive: true, IncludeExtension: true));
    Assert.Equal("", Assert.Single(listing.Rows).Folder);
}

[Fact]
public void ANestedFileCarriesItsPathRelativeToTheRoot()
{
    Touch(Path.Combine("2026", "march", "report.pdf"));
    var listing = FilenameList.Build(new[] { _dir },
        new FilenameList.Options(Recursive: true, IncludeExtension: true));
    Assert.Equal(Path.Combine("2026", "march"), Assert.Single(listing.Rows).Folder);
}

/// <summary>A file added individually is its own root, so there is no folder
/// for it to be relative to.</summary>
[Fact]
public void AnIndividuallyAddedFileHasNoFolder()
{
    var path = Touch(Path.Combine("2026", "report.pdf"));
    var listing = FilenameList.Build(new[] { path },
        new FilenameList.Options(Recursive: false, IncludeExtension: true));
    Assert.Equal("", Assert.Single(listing.Rows).Folder);
}

/// <summary>Nested roots: the file sits under both, and the LONGEST wins, so
/// Folder stays as short and as meaningful as it can be.</summary>
[Fact]
public void TheLongestMatchingRootWins()
{
    Touch(Path.Combine("2026", "march", "report.pdf"));
    var listing = FilenameList.Build(
        new[] { _dir, Path.Combine(_dir, "2026") },
        new FilenameList.Options(Recursive: true, IncludeExtension: true));

    Assert.Equal("march", listing.Rows[0].Folder);
}

[Fact]
public void RootMatchingIgnoresCase()
{
    Touch(Path.Combine("2026", "report.pdf"));
    var listing = FilenameList.Build(new[] { _dir.ToUpperInvariant() },
        new FilenameList.Options(Recursive: true, IncludeExtension: true));
    Assert.Equal("2026", Assert.Single(listing.Rows).Folder);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```
dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --filter "FullyQualifiedName~FilenameListTests" --nologo
```

Expected: FAIL — the four new folder cases report `Assert.Equal() Failure` with actual `""`, because Task 1 hard-codes `Folder` to `""`.

- [ ] **Step 3: Write the implementation**

Replace the `""` argument in the `rows.Add(...)` call with `FolderFor(file, paths)`, and add:

```csharp
/// <summary>The directory of <paramref name="file"/> relative to whichever
/// root it arrived under. Roots can nest — someone drops a folder and then a
/// subfolder of it — so the LONGEST match wins and Folder stays as short as it
/// can be. A root that IS the file, an individually added file, has no folder
/// to be relative to.</summary>
private static string FolderFor(string file, IReadOnlyList<string> roots)
{
    var best = "";
    foreach (var root in roots)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmed, file, StringComparison.OrdinalIgnoreCase))
            return "";   // the file was added directly

        var prefix = trimmed + Path.DirectorySeparatorChar;
        if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > best.Length)
            best = trimmed;
    }

    if (best.Length == 0) return "";

    var dir = Path.GetDirectoryName(file);
    if (dir is null) return "";

    var relative = Path.GetRelativePath(best, dir);
    // GetRelativePath returns "." when dir IS the root — that is not a folder.
    return relative == "." ? "" : relative;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --filter "FullyQualifiedName~FilenameListTests" --nologo
```

Expected: `Passed!`

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/FilenameList.cs tests/OrdoSort.Core.Tests/FilenameListTests.cs
git commit -m "feat(core): each row records which folder it came from"
```

---

### Task 3: `Columns` and `ToText` — the one formatting rule

**Files:**
- Modify: `src/OrdoSort.Core/FilenameList.cs`
- Test: `tests/OrdoSort.Core.Tests/FilenameListTests.cs`

**Interfaces:**
- Consumes: `FileRow` from Tasks 1–2.
- Produces: `[Flags] FilenameList.Columns { None = 0, Number = 1, Size = 2, Modified = 4, Folder = 8, FullPath = 16 }` and `FilenameList.ToText(IReadOnlyList<FileRow> rows, Columns cols)`.

- [ ] **Step 1: Write the failing tests**

Add these two fixtures as fields on `FilenameListTests`, then the cases:

```csharp
private static readonly FilenameList.FileRow RowA =
    new("invoice-2024.pdf", 241152, new DateTime(2026, 3, 4, 14, 22, 0), "2026", @"C:\in\2026\invoice-2024.pdf");
private static readonly FilenameList.FileRow RowB =
    new("invoice-2025.pdf", 198656, new DateTime(2026, 3, 9, 9, 5, 0), "", @"C:\in\invoice-2025.pdf");

/// <summary>No new columns means byte-for-byte what the tool produced before
/// this feature existed.</summary>
[Fact]
public void NameOnlyIsAPlainListWithNoHeader()
{
    var text = FilenameList.ToText(new[] { RowA, RowB }, FilenameList.Columns.None);
    Assert.Equal("invoice-2024.pdf" + Environment.NewLine + "invoice-2025.pdf", text);
}

[Fact]
public void NumberAloneStaysAListAndRendersAsAPrefix()
{
    var text = FilenameList.ToText(new[] { RowA, RowB }, FilenameList.Columns.Number);
    Assert.Equal("1. invoice-2024.pdf" + Environment.NewLine + "2. invoice-2025.pdf", text);
}

[Fact]
public void ADataColumnMakesItTabSeparatedWithAHeader()
{
    var text = FilenameList.ToText(new[] { RowA },
        FilenameList.Columns.Size | FilenameList.Columns.Modified);
    Assert.Equal(
        "Name\tSize\tModified" + Environment.NewLine +
        "invoice-2024.pdf\t241152\t2026-03-04 14:22", text);
}

[Fact]
public void NumberBecomesItsOwnColumnInTableShape()
{
    var text = FilenameList.ToText(new[] { RowA },
        FilenameList.Columns.Number | FilenameList.Columns.Size);
    Assert.Equal("#\tName\tSize" + Environment.NewLine + "1\tinvoice-2024.pdf\t241152", text);
}

[Fact]
public void ColumnsAppearInTheFixedOrderHoweverTheyWereCombined()
{
    var text = FilenameList.ToText(new[] { RowA },
        FilenameList.Columns.FullPath | FilenameList.Columns.Folder | FilenameList.Columns.Size);
    Assert.StartsWith("Name\tSize\tFolder\tFull path" + Environment.NewLine, text);
}

[Fact]
public void AnUnreadableFileLeavesItsSizeAndModifiedCellsEmpty()
{
    var unknown = new FilenameList.FileRow("gone.pdf", null, null, "", @"C:\in\gone.pdf");
    var text = FilenameList.ToText(new[] { unknown },
        FilenameList.Columns.Size | FilenameList.Columns.Modified);
    Assert.EndsWith("gone.pdf\t\t", text);
}

[Fact]
public void AnEmptyRowSetProducesAnEmptyString()
{
    Assert.Equal("", FilenameList.ToText(Array.Empty<FilenameList.FileRow>(),
        FilenameList.Columns.Size));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo`

Expected: FAIL to compile — `'FilenameList' does not contain a definition for 'Columns'`.

- [ ] **Step 3: Write the implementation**

Add `using System.Globalization;` at the top of `FilenameList.cs` if absent, then:

```csharp
/// <summary>Which optional columns are on. Name is NOT a member: it is always
/// emitted, so including it would make a HasFlag check trivially true and
/// leave the list-vs-table rule below unstateable.</summary>
[Flags]
public enum Columns
{
    None = 0,
    Number = 1,
    Size = 2,
    Modified = 4,
    Folder = 8,
    FullPath = 16,
}

/// <summary>True once any column carrying DATA is on. Number alone does not
/// count — a numbered list of names is still a list, which is what lets
/// "1. invoice-2024.pdf" exist.</summary>
private static bool IsTable(Columns cols) => (cols & ~Columns.Number) != Columns.None;

private static string Cell(FileRow row, Columns column, int index) => column switch
{
    Columns.Number => (index + 1).ToString(CultureInfo.InvariantCulture),
    Columns.Size => row.Size?.ToString(CultureInfo.InvariantCulture) ?? "",
    Columns.Modified => row.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
    Columns.Folder => row.Folder,
    Columns.FullPath => row.FullPath,
    _ => row.Name,
};

// Fixed order, however the flags were combined. Columns.None stands for the
// always-present Name column in this table.
private static readonly (Columns Flag, string Header)[] Layout =
{
    (Columns.Number, "#"),
    (Columns.None, "Name"),
    (Columns.Size, "Size"),
    (Columns.Modified, "Modified"),
    (Columns.Folder, "Folder"),
    (Columns.FullPath, "Full path"),
};

private static List<(Columns Flag, string Header)> Active(Columns cols) =>
    Layout.Where(c => c.Flag == Columns.None || (cols & c.Flag) != 0).ToList();

/// <summary>The clipboard text. One rule: Name alone is a plain list, with
/// Number as a "1. " prefix; any data column makes it tab-separated with a
/// header row, and Number becomes a column of its own.</summary>
public static string ToText(IReadOnlyList<FileRow> rows, Columns cols)
{
    if (rows.Count == 0) return "";

    if (!IsTable(cols))
    {
        var numbered = (cols & Columns.Number) != 0;
        return string.Join(Environment.NewLine, rows.Select((r, i) =>
            numbered ? $"{i + 1}. {r.Name}" : r.Name));
    }

    var active = Active(cols);
    var lines = new List<string>(rows.Count + 1)
    {
        string.Join("\t", active.Select(c => c.Header)),
    };
    for (var i = 0; i < rows.Count; i++)
        lines.Add(string.Join("\t", active.Select(c => Cell(rows[i], c.Flag, i))));

    return string.Join(Environment.NewLine, lines);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --filter "FullyQualifiedName~FilenameListTests" --nologo
```

Expected: `Passed!`

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/FilenameList.cs tests/OrdoSort.Core.Tests/FilenameListTests.cs
git commit -m "feat(core): what you see is what you copy"
```

---

### Task 4: `ToCsv`, through the formula-injection guard

**Files:**
- Modify: `src/OrdoSort.Core/FilenameList.cs`
- Test: `tests/OrdoSort.Core.Tests/FilenameListTests.cs`

**Interfaces:**
- Consumes: `Columns`, `Active`, `Cell` from Task 3; `Csv.WriteRow(IEnumerable<string>)`, which is `internal` to `OrdoSort.Core` and therefore reachable here.
- Produces: `FilenameList.ToCsv(IReadOnlyList<FileRow> rows, Columns cols)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void CsvAlwaysCarriesAHeaderEvenForNameOnly()
{
    var csv = FilenameList.ToCsv(new[] { RowA }, FilenameList.Columns.None);
    Assert.Equal("Name" + Environment.NewLine + "invoice-2024.pdf", csv);
}

[Fact]
public void CsvQuotesAFieldContainingAComma()
{
    var row = new FilenameList.FileRow("smith, john.pdf", null, null, "", @"C:\in\smith, john.pdf");
    var csv = FilenameList.ToCsv(new[] { row }, FilenameList.Columns.None);
    Assert.Equal("Name" + Environment.NewLine + "\"smith, john.pdf\"", csv);
}

/// <summary>A filename is exactly the kind of user-controlled value that trips
/// Excel's formula parser, and Csv.EscapeField already guards it. This pins
/// that FilenameList routes through that guard rather than joining with commas
/// itself.</summary>
[Fact]
public void CsvNeutralisesAFilenameThatLooksLikeAFormula()
{
    var row = new FilenameList.FileRow("=cmd|'/c calc'!A1.pdf", null, null, "", @"C:\in\x.pdf");
    var csv = FilenameList.ToCsv(new[] { row }, FilenameList.Columns.None);
    Assert.Contains("'=cmd", csv);                        // leading apostrophe added
    Assert.DoesNotContain(Environment.NewLine + "=", csv);
}

[Fact]
public void CsvNumbersRowsFromOneWhenTheNumberColumnIsOn()
{
    var csv = FilenameList.ToCsv(new[] { RowA, RowB },
        FilenameList.Columns.Number | FilenameList.Columns.Size);
    Assert.Equal(
        "#,Name,Size" + Environment.NewLine +
        "1,invoice-2024.pdf,241152" + Environment.NewLine +
        "2,invoice-2025.pdf,198656", csv);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo`

Expected: FAIL to compile — `'FilenameList' does not contain a definition for 'ToCsv'`.

- [ ] **Step 3: Write the implementation**

```csharp
/// <summary>The .csv export. Always carries a header — a CSV without one is
/// not a table — and every field goes through Csv.EscapeField, which carries
/// the Excel formula-injection guard. That guard matters more here than almost
/// anywhere else in the app: filenames are user-controlled, and a file called
/// "=cmd...pdf" is something Excel will try to interpret when the exported
/// file is opened.</summary>
public static string ToCsv(IReadOnlyList<FileRow> rows, Columns cols)
{
    var active = Active(cols);
    var lines = new List<string>(rows.Count + 1)
    {
        Csv.WriteRow(active.Select(c => c.Header)),
    };
    for (var i = 0; i < rows.Count; i++)
        lines.Add(Csv.WriteRow(active.Select(c => Cell(rows[i], c.Flag, i))));

    return string.Join(Environment.NewLine, lines);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj -p:Deterministic=false -v q --nologo
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --nologo
```

Expected: `Passed!` across the whole Core suite.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/FilenameList.cs tests/OrdoSort.Core.Tests/FilenameListTests.cs
git commit -m "feat(core): CSV export, escaped through the formula guard"
```

---

### Task 5: `AskOpenFiles` on the dialog service

**Files:**
- Modify: `src/OrdoSort.Wpf/Services/IDialogService.cs`
- Modify: `src/OrdoSort.Wpf/Services/DialogService.cs`
- Modify: `tests/OrdoSort.Wpf.Tests/Fakes.cs`
- Create: `tests/OrdoSort.Wpf.Tests/DialogServiceContractTests.cs`

**Interfaces:**
- Produces: `string[] IDialogService.AskOpenFiles(string filter)` — empty array when cancelled, never null.

**Why a default implementation:** ten classes implement `IDialogService` — the real one, `FakeDialogs`, seven file-scoped `NoDialogs` duplicates across the WPF test suite, plus `ScriptedDialogs` and `RecordingDialogs` in the smoke tool. A plain abstract member forces an edit in all ten for a method eight of them do not care about. A default interface implementation (C# 8+, fine on net8.0) means only the two that genuinely differ are touched.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/DialogServiceContractTests.cs`:

```csharp
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>AskOpenFiles ships with a default interface implementation so the
/// eight dialog fakes across this suite and the smoke tool need no edit for a
/// method they do not use. These pin that the default actually behaves — a
/// default nobody tests is a silent hole in eight classes.</summary>
public class DialogServiceContractTests
{
    private sealed class OneFileDialogs : IDialogService
    {
        public string? Answer { get; set; }
        public void Warn(string message, string title) { }
        public void Info(string message, string title) { }
        public bool Confirm(string message, string title) => true;
        public string? AskSaveFile(string filter, string suggestedName) => null;
        public string? AskOpenFile(string filter) => Answer;
        public string? AskFilePath(string filter, string suggestedName) => null;
        public string? BrowseFolder(string? startAt) => null;
    }

    [Fact]
    public void TheDefaultAskOpenFilesFallsBackToTheSingleFilePicker()
    {
        IDialogService dialogs = new OneFileDialogs { Answer = @"C:\in\report.pdf" };
        Assert.Equal(new[] { @"C:\in\report.pdf" }, dialogs.AskOpenFiles("*.*"));
    }

    [Fact]
    public void TheDefaultAskOpenFilesReturnsEmptyWhenCancelled()
    {
        IDialogService dialogs = new OneFileDialogs { Answer = null };
        Assert.Empty(dialogs.AskOpenFiles("*.*"));
    }

    [Fact]
    public void FakeDialogsCanScriptSeveralFiles()
    {
        var dialogs = new FakeDialogs { NextOpenFiles = new[] { @"C:\a.pdf", @"C:\b.pdf" } };
        Assert.Equal(2, ((IDialogService)dialogs).AskOpenFiles("*.*").Length);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo`

Expected: FAIL to compile — `'IDialogService' does not contain a definition for 'AskOpenFiles'`.

- [ ] **Step 3: Write the implementation**

In `IDialogService.cs`, after `AskOpenFile`:

```csharp
    /// <summary>Pick one or more existing files. Empty when cancelled, never
    /// null. Defaulted rather than abstract on purpose: ten classes implement
    /// this interface and only two care about multi-select, so the rest inherit
    /// a correct single-file fallback instead of each carrying a throwaway
    /// override.</summary>
    string[] AskOpenFiles(string filter) =>
        AskOpenFile(filter) is { } one ? new[] { one } : Array.Empty<string>();
```

In `DialogService.cs`, after `AskOpenFile`:

```csharp
    public string[] AskOpenFiles(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, Multiselect = true };
        return dlg.ShowDialog(_owner) == true ? dlg.FileNames : Array.Empty<string>();
    }
```

In `Fakes.cs`, add to `FakeDialogs`:

```csharp
    public string[]? NextOpenFiles { get; set; }
    public string[] AskOpenFiles(string filter) =>
        NextOpenFiles ?? (NextOpenFile is { } one ? new[] { one } : Array.Empty<string>());
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build -p:BaseOutputPath=bin-agent/ --filter "FullyQualifiedName~DialogServiceContractTests" --nologo
```

Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Services/IDialogService.cs src/OrdoSort.Wpf/Services/DialogService.cs tests/OrdoSort.Wpf.Tests/Fakes.cs tests/OrdoSort.Wpf.Tests/DialogServiceContractTests.cs
git commit -m "feat(wpf): the dialog service can pick more than one file"
```

---

### Task 6: The view model projects rows

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs`
- Modify: `src/OrdoSort.Core/FilenameList.cs` (delete the `Names` shim)
- Test: `tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs`
- Test: `tests/OrdoSort.Core.Tests/FilenameListTests.cs` (migrate off `Names`)

**Interfaces:**
- Consumes: `FilenameList.Build/ToText/ToCsv/Columns/FileRow`.
- Produces: `Rows` as `ObservableCollection<FilenameList.FileRow>`; `Columns`, `NameFilter`, `Descending`, `OutputText`, `OutputCsv`, `IsTableShape`, `BrowseFilesCommand`.

**Migration note:** every existing assertion of the form `vm.Rows[0] == "report"` becomes `vm.Rows[0].Name == "report"`, and `listing.Names` becomes `listing.Rows.Select(r => r.Name)`. Do not weaken any existing test while migrating it.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>The whole point of gathering every column up front: a column
/// toggle must be a projection, not a filesystem walk. If this ever needs a
/// WaitFor, the data is being re-read and the design has regressed.</summary>
[Fact]
public void TurningOnAColumnReprojectsWithoutRebuilding()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("report.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.Columns = FilenameList.Columns.Size;

    // asserted IMMEDIATELY — no WaitFor
    Assert.True(vm.IsTableShape);
    Assert.StartsWith("Name\tSize", vm.OutputText);
}

[Fact]
public void TheNameFilterNarrowsRowsInMemory()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("invoice.pdf"); Touch("report.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.NameFilter = "inv";

    Assert.Single(vm.Rows);
    Assert.Equal("invoice.pdf", vm.Rows[0].Name);
}

[Fact]
public void TheNameFilterIsCaseInsensitive()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("Invoice.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.NameFilter = "INVOICE";

    Assert.Single(vm.Rows);
}

[Fact]
public void DescendingReversesTheProjectionWithoutRebuilding()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf"); Touch("b.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.Descending = true;

    Assert.Equal(new[] { "b.pdf", "a.pdf" }, vm.Rows.Select(r => r.Name).ToArray());
}

[Fact]
public void OutputCsvFollowsTheSameColumnsAsOutputText()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("report.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.Columns = FilenameList.Columns.Folder;

    Assert.StartsWith("Name,Folder", vm.OutputCsv);
}

[Fact]
public void BrowseFilesAddsEveryFileThePickerReturned()
{
    var a = Touch("a.pdf");
    var b = Touch("b.pdf");
    var dialogs = new FakeDialogs { NextOpenFiles = new[] { a, b } };
    var vm = MakeVm(dialogs);

    vm.BrowseFilesCommand.Execute(null);

    WaitFor(() => vm.Rows.Count == 2, "both picked files should be listed");
}

[Fact]
public async Task SaveWritesCsvOnceThereAreColumns()
{
    var target = Path.Combine(_dir, "out.csv");
    var dialogs = new FakeDialogs { NextFolder = _dir, NextSaveFile = target };
    Touch("report.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.Columns = FilenameList.Columns.Size;
    await vm.SaveAsync();

    var written = File.ReadAllText(target);
    Assert.StartsWith("Name,Size", written);
}

[Fact]
public async Task SaveStillWritesThePlainTextListWithNoColumns()
{
    var target = Path.Combine(_dir, "out.txt");
    var dialogs = new FakeDialogs { NextFolder = _dir, NextSaveFile = target };
    Touch("report.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    await vm.SaveAsync();

    Assert.Equal("report.pdf", File.ReadAllText(target));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo`

Expected: FAIL to compile — no `Columns`, `NameFilter`, `Descending`, `IsTableShape`, `OutputCsv` or `BrowseFilesCommand` on the view model.

- [ ] **Step 3: Write the implementation**

In `FilenameListViewModel.cs`, replace the `Rows` declaration and add the projection state:

```csharp
    // The last Build result, unfiltered. Rows below is the VISIBLE projection
    // of it — everything the user sees, copies and saves comes off that, which
    // is what keeps "what you see is what you copy" true of the name filter and
    // the sort direction and not only of the columns.
    private IReadOnlyList<FilenameList.FileRow> _allRows = Array.Empty<FilenameList.FileRow>();
    private int _lastIgnored;
    private string _lastError = "";

    public ObservableCollection<FilenameList.FileRow> Rows { get; } = new();

    private FilenameList.Columns _columns = FilenameList.Columns.None;
    public FilenameList.Columns Columns
    {
        get => _columns;
        set
        {
            if (!Set(ref _columns, value)) return;
            Raise(nameof(IsTableShape));
            Reproject();
        }
    }

    private string _nameFilter = "";
    public string NameFilter
    {
        get => _nameFilter;
        set { if (Set(ref _nameFilter, value)) Reproject(); }
    }

    private bool _descending;
    public bool Descending
    {
        get => _descending;
        set { if (Set(ref _descending, value)) Reproject(); }
    }

    /// <summary>Drives the Save dialog's filter. The shape rule itself lives in
    /// Core; this only mirrors it for the one thing the window needs.</summary>
    public bool IsTableShape =>
        (Columns & ~FilenameList.Columns.Number) != FilenameList.Columns.None;

    public string OutputText => FilenameList.ToText(Rows.ToList(), Columns);
    public string OutputCsv => FilenameList.ToCsv(Rows.ToList(), Columns);
```

Replace `ApplyListing` and add `Reproject`/`FormatCounts`:

```csharp
    private void ApplyListing(FilenameList.Listing listing)
    {
        _allRows = listing.Rows;
        _lastIgnored = listing.Ignored;
        _lastError = listing.Error;
        Reproject();
    }

    /// <summary>Rebuilds Rows from _allRows in memory. Deliberately never
    /// touches _listingProbe: only the roots and the three intake filters
    /// (IncludeSubfolders, IncludeExtension, ExtensionFilter) justify going back
    /// to the disk.</summary>
    private void Reproject()
    {
        IEnumerable<FilenameList.FileRow> visible = _allRows;

        if (NameFilter.Length > 0)
            visible = visible.Where(r =>
                r.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase));

        var projected = visible.ToList();
        if (Descending) projected.Reverse();

        Rows.Clear();
        foreach (var row in projected) Rows.Add(row);

        CountsLine = _sources.Count == 0 ? "" : FormatCounts();
        Raise(nameof(OutputText));
        Raise(nameof(OutputCsv));
    }

    private string FormatCounts()
    {
        var total = _allRows.Count;
        var line = $"{total} file{(total == 1 ? "" : "s")}";
        var hidden = total - Rows.Count;
        if (hidden > 0) line += $" · {hidden} filtered out";
        if (_lastIgnored > 0) line += $" · {_lastIgnored} ignored";
        if (_lastError.Length > 0) line += $" · {_lastError}";
        return line;
    }
```

Add the browse-files command — declaration `public RelayCommand BrowseFilesCommand { get; }` beside the others, and in the constructor:

```csharp
        BrowseFilesCommand = new RelayCommand(() =>
        {
            var files = _dialogs.AskOpenFiles("All files (*.*)|*.*");
            if (files.Length > 0) AddPaths(files);
        });
```

Point `SaveAsync` at the right shape:

```csharp
    internal async Task SaveAsync()
    {
        var (filter, suggested) = IsTableShape
            ? ("CSV file (*.csv)|*.csv", "filenames.csv")
            : ("Text file (*.txt)|*.txt", "filenames.txt");
        var path = _dialogs.AskSaveFile(filter, suggested);
        if (path is null) return;
        var text = IsTableShape ? OutputCsv : OutputText;   // read on the UI thread
        try
        {
            await _scheduler.Run(() => File.WriteAllText(path, text));
            Status = $"Saved to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            // never throw out of a command — the write failing (locked file,
            // gone folder, no permission) is feedback, not a crash
            Status = $"Couldn't save: {ex.Message}";
        }
    }
```

Then delete the `Names` shim from `src/OrdoSort.Core/FilenameList.cs`, so the record becomes:

```csharp
public sealed record Listing(IReadOnlyList<FileRow> Rows, int Ignored, string Error = "");
```

and migrate the pre-existing `FilenameListTests` cases that assert on `listing.Names` to `listing.Rows.Select(r => r.Name).ToArray()`.

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v minimal
dotnet test tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj --no-build --nologo
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build -p:BaseOutputPath=bin-agent/ --filter "FullyQualifiedName~FilenameList" --nologo
```

Expected: `Passed!` on both, with every pre-existing `FilenameListViewModelTests` case migrated and green.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/FilenameList.cs src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs tests/OrdoSort.Core.Tests/FilenameListTests.cs tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs
git commit -m "feat(wpf): the filename list projects rows instead of holding strings"
```

---

### Task 7: Removal that survives a rebuild

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs`

**Interfaces:**
- Consumes: `Reproject`, `_allRows`, `Rows` from Task 6.
- Produces: `SelectedPaths` (settable by the window), `RemoveSelectedCommand`, `RestoreRemovedCommand`, `RemovedCount`.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>The defect the exclusion set exists to prevent. A naive
/// Rows.Remove passes the first two asserts and fails the last: one keystroke
/// in the extension box re-walks the folder and the removed row comes straight
/// back.</summary>
[Fact]
public void ARemovedRowStaysRemovedAcrossARebuild()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("keep.pdf"); Touch("drop.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
    vm.RemoveSelectedCommand.Execute(null);
    Assert.Single(vm.Rows);
    Assert.Equal("keep.pdf", vm.Rows[0].Name);

    vm.ExtensionFilter = "pdf";   // forces a real rebuild through the probe

    WaitFor(() => vm.Rows.Count == 1 && vm.Rows[0].Name == "keep.pdf",
        "the removed row must not come back when the listing is rebuilt");
}

[Fact]
public void TheCountsLineReportsWhatWasRemoved()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("keep.pdf"); Touch("drop.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
    vm.RemoveSelectedCommand.Execute(null);

    Assert.Equal("2 files · 1 removed", vm.CountsLine);
}

[Fact]
public void RestoreRemovedBringsThemBack()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("keep.pdf"); Touch("drop.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
    vm.RemoveSelectedCommand.Execute(null);
    vm.RestoreRemovedCommand.Execute(null);

    Assert.Equal(2, vm.Rows.Count);
    Assert.Equal(0, vm.RemovedCount);
}

[Fact]
public void ClearForgetsTheRemovals()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("keep.pdf"); Touch("drop.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");
    vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
    vm.RemoveSelectedCommand.Execute(null);

    vm.ClearCommand.Execute(null);
    vm.BrowseFolderCommand.Execute(null);

    WaitFor(() => vm.Rows.Count == 2, "Clear resets the exclusion set as well as the sources");
}

[Fact]
public void RemoveSelectedDoesNothingWithAnEmptySelection()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("keep.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.RemoveSelectedCommand.Execute(null);

    Assert.Single(vm.Rows);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo`

Expected: FAIL to compile — no `SelectedPaths`, `RemoveSelectedCommand`, `RestoreRemovedCommand` or `RemovedCount`.

- [ ] **Step 3: Write the implementation**

```csharp
    // Full paths the user has removed. Keyed on PATH rather than on the row,
    // because a rebuild produces NEW FileRow instances for the same files —
    // holding row references would let every removed row reappear on the next
    // keystroke in the extension box.
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public int RemovedCount => _allRows.Count(r => _excluded.Contains(r.FullPath));

    /// <summary>Pushed in by the window on SelectionChanged — DataGrid's
    /// SelectedItems is not bindable.</summary>
    private IReadOnlyList<string> _selectedPaths = Array.Empty<string>();
    public IReadOnlyList<string> SelectedPaths
    {
        get => _selectedPaths;
        set
        {
            _selectedPaths = value;
            Raise(nameof(SelectedPaths));
        }
    }
```

In the constructor, beside the other commands:

```csharp
        RemoveSelectedCommand = new RelayCommand(() =>
        {
            if (_selectedPaths.Count == 0) return;
            foreach (var path in _selectedPaths) _excluded.Add(path);
            SelectedPaths = Array.Empty<string>();
            Reproject();
        });
        RestoreRemovedCommand = new RelayCommand(() =>
        {
            if (_excluded.Count == 0) return;
            _excluded.Clear();
            Reproject();
        });
```

with `public RelayCommand RemoveSelectedCommand { get; }` and `public RelayCommand RestoreRemovedCommand { get; }` declared beside `ClearCommand`.

Add `_excluded.Clear();` to `ClearCommand`'s body, before `Refresh(immediate: true);`.

In `Reproject`, apply the exclusion set first — replace the opening line with:

```csharp
        IEnumerable<FilenameList.FileRow> visible =
            _allRows.Where(r => !_excluded.Contains(r.FullPath));
```

and add `Raise(nameof(RemovedCount));` beside the other `Raise` calls at its end.

In `FormatCounts`, report removals ahead of the filter:

```csharp
    private string FormatCounts()
    {
        var total = _allRows.Count;
        var line = $"{total} file{(total == 1 ? "" : "s")}";
        var removed = RemovedCount;
        if (removed > 0) line += $" · {removed} removed";
        var hidden = total - removed - Rows.Count;
        if (hidden > 0) line += $" · {hidden} filtered out";
        if (_lastIgnored > 0) line += $" · {_lastIgnored} ignored";
        if (_lastError.Length > 0) line += $" · {_lastError}";
        return line;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build -p:BaseOutputPath=bin-agent/ --filter "FullyQualifiedName~FilenameListViewModelTests" --nologo
```

Expected: `Passed!`

- [ ] **Step 5: Prove the test is revert-proof**

Temporarily replace `RemoveSelectedCommand`'s body with a direct removal:

```csharp
            foreach (var p in _selectedPaths)
                Rows.Remove(Rows.First(r => r.FullPath == p));
```

Re-run the filter from Step 4.

Expected: `ARemovedRowStaysRemovedAcrossARebuild` FAILS with *"the removed row must not come back when the listing is rebuilt"*. Restore the real implementation and re-run to green before committing.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs
git commit -m "feat(wpf): removed rows stay removed when the listing rebuilds"
```

---

### Task 8: Copy follows the selection

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs`

**Interfaces:**
- Consumes: `SelectedPaths` from Task 7, `Rows`/`Columns` from Task 6.
- Produces: `CopyText` — what the window puts on the clipboard — and a `NoteCopied()` that reports the selected count.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void CopyTextIsEverythingWhenNothingIsSelected()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf"); Touch("b.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    Assert.Equal("a.pdf" + Environment.NewLine + "b.pdf", vm.CopyText);
}

[Fact]
public void CopyTextIsJustTheSelectionWhenThereIsOne()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf"); Touch("b.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };

    Assert.Equal("b.pdf", vm.CopyText);
}

[Fact]
public void TheSelectionKeepsTheColumnsAndTheirOrder()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf"); Touch("b.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.Columns = FilenameList.Columns.Folder;
    vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };

    Assert.StartsWith("Name\tFolder" + Environment.NewLine + "b.pdf", vm.CopyText);
}

[Fact]
public void NoteCopiedSaysHowManyOfHowMany()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf"); Touch("b.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

    vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };
    vm.NoteCopied();

    Assert.Equal("Copied 1 of 2", vm.Status);
}

[Fact]
public void NoteCopiedSaysThePlainCountWhenNothingIsSelected()
{
    var dialogs = new FakeDialogs { NextFolder = _dir };
    Touch("a.pdf");
    var vm = MakeVm(dialogs);
    vm.BrowseFolderCommand.Execute(null);
    WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

    vm.NoteCopied();

    Assert.Equal("Copied 1 name", vm.Status);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo`

Expected: FAIL to compile — no `CopyText` on the view model.

- [ ] **Step 3: Write the implementation**

```csharp
    /// <summary>What the Copy button puts on the clipboard: the selected rows
    /// when there are any, everything otherwise. This is what makes the button
    /// agree with the Ctrl+C the grid already supports — before this, the
    /// button copied all 200 rows while Ctrl+C copied the 5 you had picked.</summary>
    public string CopyText => FilenameList.ToText(SelectedRows(), Columns);

    private List<FilenameList.FileRow> SelectedRows()
    {
        if (_selectedPaths.Count == 0) return Rows.ToList();
        var wanted = new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase);
        return Rows.Where(r => wanted.Contains(r.FullPath)).ToList();
    }
```

Replace `NoteCopied`:

```csharp
    /// <summary>Set by the window's code-behind after Clipboard.SetText
    /// succeeds — Clipboard itself is a WPF/COM type and must never appear in
    /// this class (it isn't safe to touch from the headless MTA tests run
    /// under).</summary>
    public void NoteCopied()
    {
        var copied = SelectedRows().Count;
        Status = _selectedPaths.Count == 0
            ? $"Copied {copied} name{(copied == 1 ? "" : "s")}"
            : $"Copied {copied} of {Rows.Count}";
    }
```

Add `Raise(nameof(CopyText));` to `SelectedPaths`' setter and to the end of `Reproject`.

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build -p:BaseOutputPath=bin-agent/ --filter "FullyQualifiedName~FilenameListViewModelTests" --nologo
```

Expected: `Passed!`

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs
git commit -m "feat(wpf): Copy takes the rows you selected"
```

---

### Task 9: The window

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml`
- Modify: `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml.cs`
- Modify: `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs` (the `Show*` adapters)
- Modify: `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs:103-107`

**Interfaces:**
- Consumes: every view-model member from Tasks 6–8.
- Produces: `ShowNumber`, `ShowSize`, `ShowModified`, `ShowFolder`, `ShowFullPath` on the view model — `bool` adapters over the `Columns` flags, because `MenuItem.IsChecked` cannot bind a flags enum.

- [ ] **Step 1: Add the `Show*` adapters and their test**

Test first, in `FilenameListViewModelTests.cs`:

```csharp
[Fact]
public void TheShowAdaptersAreTwoWayOverTheColumnsFlags()
{
    var vm = MakeVm(new FakeDialogs());

    vm.ShowSize = true;
    Assert.Equal(FilenameList.Columns.Size, vm.Columns);

    vm.ShowFolder = true;
    Assert.Equal(FilenameList.Columns.Size | FilenameList.Columns.Folder, vm.Columns);

    vm.ShowSize = false;
    Assert.Equal(FilenameList.Columns.Folder, vm.Columns);
    Assert.False(vm.ShowSize);
    Assert.True(vm.ShowFolder);
}
```

Then in `FilenameListViewModel.cs`:

```csharp
    // MenuItem.IsChecked is a bool, so each flag needs its own two-way adapter
    // over Columns; the enum stays the single source of truth.
    private bool Has(FilenameList.Columns flag) => (Columns & flag) != 0;
    private void Toggle(FilenameList.Columns flag, bool on) =>
        Columns = on ? Columns | flag : Columns & ~flag;

    public bool ShowNumber   { get => Has(FilenameList.Columns.Number);   set => Toggle(FilenameList.Columns.Number, value); }
    public bool ShowSize     { get => Has(FilenameList.Columns.Size);     set => Toggle(FilenameList.Columns.Size, value); }
    public bool ShowModified { get => Has(FilenameList.Columns.Modified); set => Toggle(FilenameList.Columns.Modified, value); }
    public bool ShowFolder   { get => Has(FilenameList.Columns.Folder);   set => Toggle(FilenameList.Columns.Folder, value); }
    public bool ShowFullPath { get => Has(FilenameList.Columns.FullPath); set => Toggle(FilenameList.Columns.FullPath, value); }
```

and raise all five from `Columns`' setter, beside the existing `Raise(nameof(IsTableShape));`:

```csharp
            Raise(nameof(ShowNumber)); Raise(nameof(ShowSize)); Raise(nameof(ShowModified));
            Raise(nameof(ShowFolder)); Raise(nameof(ShowFullPath));
```

Run the same filtered test command as Task 8 Step 4 — expect FAIL to compile first, then `Passed!`.

- [ ] **Step 2: Widen the overflow seed**

In `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs`, replace the `FilenameListWindow` registry entry with the widest state the window can reach, so the new toolbar is what gets measured:

```csharp
        ["FilenameListWindow"] = new(480, 640, 400, 560, () =>
        {
            var vm = new FilenameListViewModel(new NoDialogs())
            {
                // every column on, a filter typed, Z to A ticked: the longest
                // the toolbar and the counts line ever get
                Columns = FilenameList.Columns.Number | FilenameList.Columns.Size
                        | FilenameList.Columns.Modified | FilenameList.Columns.Folder
                        | FilenameList.Columns.FullPath,
                NameFilter = "invoice",
                Descending = true,
            };
            return (new FilenameListWindow(vm), null);
        }),
```

Add `using OrdoSort.Core;` to that file if it is not already present.

- [ ] **Step 3: Write the window**

In `FilenameListWindow.xaml`, add a **Browse files…** button to the existing toolbar `WrapPanel`, right after **Browse folder…**:

```xml
                    <Button Content="Browse files…" Command="{Binding BrowseFilesCommand}" Margin="0,0,6,4" />
```

Add a second controls row as its own `WrapPanel`. It is a separate row rather than more items in the existing one because that toolbar already carries three buttons and two checkboxes, and five more controls would wrap to a third line at the 480px `MinWidth` the overflow tests pin:

```xml
            <WrapPanel Grid.Row="1" Margin="0,0,0,10">
                <TextBlock Text="Find:" VerticalAlignment="Center" Margin="0,4,6,4" />
                <TextBox Width="140" VerticalAlignment="Center" Margin="0,4,14,4"
                         Text="{Binding NameFilter, UpdateSourceTrigger=PropertyChanged}" />
                <Menu Background="Transparent" VerticalAlignment="Center" Margin="0,4,14,4">
                    <MenuItem Header="Columns ▾">
                        <MenuItem Header="Row number" IsCheckable="True"
                                  IsChecked="{Binding ShowNumber}" StaysOpenOnClick="True" />
                        <MenuItem Header="Size" IsCheckable="True"
                                  IsChecked="{Binding ShowSize}" StaysOpenOnClick="True" />
                        <MenuItem Header="Modified" IsCheckable="True"
                                  IsChecked="{Binding ShowModified}" StaysOpenOnClick="True" />
                        <MenuItem Header="Folder" IsCheckable="True"
                                  IsChecked="{Binding ShowFolder}" StaysOpenOnClick="True" />
                        <MenuItem Header="Full path" IsCheckable="True"
                                  IsChecked="{Binding ShowFullPath}" StaysOpenOnClick="True" />
                    </MenuItem>
                </Menu>
                <CheckBox Content="Z to A" IsChecked="{Binding Descending}"
                          VerticalAlignment="Center" Margin="0,4,14,4" />
                <Button Content="Restore removed" Command="{Binding RestoreRemovedCommand}" Margin="0,4" />
            </WrapPanel>
```

Add a fourth `RowDefinition Height="Auto"` to the outer `Grid` and bump the `Grid.Row` of the existing "Only these types" row and the grid row below it by one.

Replace the grid's single column with six. Each optional column takes a `Visibility` binding to its `Show*` flag through `BoolToVis`, and every one keeps the `GridCellTextSelectionAware` element style the existing Name column already carries — that style is what keeps selected-row text above the 4.5:1 contrast floor, as the existing comment in this file explains at length:

```xml
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="#" Width="Auto" MinWidth="30"
                                            Binding="{Binding (ItemsControl.AlternationIndex),
                                              RelativeSource={RelativeSource AncestorType=DataGridRow},
                                              Converter={StaticResource PlusOne}}"
                                            Visibility="{Binding DataContext.ShowNumber,
                                              RelativeSource={RelativeSource AncestorType=Window},
                                              Converter={StaticResource BoolToVis}}">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}" />
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>

                        <DataGridTextColumn Header="File name" Binding="{Binding Name}" Width="*" MinWidth="180">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Name}" />
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>

                        <DataGridTextColumn Header="Size" Binding="{Binding Size}" Width="Auto" MinWidth="60"
                                            Visibility="{Binding DataContext.ShowSize,
                                              RelativeSource={RelativeSource AncestorType=Window},
                                              Converter={StaticResource BoolToVis}}">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}" />
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>

                        <DataGridTextColumn Header="Modified" Width="Auto" MinWidth="120"
                                            Binding="{Binding Modified, StringFormat='yyyy-MM-dd HH:mm'}"
                                            Visibility="{Binding DataContext.ShowModified,
                                              RelativeSource={RelativeSource AncestorType=Window},
                                              Converter={StaticResource BoolToVis}}">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}" />
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>

                        <DataGridTextColumn Header="Folder" Binding="{Binding Folder}" Width="Auto" MinWidth="100"
                                            Visibility="{Binding DataContext.ShowFolder,
                                              RelativeSource={RelativeSource AncestorType=Window},
                                              Converter={StaticResource BoolToVis}}">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding Folder}" />
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>

                        <DataGridTextColumn Header="Full path" Binding="{Binding FullPath}" Width="Auto" MinWidth="140"
                                            Visibility="{Binding DataContext.ShowFullPath,
                                              RelativeSource={RelativeSource AncestorType=Window},
                                              Converter={StaticResource BoolToVis}}">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                    <Setter Property="ToolTip" Value="{Binding FullPath}" />
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                    </DataGrid.Columns>
```

On the `DataGrid` element itself add `AlternationCount="{Binding Rows.Count}"`, `SelectionChanged="OnSelectionChanged"`, `PreviewKeyDown="OnGridKeyDown"`, and the context menu:

```xml
                    <DataGrid.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="Remove from list" Command="{Binding RemoveSelectedCommand}" />
                            <MenuItem Header="Restore removed"  Command="{Binding RestoreRemovedCommand}" />
                        </ContextMenu>
                    </DataGrid.ContextMenu>
```

**Two converters this needs.** Both are keyed resources in `src/OrdoSort.Wpf/App.xaml` — that is where `ZeroToVis` and the rest live, *not* `Theme/Styles.xaml`.

`BoolToVis` already exists (`App.xaml:12`, WPF's built-in `BooleanToVisibilityConverter`) — use it as-is.

`PlusOne` does **not** exist and must be created. The `#` column shows the row's position, which no per-row property binding can express because `FileRow` has no index; `AlternationIndex` supplies it zero-based, so it needs +1. Create `src/OrdoSort.Wpf/Views/PlusOneConverter.cs`, matching the shape of the converters already in that folder:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Views;

/// <summary>DataGrid's AlternationIndex is zero-based; the "#" column people
/// read is one-based. Used only by FilenameListWindow's row-number column,
/// whose value cannot come from a property because FileRow has no index.</summary>
public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

and register it in `App.xaml` beside `ZeroToVis`:

```xml
            <views:PlusOneConverter x:Key="PlusOne" />
```

In `FilenameListWindow.xaml.cs`, add `using System.Linq;` and:

```csharp
    // DataGrid.SelectedItems is not bindable, so the window pushes the
    // selection down rather than the view model reaching up for it.
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _vm.SelectedPaths = NamesGrid.SelectedItems
            .OfType<OrdoSort.Core.FilenameList.FileRow>()
            .Select(r => r.FullPath)
            .ToList();

    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        _vm.RemoveSelectedCommand.Execute(null);
        e.Handled = true;
    }
```

and point `OnCopy` at the selection-aware text — replace its first line with:

```csharp
        var text = _vm.CopyText;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v minimal
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build -p:BaseOutputPath=bin-agent/ --filter "FullyQualifiedName!~Triage&DisplayName!~MainWindow&FullyQualifiedName!~WebView" --nologo
```

Expected: `Passed!` — **read the count**, and see the Global Constraints note about the host aborting after the count is printed.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml.cs src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs src/OrdoSort.Wpf/Views/PlusOneConverter.cs src/OrdoSort.Wpf/App.xaml tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs
git commit -m "feat(wpf): columns, find, and per-row removal in the filename list"
```

---

### Task 10: E2E scenario and docs

**Files:**
- Modify: `tools/OrdoSort.Smoke/E2E/Scenarios/SmallToolScenarios.cs`
- Modify: `README.md:104`

**Interfaces:**
- Consumes: the whole feature. Produces nothing new.

- [ ] **Step 1: Extend the scenario**

In `SmallToolScenarios.cs`, find `FilenamesAwkward` and add before its `ctx.Capture(win);`:

```csharp
        // A column toggle is a projection, not a rebuild — which is exactly why
        // no E2EPump wait appears on these two checks.
        vm.Columns = FilenameList.Columns.Size | FilenameList.Columns.Folder;
        ctx.Check("turning columns on switches the copy text to a table with a header",
            vm.CopyText.StartsWith("Name\tSize\tFolder", StringComparison.Ordinal), vm.CopyText);

        var doomed = vm.Rows[0].FullPath;
        vm.SelectedPaths = new[] { doomed };
        vm.RemoveSelectedCommand.Execute(null);
        ctx.Check("the removed row is gone and the counts line says so",
            vm.Rows.All(r => r.FullPath != doomed)
            && vm.CountsLine.Contains("1 removed", StringComparison.Ordinal),
            vm.CountsLine);

        // ExtensionFilter is a real rebuild through the probe, so this one DOES
        // need a pump — and it is the check that matters: the exclusion set has
        // to outlive the walk.
        vm.ExtensionFilter = "pdf";
        E2EPump.Until(() => vm.Rows.Count > 0 && vm.Rows.All(r => r.FullPath != doomed),
            "the removed row must not return when the listing rebuilds");
        ctx.Check("it stays gone across a real rebuild",
            vm.Rows.All(r => r.FullPath != doomed), vm.CountsLine);
```

Add `using OrdoSort.Core;` to the file if it is not already present.

**Also correct that file's `<b>FilenameListViewModel</b>` doc-comment paragraph.** It currently states that `Rows`, `CountsLine` and the `OutputText` notification are set only inside `ApplyListing`, which is `DebouncedProbe`'s apply callback — and concludes that waiting on any of the three genuinely needs a pump. After Task 6 they are set in `Reproject`, which `ApplyListing` calls *and* which every in-memory control (columns, find, Z to A, removal) calls directly with no probe involved. Say so explicitly: the accuracy of that paragraph is how the scenarios know what to wait on and what to assert immediately.

- [ ] **Step 2: Build the smoke tool**

Run: `dotnet build tools/OrdoSort.Smoke/OrdoSort.Smoke.csproj -p:Deterministic=false -p:BaseOutputPath=bin-agent/ -v q --nologo`

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Update the README**

`README.md:104` currently reads `an alternative). Also *Filename list*, *PDF page counts*,`. Replace the *Filename list* mention with:

```
  an alternative). Also *Filename list* (drop files or folders and get their
  names as a list you curate — remove rows and they stay removed, add size,
  modified date, folder or full path as columns, and copy or export exactly
  the columns you can see), *PDF page counts*,
```

- [ ] **Step 4: Full verification**

Close any running `OrdoSort.exe` first, then:

```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

(If the app cannot be closed, add `-p:BaseOutputPath=bin-agent/` to both commands.)

Expected: `Passed!` on both assemblies. **Read both counts** — an exit code of 0 is not evidence anything ran.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/SmallToolScenarios.cs README.md
git commit -m "test(e2e): the filename list keeps its removals and its columns"
```

---

## Notes for the reviewer

**The two tests that carry the design.** `ARemovedRowStaysRemovedAcrossARebuild` (Task 7) and `TurningOnAColumnReprojectsWithoutRebuilding` (Task 6) each pin a decision the spec argued for, and each fails against the obvious wrong implementation — `Rows.Remove` for the first, gather-on-demand for the second. Task 7 Step 5 proves the first is genuinely revert-proof rather than merely green.

**A test that needs a `WaitFor` where this plan says it should not is a design regression, not a flaky test.** `Reproject` must never re-arm `_listingProbe`. The only things that justify a filesystem walk are the roots and the three intake filters.

**Two spec items are deliberately absent from every task**, both listed under the spec's own Out of scope: `.xlsx` export (`XlsxTable` is a reader only) and folding this window into a tabbed shell.
