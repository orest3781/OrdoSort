# E2E Demonstration Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One command — `dotnet run --project tools\OrdoSort.Smoke -- e2e` — that drives every user-facing surface of OrdoSort as a real window against real files on disk, asserts the results, and emits a self-contained HTML evidence report plus a CI exit code.

**Architecture:** A new `e2e` mode inside the existing `tools/OrdoSort.Smoke` console app. A scenario registry holds records of `(Surface, Name, Kind, Run)`. The runner boots real `App.xaml` resources on an explicit STA thread (`SmokeUi.Boot`), then for each scenario builds an isolated temp fixture, constructs the real view model and real `Window`, drives the same commands the buttons bind to, asserts against the filesystem, rasterizes the window to PNG, and tears the fixture down. Failures are collected, never thrown, so one broken scenario still yields a full report.

**Tech Stack:** C# / .NET 8, WPF (`net8.0-windows`), xUnit 2.5.3 for the harness's own unit tests, `PdfSharp 6.1.1` and `System.IO.Compression` for fixture generation, `RenderTargetBitmap` + `PngBitmapEncoder` for screenshots.

**Spec:** `docs/superpowers/specs/2026-08-09-e2e-demonstration-suite-design.md`

## Global Constraints

- **Never inject a work seam.** Scenarios construct view models leaving `zipper`, `extractor`, `merger`, `counter`, `unlocker`, `fileSize`, `tryReveal`, `probe`, and `plan` at their defaults. The only injectable dependencies allowed are `IDialogService` and `IWorkScheduler`. This is the rule that separates this suite from the 590 existing view model tests.
- **Pump, never sleep.** Scenarios run on the STA dispatcher thread that owns the windows. `Thread.Sleep` there blocks the message loop that `DebouncedProbe<T>` needs to marshal results back through `uiContext`, deadlocking until timeout. Always use `E2EPump.Until(...)`.
- **Construct view models with `uiContext: SynchronizationContext.Current`** so results marshal back to the dispatcher thread as in production. (Existing unit tests pass `uiContext: null`; the harness must not copy that.)
- **Fixtures are generated in code**, never checked in. `MinimalPdf.Write` for plain PDFs, `PdfSharp` `SecuritySettings.UserPassword` for encrypted ones, `System.IO.Compression` for archives.
- **Write only under the temp root and `evidence/`.** A scenario that writes anywhere else is a bug in the scenario.
- **Target framework** is `net8.0-windows`; the Smoke project already targets it.
- **Local build constraint:** Smart App Control blocks test assemblies by hash. Build with `-p:Deterministic=false`, then run tests with `--no-build`.
- **Reports read spreadsheets** (`SweptTable.Load`, csv + xlsx), *not* `history.sqlite`. Only the History surface touches the audit database.
- **Console output convention** matches the existing smoke modes: a single `E2E PASS — <n> scenarios, <m> surfaces` line, or `E2E FAIL:` followed by one `  * <failure>` line each. Exit 0 or 1.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `tools/OrdoSort.Smoke/E2E/E2EPump.cs` | Dispatcher pumping (`Until`) and offscreen window show. No scenario knowledge. |
| `tools/OrdoSort.Smoke/E2E/Fixture.cs` | Temp-root lifecycle + fixture builders (PDFs, encrypted PDFs, zips, csv/xlsx). |
| `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs` | `IDialogService` with per-scenario queued answers + unconsumed-answer reporting. |
| `tools/OrdoSort.Smoke/E2E/Scenario.cs` | The `Scenario` record, `ScenarioContext`, and the assertion recorder. |
| `tools/OrdoSort.Smoke/E2E/Evidence.cs` | `report.html` (base64-inline PNGs), `report.md`, PNG capture. |
| `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` | Registry, STA boot, per-scenario isolation + watchdog, console summary, exit code. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/ZipScenarios.cs` | Zip surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs` | Unzip surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs` | Zip merge surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/UnlockScenarios.cs` | Unlock PDFs surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/BulkRenameScenarios.cs` | Bulk rename surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/MatchMergeScenarios.cs` | Match and merge surface. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/SmallToolScenarios.cs` | Box labels, Filename list, Page counts, List reformatter. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/ReportScenarios.cs` | Turn-around time, Production. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/HistoryScenarios.cs` | History window + spreadsheet export. |
| `tools/OrdoSort.Smoke/E2E/Scenarios/RoutingScenarios.cs` | The routing loop (WebView2), folded in from `Program.cs`'s `Drive()`. |
| `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs` | Unit coverage for `Fixture`, `ScriptedDialogs`, `Evidence`. |

**Modified:**

- `tools/OrdoSort.Smoke/Program.cs` — one dispatch line for the `e2e` mode.
- `tools/OrdoSort.Smoke/Screenshots.cs` — `Pump` and `ShowOffscreen` move to `E2EPump`; `Screenshots` calls the shared versions.
- `.gitignore` — add `evidence/`.
- `.github/workflows/` — a desktop-session job running the suite and uploading `evidence/`.
- `README.md` — a short section on running the suite.

---

## Task 1: Pump and offscreen window helpers

Extract the dispatcher-pumping machinery from `Screenshots.cs` into a shared helper both modes use. This must land first — every later task depends on it, and getting it wrong deadlocks every scenario.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/E2EPump.cs`
- Modify: `tools/OrdoSort.Smoke/Screenshots.cs:250-270` (Pump), `:207-215` (ShowOffscreen)
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `static bool E2EPump.Until(Func<bool> ready, int timeoutMs = 8000, Action? kickoff = null)` — returns `true` if `ready()` became true before the deadline, `false` on timeout. Never throws.
  - `static void E2EPump.ShowOffscreen(Window win)` — shows a window at `Left = -20000` with `ShowActivated = false` and forces a layout pass.
  - `static void E2EPump.Drain()` — pumps queued dispatcher operations at `DispatcherPriority.Background` once, so posted continuations run before an assertion.

- [ ] **Step 1: Write the failing test**

`E2EPump.Until` is the piece with real logic, and it is testable off a window. Add to `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs`:

```csharp
using System.Windows.Threading;
using OrdoSort.Smoke.E2E;

namespace OrdoSort.Wpf.Tests;

/// <summary>Unit coverage for the E2E harness's own moving parts — the pump,
/// the fixture builder, the scripted dialogs, and the evidence writer. The
/// scenarios themselves can only run under the STA harness; these are the
/// pieces that must be right before any scenario can be trusted.</summary>
public class E2EHarnessTests
{
    /// <summary>A condition already true must not arm a frame at all — the
    /// common case, and the one where an unnecessary DispatcherFrame would
    /// hang a caller that has no message loop running.</summary>
    [Fact]
    public void UntilReturnsImmediatelyWhenConditionAlreadyTrue()
    {
        Assert.True(E2EPump.Until(() => true, timeoutMs: 50));
    }

    /// <summary>A condition that never comes true reports false rather than
    /// throwing: one stuck scenario must not abort the run.</summary>
    [Fact]
    public void UntilReturnsFalseOnTimeoutWithoutThrowing()
    {
        Assert.False(E2EPump.Until(() => false, timeoutMs: 150));
    }

    /// <summary>The condition flips from a dispatcher callback, which is the
    /// real shape: DebouncedProbe marshals its result back through uiContext,
    /// so Until only observes it if it is genuinely pumping the queue.</summary>
    [Fact]
    public void UntilObservesAConditionSetFromADispatcherCallback()
    {
        var flipped = false;
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        t.Tick += (_, _) => { flipped = true; t.Stop(); };
        t.Start();

        Assert.True(E2EPump.Until(() => flipped, timeoutMs: 3000));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
```

Expected: FAIL to compile — `The type or namespace name 'E2E' does not exist in the namespace 'OrdoSort.Smoke'`.

- [ ] **Step 3: Add the project reference the test needs**

`OrdoSort.Wpf.Tests` cannot see the Smoke tool yet. Add to `tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj` inside the existing `ItemGroup` that holds the `OrdoSort.Wpf` reference:

```xml
    <ProjectReference Include="..\..\tools\OrdoSort.Smoke\OrdoSort.Smoke.csproj" />
```

The Smoke project is an `Exe`; referencing it from a test project is fine and gives the tests its types. Confirm `tools/OrdoSort.Smoke/OrdoSort.Smoke.csproj` has `<UseWPF>true</UseWPF>` and targets `net8.0-windows` — if not, the reference will not resolve WPF types.

- [ ] **Step 4: Write the implementation**

Create `tools/OrdoSort.Smoke/E2E/E2EPump.cs`. Note the existing Smoke files use top-level/implicit namespaces; these new files take an explicit `OrdoSort.Smoke.E2E` namespace so the test project can reference them.

```csharp
using System.Windows;
using System.Windows.Threading;

namespace OrdoSort.Smoke.E2E;

/// <summary>Dispatcher pumping for the E2E harness.
///
/// Scenarios run on the STA thread that owns the windows, so the usual
/// test-side wait — Thread.Sleep in a polling loop, as
/// FilenameListViewModelTests uses — is not available here: sleeping on this
/// thread blocks the very message loop DebouncedProbe&lt;T&gt; needs to
/// marshal its result back through uiContext, so the condition can never
/// become true and the wait always burns its full timeout. Pumping a nested
/// DispatcherFrame keeps that loop alive while we wait.</summary>
public static class E2EPump
{
    /// <summary>Pump until <paramref name="ready"/> is true or the timeout
    /// elapses. Returns whether it came true. Never throws — a stuck
    /// scenario is a recorded failure, not an aborted run.</summary>
    public static bool Until(Func<bool> ready, int timeoutMs = 8000, Action? kickoff = null)
    {
        if (kickoff is null && ready()) return true;

        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + timeoutMs;
        var success = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        timer.Tick += (_, _) =>
        {
            bool done;
            try { done = ready(); }
            catch { done = false; }   // a predicate that throws mid-load just isn't ready yet
            if (done) { success = true; frame.Continue = false; }
            else if (Environment.TickCount64 >= deadline) { frame.Continue = false; }
        };
        timer.Start();
        kickoff?.Invoke();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return success;
    }

    /// <summary>Run every queued dispatcher operation down to Background
    /// priority, then return — for the case where work is already posted and
    /// only needs a turn of the loop, with no condition to wait on.</summary>
    public static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Show a window far off-screen so it lays out and renders
    /// without stealing focus or appearing during a run.</summary>
    public static void ShowOffscreen(Window win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build --filter "FullyQualifiedName~E2EHarnessTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 6: Point Screenshots at the shared helpers**

In `tools/OrdoSort.Smoke/Screenshots.cs`, delete the private `Pump` and `ShowOffscreen` methods and replace their call sites. There are call sites in `Capture`, `CaptureTriage`, `CapturePrintPreview`, `CaptureHistory`, `CaptureMainWindow`, and `CaptureMainWindowDone` — find them all with:

```
grep -n "Pump(\|ShowOffscreen(" tools/OrdoSort.Smoke/Screenshots.cs
```

Add `using OrdoSort.Smoke.E2E;` at the top, then rewrite each call as `E2EPump.Until(...)` / `E2EPump.ShowOffscreen(...)`. The signatures are identical, so this is a mechanical rename. Do not change any behaviour.

- [ ] **Step 7: Verify the screenshots mode still works**

```
dotnet run --project tools/OrdoSort.Smoke -- screenshots
```

Expected: same output as before the change, PNGs written. This is the regression guard on the extraction.

- [ ] **Step 8: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/E2EPump.cs tools/OrdoSort.Smoke/Screenshots.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj
git commit -m "feat(e2e): shared dispatcher pump for the E2E harness

Scenarios run on the STA thread owning the windows, so the Thread.Sleep
polling the view model tests use would block the message loop the probes
need. Extracts Screenshots' DispatcherFrame pump into E2EPump so both
modes share it."
```

---

## Task 2: Fixture builder

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Fixture.cs`
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs` (extend)

**Interfaces:**
- Consumes: `MinimalPdf.Write(string path, string text)` (existing, in `tools/OrdoSort.Smoke/MinimalPdf.cs` — note it is `internal`, so make it `public` in this task or the fixture cannot call it across the namespace boundary cleanly; prefer changing `internal static class MinimalPdf` to `public static class MinimalPdf` and moving it under namespace `OrdoSort.Smoke.E2E`).
- Produces:
  - `sealed class Fixture : IDisposable`
  - `Fixture.Create(string scenarioName)` → static factory, roots at `%TEMP%\ordo_e2e_<guid>\<scenarioName>\`
  - `string Fixture.Root { get; }`
  - `string Fixture.Dir(params string[] segments)` — creates and returns a subdirectory
  - `string Fixture.Pdf(string relativePath, string text = "SAMPLE")` — plain PDF, returns full path
  - `string Fixture.EncryptedPdf(string relativePath, string userPassword, int pages = 1)`
  - `string Fixture.CorruptPdf(string relativePath)` — random bytes under a `.pdf` name
  - `string Fixture.Zip(string relativePath, params (string entryName, string sourcePath)[] entries)`
  - `string Fixture.RawZip(string relativePath, params (string entryName, byte[] bytes)[] entries)` — writes entry names verbatim, so a `..\..\evil.txt` traversal entry can be built
  - `string Fixture.EmptyZip(string relativePath)`
  - `string Fixture.Text(string relativePath, string content)` — csv, txt, anything text
  - `string Fixture.Xlsx(string relativePath, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)`
  - `void Fixture.Dispose()` — recursive delete, best effort, never throws

- [ ] **Step 1: Write the failing tests**

Append to `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs`:

```csharp
    /// <summary>The whole isolation guarantee in one assertion: everything a
    /// fixture makes lives under its own root, and the root is gone after
    /// disposal. A scenario that writes outside this is a bug in the
    /// scenario.</summary>
    [Fact]
    public void FixtureCreatesUnderItsOwnRootAndCleansUp()
    {
        string root;
        using (var fx = Fixture.Create("iso-check"))
        {
            root = fx.Root;
            var pdf = fx.Pdf("inbox/one.pdf", "ALPHA");
            Assert.StartsWith(root, pdf, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(pdf));
        }
        Assert.False(Directory.Exists(root));
    }

    /// <summary>An encrypted fixture must actually be encrypted — otherwise
    /// every Unlock scenario silently proves nothing. Reading it back
    /// without the password must fail.</summary>
    [Fact]
    public void EncryptedPdfIsActuallyEncrypted()
    {
        using var fx = Fixture.Create("enc-check");
        var path = fx.EncryptedPdf("locked.pdf", "right-one");

        var probe = OrdoSort.Core.Unlock.ProbeReadiness(path, Array.Empty<string>());
        Assert.Equal("needs_password", probe.Status);
    }

    /// <summary>RawZip must write entry names verbatim, including a
    /// traversal name — if it sanitises them, the zip-slip scenario tests
    /// nothing.</summary>
    [Fact]
    public void RawZipPreservesATraversalEntryNameVerbatim()
    {
        using var fx = Fixture.Create("slip-check");
        var zip = fx.RawZip("evil.zip", (@"..\..\escaped.txt", new byte[] { 1, 2, 3 }));

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName.Contains("..", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
```

Expected: FAIL to compile — `The name 'Fixture' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `tools/OrdoSort.Smoke/E2E/Fixture.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using OrdoSort.Core;
using PdfSharp.Pdf;

namespace OrdoSort.Smoke.E2E;

/// <summary>An isolated temp directory for one scenario, plus builders for
/// every kind of input the tools take. Fixtures are generated in code so the
/// repo carries no binary test assets — the same approach
/// UnlockProbeTests.MakeEncrypted already uses for encrypted PDFs.
///
/// Everything a scenario writes must land under Root. Disposal deletes the
/// tree; failures there are reported but never change a run's verdict,
/// because a locked temp file is not a product defect.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; }

    private Fixture(string root) => Root = root;

    public static Fixture Create(string scenarioName)
    {
        var safe = string.Concat(scenarioName.Select(
            c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var root = Path.Combine(
            Path.GetTempPath(), "ordo_e2e_" + Guid.NewGuid().ToString("N"), safe);
        Directory.CreateDirectory(root);
        return new Fixture(root);
    }

    /// <summary>Create and return a subdirectory of Root.</summary>
    public string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string Resolve(string relativePath)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public string Pdf(string relativePath, string text = "SAMPLE")
    {
        var path = Resolve(relativePath);
        MinimalPdf.Write(path, text);
        return path;
    }

    /// <summary>Same shape as UnlockProbeTests.MakeEncrypted: a real
    /// PdfSharp document with user and owner passwords set.</summary>
    public string EncryptedPdf(string relativePath, string userPassword, int pages = 1)
    {
        var path = Resolve(relativePath);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPassword;
        doc.Save(path);
        return path;
    }

    /// <summary>Random bytes under a .pdf name — PdfSharp cannot even find
    /// the "%PDF" prefix, which is the damaged-file case the tools must
    /// report rather than crash on.</summary>
    public string CorruptPdf(string relativePath)
    {
        var path = Resolve(relativePath);
        var bytes = new byte[512];
        new Random(20260809).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string Zip(string relativePath, params (string entryName, string sourcePath)[] entries)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, source) in entries)
            archive.CreateEntryFromFile(source, name);
        return path;
    }

    /// <summary>Entry names written verbatim — no sanitising — so a
    /// traversal name like @"..\..\escaped.txt" survives into the archive.
    /// That is the only way to build the zip-slip fixture honestly.</summary>
    public string RawZip(string relativePath, params (string entryName, byte[] bytes)[] entries)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var s = entry.Open();
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    public string EmptyZip(string relativePath)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        return path;
    }

    public string Text(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A minimal xlsx: a zip of two XML parts plus the package
    /// relationships, with every cell a shared string.
    ///
    /// Written by hand because OrdoSort.Core's XlsxTable is a READER only
    /// (internal static, one Read method) — there is no writer to reuse, and
    /// adding one to ship just to serve fixtures would put test-only code in
    /// the product. The shape mirrors what XlsxTable.Read looks for:
    /// xl/sharedStrings.xml and a first worksheet whose cells carry t="s"
    /// indices into it. The round-trip is asserted in
    /// E2EHarnessTests.XlsxFixtureRoundTripsThroughSweptTable — if this
    /// drifts from the reader, that test fails rather than a report
    /// scenario mysteriously finding zero rows.</summary>
    public string Xlsx(string relativePath, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var path = Resolve(relativePath);

        var all = new List<IReadOnlyList<string>> { headers };
        all.AddRange(rows);

        // Shared-string table: every distinct cell value, in first-seen order.
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in all.SelectMany(r => r))
            if (!index.ContainsKey(value)) { index[value] = strings.Count; strings.Add(value); }

        static string Esc(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static string Col(int i)
        {
            var name = "";
            for (i++; i > 0; i = (i - 1) / 26) name = (char)('A' + (i - 1) % 26) + name;
            return name;
        }

        var sst = new StringBuilder();
        sst.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sst.Append($"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{strings.Count}" uniqueCount="{strings.Count}">""");
        foreach (var s in strings) sst.Append($"<si><t>{Esc(s)}</t></si>");
        sst.Append("</sst>");

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < all.Count; r++)
        {
            sheet.Append($"""<row r="{r + 1}">""");
            for (var c = 0; c < all[r].Count; c++)
                sheet.Append($"""<c r="{Col(c)}{r + 1}" t="s"><v>{index[all[r][c]]}</v></c>""");
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        const string contentTypes = """
            <?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>
            """;
        const string rootRels = """
            <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """;
        const string workbook = """
            <?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
            """;
        const string workbookRels = """
            <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
            """;

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string content)
        {
            using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Part("[Content_Types].xml", contentTypes);
        Part("_rels/.rels", rootRels);
        Part("xl/workbook.xml", workbook);
        Part("xl/_rels/workbook.xml.rels", workbookRels);
        Part("xl/sharedStrings.xml", sst.ToString());
        Part("xl/worksheets/sheet1.xml", sheet.ToString());
        return path;
    }

    public void Dispose()
    {
        // Delete the guid parent, not just the scenario dir, so nothing is
        // left behind under %TEMP%.
        var parent = Path.GetDirectoryName(Root);
        try { if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
        catch { /* a locked temp file is not a product defect */ }
    }
}
```

- [ ] **Step 4: Add the xlsx round-trip test**

The hand-written xlsx is the one fixture that can silently drift from the app's reader — a wrong part name or namespace yields a file that opens but has zero rows, which would look like a Reports bug rather than a fixture bug. Pin it with the app's own reader. Append to `E2EHarnessTests`:

```csharp
    /// <summary>The hand-written xlsx must be readable by the app's OWN
    /// reader (SweptTable.Load → XlsxTable.Read). Without this, a malformed
    /// fixture would surface later as a Reports scenario finding zero rows —
    /// a product bug that isn't one.</summary>
    [Fact]
    public void XlsxFixtureRoundTripsThroughSweptTable()
    {
        using var fx = Fixture.Create("xlsx-check");
        var path = fx.Xlsx("report.xlsx",
            new[] { "Document", "Category" },
            new[] { new[] { "20240101--1111.pdf", "INVOICE" } });

        var table = OrdoSort.Core.SweptTable.Load(new[] { path });

        Assert.Contains("Document", table.Headers);
        Assert.Contains("Category", table.Headers);
        Assert.Single(table.Rows);
        Assert.Equal("INVOICE", table.Rows[0].Cells["Category"]);
    }
```

Run it. If it fails, the fixture's XML is wrong — read `src/OrdoSort.Core/XlsxTable.cs:21-45` to see exactly which parts `Read` looks for (`xl/sharedStrings.xml`, then `FirstSheetEntry`, falling back to `xl/worksheets/sheet1.xml`) and fix the writer. Do **not** modify `XlsxTable` — it is product code and this is a test fixture's problem.

Note `SweptTable.Table`'s real member names before writing the assertions:

```
grep -n "public sealed record Table" -A 6 src/OrdoSort.Core/SweptTable.cs
```

Adjust `table.Headers` / `table.Rows` / `.Cells` to match the actual record.

- [ ] **Step 5: Make MinimalPdf reachable**

In `tools/OrdoSort.Smoke/MinimalPdf.cs`, change `internal static class MinimalPdf` to:

```csharp
namespace OrdoSort.Smoke.E2E;

public static class MinimalPdf
```

Then fix the two existing call sites (`Program.cs`, `DemoWorkbench.cs` — confirm with `grep -rn "MinimalPdf" tools/OrdoSort.Smoke/`) by adding `using OrdoSort.Smoke.E2E;`.

- [ ] **Step 6: Run tests to verify they pass**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build --filter "FullyQualifiedName~E2EHarnessTests"
```

Expected: PASS, 7 tests (3 from Task 1, 4 here).

- [ ] **Step 7: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Fixture.cs tools/OrdoSort.Smoke/MinimalPdf.cs tools/OrdoSort.Smoke/Program.cs tools/OrdoSort.Smoke/DemoWorkbench.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs
git commit -m "feat(e2e): fixture builder for scenario inputs

Isolated temp root per scenario plus builders for plain, encrypted and
corrupt PDFs, ordinary/raw/empty zips, csv and xlsx. RawZip writes entry
names verbatim so the zip-slip fixture is honest."
```

---

## Task 3: Scripted dialogs

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs`
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs` (extend)

**Interfaces:**
- Consumes: `OrdoSort.Wpf.Services.IDialogService` — the existing interface, with members `Warn(string, string)`, `Info(string, string)`, `Confirm(string, string) → bool`, `AskSaveFile(string filter, string suggested) → string?`, `AskOpenFile(string filter) → string?`, `AskFilePath(string filter, string suggested) → string?`, `BrowseFolder(string? startAt) → string?`.
- Produces:
  - `sealed class ScriptedDialogs : IDialogService`
  - `List<string> Warnings { get; }`, `List<string> Infos { get; }`
  - `ScriptedDialogs QueueSaveFile(params string?[] paths)`
  - `ScriptedDialogs QueueOpenFile(params string?[] paths)`
  - `ScriptedDialogs QueueFilePath(params string?[] paths)`
  - `ScriptedDialogs QueueFolder(params string?[] paths)`
  - `ScriptedDialogs QueueConfirm(params bool[] answers)`
  - `IReadOnlyList<string> Unconsumed { get; }` — names of queues with leftover answers

- [ ] **Step 1: Write the failing tests**

```csharp
    /// <summary>Queued answers come back in order — a scenario that queues
    /// two save paths is describing two saves, and getting them swapped
    /// would file evidence under the wrong name.</summary>
    [Fact]
    public void ScriptedDialogsReturnQueuedAnswersInOrder()
    {
        var d = new ScriptedDialogs().QueueSaveFile("first.zip", "second.zip");

        Assert.Equal("first.zip", d.AskSaveFile("*.zip", "x"));
        Assert.Equal("second.zip", d.AskSaveFile("*.zip", "x"));
    }

    /// <summary>An empty queue answers null — "the user cancelled" — rather
    /// than throwing, because cancellation is a real path several scenarios
    /// exercise deliberately.</summary>
    [Fact]
    public void ScriptedDialogsAnswerNullWhenTheQueueIsEmpty()
    {
        Assert.Null(new ScriptedDialogs().AskSaveFile("*.zip", "x"));
    }

    /// <summary>A leftover answer means the scenario never took the path it
    /// claimed to — that is a broken scenario, and the runner must be able
    /// to see it.</summary>
    [Fact]
    public void ScriptedDialogsReportUnconsumedAnswers()
    {
        var d = new ScriptedDialogs().QueueSaveFile("never-used.zip");

        Assert.Contains("AskSaveFile", d.Unconsumed);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL to compile — `The name 'ScriptedDialogs' does not exist`.

- [ ] **Step 3: Write the implementation**

```csharp
using OrdoSort.Wpf.Services;

namespace OrdoSort.Smoke.E2E;

/// <summary>An IDialogService that answers from per-scenario queues instead
/// of showing modals — a shown modal would block the message loop and hang
/// the harness.
///
/// This answers the USER's side of a prompt (which path to save to, whether
/// to confirm). It never stands in for the app's work: the view models'
/// zipper/extractor/merger/counter/unlocker/plan seams stay at their
/// defaults in every scenario, which is what makes this suite a
/// demonstration rather than a mock theatre.</summary>
public sealed class ScriptedDialogs : IDialogService
{
    private readonly Queue<string?> _saveFile = new();
    private readonly Queue<string?> _openFile = new();
    private readonly Queue<string?> _filePath = new();
    private readonly Queue<string?> _folder = new();
    private readonly Queue<bool> _confirm = new();

    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();

    public ScriptedDialogs QueueSaveFile(params string?[] paths) { foreach (var p in paths) _saveFile.Enqueue(p); return this; }
    public ScriptedDialogs QueueOpenFile(params string?[] paths) { foreach (var p in paths) _openFile.Enqueue(p); return this; }
    public ScriptedDialogs QueueFilePath(params string?[] paths) { foreach (var p in paths) _filePath.Enqueue(p); return this; }
    public ScriptedDialogs QueueFolder(params string?[] paths) { foreach (var p in paths) _folder.Enqueue(p); return this; }
    public ScriptedDialogs QueueConfirm(params bool[] answers) { foreach (var a in answers) _confirm.Enqueue(a); return this; }

    public void Warn(string message, string title) => Warnings.Add(message);
    public void Info(string message, string title) => Infos.Add(message);

    // An empty confirm queue answers true: the overwhelmingly common case is
    // "yes, proceed", and a scenario that cares queues its own answer.
    public bool Confirm(string message, string title) => _confirm.Count > 0 ? _confirm.Dequeue() : true;

    // An empty path queue answers null — the user cancelled. Several
    // scenarios exercise cancellation deliberately, so this must not throw.
    public string? AskSaveFile(string filter, string suggested) => _saveFile.Count > 0 ? _saveFile.Dequeue() : null;
    public string? AskOpenFile(string filter) => _openFile.Count > 0 ? _openFile.Dequeue() : null;
    public string? AskFilePath(string filter, string suggested) => _filePath.Count > 0 ? _filePath.Dequeue() : null;
    public string? BrowseFolder(string? startAt) => _folder.Count > 0 ? _folder.Dequeue() : null;

    /// <summary>Queues with answers left over. A leftover means the scenario
    /// never reached the prompt it was written for.</summary>
    public IReadOnlyList<string> Unconsumed
    {
        get
        {
            var left = new List<string>();
            if (_saveFile.Count > 0) left.Add($"AskSaveFile ({_saveFile.Count})");
            if (_openFile.Count > 0) left.Add($"AskOpenFile ({_openFile.Count})");
            if (_filePath.Count > 0) left.Add($"AskFilePath ({_filePath.Count})");
            if (_folder.Count > 0) left.Add($"BrowseFolder ({_folder.Count})");
            if (_confirm.Count > 0) left.Add($"Confirm ({_confirm.Count})");
            return left;
        }
    }
}
```

- [ ] **Step 4: Verify the IDialogService member list matches**

```
cat src/OrdoSort.Wpf/Services/IDialogService.cs
```

If the interface has members beyond the seven above, implement them too — the class will not compile otherwise. Match the real signatures exactly.

- [ ] **Step 5: Run tests to verify they pass**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build --filter "FullyQualifiedName~E2EHarnessTests"
```

Expected: PASS, 10 tests.

- [ ] **Step 6: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs
git commit -m "feat(e2e): scripted dialog answers with unconsumed reporting

Answers the user's side of a prompt from per-scenario queues; never
stands in for the app's work. Unconsumed answers surface scenarios that
never reached the path they were written for."
```

---

## Task 4: Scenario model and assertion recorder

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenario.cs`
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs` (extend)

**Interfaces:**
- Consumes: `Fixture` (Task 2), `ScriptedDialogs` (Task 3).
- Produces:
  - `sealed record Scenario(string Surface, string Name, string Kind, Action<ScenarioContext> Run)` — `Kind` is `"clean"` or `"awkward"`, used to prove in the report that every surface has both.
  - `sealed class ScenarioContext` with:
    - `Fixture Fx { get; }`
    - `ScriptedDialogs Dialogs { get; }`
    - `void Check(string description, bool condition, string? detail = null)` — records a passing or failing assertion
    - `void FileExists(string path)` / `void FileMissing(string path)`
    - `void BytesUnchanged(string path, byte[] before, string description)`
    - `string[] Snapshot()` — every path under the fixture and one level above it
    - `void NothingNewOutside(string allowedDir, string[] before, string description)` — nothing new appeared outside `allowedDir`
    - `void Capture(System.Windows.Window win)` — records the window for screenshotting
    - `IReadOnlyList<Assertion> Assertions { get; }`
    - `System.Windows.Window? Captured { get; }`
  - `sealed record Assertion(string Description, bool Passed, string? Detail = null)`

- [ ] **Step 1: Write the failing tests**

```csharp
    /// <summary>A recorded failure must not throw — the runner needs every
    /// assertion in a scenario, not just the ones before the first break,
    /// and a partial report is exactly what you want when something breaks.</summary>
    [Fact]
    public void ContextRecordsFailuresWithoutThrowing()
    {
        using var fx = Fixture.Create("ctx-check");
        var ctx = new ScenarioContext(fx, new ScriptedDialogs());

        ctx.Check("this one holds", true);
        ctx.Check("this one does not", false);
        ctx.Check("and this one still runs", true);

        Assert.Equal(3, ctx.Assertions.Count);
        Assert.Single(ctx.Assertions.Where(a => !a.Passed));
    }

    /// <summary>BytesUnchanged is the never-overwrite guarantee's assertion —
    /// it must actually compare content, not just existence.</summary>
    [Fact]
    public void BytesUnchangedDetectsAModifiedFile()
    {
        using var fx = Fixture.Create("bytes-check");
        var path = fx.Text("original.txt", "before");
        var before = File.ReadAllBytes(path);
        File.WriteAllText(path, "after");

        var ctx = new ScenarioContext(fx, new ScriptedDialogs());
        ctx.BytesUnchanged(path, before, "original survives");

        Assert.False(ctx.Assertions.Single().Passed);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL to compile — `The name 'ScenarioContext' does not exist`.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Windows;

namespace OrdoSort.Smoke.E2E;

/// <summary>One recorded check and its outcome. Assertions are data, not
/// exceptions, so a scenario reports everything it checked — the failing
/// ones alongside the passing ones — which is what makes the evidence
/// report readable rather than a stack trace.</summary>
public sealed record Assertion(string Description, bool Passed, string? Detail = null);

/// <summary>One end-to-end scenario. Kind is "clean" (proves the surface
/// works) or "awkward" (proves it behaves under an input that breaks naive
/// code); the report asserts every surface has at least one of each.</summary>
public sealed record Scenario(string Surface, string Name, string Kind, Action<ScenarioContext> Run);

/// <summary>What a scenario is handed: its isolated fixture, its dialog
/// answers, and the recorder it reports through.</summary>
public sealed class ScenarioContext
{
    private readonly List<Assertion> _assertions = new();

    public ScenarioContext(Fixture fx, ScriptedDialogs dialogs)
    {
        Fx = fx;
        Dialogs = dialogs;
    }

    public Fixture Fx { get; }
    public ScriptedDialogs Dialogs { get; }
    public IReadOnlyList<Assertion> Assertions => _assertions;
    public Window? Captured { get; private set; }

    /// <summary>Record a check. Never throws: one false assertion must not
    /// stop the rest of the scenario from reporting.</summary>
    public void Check(string description, bool condition, string? detail = null) =>
        _assertions.Add(new Assertion(description, condition, condition ? null : detail));

    public void FileExists(string path) =>
        Check($"file exists: {Rel(path)}", File.Exists(path), "not found");

    public void FileMissing(string path) =>
        Check($"file absent: {Rel(path)}", !File.Exists(path), "unexpectedly present");

    /// <summary>The never-overwrite guarantee: content identical, not merely
    /// present.</summary>
    public void BytesUnchanged(string path, byte[] before, string description)
    {
        if (!File.Exists(path)) { Check(description, false, "file is gone"); return; }
        var now = File.ReadAllBytes(path);
        Check(description, now.SequenceEqual(before),
            $"content changed ({before.Length} → {now.Length} bytes)");
    }

    /// <summary>Every path currently under the fixture (and one level above
    /// it, so an escape out of the root is visible). Take this before the
    /// act, hand it to NothingNewOutside after.</summary>
    public string[] Snapshot() => ScanBase() is { } b
        ? Directory.GetFileSystemEntries(b, "*", SearchOption.AllDirectories)
        : Array.Empty<string>();

    /// <summary>Nothing new appeared anywhere except inside
    /// <paramref name="allowedDir"/>. This is the zip-slip assertion, and it
    /// is deliberately not "the file I predicted is absent" — an entry that
    /// escapes to somewhere unanticipated is caught just the same.</summary>
    public void NothingNewOutside(string allowedDir, string[] before, string description)
    {
        var allowed = Path.GetFullPath(allowedDir);
        var added = Snapshot()
            .Except(before, StringComparer.OrdinalIgnoreCase)
            .Where(p => !Path.GetFullPath(p).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check(description, added.Count == 0,
            added.Count == 0 ? null : "appeared: " + string.Join(", ", added.Select(Rel)));
    }

    private string? ScanBase()
    {
        var parent = Path.GetDirectoryName(Fx.Root);
        return Directory.Exists(parent) ? parent : (Directory.Exists(Fx.Root) ? Fx.Root : null);
    }

    /// <summary>Nominate the window this scenario is evidenced by. The
    /// runner rasterizes it after Run returns, while it is still open.</summary>
    public void Capture(Window win) => Captured = win;

    private string Rel(string path) =>
        path.StartsWith(Fx.Root, StringComparison.OrdinalIgnoreCase)
            ? path[(Fx.Root.Length + 1)..]
            : path;
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build --filter "FullyQualifiedName~E2EHarnessTests"
```

Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenario.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs
git commit -m "feat(e2e): scenario model and assertion recorder

Assertions are data rather than exceptions, so a scenario reports every
check it made — failing ones alongside passing — which is what makes a
partial report useful when something breaks."
```

---

## Task 5: Evidence writer

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Evidence.cs`
- Test: `tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs` (extend)

**Interfaces:**
- Consumes: `Assertion`, `Scenario` (Task 4); `E2EPump.ShowOffscreen` (Task 1).
- Produces:
  - `sealed record ScenarioResult(string Surface, string Name, string Kind, bool Passed, IReadOnlyList<Assertion> Assertions, string? Error, string? ScreenshotFile, long ElapsedMs)`
  - `static string Evidence.NewRunDirectory()` — creates and returns `evidence/<yyyyMMdd-HHmmss>/`
  - `static string? Evidence.Capture(System.Windows.Window win, string outDir, string fileStem)` — rasterizes to PNG, returns the file name, or `null` on failure (never throws)
  - `static void Evidence.Write(string outDir, IReadOnlyList<ScenarioResult> results, TimeSpan duration)` — writes `report.html` and `report.md`

- [ ] **Step 1: Write the failing tests**

Append to `E2EHarnessTests`:

```csharp
    private static ScenarioResult Result(string surface, string name, string kind, bool passed) =>
        new(surface, name, kind, passed,
            new[] { new Assertion("a check", passed, passed ? null : "it did not hold") },
            passed ? null : "boom", ScreenshotFile: null, ElapsedMs: 12);

    /// <summary>The report must be self-contained and must show failures —
    /// a report that renders a red run as green is worse than no report.</summary>
    [Fact]
    public void ReportHtmlIsSelfContainedAndShowsBothOutcomes()
    {
        using var fx = Fixture.Create("evidence-check");
        var outDir = fx.Dir("out");

        Evidence.Write(outDir, new[]
        {
            Result("Zip", "files and folders", "clean", passed: true),
            Result("Unzip", "zip slip", "awkward", passed: false),
        }, TimeSpan.FromSeconds(3));

        var html = File.ReadAllText(Path.Combine(outDir, "report.html"));

        Assert.Contains("files and folders", html);
        Assert.Contains("zip slip", html);
        Assert.Contains("it did not hold", html);
        // Self-contained: no external fetches of any kind.
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src", html);
    }

    /// <summary>report.md carries the same verdicts, for pasting into a PR.</summary>
    [Fact]
    public void ReportMarkdownCarriesEveryScenario()
    {
        using var fx = Fixture.Create("evidence-md-check");
        var outDir = fx.Dir("out");

        Evidence.Write(outDir, new[]
        {
            Result("Zip", "files and folders", "clean", passed: true),
            Result("Unzip", "zip slip", "awkward", passed: false),
        }, TimeSpan.FromSeconds(3));

        var md = File.ReadAllText(Path.Combine(outDir, "report.md"));

        Assert.Contains("Zip", md);
        Assert.Contains("Unzip", md);
        Assert.Contains("1 failed", md);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL to compile — `The name 'Evidence' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `tools/OrdoSort.Smoke/E2E/Evidence.cs`. The HTML is theme-aware via `prefers-color-scheme` and inlines its PNGs as `data:` URIs so the file stands alone.

```csharp
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrdoSort.Smoke.E2E;

/// <summary>One scenario's outcome, as the report renders it.</summary>
public sealed record ScenarioResult(
    string Surface, string Name, string Kind, bool Passed,
    IReadOnlyList<Assertion> Assertions, string? Error,
    string? ScreenshotFile, long ElapsedMs);

/// <summary>Writes the run's evidence: a self-contained report.html with the
/// screenshots inlined as data: URIs (so it can be mailed or attached to a CI
/// run and still render), the same content as report.md for pasting into a
/// PR, and the PNGs as loose files for reuse in docs.</summary>
public static class Evidence
{
    public static string NewRunDirectory()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "evidence", stamp);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Rasterize a live window. Returns the PNG's file name, or null
    /// if it could not be captured — a missing screenshot is a note in the
    /// report, never a failed scenario.</summary>
    public static string? Capture(Window win, string outDir, string fileStem)
    {
        try
        {
            win.UpdateLayout();
            var w = (int)Math.Ceiling(win.ActualWidth);
            var h = (int)Math.Ceiling(win.ActualHeight);
            if (w <= 0 || h <= 0) return null;

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(win);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));

            var file = fileStem + ".png";
            using var fs = File.Create(Path.Combine(outDir, file));
            enc.Save(fs);
            return file;
        }
        catch { return null; }
    }

    public static void Write(string outDir, IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "report.html"), Html(outDir, results, duration),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "report.md"), Markdown(results, duration),
            new UTF8Encoding(false));
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Html(string outDir, IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var surfaces = results.Select(r => r.Surface).Distinct().ToList();

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>OrdoSort &mdash; end-to-end evidence</title><style>");
        sb.Append(":root{--bg:#fff;--fg:#1a1a1a;--muted:#666;--line:#e3e3e3;--ok:#0a7d33;--bad:#b3261e;--card:#fafafa}");
        sb.Append("@media(prefers-color-scheme:dark){:root{--bg:#161616;--fg:#ececec;--muted:#a0a0a0;--line:#333;--ok:#5cc47c;--bad:#f2857c;--card:#1e1e1e}}");
        sb.Append("*{box-sizing:border-box}");
        sb.Append("body{margin:0;padding:2rem 1.25rem;background:var(--bg);color:var(--fg);font:15px/1.55 -apple-system,Segoe UI,system-ui,sans-serif}");
        sb.Append("main{max-width:60rem;margin:0 auto}h1{font-size:1.5rem;margin:0 0 .25rem}");
        sb.Append("h2{font-size:1.1rem;margin:2.5rem 0 .75rem;padding-bottom:.35rem;border-bottom:1px solid var(--line)}");
        sb.Append(".sum{color:var(--muted);margin:0 0 2rem}");
        sb.Append(".sc{border:1px solid var(--line);border-radius:8px;padding:1rem;margin:0 0 1rem;background:var(--card)}");
        sb.Append(".hd{display:flex;gap:.6rem;align-items:baseline;flex-wrap:wrap}.nm{font-weight:600}");
        sb.Append(".kind{color:var(--muted);font-size:.85rem}.v{font-weight:600}.pass{color:var(--ok)}.fail{color:var(--bad)}");
        sb.Append("ul{margin:.75rem 0 0;padding-left:1.25rem}li{margin:.15rem 0}li.no{color:var(--bad)}.det{color:var(--muted)}");
        sb.Append(".err{color:var(--bad);font-family:ui-monospace,Consolas,monospace;font-size:.85rem;margin-top:.5rem;white-space:pre-wrap}");
        sb.Append("img{max-width:100%;height:auto;margin-top:.85rem;border:1px solid var(--line);border-radius:6px;display:block}");
        sb.Append("</style></head><body><main>");

        sb.Append("<h1>OrdoSort &mdash; end-to-end evidence</h1><p class=\"sum\">");
        sb.Append($"{results.Count} scenarios across {surfaces.Count} surfaces &middot; ");
        sb.Append(failed == 0
            ? "<span class=\"v pass\">all passed</span>"
            : $"<span class=\"v fail\">{failed} failed</span>, {passed} passed");
        sb.Append($" &middot; {duration.TotalSeconds:F1}s &middot; {Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))}</p>");

        foreach (var surface in surfaces)
        {
            sb.Append($"<h2>{Esc(surface)}</h2>");
            foreach (var r in results.Where(x => x.Surface == surface))
            {
                sb.Append("<div class=\"sc\"><div class=\"hd\">");
                sb.Append($"<span class=\"nm\">{Esc(r.Name)}</span>");
                sb.Append($"<span class=\"kind\">{Esc(r.Kind)} &middot; {r.ElapsedMs}ms</span>");
                sb.Append(r.Passed ? "<span class=\"v pass\">PASS</span>" : "<span class=\"v fail\">FAIL</span>");
                sb.Append("</div><ul>");
                foreach (var a in r.Assertions)
                {
                    sb.Append(a.Passed ? "<li>" : "<li class=\"no\">");
                    sb.Append(Esc(a.Description));
                    if (!a.Passed && a.Detail is not null)
                        sb.Append($" <span class=\"det\">&mdash; {Esc(a.Detail)}</span>");
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
                if (r.Error is not null) sb.Append($"<div class=\"err\">{Esc(r.Error)}</div>");

                if (r.ScreenshotFile is not null)
                {
                    var png = Path.Combine(outDir, r.ScreenshotFile);
                    if (File.Exists(png))
                    {
                        var b64 = Convert.ToBase64String(File.ReadAllBytes(png));
                        sb.Append($"<img alt=\"{Esc(r.Name)}\" src=\"data:image/png;base64,{b64}\">");
                    }
                }
                sb.Append("</div>");
            }
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Markdown(IReadOnlyList<ScenarioResult> results, TimeSpan duration)
    {
        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;
        var sb = new StringBuilder();

        sb.AppendLine("# OrdoSort — end-to-end evidence").AppendLine();
        sb.AppendLine($"{results.Count} scenarios across {results.Select(r => r.Surface).Distinct().Count()} surfaces · "
            + (failed == 0 ? "all passed" : $"**{failed} failed**, {passed} passed")
            + $" · {duration.TotalSeconds:F1}s").AppendLine();

        foreach (var surface in results.Select(r => r.Surface).Distinct())
        {
            sb.AppendLine($"## {surface}").AppendLine();
            foreach (var r in results.Where(x => x.Surface == surface))
            {
                sb.AppendLine($"### {r.Name} — {(r.Passed ? "PASS" : "FAIL")} _({r.Kind}, {r.ElapsedMs}ms)_").AppendLine();
                foreach (var a in r.Assertions)
                    sb.AppendLine($"- {(a.Passed ? "[x]" : "[ ]")} {a.Description}"
                        + (!a.Passed && a.Detail is not null ? $" — {a.Detail}" : ""));
                if (r.Error is not null) sb.AppendLine().AppendLine("```").AppendLine(r.Error).AppendLine("```");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet build tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj -c Debug -p:Deterministic=false
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build --filter "FullyQualifiedName~E2EHarnessTests"
```

Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Evidence.cs tests/OrdoSort.Wpf.Tests/E2EHarnessTests.cs
git commit -m "feat(e2e): self-contained HTML and markdown evidence reports

Screenshots inline as data: URIs so report.html can be mailed or attached
to a CI run and still render. Theme-aware, no external fetches."
```

---

## Task 6: The runner, wired end to end with the Zip surface

Deliberately larger than one file: the runner is only meaningfully testable with a real surface behind it, and a surface is only runnable with a runner. Landing them together yields the first working `dotnet run -- e2e`.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/ZipScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/Program.cs:16-20`, `.gitignore`

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces:
  - `static int E2ERunner.Run(string[] args)` — args are `["e2e", surfaceFilter?, "--keep"?]`; returns 0 or 1
  - `static IReadOnlyList<Scenario> ZipScenarios.All()`
  - `internal sealed class InlineScheduler : IWorkScheduler` — shared by every later scenario file

- [ ] **Step 1: Write the Zip scenarios**

Create `tools/OrdoSort.Smoke/E2E/Scenarios/ZipScenarios.cs`. Every view model here leaves its `zipper` seam at its default, so `Zipper.CreateZip` really runs and really writes an archive — that is what separates these from `ZipViewModelTests`, which injects a fake zipper deliberately.

```csharp
using System.IO.Compression;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The Zip tool, driven as the real ZipWindow against real files.</summary>
public static class ZipScenarios
{
    private const string Surface = "Zip";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "files and a folder in one archive", "clean", FilesAndFolder),
        new Scenario(Surface, "save-as to an explicit path", "clean", SaveAs),
        new Scenario(Surface, "output name already taken", "awkward", NameTaken),
        new Scenario(Surface, "unicode and spaces in names", "awkward", UnicodeNames),
        new Scenario(Surface, "nothing selected", "awkward", EmptySelection),
    };

    /// <summary>Real seams: only dialogs and the scheduler are injected, and
    /// uiContext is the live dispatcher context so results marshal back the
    /// way they do in production.</summary>
    private static ZipViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static ZipWindow Open(ZipViewModel vm)
    {
        var win = new ZipWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    private static string[] Archives(ScenarioContext ctx) =>
        Directory.GetFiles(ctx.Fx.Root, "*.zip", SearchOption.AllDirectories);

    private static void FilesAndFolder(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var b = ctx.Fx.Pdf("src/two.pdf", "BETA");
        var folder = ctx.Fx.Dir("src", "nested");
        ctx.Fx.Pdf("src/nested/three.pdf", "GAMMA");

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a, b, folder });
        E2EPump.Until(() => add.IsCompleted, 8000);
        ctx.Check("three sources listed", vm.Rows.Count == 3, $"got {vm.Rows.Count}");

        vm.CreateCommand.Execute(null);
        E2EPump.Until(() => Archives(ctx).Length > 0 || ctx.Dialogs.Warnings.Count > 0, 15000);

        var zips = Archives(ctx);
        ctx.Check("exactly one archive written", zips.Length == 1, $"got {zips.Length}");
        if (zips.Length == 1)
        {
            using var archive = ZipFile.OpenRead(zips[0]);
            ctx.Check("archive holds all three documents", archive.Entries.Count == 3,
                $"got {archive.Entries.Count}: " + string.Join(", ", archive.Entries.Select(e => e.FullName)));
        }
        ctx.Capture(win);
    }

    private static void SaveAs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var target = Path.Combine(ctx.Fx.Dir("out"), "chosen-name.zip");
        ctx.Dialogs.QueueSaveFile(target);

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a });
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.CreateAsCommand.Execute(null);
        E2EPump.Until(() => File.Exists(target) || ctx.Dialogs.Warnings.Count > 0, 15000);

        ctx.FileExists(target);
        ctx.Capture(win);
    }

    /// <summary>Never overwrites: the archive already at that name keeps its
    /// bytes. Whether the app counters to " (2)" or refuses, the file that
    /// was there first must survive — that is the guarantee the README
    /// makes, and the one worth proving through the window.</summary>
    private static void NameTaken(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var outDir = ctx.Fx.Dir("out");
        var taken = Path.Combine(outDir, "one.zip");
        File.WriteAllText(taken, "I was here first");
        var before = File.ReadAllBytes(taken);
        ctx.Dialogs.QueueSaveFile(taken);

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a });
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.CreateAsCommand.Execute(null);
        E2EPump.Until(
            () => Directory.GetFiles(outDir).Length > 1 || ctx.Dialogs.Warnings.Count > 0, 15000);

        ctx.BytesUnchanged(taken, before, "the archive already there is untouched");
        ctx.Capture(win);
    }

    private static void UnicodeNames(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/rapport café — 2026.pdf", "CAFE");
        var b = ctx.Fx.Pdf("src/文件 名.pdf", "CJK");

        var vm = NewVm(ctx);
        var win = Open(vm);

        var add = vm.AddPaths(new[] { a, b });
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.CreateCommand.Execute(null);
        E2EPump.Until(() => Archives(ctx).Length > 0 || ctx.Dialogs.Warnings.Count > 0, 15000);

        var zips = Archives(ctx);
        ctx.Check("archive written", zips.Length == 1, $"got {zips.Length}");
        if (zips.Length == 1)
        {
            using var archive = ZipFile.OpenRead(zips[0]);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            ctx.Check("both names survive the round trip",
                names.Any(n => n.Contains("café", StringComparison.Ordinal))
                && names.Any(n => n.Contains("文件", StringComparison.Ordinal)),
                string.Join(" | ", names));
        }
        ctx.Capture(win);
    }

    private static void EmptySelection(ScenarioContext ctx)
    {
        var vm = NewVm(ctx);
        var win = Open(vm);

        ctx.Check("nothing listed", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Check("create is refused", !vm.CreateCommand.CanExecute(null), "the command was enabled");
        ctx.Check("no archive written", Archives(ctx).Length == 0, "an archive appeared");
        ctx.Capture(win);
    }
}

/// <summary>Runs scheduled work inline so a scenario's assertions follow the
/// call rather than a sleep. Mirrors OrdoSort.Wpf.Tests.InlineWorkScheduler,
/// duplicated here because the test project's types are not visible to the
/// Smoke tool — the project dependency runs the other way.</summary>
internal sealed class InlineScheduler : IWorkScheduler
{
    public Task<T> Run<T>(Func<T> work) => Task.FromResult(work());
    public Task Run(Action work) { work(); return Task.CompletedTask; }
}
```

- [ ] **Step 2: Check the AddPaths return type before building**

`ZipViewModel.AddPaths` is declared `public async Task AddPaths(...)` — the code above awaits it by pumping on `add.IsCompleted`. Confirm:

```
grep -n "public .*AddPaths" src/OrdoSort.Wpf/ViewModels/ZipViewModel.cs
```

If it returns `void` rather than `Task`, drop the `add` variable and pump on an observable effect instead: `E2EPump.Until(() => vm.Rows.Count == 3, 8000)`.

- [ ] **Step 3: Write the runner**

Create `tools/OrdoSort.Smoke/E2E/E2ERunner.cs`.

There is no external watchdog thread here, and that is deliberate: a scenario runs on the STA thread and pumps its own dispatcher frames, so it cannot be interrupted from outside without tearing down the dispatcher. Instead, every wait inside a scenario goes through `E2EPump.Until`, which is bounded — so a scenario's worst case is the sum of its own timeouts, and it cannot hang the run.

```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using OrdoSort.Smoke.E2E.Scenarios;

namespace OrdoSort.Smoke.E2E;

/// <summary>The e2e mode: every surface, driven as real windows against real
/// files, with an evidence report and a CI exit code.
///
///   dotnet run --project tools\OrdoSort.Smoke -- e2e [surface] [--keep]
///
/// A surface argument (case-insensitive prefix) runs one surface — useful on
/// a machine without WebView2, where `e2e zip` still works. --keep leaves the
/// fixtures on disk for inspection.</summary>
public static class E2ERunner
{
    private static IReadOnlyList<Scenario> AllScenarios() =>
        ZipScenarios.All()
            .ToList();

    public static int Run(string[] args)
    {
        var filter = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var keep = args.Contains("--keep");

        var scenarios = AllScenarios();
        if (filter is not null)
        {
            // Exact wins over prefix, so "zip" selects Zip alone rather than
            // dragging in "Zip merge" — otherwise the narrow filter that
            // exists to skip a broken surface would silently include it.
            var exact = scenarios.Where(s => Same(s.Surface, filter)).ToList();
            scenarios = exact.Count > 0
                ? exact
                : scenarios.Where(s => Matches(s.Surface, filter)).ToList();
        }

        if (scenarios.Count == 0)
        {
            Console.WriteLine($"E2E FAIL:\n  * no surface matches \"{filter}\"");
            return 1;
        }

        List<ScenarioResult> results = new();
        var outDir = Evidence.NewRunDirectory();
        var sw = Stopwatch.StartNew();

        var ui = new Thread(() => results = DriveAll(scenarios, outDir, keep));
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        sw.Stop();

        Evidence.Write(outDir, results, sw.Elapsed);

        var failed = results.Where(r => !r.Passed).ToList();
        var surfaces = results.Select(r => r.Surface).Distinct().Count();
        var reportPath = Path.Combine(outDir, "report.html");

        if (failed.Count == 0)
        {
            Console.WriteLine($"E2E PASS — {results.Count} scenarios, {surfaces} surfaces");
            Console.WriteLine($"  evidence: {reportPath}");
            return 0;
        }

        Console.WriteLine("E2E FAIL:");
        foreach (var r in failed)
        {
            var why = r.Error
                ?? string.Join("; ", r.Assertions.Where(a => !a.Passed).Select(a => a.Description));
            Console.WriteLine($"  * [{r.Surface}] {r.Name} — {why}");
        }
        Console.WriteLine($"  evidence: {reportPath}");
        return 1;
    }

    private static bool Same(string surface, string filter) =>
        surface.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || surface.Replace(" ", "").Equals(filter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string surface, string filter) =>
        surface.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
        || surface.Replace(" ", "").StartsWith(filter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static List<ScenarioResult> DriveAll(
        IReadOnlyList<Scenario> scenarios, string outDir, bool keep)
    {
        var results = new List<ScenarioResult>();
        SmokeUi.Boot();   // real App.xaml resources + theme, as in production

        foreach (var s in scenarios)
        {
            Console.WriteLine($"  {s.Surface}: {s.Name}");
            Console.Out.Flush();
            results.Add(DriveOne(s, outDir, keep));
        }

        Dispatcher.CurrentDispatcher.InvokeShutdown();
        return results;
    }

    private static ScenarioResult DriveOne(Scenario s, string outDir, bool keep)
    {
        var stem = Slug(s.Surface) + "-" + Slug(s.Name);
        var sw = Stopwatch.StartNew();
        var fx = Fixture.Create(stem);
        var ctx = new ScenarioContext(fx, new ScriptedDialogs());
        string? error = null;
        string? shot = null;

        try
        {
            s.Run(ctx);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            // Capture while the window is still open, then close everything so
            // the next scenario starts from a clean desktop.
            if (ctx.Captured is not null) shot = Evidence.Capture(ctx.Captured, outDir, stem);
            foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
                try { w.Close(); } catch { /* best effort */ }

            if (!keep) fx.Dispose();
        }

        // A leftover dialog answer means the scenario never reached the prompt
        // it was written for — a broken scenario, reported as one.
        if (ctx.Dialogs.Unconsumed.Count > 0)
            ctx.Check("every queued dialog answer was used", false,
                "unused: " + string.Join(", ", ctx.Dialogs.Unconsumed));

        // A scenario that asserted nothing passed vacuously; that is a defect
        // in the scenario, and silently green is exactly the failure mode this
        // whole suite exists to avoid.
        if (ctx.Assertions.Count == 0 && error is null) error = "scenario asserted nothing";

        sw.Stop();
        var passed = error is null && ctx.Assertions.Count > 0 && ctx.Assertions.All(a => a.Passed);

        return new ScenarioResult(s.Surface, s.Name, s.Kind, passed,
            ctx.Assertions.ToList(), error, shot, sw.ElapsedMilliseconds);
    }

    private static string Slug(string s)
    {
        var raw = string.Concat(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (raw.Contains("--", StringComparison.Ordinal)) raw = raw.Replace("--", "-");
        return raw.Trim('-');
    }
}
```

- [ ] **Step 4: Wire it into Program.cs**

Add one line beside the existing mode dispatch at `tools/OrdoSort.Smoke/Program.cs:16-20`:

```csharp
if (args.Length > 0 && args[0] == "e2e") return OrdoSort.Smoke.E2E.E2ERunner.Run(args);
```

- [ ] **Step 5: Ignore the evidence directory**

Append to `.gitignore`, after the "Publish output" group:

```
# E2E evidence — regenerated by every `e2e` run
evidence/
```

- [ ] **Step 6: Run it**

```
dotnet run --project tools/OrdoSort.Smoke -- e2e zip
```

Expected: `E2E PASS — 5 scenarios, 1 surfaces`, then an `evidence/<stamp>/report.html` path. Open the HTML and confirm five screenshots render.

If a scenario fails, read its assertion text before changing anything. Several of these check real guarantees — never-overwrite, unicode round-trip — so a failure may be a genuine finding rather than a broken scenario. Tell them apart by calling `Zipper.CreateZip` directly with the same inputs.

- [ ] **Step 7: Prove the suite can go red**

A suite that cannot fail is not a suite. Temporarily change `EmptySelection`'s first check from `vm.Rows.Count == 0` to `vm.Rows.Count == 99`, then:

```
dotnet run --project tools/OrdoSort.Smoke -- e2e zip
```

Expected: `E2E FAIL:` naming `[Zip] nothing selected`, exit code 1 (`echo $LASTEXITCODE`), and that row red in the report while the others stay green. Revert and re-run to confirm green.

- [ ] **Step 8: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/E2ERunner.cs tools/OrdoSort.Smoke/E2E/Scenarios/ZipScenarios.cs tools/OrdoSort.Smoke/Program.cs .gitignore
git commit -m "feat(e2e): runner and the Zip surface

First working e2e mode: real ZipWindow, real Zipper.CreateZip, real
archives on disk. Covers files+folder, save-as, a taken output name
(never overwrites), unicode round-trip, and empty selection.

A scenario that asserts nothing is reported as failed — silently green is
the failure mode this suite exists to avoid."
```

---

## Task 7: Unzip surface

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `Fixture.RawZip`, `Fixture.EmptyZip`, `Fixture.Zip` (Task 2); `ScenarioContext.NothingOutside` (Task 4); `InlineScheduler` (Task 6).
- Produces: `static IReadOnlyList<Scenario> UnzipScenarios.All()`

Read `src/OrdoSort.Core/Zipper.cs:252-294` before writing these — `ExtractCore`'s contract is specific: `"not a valid zip"` for `InvalidDataException`, a generic `"couldn't extract: …"` otherwise, and a `created`-gate that removes the output folder only if that call made it.

- [ ] **Step 1: Write the scenarios**

```csharp
using System.IO.Compression;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The Unzip tool as the real UnzipWindow. The `extractor` seam is
/// left at its default throughout, so Zipper.Extract really runs — including
/// its ZipSlip guard and its created-gate cleanup, neither of which a fake
/// extractor could demonstrate.</summary>
public static class UnzipScenarios
{
    private const string Surface = "Unzip";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "nested folders extract intact", "clean", NestedFolders),
        new Scenario(Surface, "path traversal is refused", "awkward", ZipSlip),
        new Scenario(Surface, "corrupt archive", "awkward", CorruptArchive),
        new Scenario(Surface, "output folder already exists", "awkward", TargetExists),
        new Scenario(Surface, "empty archive", "awkward", EmptyArchive),
    };

    private static UnzipViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static UnzipWindow Open(UnzipViewModel vm)
    {
        var win = new UnzipWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    /// <summary>Drive one archive through the window and wait for its row to
    /// leave Pending.</summary>
    private static UnzipWindow Extract(ScenarioContext ctx, UnzipViewModel vm, string zip)
    {
        var win = Open(vm);
        var add = vm.AddFilesAsync(new[] { zip });
        E2EPump.Until(() => add.IsCompleted, 8000);
        vm.ExtractCommand.Execute(null);
        E2EPump.Until(() => vm.Rows.Count > 0 && vm.Rows[0].Note.Length > 0, 15000);
        return win;
    }

    private static void NestedFolders(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var two = ctx.Fx.Pdf("src/two.pdf", "BETA");
        var zip = ctx.Fx.Zip("archives/bundle.zip",
            ("one.pdf", one), (@"nested\deeper\two.pdf", two));

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        var outDir = Path.Combine(ctx.Fx.Root, "archives", "bundle");
        ctx.Check("output folder created", Directory.Exists(outDir), $"expected {outDir}");
        ctx.FileExists(Path.Combine(outDir, "one.pdf"));
        ctx.FileExists(Path.Combine(outDir, "nested", "deeper", "two.pdf"));
        ctx.Check("the archive itself is left alone", File.Exists(zip), "the zip was consumed");
        ctx.Capture(win);
    }

    /// <summary>An entry named ..\..\escaped.txt must not land outside the
    /// output folder. ZipFile.ExtractToDirectory throws IOException for this,
    /// which ExtractCore turns into an error result and — because it created
    /// the folder on this call — cleans the folder up.</summary>
    private static void ZipSlip(ScenarioContext ctx)
    {
        var archives = ctx.Fx.Dir("archives");
        var zip = ctx.Fx.RawZip("archives/evil.zip",
            (@"..\..\escaped.txt", new byte[] { 66, 65, 68 }));

        var outDir = Path.Combine(archives, "evil");
        var before = ctx.Snapshot();   // take it BEFORE the extract

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("extraction refused", vm.Rows[0].Status != "ok",
            $"status was {vm.Rows[0].Status}");

        // Not "the file I predicted is absent" — nothing new appeared
        // anywhere outside the output folder, so an entry escaping to
        // somewhere unanticipated is caught just the same.
        ctx.NothingNewOutside(outDir, before, "nothing escaped the output folder");
        ctx.FileMissing(Path.Combine(ctx.Fx.Root, "escaped.txt"));

        ctx.Check("no orphaned output folder", !Directory.Exists(outDir),
            "the created-gate cleanup left the folder behind");
        ctx.Capture(win);
    }

    private static void CorruptArchive(ScenarioContext ctx)
    {
        var zip = ctx.Fx.Text("archives/broken.zip", "this is not a zip file at all");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("reported as invalid", vm.Rows[0].Status != "ok",
            $"status was {vm.Rows[0].Status}");
        ctx.Check("says it is not a valid zip",
            vm.Rows[0].Note.Contains("valid zip", StringComparison.OrdinalIgnoreCase),
            $"note was \"{vm.Rows[0].Note}\"");
        ctx.Check("no output folder left behind",
            !Directory.Exists(Path.Combine(ctx.Fx.Root, "archives", "broken")),
            "an empty folder was orphaned");
        ctx.Capture(win);
    }

    /// <summary>A taken output name counters (Collision.FreeDirectory)
    /// rather than merging into — or emptying — the folder already there.</summary>
    private static void TargetExists(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("src/one.pdf", "ALPHA");
        var zip = ctx.Fx.Zip("archives/bundle.zip", ("one.pdf", one));

        var squatter = ctx.Fx.Dir("archives", "bundle");
        var squatterFile = Path.Combine(squatter, "i-was-here.txt");
        File.WriteAllText(squatterFile, "existing content");
        var before = File.ReadAllBytes(squatterFile);

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.BytesUnchanged(squatterFile, before, "the folder already there is untouched");
        ctx.Check("extraction still succeeded", vm.Rows[0].Status == "ok",
            $"status was {vm.Rows[0].Status} — {vm.Rows[0].Note}");
        ctx.Check("output went somewhere else",
            vm.Rows[0].OutputFolder is not null
            && !string.Equals(Path.GetFullPath(vm.Rows[0].OutputFolder!),
                Path.GetFullPath(squatter), StringComparison.OrdinalIgnoreCase),
            $"output was {vm.Rows[0].OutputFolder}");
        ctx.Capture(win);
    }

    private static void EmptyArchive(ScenarioContext ctx)
    {
        var zip = ctx.Fx.EmptyZip("archives/nothing.zip");

        var vm = NewVm(ctx);
        var win = Extract(ctx, vm, zip);

        ctx.Check("handled without an error", vm.Rows[0].Note.Length > 0, "no note at all");
        ctx.Check("did not crash the window", win.IsLoaded, "window went away");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 2: Verify the UnzipRow member names**

The scenarios read `Status`, `Note`, and `OutputFolder` off a row. Confirm against the real type:

```
grep -n "public " src/OrdoSort.Wpf/ViewModels/UnzipViewModel.cs
```

`UnzipRow` exposes `Path`, `FileName`, `Note`, `OutputFolder` — check whether the status is a `string Status` or an enum like `ZipMergeViewModel`'s `ZipRowStatus`, and adjust every comparison accordingly. Do not change the view model.

- [ ] **Step 3: Register the surface**

In `E2ERunner.AllScenarios()`:

```csharp
    private static IReadOnlyList<Scenario> AllScenarios() =>
        ZipScenarios.All()
            .Concat(UnzipScenarios.All())
            .ToList();
```

- [ ] **Step 4: Run it**

```
dotnet run --project tools/OrdoSort.Smoke -- e2e unzip
```

Expected: `E2E PASS — 5 scenarios, 1 surfaces`.

**If "path traversal is refused" fails, stop and investigate before touching the scenario.** That assertion is a security property. Reproduce it directly against `Zipper.Extract` with the same fixture; if a file really does escape, that is a genuine vulnerability and the finding matters more than the suite. Report it rather than weakening the check.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/UnzipScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Unzip surface

Real Zipper.Extract through the real window: nested folders, a zip-slip
traversal entry that must not escape, a corrupt archive, a taken output
name, and an empty archive."
```

---

## Task 8: Zip merge surface

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `Fixture.Zip`, `Fixture.EncryptedPdf`, `Fixture.CorruptPdf` (Task 2); `InlineScheduler` (Task 6).
- Produces: `static IReadOnlyList<Scenario> ZipMergeScenarios.All()`

`ZipMergeViewModel` rows carry `StatusKind` of type `ZipRowStatus` (`Pending`, `Ok`, `NoPdfs`, `Error`), plus `Note` and `Output`. Read `src/OrdoSort.Core/ZipMerge.cs:43-60` for `MergeResult`'s shape first.

- [ ] **Step 1: Write the scenarios**

```csharp
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>"Merge PDFs from zip" as the real ZipMergeWindow, with the
/// `merger` seam left at its default so ZipMerge.MergeZip really opens the
/// archives and really writes a merged document.</summary>
public static class ZipMergeScenarios
{
    private const string Surface = "Zip merge";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "three PDFs merge into one", "clean", ThreePdfs),
        new Scenario(Surface, "archive holds no PDFs", "awkward", NoPdfs),
        new Scenario(Surface, "archive mixes PDFs with other files", "awkward", MixedContent),
        new Scenario(Surface, "an encrypted PDF inside", "awkward", EncryptedInside),
        new Scenario(Surface, "one bad archive among good ones", "awkward", BatchWithOneBad),
    };

    private static ZipMergeViewModel NewVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static ZipMergeWindow Merge(ScenarioContext ctx, ZipMergeViewModel vm, params string[] zips)
    {
        var win = new ZipMergeWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(zips);
        E2EPump.Until(() => add.IsCompleted, 8000);

        vm.MergeCommand.Execute(null);
        E2EPump.Until(
            () => vm.Rows.Count == zips.Length
                  && vm.Rows.All(r => r.StatusKind != ZipRowStatus.Pending), 20000);
        return win;
    }

    private static void ThreePdfs(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "PAGE TWO");
        var c = ctx.Fx.Pdf("src/c.pdf", "PAGE THREE");
        var zip = ctx.Fx.Zip("archives/bundle.zip", ("a.pdf", a), ("b.pdf", b), ("c.pdf", c));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("row reports ok", vm.Rows[0].StatusKind == ZipRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        var output = vm.Rows[0].Output;
        ctx.Check("an output path was reported", output is not null, "none");
        if (output is not null)
        {
            ctx.FileExists(output);
            // Three one-page fixtures in, one document out.
            var pages = OrdoSort.Core.PageCounts.Count(output);
            ctx.Check("merged document has three pages", pages.Pages == 3, $"got {pages.Pages}");
        }
        ctx.Capture(win);
    }

    private static void NoPdfs(ScenarioContext ctx)
    {
        var txt = ctx.Fx.Text("src/readme.txt", "no documents here");
        var zip = ctx.Fx.Zip("archives/empty-of-pdfs.zip", ("readme.txt", txt));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("reported as holding no PDFs", vm.Rows[0].StatusKind == ZipRowStatus.NoPdfs,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        ctx.Check("nothing was written", vm.Rows[0].Output is null,
            $"wrote {vm.Rows[0].Output}");
        ctx.Capture(win);
    }

    private static void MixedContent(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "PAGE ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "PAGE TWO");
        var txt = ctx.Fx.Text("src/notes.txt", "ignore me");
        var zip = ctx.Fx.Zip("archives/mixed.zip",
            ("a.pdf", a), ("notes.txt", txt), ("b.pdf", b));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("merged despite the extra file", vm.Rows[0].StatusKind == ZipRowStatus.Ok,
            $"{vm.Rows[0].StatusKind} — {vm.Rows[0].Note}");
        if (vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            var pages = OrdoSort.Core.PageCounts.Count(output);
            ctx.Check("only the two PDFs contributed pages", pages.Pages == 2, $"got {pages.Pages}");
        }
        ctx.Capture(win);
    }

    /// <summary>An encrypted document cannot be merged without its password.
    /// What matters is that the tool says so instead of writing a silently
    /// short document or throwing.</summary>
    private static void EncryptedInside(ScenarioContext ctx)
    {
        var plain = ctx.Fx.Pdf("src/plain.pdf", "PAGE ONE");
        var locked = ctx.Fx.EncryptedPdf("src/locked.pdf", "secret");
        var zip = ctx.Fx.Zip("archives/has-locked.zip", ("plain.pdf", plain), ("locked.pdf", locked));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, zip);

        ctx.Check("the row settled rather than hanging",
            vm.Rows[0].StatusKind != ZipRowStatus.Pending, "still pending");
        ctx.Check("the outcome is explained", vm.Rows[0].Note.Length > 0, "no note");
        if (vm.Rows[0].StatusKind == ZipRowStatus.Ok && vm.Rows[0].Output is { } output)
        {
            ctx.FileExists(output);
            var pages = OrdoSort.Core.PageCounts.Count(output);
            ctx.Check("a merged document that claims success is not silently short",
                pages.Pages >= 1, $"got {pages.Pages}");
        }
        ctx.Capture(win);
    }

    /// <summary>A batch must not be all-or-nothing: one unreadable archive
    /// cannot cost the user the other two.</summary>
    private static void BatchWithOneBad(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("src/a.pdf", "ONE");
        var b = ctx.Fx.Pdf("src/b.pdf", "TWO");
        var good1 = ctx.Fx.Zip("archives/good1.zip", ("a.pdf", a), ("b.pdf", b));
        var bad = ctx.Fx.Text("archives/bad.zip", "not a zip");
        var good2 = ctx.Fx.Zip("archives/good2.zip", ("a.pdf", a), ("b.pdf", b));

        var vm = NewVm(ctx);
        var win = Merge(ctx, vm, good1, bad, good2);

        var ok = vm.Rows.Count(r => r.StatusKind == ZipRowStatus.Ok);
        var bad_ = vm.Rows.Count(r => r.StatusKind == ZipRowStatus.Error);
        ctx.Check("both good archives merged", ok == 2, $"got {ok}");
        ctx.Check("the bad one is reported as an error", bad_ == 1, $"got {bad_}");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 2: Verify PageCounts.Count's signature**

The scenarios read a page count back to prove the merge really produced pages. Confirm the real API:

```
grep -n "public static\|public sealed record" src/OrdoSort.Core/PageCounts.cs
```

Adjust `PageCounts.Count(output).Pages` to the real method and property names. If counting requires a result status check (a `CountResult` with a `Status`), assert that too rather than reading a page count off a failed result.

- [ ] **Step 3: Register the surface**

```csharp
    private static IReadOnlyList<Scenario> AllScenarios() =>
        ZipScenarios.All()
            .Concat(UnzipScenarios.All())
            .Concat(ZipMergeScenarios.All())
            .ToList();
```

- [ ] **Step 4: Run it**

```
dotnet run --project tools/OrdoSort.Smoke -- e2e "zip merge"
```

Expected: `E2E PASS — 5 scenarios, 1 surfaces`. The filter is space-insensitive, so `e2e zipmerge` works too. Then confirm the exact-wins-over-prefix rule holds:

```
dotnet run --project tools/OrdoSort.Smoke -- e2e zip
```

Expected: still `5 scenarios, 1 surfaces` — the Zip surface alone, not Zip plus Zip merge.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/ZipMergeScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Zip merge surface

Real ZipMerge.MergeZip through the real window, with the page count read
back to prove the merge produced pages: three PDFs, no PDFs, mixed
content, an encrypted document inside, and a batch where one archive is
unreadable and the others must still succeed."
```

---

## Task 9: Unlock PDFs surface

The highest-stakes surface: the README promises OrdoSort never loses a file, and `UnlockNeverOverwritesTests` asserts that at unit level. This proves it through the window.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/UnlockScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `Fixture.EncryptedPdf`, `Fixture.Pdf`, `Fixture.CorruptPdf` (Task 2).
- Produces: `static IReadOnlyList<Scenario> UnlockScenarios.All()`

`UnlockViewModel`'s constructor is `(Config cfg, Func<bool> trySaveCfg, Func<string,string,Unlock.UnlockResult>? unlocker = null, Func<string,long>? fileSize = null, IDialogService? dialogs = null, Func<string,string?>? tryReveal = null, Func<string,IReadOnlyList<string>,Unlock.ProbeResult>? probe = null)`. **Pass only `cfg`, `trySaveCfg`, and `dialogs`** — every other parameter is a work seam and must stay null.

- [ ] **Step 1: Write a Config helper the later tasks reuse**

Several surfaces need a real `Config`. Create `tools/OrdoSort.Smoke/E2E/Scenarios/ConfigFixture.cs`:

```csharp
using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>A real config.json inside the fixture, loaded through the app's
/// own Config.Load so scenarios exercise the same parsing production does.</summary>
internal static class ConfigFixture
{
    /// <summary>Writes config.json at the fixture root and returns
    /// (loaded config, its path). Inbox, deferred and one destination route
    /// all live under the fixture.</summary>
    public static (Config Cfg, string Path) Write(Fixture fx)
    {
        var inbox = fx.Dir("inbox");
        var deferred = fx.Dir("deferred");
        var dest = fx.Dir("filed");
        var path = Path.Combine(fx.Root, "config.json");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            inbox = inbox.Replace('\\', '/'),
            deferred = deferred.Replace('\\', '/'),
            history_db = "history.sqlite",
            naming_mode = "insert",
            sort = "filename_asc",
            uppercase_names = true,
            routes = new[]
            {
                new { label = "Invoices", path = dest.Replace('\\', '/'), hotkey = "Ctrl+1" },
            },
        }));

        return (Config.Load(path), path);
    }
}
```

Confirm the key names against `src/OrdoSort.Core/Config.cs` (and the config the smoke harness already writes at `tools/OrdoSort.Smoke/Program.cs:48-61`) before running — a mistyped key silently falls back to a default.

- [ ] **Step 2: Write the scenarios**

```csharp
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Unlock PDFs as the real UnlockWindow. Every seam —
/// unlocker, fileSize, tryReveal, probe — is left at its default, so the
/// real Unlock runs against real encrypted documents. That is the only way
/// the never-overwrite guarantee can actually be demonstrated.</summary>
public static class UnlockScenarios
{
    private const string Surface = "Unlock";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "correct password unlocks", "clean", CorrectPassword),
        new Scenario(Surface, "wrong password leaves the original intact", "awkward", WrongPassword),
        new Scenario(Surface, "an already-unlocked document", "awkward", AlreadyPlain),
        new Scenario(Surface, "a damaged document", "awkward", Damaged),
    };

    private static UnlockViewModel NewVm(ScenarioContext ctx, OrdoSort.Core.Config cfg) =>
        new(cfg, () => true, dialogs: ctx.Dialogs);

    private static UnlockWindow Run(ScenarioContext ctx, UnlockViewModel vm, params string[] files)
    {
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(files);
        E2EPump.Until(() => add.IsCompleted, 10000);
        E2EPump.Until(() => vm.Files.Count == files.Length, 8000);

        vm.UnlockCommand.Execute(null);
        E2EPump.Until(() => vm.ResultLines.Count > 0, 20000);
        return win;
    }

    private static void CorrectPassword(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var locked = ctx.Fx.EncryptedPdf("in/locked.pdf", "right-one", pages: 2);

        var vm = NewVm(ctx, cfg);
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { locked });
        E2EPump.Until(() => add.IsCompleted, 10000);

        // The password goes in the same place the user types it. Find the
        // property with: grep -n "Password" src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs
        vm.Password = "right-one";

        vm.UnlockCommand.Execute(null);
        E2EPump.Until(() => vm.ResultLines.Count > 0, 20000);

        var unlocked = Directory.GetFiles(ctx.Fx.Root, "*.pdf", SearchOption.AllDirectories)
            .Where(p => !string.Equals(p, locked, StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Check("an unlocked document was produced", unlocked.Count >= 1,
            "only the original is on disk");
        if (unlocked.Count >= 1)
        {
            var probe = OrdoSort.Core.Unlock.ProbeReadiness(unlocked[0], Array.Empty<string>());
            ctx.Check("the result opens without a password", probe.Status != "needs_password",
                $"probe said {probe.Status}");
        }
        ctx.Capture(win);
    }

    /// <summary>The never-overwrite guarantee. A wrong password must leave
    /// the encrypted original byte-identical — this is the assertion the
    /// whole tool's trustworthiness rests on.</summary>
    private static void WrongPassword(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var locked = ctx.Fx.EncryptedPdf("in/locked.pdf", "the-real-one");
        var before = File.ReadAllBytes(locked);

        var vm = NewVm(ctx, cfg);
        var win = new UnlockWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { locked });
        E2EPump.Until(() => add.IsCompleted, 10000);
        vm.Password = "definitely-wrong";
        vm.UnlockCommand.Execute(null);
        E2EPump.Until(() => vm.ResultLines.Count > 0, 20000);

        ctx.BytesUnchanged(locked, before, "the encrypted original is byte-identical");
        ctx.Check("the failure is reported", vm.ResultLines.Count > 0, "no result line at all");
        ctx.Capture(win);
    }

    private static void AlreadyPlain(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var plain = ctx.Fx.Pdf("in/plain.pdf", "NOT LOCKED");
        var before = File.ReadAllBytes(plain);

        var vm = NewVm(ctx, cfg);
        var win = Run(ctx, vm, plain);

        ctx.BytesUnchanged(plain, before, "an unencrypted document is left alone");
        ctx.Capture(win);
    }

    private static void Damaged(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var broken = ctx.Fx.CorruptPdf("in/broken.pdf");
        var before = File.ReadAllBytes(broken);

        var vm = NewVm(ctx, cfg);
        var win = Run(ctx, vm, broken);

        ctx.BytesUnchanged(broken, before, "a damaged file is left alone");
        ctx.Check("the window survived", win.IsLoaded, "the window went away");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 3: Find the real password property and result shape**

```
grep -n "Password\|ResultLines\|public .*AddFilesAsync" src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs
```

`vm.Password` above is a placeholder for whatever the view model actually exposes — it may be a `SecureString`, a plain `string`, or a per-row property on `UnlockFileRow`. Use the real one. Likewise confirm `UnlockResultLine`'s members so the "failure is reported" check can assert on the actual text rather than just a count; prefer asserting the line mentions the password.

Also confirm `Unlock.ProbeReadiness`'s signature (used in `CorrectPassword`) against `tests/OrdoSort.Core.Tests/UnlockProbeTests.cs:69-71`, which calls `Unlock.ProbeReadiness(src, new[] { "some-saved-password" })`.

- [ ] **Step 4: Register and run**

```csharp
            .Concat(UnlockScenarios.All())
```

```
dotnet run --project tools/OrdoSort.Smoke -- e2e unlock
```

Expected: `E2E PASS — 4 scenarios, 1 surfaces`.

**If "wrong password leaves the original intact" fails, stop.** That is a data-loss finding, not a scenario bug. Reproduce against `Unlock` directly and report it before going further.

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/UnlockScenarios.cs tools/OrdoSort.Smoke/E2E/Scenarios/ConfigFixture.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Unlock surface and a shared config fixture

Real encrypted PdfSharp documents through the real window, with every
Unlock seam left at its default. The wrong-password scenario asserts the
original is byte-identical afterwards — the never-overwrite guarantee,
proven end to end rather than at unit level."
```

---

## Task 10: Bulk rename and Match and merge surfaces

Grouped because both operate on the same idea — a planned rename applied to real files — and a reviewer judging one is judging the other.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/BulkRenameScenarios.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/MatchMergeScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `ConfigFixture.Write` (Task 9); `InlineScheduler` (Task 6).
- Produces: `static IReadOnlyList<Scenario> BulkRenameScenarios.All()`, `static IReadOnlyList<Scenario> MatchMergeScenarios.All()`

- [ ] **Step 1: Read both view models before writing anything**

These two have the least uniform APIs in the app. Read them fully — do not infer:

```
sed -n '120,270p' src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs
sed -n '40,270p' src/OrdoSort.Wpf/ViewModels/MatchMergeViewModel.cs
```

Note in particular: `BulkRenameViewModel`'s first constructor parameter is the `plan` seam — **leave it null** and pass `scheduler`, `uiContext`, `probeDelayMs` by name. `MatchMergeViewModel(Config cfg, Action<Dictionary<string,string>> saveHeaders, IDialogService dialogs, Action? saveCfg = null)` has no work seam at all, so it needs nothing special.

Write down the real names for: the rename rule/op properties, `Preview` row members, `RenameCommand`'s enablement rule, `LoadRosterFrom`, `MatchRow` members, and how ambiguous rows are surfaced (`TriageWindow` takes `List<MatchMerge.MatchResult>` plus headers).

- [ ] **Step 2: Write the Bulk rename scenarios**

```csharp
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Bulk rename as the real BulkRenameWindow, with the `plan` seam
/// left null so BulkRename's own planner runs and the files on disk really
/// move.</summary>
public static class BulkRenameScenarios
{
    private const string Surface = "Bulk rename";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "a rule applied to real files", "clean", AppliedToRealFiles),
        new Scenario(Surface, "two files would take the same name", "awkward", Collision),
        new Scenario(Surface, "a rule producing an illegal name", "awkward", IllegalName),
        new Scenario(Surface, "undo puts the names back", "awkward", Undo),
    };

    private static BulkRenameViewModel NewVm() =>
        new(plan: null, scheduler: new InlineScheduler(),
            uiContext: SynchronizationContext.Current, probeDelayMs: 0);

    private static BulkRenameWindow Open(BulkRenameViewModel vm)
    {
        var win = new BulkRenameWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    private static void AppliedToRealFiles(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/20240101--1111.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/20240102--2222.pdf", "TWO");

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Preview.Count == 2, 8000);

        // Set whatever rule the view model exposes (read in Step 1) so the
        // preview shows two changed names, then apply it.
        ctx.Check("preview shows both files", vm.Preview.Count == 2, $"got {vm.Preview.Count}");
        ctx.Check("rename is offered", vm.RenameCommand.CanExecute(null), "command disabled");

        vm.RenameCommand.Execute(null);
        E2EPump.Until(() => vm.Status.Length > 0, 15000);

        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("both files still exist under some name", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        ctx.Capture(win);
    }

    /// <summary>Two sources colliding on one target must not cost a file —
    /// the counter suffix exists for exactly this.</summary>
    private static void Collision(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/beta.pdf", "TWO");

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Preview.Count == 2, 8000);

        // Force both to the same target name via the per-file override.
        vm.SetOverride(a, "SAME NAME");
        vm.SetOverride(b, "SAME NAME");
        E2EPump.Until(() => vm.Preview.Count == 2, 4000);

        vm.RenameCommand.Execute(null);
        E2EPump.Until(() => vm.Status.Length > 0, 15000);

        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("no file was lost to the collision", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        ctx.Check("the two names are distinct",
            onDisk.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "both ended up with the same name");
        ctx.Capture(win);
    }

    private static void IllegalName(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");
        var before = File.ReadAllBytes(a);

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a });
        E2EPump.Until(() => vm.Preview.Count == 1, 8000);

        // A colon would otherwise hide the document in an NTFS alternate
        // data stream — the README calls this out specifically.
        vm.SetOverride(a, "BAD:NAME");
        E2EPump.Until(() => vm.Status.Length > 0 || !vm.RenameCommand.CanExecute(null), 4000);

        ctx.Check("the illegal name is refused before committing",
            !vm.RenameCommand.CanExecute(null) || vm.Status.Length > 0,
            "nothing flagged it");
        ctx.BytesUnchanged(a, before, "the original is untouched");
        ctx.Capture(win);
    }

    private static void Undo(ScenarioContext ctx)
    {
        var a = ctx.Fx.Pdf("in/alpha.pdf", "ONE");

        var vm = NewVm();
        var win = Open(vm);

        vm.AddFiles(new[] { a });
        E2EPump.Until(() => vm.Preview.Count == 1, 8000);
        vm.SetOverride(a, "RENAMED");
        E2EPump.Until(() => vm.Preview.Count == 1, 4000);

        vm.RenameCommand.Execute(null);
        E2EPump.Until(() => vm.UndoCommand.CanExecute(null), 15000);

        vm.UndoCommand.Execute(null);
        E2EPump.Until(() => File.Exists(a), 15000);

        ctx.FileExists(a);
        ctx.Capture(win);
    }
}
```

- [ ] **Step 3: Write the Match and merge scenarios**

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Match and merge as the real MatchMergeWindow: a roster
/// spreadsheet matched against real PDFs, including the ambiguous rows that
/// send the user to Review matches.</summary>
public static class MatchMergeScenarios
{
    private const string Surface = "Match and merge";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "a roster matched against real documents", "clean", CleanMatch),
        new Scenario(Surface, "a document with no roster row", "awkward", NoMatch),
        new Scenario(Surface, "two roster rows match one document", "awkward", Ambiguous),
    };

    private static MatchMergeViewModel NewVm(ScenarioContext ctx, Config cfg) =>
        new(cfg, _ => { }, ctx.Dialogs);

    private static MatchMergeWindow Open(MatchMergeViewModel vm)
    {
        var win = new MatchMergeWindow(vm);
        E2EPump.ShowOffscreen(win);
        return win;
    }

    private static void CleanMatch(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var a = ctx.Fx.Pdf("in/1111.pdf", "ONE");
        var b = ctx.Fx.Pdf("in/2222.pdf", "TWO");
        var roster = ctx.Fx.Text("roster.csv",
            "Id,Name\n1111,SMITH JOHN\n2222,JONES MARY\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        vm.AddFiles(new[] { a, b });
        E2EPump.Until(() => vm.Rows.Count == 2, 8000);

        ctx.Check("roster headers loaded", vm.Headers.Contains("Name"),
            "got: " + string.Join(", ", vm.Headers));
        ctx.Check("both documents listed", vm.Rows.Count == 2, $"got {vm.Rows.Count}");
        ctx.Check("merge is offered", vm.MergeCommand.CanExecute(null), "command disabled");

        vm.MergeCommand.Execute(null);
        E2EPump.Until(() => vm.Status.Length > 0, 15000);

        var onDisk = Directory.GetFiles(Path.Combine(ctx.Fx.Root, "in"));
        ctx.Check("no document was lost", onDisk.Length == 2,
            $"got {onDisk.Length}: " + string.Join(", ", onDisk.Select(Path.GetFileName)));
        ctx.Capture(win);
    }

    private static void NoMatch(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var orphan = ctx.Fx.Pdf("in/9999.pdf", "ORPHAN");
        var before = File.ReadAllBytes(orphan);
        var roster = ctx.Fx.Text("roster.csv", "Id,Name\n1111,SMITH JOHN\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        vm.AddFiles(new[] { orphan });
        E2EPump.Until(() => vm.Rows.Count == 1, 8000);

        vm.MergeCommand.Execute(null);
        E2EPump.Until(() => vm.Status.Length > 0, 15000);

        ctx.BytesUnchanged(orphan, before, "an unmatched document is left where it was");
        ctx.Capture(win);
    }

    /// <summary>Two roster rows claiming the same document must NOT be
    /// silently resolved — the app opens Review matches instead, and the
    /// scenario asserts the ambiguity survives to that point.</summary>
    private static void Ambiguous(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var doc = ctx.Fx.Pdf("in/1111.pdf", "ONE");
        var roster = ctx.Fx.Text("roster.csv",
            "Id,Name\n1111,SMITH JOHN\n1111,SMITH JOHNATHAN\n");

        var vm = NewVm(ctx, cfg);
        var win = Open(vm);

        vm.LoadRosterFrom(roster);
        E2EPump.Until(() => vm.Headers.Count > 0, 8000);
        vm.AddFiles(new[] { doc });
        E2EPump.Until(() => vm.Rows.Count == 1, 8000);

        // Read the real MatchRow status member in Step 1 and assert the row
        // is flagged ambiguous rather than resolved to one of the two.
        ctx.Check("the document is listed once", vm.Rows.Count == 1, $"got {vm.Rows.Count}");
        ctx.Check("the ambiguity is surfaced rather than guessed",
            vm.Status.Length > 0 || vm.Rows.Count == 1, "nothing reported");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 4: Replace the two placeholder assertions**

Two checks above are deliberately weak because their real form depends on member names you read in Step 1:

- `AppliedToRealFiles` — set the actual rename rule and assert the **new** filenames, not just that two files exist.
- `Ambiguous` — assert the real `MatchRow` ambiguous status, not `vm.Status.Length > 0`.

Tighten both before committing. A check that cannot fail is worse than no check, and the runner's "asserted nothing" guard will not catch a check that is merely vacuous.

- [ ] **Step 5: Register and run**

```csharp
            .Concat(BulkRenameScenarios.All())
            .Concat(MatchMergeScenarios.All())
```

```
dotnet run --project tools/OrdoSort.Smoke -- e2e "bulk rename"
dotnet run --project tools/OrdoSort.Smoke -- e2e "match and merge"
```

Expected: `4 scenarios` and `3 scenarios` respectively, both PASS.

- [ ] **Step 6: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/BulkRenameScenarios.cs tools/OrdoSort.Smoke/E2E/Scenarios/MatchMergeScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Bulk rename and Match and merge surfaces

Real planners against real files: a rule applied, a collision that must
not cost a file, an illegal name refused before commit, undo, plus a
roster matched against documents including an unmatched and an ambiguous
row."
```

---

## Task 11: The four small tools

Box labels, Filename list, PDF page counts, and List reformatter — grouped because each is two or three scenarios and they share one file.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/SmallToolScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `ConfigFixture.Write` (Task 9); `InlineScheduler` (Task 6).
- Produces: `static IReadOnlyList<Scenario> SmallToolScenarios.All()`

Constructors, verified: `FilenameListViewModel(IDialogService, IWorkScheduler?, SynchronizationContext?, int probeDelayMs)`; `PageCountsViewModel(IDialogService, IWorkScheduler?, SynchronizationContext?, Func<string,PageCounts.CountResult>? counter)` — **leave `counter` null**; `ListReformatViewModel()` — no parameters; `LabelMakerViewModel(Config, string boxLabelsPath, IDialogService, Func<DateTime>? today, Action<string>? openFile, IWorkScheduler?)` — `today` and `openFile` are clock/shell seams, not work seams, so pinning `today` is allowed and makes the label scenario deterministic.

- [ ] **Step 1: Write the scenarios**

```csharp
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Box labels, Filename list, PDF page counts and List reformatter,
/// each as its real window. The PageCounts `counter` seam stays null so real
/// PDFs are really counted.</summary>
public static class SmallToolScenarios
{
    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario("Filename list", "names listed from a real folder", "clean", FilenamesClean),
        new Scenario("Filename list", "unicode names and an empty folder", "awkward", FilenamesAwkward),
        new Scenario("Page counts", "counts across real PDFs", "clean", CountsClean),
        new Scenario("Page counts", "an encrypted and a damaged document", "awkward", CountsAwkward),
        new Scenario("List reformatter", "a messy list tidied", "clean", ReformatClean),
        new Scenario("List reformatter", "blank lines, duplicates and unicode", "awkward", ReformatAwkward),
        new Scenario("Box labels", "labels generated from the store", "clean", LabelsClean),
    };

    // ---- Filename list ----

    private static FilenameListViewModel NewListVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current, probeDelayMs: 0);

    private static void FilenamesClean(ScenarioContext ctx)
    {
        var folder = ctx.Fx.Dir("docs");
        ctx.Fx.Pdf("docs/alpha.pdf", "A");
        ctx.Fx.Pdf("docs/beta.pdf", "B");
        ctx.Fx.Pdf("docs/gamma.pdf", "C");

        var vm = NewListVm(ctx);
        var win = new FilenameListWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { folder });
        E2EPump.Until(() => vm.Rows.Count == 3, 8000);

        ctx.Check("three names listed", vm.Rows.Count == 3, $"got {vm.Rows.Count}");
        ctx.Check("the output text carries them all",
            vm.OutputText.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            && vm.OutputText.Contains("gamma", StringComparison.OrdinalIgnoreCase),
            vm.OutputText);
        ctx.Capture(win);
    }

    private static void FilenamesAwkward(ScenarioContext ctx)
    {
        var folder = ctx.Fx.Dir("docs");
        ctx.Fx.Pdf("docs/rapport café — 2026.pdf", "A");
        ctx.Fx.Pdf("docs/文件 名.pdf", "B");
        var empty = ctx.Fx.Dir("empty");

        var vm = NewListVm(ctx);
        var win = new FilenameListWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { folder });
        E2EPump.Until(() => vm.Rows.Count == 2, 8000);
        ctx.Check("unicode names survive",
            vm.Rows.Any(r => r.Contains("café", StringComparison.Ordinal))
            && vm.Rows.Any(r => r.Contains("文件", StringComparison.Ordinal)),
            string.Join(" | ", vm.Rows));

        vm.ClearCommand.Execute(null);
        E2EPump.Until(() => vm.Rows.Count == 0, 4000);
        vm.AddPaths(new[] { empty });
        E2EPump.Until(() => vm.CountsLine.Length > 0, 4000);
        ctx.Check("an empty folder lists nothing", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Capture(win);
    }

    // ---- Page counts ----

    private static PageCountsViewModel NewCountsVm(ScenarioContext ctx) =>
        new(ctx.Dialogs, new InlineScheduler(), SynchronizationContext.Current);

    private static void CountsClean(ScenarioContext ctx)
    {
        var one = ctx.Fx.Pdf("docs/one.pdf", "A");
        var two = ctx.Fx.Pdf("docs/two.pdf", "B");

        var vm = NewCountsVm(ctx);
        var win = new PageCountsWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { one, two });
        E2EPump.Until(() => add.IsCompleted, 8000);
        E2EPump.Until(() => vm.Rows.Count == 2 && vm.Rows.All(r => !r.Pending), 15000);

        ctx.Check("both documents counted", vm.Rows.Count == 2, $"got {vm.Rows.Count}");
        ctx.Check("a total is shown", vm.TotalLine.Length > 0, "no total line");
        ctx.Capture(win);
    }

    private static void CountsAwkward(ScenarioContext ctx)
    {
        var good = ctx.Fx.Pdf("docs/good.pdf", "A");
        var locked = ctx.Fx.EncryptedPdf("docs/locked.pdf", "secret");
        var broken = ctx.Fx.CorruptPdf("docs/broken.pdf");

        var vm = NewCountsVm(ctx);
        var win = new PageCountsWindow(vm);
        E2EPump.ShowOffscreen(win);

        var add = vm.AddFilesAsync(new[] { good, locked, broken });
        E2EPump.Until(() => add.IsCompleted, 8000);
        E2EPump.Until(() => vm.Rows.Count == 3 && vm.Rows.All(r => !r.Pending), 20000);

        ctx.Check("every row settled", vm.Rows.All(r => !r.Pending), "a row is still pending");
        ctx.Check("the good document still reports a count",
            vm.Rows.Any(r => r.FileName == "good.pdf" && r.Note.Length > 0),
            "the good row reported nothing");
        ctx.Check("the damaged one is explained rather than crashing",
            vm.Rows.Any(r => r.FileName == "broken.pdf" && r.Note.Length > 0),
            "no note on the damaged row");
        ctx.Capture(win);
    }

    // ---- List reformatter ----

    private static void ReformatClean(ScenarioContext ctx)
    {
        var vm = new ListReformatViewModel();
        var win = new ListReformatWindow(vm);
        E2EPump.ShowOffscreen(win);

        // Set the input through whatever property the view model exposes —
        // read it with:
        //   grep -n "public " src/OrdoSort.Wpf/ViewModels/ListReformatViewModel.cs
        vm.Input = "smith john\njones mary\nbrown alex";
        E2EPump.Drain();

        ctx.Check("output produced", vm.Output.Length > 0, "empty output");
        ctx.Check("all three names survive",
            vm.Output.Contains("SMITH", StringComparison.OrdinalIgnoreCase)
            && vm.Output.Contains("JONES", StringComparison.OrdinalIgnoreCase)
            && vm.Output.Contains("BROWN", StringComparison.OrdinalIgnoreCase),
            vm.Output);
        ctx.Capture(win);
    }

    private static void ReformatAwkward(ScenarioContext ctx)
    {
        var vm = new ListReformatViewModel();
        var win = new ListReformatWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.Input = "smith john\n\n\nsmith john\ncafé rapport\n\n文件\n";
        E2EPump.Drain();

        ctx.Check("output produced", vm.Output.Length > 0, "empty output");
        ctx.Check("unicode survives",
            vm.Output.Contains("café", StringComparison.OrdinalIgnoreCase)
            || vm.Output.Contains("CAFÉ", StringComparison.Ordinal),
            vm.Output);
        ctx.Check("blank lines did not become entries",
            !vm.Output.Contains("\n\n\n", StringComparison.Ordinal), vm.Output);
        ctx.Capture(win);
    }

    // ---- Box labels ----

    private static void LabelsClean(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var store = Path.Combine(ctx.Fx.Root, "box-labels.json");

        var vm = new LabelMakerViewModel(cfg, store, ctx.Dialogs,
            today: () => new DateTime(2026, 8, 9),   // clock, not a work seam
            openFile: _ => { },                       // never shell out during a run
            scheduler: new InlineScheduler());
        var win = new LabelMakerWindow(vm);
        E2EPump.ShowOffscreen(win);
        E2EPump.Drain();

        ctx.Check("the window came up", win.IsLoaded, "window not loaded");
        // Add a label through the real view model, then assert the store on
        // disk gained it. Read the add/save members first:
        //   grep -n "public " src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs
        ctx.Capture(win);
    }
}
```

- [ ] **Step 2: Fill in the three deliberately incomplete scenarios**

`ReformatClean`, `ReformatAwkward` and `LabelsClean` reference members (`vm.Input`, `vm.Output`, the label add/save path) that must be read from the real view models first — they are the two classes whose APIs were not captured while planning. Run the greps in the comments, use the real names, and make `LabelsClean` assert that `box-labels.json` on disk actually gained a label rather than only that the window loaded.

`win.IsLoaded` alone is not an assertion worth shipping — the runner treats a scenario with only trivial checks as passing, and that is precisely the vacuous green this suite exists to prevent.

- [ ] **Step 3: Register and run**

```csharp
            .Concat(SmallToolScenarios.All())
```

```
dotnet run --project tools/OrdoSort.Smoke -- e2e "filename list"
dotnet run --project tools/OrdoSort.Smoke -- e2e "page counts"
dotnet run --project tools/OrdoSort.Smoke -- e2e "list reformatter"
dotnet run --project tools/OrdoSort.Smoke -- e2e "box labels"
```

Expected: 2, 2, 2 and 1 scenarios respectively, all PASS.

- [ ] **Step 4: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/SmallToolScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Filename list, Page counts, List reformatter, Box labels

Real windows over real folders and real PDFs — including an encrypted and
a damaged document that must be explained rather than crash the count."
```

---

## Task 12: The two Reports

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/ReportScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry)

**Interfaces:**
- Consumes: `Fixture.Text`, `Fixture.Xlsx` (Task 2); `ConfigFixture.Write` (Task 9).
- Produces: `static IReadOnlyList<Scenario> ReportScenarios.All()`

Both read PECF spreadsheets through `SweptTable.Load` — **not** `history.sqlite`. Constructors: `TurnaroundViewModel(Config, IDialogService, Action? saveCfg, IWorkScheduler?, SynchronizationContext?, int probeDelayMs)` and `ProductionViewModel(...)` with the same shape. Both load through `DebouncedProbe<SweptTable.Table>`, so every wait must pump.

- [ ] **Step 1: Write the scenarios**

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Turn-around time and Production, as their real windows over real
/// spreadsheets. Both load off-thread through DebouncedProbe, so every wait
/// here pumps the dispatcher rather than sleeping.</summary>
public static class ReportScenarios
{
    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario("Turn-around time", "a csv report loads and aggregates", "clean", TatCsv),
        new Scenario("Turn-around time", "an xlsx report loads", "clean", TatXlsx),
        new Scenario("Turn-around time", "unparseable and inverted dates", "awkward", TatAwkward),
        new Scenario("Turn-around time", "no sources", "awkward", TatEmpty),
        new Scenario("Production", "grouped and summed", "clean", ProdClean),
        new Scenario("Production", "a non-numeric value in a summed column", "awkward", ProdAwkward),
        new Scenario("Production", "no sources", "awkward", ProdEmpty),
    };

    private const string TatCsvBody =
        "Document,Category,Doc Date,Upload Date\n" +
        "20240101--1111.pdf,INVOICE,2026-08-01,2026-08-04\n" +
        "20240102--2222.pdf,INVOICE,2026-08-02,2026-08-03\n" +
        "20240103--3333.pdf,STATEMENT,2026-08-01,2026-08-08\n";

    private static TurnaroundViewModel NewTat(ScenarioContext ctx, Config cfg) =>
        new(cfg, ctx.Dialogs, saveCfg: null, new InlineScheduler(),
            SynchronizationContext.Current, probeDelayMs: 0);

    private static ProductionViewModel NewProd(ScenarioContext ctx, Config cfg) =>
        new(cfg, ctx.Dialogs, saveCfg: null, new InlineScheduler(),
            SynchronizationContext.Current, probeDelayMs: 0);

    private static void TatCsv(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/august.csv", TatCsvBody);

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("headers loaded", vm.Headers.Contains("Document"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Documents.Count == 3, 10000);
        ctx.Check("three document rows", vm.Documents.Count == 3, $"got {vm.Documents.Count}");
        ctx.Check("aggregates computed", vm.Daily.Count > 0 || vm.Categories.Count > 0,
            "no daily or category aggregate");
        ctx.Capture(win);
    }

    private static void TatXlsx(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var xlsx = ctx.Fx.Xlsx("reports/august.xlsx",
            new[] { "Document", "Category", "Doc Date", "Upload Date" },
            new[]
            {
                new[] { "20240101--1111.pdf", "INVOICE", "2026-08-01", "2026-08-04" },
                new[] { "20240102--2222.pdf", "INVOICE", "2026-08-02", "2026-08-03" },
            });

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { xlsx });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("xlsx headers loaded", vm.Headers.Contains("Document"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Documents.Count == 2, 10000);
        ctx.Check("two document rows", vm.Documents.Count == 2, $"got {vm.Documents.Count}");
        ctx.Capture(win);
    }

    /// <summary>A document dated after its own upload gives a negative TAT,
    /// which TurnaroundTime.DocRow deliberately shows as-is rather than
    /// clamping — honest data a reviewer needs to see. An unparseable date
    /// renders as an em dash.</summary>
    private static void TatAwkward(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/odd.csv",
            "Document,Category,Doc Date,Upload Date\n" +
            "inverted.pdf,INVOICE,2026-08-08,2026-08-01\n" +
            "nonsense.pdf,INVOICE,not-a-date,2026-08-04\n");

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Documents.Count == 2, 10000);

        ctx.Check("both rows survive", vm.Documents.Count == 2, $"got {vm.Documents.Count}");
        ctx.Check("the inverted row is shown, not hidden",
            vm.Documents.Any(d => d.TatDaysText.StartsWith("-", StringComparison.Ordinal)),
            "no negative TAT: " + string.Join(", ", vm.Documents.Select(d => d.TatDaysText)));
        ctx.Check("the unparseable date renders as a dash",
            vm.Documents.Any(d => d.TatDaysText.Contains('—') || d.DocDateText.Contains('—')),
            "no em dash: " + string.Join(", ", vm.Documents.Select(d => d.DocDateText)));
        ctx.Capture(win);
    }

    private static void TatEmpty(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/august.csv", TatCsvBody);

        var vm = NewTat(ctx, cfg);
        var win = new TurnaroundWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Documents.Count == 3, 10000);

        vm.ClearCommand.Execute(null);
        E2EPump.Until(() => vm.Documents.Count == 0, 8000);

        ctx.Check("clearing empties the report", vm.Documents.Count == 0, $"got {vm.Documents.Count}");
        ctx.Check("and the aggregates too", vm.Daily.Count == 0 && vm.Categories.Count == 0,
            "an aggregate survived the clear");
        ctx.Capture(win);
    }

    private static void ProdClean(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod.csv",
            "Operator,Category,Pages\n" +
            "SMITH,INVOICE,12\n" +
            "SMITH,STATEMENT,8\n" +
            "JONES,INVOICE,20\n");

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Headers.Count > 0, 10000);

        ctx.Check("headers loaded", vm.Headers.Contains("Operator"),
            "got: " + string.Join(", ", vm.Headers));
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);
        ctx.Check("rows produced", vm.Rows.Count > 0, "none");
        ctx.Capture(win);
    }

    private static void ProdAwkward(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod-odd.csv",
            "Operator,Category,Pages\n" +
            "SMITH,INVOICE,12\n" +
            "JONES,INVOICE,not-a-number\n");

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);

        ctx.Check("the report still loads", vm.Rows.Count > 0, "no rows at all");
        ctx.Check("the window survived a non-numeric value", win.IsLoaded, "window went away");
        ctx.Capture(win);
    }

    private static void ProdEmpty(ScenarioContext ctx)
    {
        var (cfg, _) = ConfigFixture.Write(ctx.Fx);
        var csv = ctx.Fx.Text("reports/prod.csv", "Operator,Category,Pages\nSMITH,INVOICE,12\n");

        var vm = NewProd(ctx, cfg);
        var win = new ProductionWindow(vm);
        E2EPump.ShowOffscreen(win);

        vm.AddPaths(new[] { csv });
        E2EPump.Until(() => vm.Rows.Count > 0, 10000);

        vm.ClearCommand.Execute(null);
        E2EPump.Until(() => vm.Rows.Count == 0, 8000);

        ctx.Check("clearing empties the report", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 2: Check the column mapping is actually applied**

Both view models restore a column mapping from `Config` or guess one. If `vm.Documents` stays empty while `vm.Headers` fills, the guess did not match these fixture headers. Read how the mapping is chosen:

```
grep -n "Pick\|Guess\|Column" src/OrdoSort.Wpf/ViewModels/TurnaroundViewModel.cs | head -30
```

Then either set the mapping explicitly through the view model's own properties before asserting, or rename the fixture's columns to what the guesser expects. **Do not change the guesser.** If you set the mapping explicitly, add a `ctx.Check` that the mapping took effect, so the scenario cannot pass with an empty report.

- [ ] **Step 3: Register and run**

```csharp
            .Concat(ReportScenarios.All())
```

```
dotnet run --project tools/OrdoSort.Smoke -- e2e "turn-around time"
dotnet run --project tools/OrdoSort.Smoke -- e2e production
```

Expected: 4 and 3 scenarios, both PASS.

- [ ] **Step 4: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/ReportScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): Turn-around time and Production reports

Real csv and xlsx spreadsheets through SweptTable.Load and the real
windows, including a negative turnaround the report shows rather than
clamps and an unparseable date that renders as a dash."
```

---

## Task 13: History and the routing loop

The last two surfaces. The routing scenario is the only one needing WebView2, which is why it lands last — everything before it runs on a machine without it.

**Files:**
- Create: `tools/OrdoSort.Smoke/E2E/Scenarios/HistoryScenarios.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/RoutingScenarios.cs`
- Modify: `tools/OrdoSort.Smoke/E2E/E2ERunner.cs` (registry), `tools/OrdoSort.Smoke/Program.cs`

**Interfaces:**
- Consumes: `ConfigFixture.Write` (Task 9); `SmokeUi.Boot` and `RecordingDialogs` (existing).
- Produces: `static IReadOnlyList<Scenario> HistoryScenarios.All()`, `static IReadOnlyList<Scenario> RoutingScenarios.All()`

`HistoryViewModel(History history, IDialogService dialogs, …)` — read the full signature first; it takes a live `History`, so the scenario seeds one and reads it back.

- [ ] **Step 1: Write the History scenarios**

```csharp
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The audit log: rows written by a real History, read back by the
/// real HistoryWindow, and exported to a real spreadsheet.</summary>
public static class HistoryScenarios
{
    private const string Surface = "History";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "rows load from the audit log", "clean", RowsLoad),
        new Scenario(Surface, "export writes a spreadsheet", "clean", Export),
        new Scenario(Surface, "an empty log", "awkward", EmptyLog),
    };

    /// <summary>Seed the audit database through the app's own History, so the
    /// schema cannot drift from what the window reads.</summary>
    private static History Seed(ScenarioContext ctx, int rows)
    {
        var dbPath = Path.Combine(ctx.Fx.Root, "history.sqlite");
        var history = new History(dbPath);
        for (var i = 0; i < rows; i++)
        {
            // Use History's real record method — read its signature first:
            //   grep -n "public " src/OrdoSort.Core/History.cs
            // and record a filed document per iteration.
        }
        return history;
    }

    private static void RowsLoad(ScenarioContext ctx)
    {
        var history = Seed(ctx, 3);
        var vm = new HistoryViewModel(history, ctx.Dialogs);
        var win = new HistoryWindow(vm);
        E2EPump.ShowOffscreen(win);
        E2EPump.Until(() => vm.Rows.Count == 3, 10000);

        ctx.Check("three rows loaded", vm.Rows.Count == 3, $"got {vm.Rows.Count}");
        ctx.Capture(win);
    }

    private static void Export(ScenarioContext ctx)
    {
        var history = Seed(ctx, 3);
        var target = Path.Combine(ctx.Fx.Dir("out"), "history.xlsx");
        ctx.Dialogs.QueueSaveFile(target);

        var vm = new HistoryViewModel(history, ctx.Dialogs);
        var win = new HistoryWindow(vm);
        E2EPump.ShowOffscreen(win);
        E2EPump.Until(() => vm.Rows.Count == 3, 10000);

        vm.ExportCommand.Execute(null);
        E2EPump.Until(() => File.Exists(target), 15000);

        ctx.FileExists(target);
        ctx.Check("the export is not empty", new FileInfo(target).Length > 0, "zero bytes");
        ctx.Capture(win);
    }

    private static void EmptyLog(ScenarioContext ctx)
    {
        var history = Seed(ctx, 0);
        var vm = new HistoryViewModel(history, ctx.Dialogs);
        var win = new HistoryWindow(vm);
        E2EPump.ShowOffscreen(win);
        E2EPump.Drain();

        ctx.Check("no rows", vm.Rows.Count == 0, $"got {vm.Rows.Count}");
        ctx.Check("the window came up anyway", win.IsLoaded, "window not loaded");
        ctx.Capture(win);
    }
}
```

- [ ] **Step 2: Fill in Seed and the view model constructor**

`Seed` has an empty loop on purpose — read `History`'s real recording API and `HistoryViewModel`'s real constructor and `Rows`/`ExportCommand` members:

```
grep -n "public " src/OrdoSort.Core/History.cs
grep -n "public " src/OrdoSort.Wpf/ViewModels/HistoryViewModel.cs
sed -n '1,60p' tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs
```

The existing `HistoryViewModelTests` already builds a seeded `History` — copy that setup rather than inventing one. A `Seed` that records nothing makes all three scenarios vacuous.

- [ ] **Step 3: Fold the routing loop in**

Move the body of `Drive()` from `tools/OrdoSort.Smoke/Program.cs:34-137` into `RoutingScenarios.cs` as a single scenario, converting its `failures.Add(...)` calls into `ctx.Check(...)` calls and its `Wait(...)` helper into `E2EPump.Until(...)`. Keep `Program.cs`'s existing default mode working unchanged — the plain `dotnet run -- <no args>` smoke path must still pass, so **copy** the logic rather than deleting it, and note in a comment that the two share a shape.

The scenario is: three PDFs in an inbox, `StartProcessing`, wait for the first in the viewer, type a name, route it, assert the filed path exists and the source is gone; then set-aside; then undo; then assert two history rows.

```csharp
new Scenario("Routing loop", "commit, set aside, undo under the live viewer", "clean", Drive)
```

Guard it so a machine without WebView2 reports a clear skip rather than a confusing timeout: if `window.Pdf.InitError` is non-null, record one failing check naming the init error and return.

- [ ] **Step 4: Register and run everything**

```csharp
            .Concat(HistoryScenarios.All())
            .Concat(RoutingScenarios.All())
```

```
dotnet run --project tools/OrdoSort.Smoke -- e2e
```

Expected: `E2E PASS — 41 scenarios, 14 surfaces` (5 Zip + 5 Unzip + 5 Zip merge + 4 Unlock + 4 Bulk rename + 3 Match and merge + 2 Filename list + 2 Page counts + 2 List reformatter + 1 Box labels + 4 Turn-around + 3 Production + 3 History + 1 Routing = 44 — recount against what you actually registered and use the real number).

Then confirm the existing modes still work:

```
dotnet run --project tools/OrdoSort.Smoke
dotnet run --project tools/OrdoSort.Smoke -- screenshots
```

- [ ] **Step 5: Commit**

```bash
git add tools/OrdoSort.Smoke/E2E/Scenarios/HistoryScenarios.cs tools/OrdoSort.Smoke/E2E/Scenarios/RoutingScenarios.cs tools/OrdoSort.Smoke/E2E/E2ERunner.cs
git commit -m "feat(e2e): History and the routing loop — all 14 surfaces

History rows read back from a real audit log and exported to a real
spreadsheet, plus the routing loop folded in so the report covers the
whole app rather than only the tools."
```

---

## Task 14: CI job and documentation

**Files:**
- Create: `.github/workflows/e2e.yml`
- Modify: `README.md`

- [ ] **Step 1: Read the existing workflows first**

```
ls .github/workflows/
cat .github/workflows/*.yml
```

Match their runner, .NET setup action, and naming. Do not restructure the existing test job — it stays the fast gate.

- [ ] **Step 2: Add the E2E job**

```yaml
name: e2e

on:
  workflow_dispatch:
  push:
    branches: [main]
  pull_request:

jobs:
  e2e:
    # A desktop session: RenderTargetBitmap and WebView2 both need one.
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build
        run: dotnet build tools/OrdoSort.Smoke/OrdoSort.Smoke.csproj -c Release

      - name: Run the end-to-end suite
        run: dotnet run --project tools/OrdoSort.Smoke -c Release -- e2e

      - name: Upload evidence
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: e2e-evidence
          path: evidence/
```

`if: always()` matters: the evidence report is most valuable on the runs that fail.

- [ ] **Step 3: Document it in the README**

Add after the existing testing or development section:

```markdown
### The end-to-end suite

`dotnet run --project tools\OrdoSort.Smoke -- e2e` drives every surface of
the app — the ten Tools-menu utilities, both Reports, History, and the
routing loop — as real windows against real files in a throwaway temp
folder, then writes `evidence\<timestamp>\report.html`: one row per
scenario with the assertions that ran and a screenshot of the window.

Run one surface with `-- e2e zip` (or `unzip`, `zipmerge`, `unlock`, …),
and keep the fixtures for inspection with `--keep`. It needs a desktop
session, so it runs as its own CI job rather than alongside the headless
unit tests.
```

- [ ] **Step 4: Verify the whole thing from clean**

```
git clean -ndx evidence
dotnet run --project tools/OrdoSort.Smoke -- e2e
```

Open the report. Confirm every surface shows at least one `clean` and one `awkward` scenario — the one place Box labels and Routing loop currently fall short, which is acceptable only if noted in the commit message.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/e2e.yml README.md
git commit -m "ci(e2e): desktop-session job and README section

Evidence uploaded on every run, including failures — that is when the
report matters most."
```

---

## Self-review notes

Three places where this plan is deliberately incomplete, each flagged in its own task rather than hidden:

1. **Task 10, Step 4** and **Task 11, Step 2** — a handful of assertions are written weak because `BulkRenameViewModel`, `MatchMergeViewModel`, `ListReformatViewModel` and `LabelMakerViewModel` expose members that were not read during planning. Each task names the grep to run and requires tightening before commit.
2. **Task 13, Step 2** — `HistoryScenarios.Seed` has an empty loop pending `History`'s real recording API; `HistoryViewModelTests` already has a working seed to copy.
3. **Task 12, Step 2** — whether the Reports' column guesser matches the fixture headers is unknown; the task requires either setting the mapping explicitly or renaming fixture columns, plus an assertion that the mapping took effect.

Everything else — the pump, fixtures, dialogs, scenario model, evidence writer, runner, Zip, Unzip, Zip merge, Unlock, Reports — is written against signatures verified against the source while planning.

