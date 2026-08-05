# WebView2 Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close finding 4.1 of `docs/superpowers/audits/2026-08-04-full-audit.md` — the PDF pane renders the app's defining untrusted input (a PDF off a scanner or a share) in a browser with every default capability enabled.

**Architecture:** The pane only ever navigates to two things: a `file://` URL for the document currently being triaged, and `about:blank`. So the control is not a URL-scheme filter but something stricter and simpler — **permit only navigations this viewer itself initiated**. That reduces to a pure predicate, which is fully unit-testable without a real `WebView2`; the event wiring around it is verified by the smoke harness, which drives real WebView2 against real PDFs.

**Tech Stack:** C# / .NET 8, WPF, `Microsoft.Web.WebView2` 1.0.2903.40. Repo `S:\OrdoSort`, branch `main`, base `79e9768`.

## Global Constraints

- **Decision already taken (do not re-open): security-only, no UX change.** The user chose this explicitly over two more aggressive options. `AreDefaultContextMenusEnabled` and `AreBrowserAcceleratorKeysEnabled` stay **enabled** — right-click and Ctrl+P/Ctrl+F in the pane must keep working exactly as today. Do not disable them "while you're in there".
- **Nothing may break PDF rendering.** The pane's whole purpose is showing the document being triaged. Any setting that blanks or degrades it is a failed step, not a trade-off.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 377 + Wpf 520 = 897 green.** A WPF line that is missing, reports a skip, or reports a much smaller number means the suite did not run — a failed step, not a pass.
- A stray `OrdoSort.exe` holding `OrdoSort.Core.dll` breaks rebuilds. `tasklist | findstr OrdoSort` before building and after any launch.
- **Proof standard:** demonstrate the failing state before the fix; confirm the compiled assembly under test lacks the fix before trusting a "before" measurement.
- **The lesson from the previous program, which cost four fix rounds:** every failure was *an untested branch carrying the entire safety argument for its change*, while the feature itself was well tested. For this plan that means: a test that proves a PDF still displays is not a test of the guard. **Ask what test fails if the guard is deleted.**
- Never `--no-verify`, never force, **never push** — the push is the user's call after the plan is green.
- Commit per task.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/OrdoSort.Wpf/Services/WebViewPdfViewer.cs` | the navigation predicate, the event wiring, the settings block | 1 |
| `tests/OrdoSort.Wpf.Tests/ViewerNavigationPolicyTests.cs` | NEW — the predicate, exhaustively | 1 |
| `docs/superpowers/audits/2026-08-04-full-audit.md` | mark 4.1 fixed | 2 |

---

### Task 1: Lock the pane to navigations it initiated itself

**Files:**
- Modify: `src/OrdoSort.Wpf/Services/WebViewPdfViewer.cs`
- Create: `tests/OrdoSort.Wpf.Tests/ViewerNavigationPolicyTests.cs`

**Interfaces:**
- Produces: `internal static bool IsPermittedNavigation(string requested, string? expected)` on `WebViewPdfViewer` — a pure function, no WebView2 types, so it is testable without a browser. Returns true when `requested` is the navigation the viewer itself just asked for, or `about:blank`.
- `ShowAsync`, `Blank` and `ReleaseAsync` each record what they are about to navigate to, before calling `Navigate`.

- [ ] **Step 1: Write the failing tests.** Create `tests/OrdoSort.Wpf.Tests/ViewerNavigationPolicyTests.cs`. The predicate does not exist yet, so these fail to compile — that is the red state for a pure function, and it is acceptable here *only because* Step 3's teeth-proof re-establishes it properly.

```csharp
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>The PDF pane renders this app's defining untrusted input — a
/// document that arrived from a scanner or a shared folder. Before this, the
/// viewer called EnsureCoreWebView2Async and nothing else: a link annotation
/// in a hostile PDF could navigate the pane to any http(s) or file: URL, run
/// script there, and put up a convincing fake "enter the PDF password" prompt
/// inside the app's own frame (2026-08-04 audit, finding 4.1).
///
/// The policy is deliberately NOT a scheme allowlist. The viewer only ever
/// goes two places — the file:// URL of the document being triaged, and
/// about:blank — and it knows which one it just asked for. So the rule is the
/// strictest one available: permit only what this viewer itself initiated.
/// A hostile PDF cannot forge that, because it cannot make the viewer set its
/// own expectation.</summary>
public class ViewerNavigationPolicyTests
{
    private const string Doc = "file:///C:/inbox/20240115--111111.pdf";

    [Fact]
    public void TheDocumentTheViewerAskedForIsPermitted() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation(Doc, Doc));

    [Fact]
    public void BlankIsAlwaysPermittedBecauseReleaseDependsOnIt() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation("about:blank", null));

    /// <summary>The attack this whole task exists to stop.</summary>
    [Theory]
    [InlineData("https://evil.example/login")]
    [InlineData("http://evil.example/login")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("file://server/share/other.pdf")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>Enter your password</h1>")]
    [InlineData("about:config")]
    public void AnythingTheViewerDidNotAskForIsRefused(string requested) =>
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(requested, Doc));

    /// <summary>A different local PDF is still refused — "it's only a file
    /// URL" is not the rule; "it's the one we asked for" is.</summary>
    [Fact]
    public void ADifferentLocalPdfIsRefused() =>
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(
            "file:///C:/inbox/20240115--222222.pdf", Doc));

    /// <summary>Nothing is expected yet (before the first ShowAsync), so only
    /// about:blank may pass.</summary>
    [Fact]
    public void WithNothingExpectedOnlyBlankPasses()
    {
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(Doc, null));
        Assert.True(WebViewPdfViewer.IsPermittedNavigation("about:blank", null));
    }

    /// <summary>Windows paths are case-insensitive and Edge may normalise the
    /// URL it reports; the comparison must not refuse the very document the
    /// viewer just asked for over casing. A false refusal here blanks the
    /// pane on a legitimate document — the worst outcome this change can
    /// produce, and worse than the attack it prevents.</summary>
    [Fact]
    public void CasingDifferencesDoNotRefuseTheExpectedDocument() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation(
            "file:///C:/Inbox/20240115--111111.PDF",
            "file:///c:/inbox/20240115--111111.pdf"));
}
```

- [ ] **Step 2: Run — MUST FAIL** (the predicate does not exist). Paste the output.

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
```

- [ ] **Step 3: Implement the predicate and the expectation tracking.** In `WebViewPdfViewer.cs`:

```csharp
    /// <summary>What this viewer last asked to navigate to. A hostile PDF
    /// cannot forge it: only ShowAsync/Blank/ReleaseAsync set it, immediately
    /// before calling Navigate.</summary>
    private string? _expected;

    /// <summary>The navigation policy, as a pure function so it can be tested
    /// without a browser. Permits about:blank (ReleaseAsync depends on it) and
    /// the exact document the viewer just asked for — nothing else. Compared
    /// case-insensitively because Windows paths are case-insensitive and Edge
    /// may normalise the URL it reports back; refusing the expected document
    /// over casing would blank the pane on a legitimate file, which is worse
    /// than the attack this prevents.</summary>
    internal static bool IsPermittedNavigation(string requested, string? expected)
    {
        if (string.Equals(requested, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return expected is not null
            && string.Equals(requested, expected, StringComparison.OrdinalIgnoreCase);
    }
```

Then have each navigator record its intent first. `ShowAsync`:

```csharp
    public Task ShowAsync(string path)
    {
        if (_ready)
        {
            _expected = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            _view.CoreWebView2.Navigate(_expected);
        }
        return Task.CompletedTask;
    }
```

`Blank` and `ReleaseAsync` navigate to `about:blank`, which the predicate permits unconditionally; set `_expected = null` in both so a stale document URL cannot be replayed after the pane is cleared.

- [ ] **Step 4: Wire the guards** in `InitAsync`, after `EnsureCoreWebView2Async` and before `_ready = true`:

```csharp
            var core = _view.CoreWebView2;

            // A link annotation in a hostile PDF must not be able to steer this
            // pane anywhere — the fake in-app password prompt is the attack.
            core.NavigationStarting += (_, e) =>
                e.Cancel = !IsPermittedNavigation(e.Uri, _expected);

            // ...nor open a second window to do it in.
            core.NewWindowRequested += (_, e) => e.Handled = true;

            // ...nor start a download. Nothing in this app's workflow downloads.
            core.DownloadStarting += (_, e) => e.Cancel = true;

            var s = core.Settings;
            s.AreHostObjectsAllowed = false;        // no bridge into the process
            s.IsWebMessageEnabled = false;          // ditto; nothing uses it
            s.AreDefaultScriptDialogsEnabled = false; // no alert()/prompt() as UI
            s.AreDevToolsEnabled = false;
            s.IsPasswordAutosaveEnabled = false;    // this app handles documents,
            s.IsGeneralAutofillEnabled = false;     // never web forms
            s.IsStatusBarEnabled = false;
            // DELIBERATELY LEFT ENABLED (user's decision, security-only change):
            // AreDefaultContextMenusEnabled and AreBrowserAcceleratorKeysEnabled —
            // right-click and Ctrl+P in the pane keep working as today.
```

**`IsScriptEnabled` is deliberately absent — Step 5 decides it by measurement, not here.**

- [ ] **Step 5: Verify-then-decide on `IsScriptEnabled`.** Edge's built-in PDF viewer is itself a web application, so disabling script plausibly breaks rendering entirely. **Measure; do not assume in either direction.**

Set `s.IsScriptEnabled = false`, build, and run the smoke harness, which drives real WebView2 against real PDFs:

```bash
dotnet run --project tools/OrdoSort.Smoke -- demo-full
```

Then launch the real app and **look at the pane**:

```powershell
$p = Start-Process -FilePath "src\OrdoSort.Wpf\bin\Debug\net8.0-windows\OrdoSort.exe" `
                   -ArgumentList "--config","demo-full\config.json" -PassThru
```

Drive it to a document and confirm the PDF actually renders — not merely that navigation completed. `Program.cs:101`'s `CurrentUrl.Contains(...)` check proves navigation, **not rendering**; a blank pane would still pass it. Say explicitly how you confirmed pixels, and stop the process afterwards.

- **If the PDF still renders with script off:** keep `IsScriptEnabled = false`. Record the evidence.
- **If rendering breaks (the expected outcome):** revert it, and leave a comment in the code saying it was measured and why it cannot be disabled — so the next reader does not re-litigate it. Note in the commit body that the navigation allowlist is what carries the security argument, since script stays on.

Either way, **record the measurement in your report.** This step's deliverable is the answer, not a particular setting.

- [ ] **Step 6: Tests pass; full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core 377, Wpf **529** (520 + 9 new cases).

- [ ] **Step 7: Prove the guard's teeth — this is the step that matters.** A passing suite plus a rendering PDF proves the *feature* works, not the *guard*. Delete the `NavigationStarting` handler entirely, rebuild, and run `ViewerNavigationPolicyTests`.

If they still pass, the tests only cover a pure function nobody calls, and the wiring is unprotected — say so plainly and add coverage that fails when the handler is missing (asserting the handler is subscribed after `InitAsync` is acceptable if a real navigation cannot be driven headlessly, provided the test's doc comment says exactly that). Then restore, and separately prove the predicate's own teeth by inverting its final comparison and watching `AnythingTheViewerDidNotAskForIsRefused` fail.

Paste both outputs.

- [ ] **Step 8: Confirm the PDF still displays after all of it.** Re-run `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — must end `All checks passed`, exit 0 — and launch the real app once more to see a document render with the final settings in place. A green unit suite cannot tell you the pane is not blank.

- [ ] **Step 9: Commit** `fix(viewer): the PDF pane only navigates where the app sends it`.

Record in the body: the `IsScriptEnabled` measurement and its outcome; that context menus and accelerator keys were deliberately left enabled per the user's decision; and that blocked navigations are silent by design (the viewer has no status channel, and the pane is a triage surface where a link click is almost always accidental).

---

### Task 2: Gate and record

- [ ] **Step 1: Release build and full suites.**

```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

Record both totals. Floor: Core 377, Wpf 529.

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Launch sanity, with eyes on the pane.** Debug build, `--config demo-full\config.json`, drive to a document, confirm it renders, `Stop-Process`, confirm none remains. **Also confirm the deliberately-kept behaviour still works: right-click in the pane still opens a menu.** That is the user's stated requirement and no unit test covers it.

- [ ] **Step 4: Update the audit document.** In `docs/superpowers/audits/2026-08-04-full-audit.md`, mark finding **4.1** fixed with the commit SHA, in the same style the already-fixed findings use. State what was done and — if script could not be disabled — that the navigation allowlist carries the security argument. Correct the "What to fix, in order" list. Commit `docs: mark the WebView2 hardening done`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Harden the viewer | sonnet (measurement + WebView2 judgement) | sonnet |
| 2 Gate | sonnet | — |
