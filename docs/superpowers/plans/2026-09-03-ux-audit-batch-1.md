# UX audit batch 1 — the loop and the one-liners — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the fifteen findings in the UX audit's first batch — UX-01, UX-02, UX-03, UX-07, UX-06, UX-05, UX-19, UX-22, UX-20, UX-31, UX-32, UX-35, UX-37, UX-38, UX-36 — each as one small commit carrying a regression test that fails on today's code.

**Architecture:** Every fix is local to one view model or one window and follows a pattern the codebase already has next door (an `IsBusy` flag, an `IsDefault` attribute, a `Loaded` focus call, a `PreviewKeyDown` branch). No new abstractions. Tests use the existing doubles — `InlineWorkScheduler`, `ControlledWorkScheduler`, `FakeDialogs`, `ShellFixture`, `TempDir` — and the shared STA fixture (`HighlightContrastFixture`) for anything that builds real XAML.

**Tech Stack:** C# / .NET 8, WPF, xunit. Tests live in `tests/OrdoSort.Wpf.Tests`.

**Spec:** `docs/superpowers/audits/2026-09-03-ux-audit.md` (findings, evidence and the fix each asks for). The audit is the authority; this plan turns its fixes into steps.

## Global Constraints

- **Branch:** `fix/ux-audit-2026-09-03` (already checked out). One commit per task, message in the house style `fix(<area>): <why, lowercase>`, ending with the two trailer lines below.
- **Commit trailer (verbatim, last two lines of every commit message):**
  ```
  Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL
  ```
- **C# guidelines (repo CLAUDE.md):** `///` XML doc comments on every new public or internal member; private fields prefixed `_`; comments explain *why*, not what; no APIs you have not seen in this codebase or the BCL.
- **Test discipline:** every fix ships with a test that FAILS before the fix (a compile error counts) and PASSES after. Run the failing step and read the output before implementing. Tests are hermetic: temp folders, no sleeps except through the existing `WaitFor` polling helpers already used in the same test file.
- **Run only the tests you touched:** from `S:\OrdoSort`, `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~<TestClassName>"`. The full WPF suite can take 40+ minutes and is run once by the controller at the end, not per task.
- **Read the `Passed!` line and its count.** On this machine Smart App Control sometimes blocks a freshly built assembly by hash: `dotnet test` then prints `Skipping: … An Application Control policy has blocked this file` and exits 0 having run ZERO tests. If you see that, report it and stop — do not rebuild in a loop.
- **Do not run the app.** If `OrdoSort.exe` is running, build with `-p:BaseOutputPath=bin-agent/`; it was not running when this plan was written.
- **Never leave two ways to do one thing.** When a method is replaced (Task 6's `ClaimNumbers`, Task 15's `Ask` overload), remove the old one and fix its callers.
- **x:Name fields are reachable from tests** (the test project has `InternalsVisibleTo`); `PasswordWindowTests` reads `w.OpenButton` and `w.PwBox` that way.
- **STA pattern for window tests** (copy exactly): the class carries `[Collection(HighlightContrastTests.Name)]`, takes `HighlightContrastFixture fx` in its constructor, wraps the body in `_fx.Invoke(() => { … })`, calls `ThemeManager.Apply(_fx.App, dark: false)` first, and shows windows off-screen with `WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false`, then `Show()` and `UpdateLayout()`, closing in `finally`.

---

## File map

| File | Task | Change |
|---|---|---|
| `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` | 1 | `LoadCurrentAsync` keeps the typed name on a same-document reload |
| `tests/OrdoSort.Wpf.Tests/FilingLoopTests.cs` | 1 | two facts |
| `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs` | 2, 12 | `NextNeedingName(string?)`; trim Prefix/Suffix where the operation is built |
| `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml(.cs)` | 2, 11 | identity navigation; `x:Name="NextStrayButton"`; Delete branch |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` | 2, 12 | facts |
| `tests/OrdoSort.Wpf.Tests/BulkRenameSortedNavigationTests.cs` | 2 | new, STA |
| `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml(.cs)` | 3 | window `KeyDown` handler |
| `tests/OrdoSort.Wpf.Tests/SettingsEnterKeyTests.cs` | 3 | new, STA |
| `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` | 4 | `IsBusy` setter `protected` |
| `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` | 4 | `ZipAsync` busy + status |
| `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs` | 4 | fact |
| `src/OrdoSort.Wpf/ViewModels/HistoryViewModel.cs` | 5 | `IsBusy`; gate Show all |
| `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml(.cs)` | 5, 13 | Loading text; `FindBox` + focus |
| `tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs` | 5 | fact |
| `tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs` | 13 | fact |
| `src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs` | 6 | `PrintAsync`, `IsPrinting` |
| `tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs` | 6 | fact |
| `src/OrdoSort.Wpf/Views/ReadyView.xaml`, `Views/DoneView.xaml` | 7 | `IsDefault` |
| `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml`, `MergePdfsWindow.xaml`, `MatchMergeWindow.xaml` | 8 | `IsDefault` |
| `tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs` | 7, 8 | new, STA |
| `src/OrdoSort.Wpf/Windows/MessageWindow.xaml.cs` | 9 | copy feedback |
| `tests/OrdoSort.Wpf.Tests/MessageWindowCopyTests.cs` | 9 | new, STA |
| `src/OrdoSort.Wpf/ViewModels/ListReformatViewModel.cs`, `Windows/ListReformatWindow.xaml` | 10 | `SpaceAfterApplies` |
| `tests/OrdoSort.Wpf.Tests/ListReformatViewModelTests.cs` | 10 | fact |
| `src/OrdoSort.Wpf/Windows/{PageCounts,Unlock,ZipTools,MergePdfs,MatchMerge}Window.xaml(.cs)` | 11 | Delete key |
| `tests/OrdoSort.Wpf.Tests/DeleteKeyTests.cs` | 11 | new, STA + lint |
| `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs` | 14 | trim the filter |
| `tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs` | 14 | fact |
| `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml.cs`, `Services/DialogService.cs` | 15 | Show toggle carried per run |
| `tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs` | 15 | facts |

---

### Task 1: UX-01 — a refused filing keeps the typed name

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` — `LoadCurrentAsync` (around line 1350), `ShowDone` (around line 1370), and every `Screen = Screen.Ready` assignment
- Test: `tests/OrdoSort.Wpf.Tests/FilingLoopTests.cs`

**Interfaces:**
- Consumes: `ShellFixture` (`fx.Shell`, `fx.Dialogs.Warnings`, `fx.Inbox`), `FilingLoopTests.Started(params string[] files)` (lines 11-19: adds the files, `Initialize()`, `StartProcessing()`), `ShellViewModel.OnRouteAsync(int)` (internal, line 1392), `TypedName`, `CurrentFilename`.
- Produces: nothing new; behaviour only.

Background: `LoadCurrentAsync` (line 1350) resets `_typedName = ""` unconditionally. `OnRouteAsync`'s `CommitError` catch (line 1436) and `OnSkipAsync`'s (line 1471, falling through to line 1477) both reload through it while the session still holds the same document. A colon in the name goes `Naming.RejectIllegal` → `ArgumentException` → `CommitError` (`Commit.cs:103`), and the route buttons are gated on route validity only, so the path is reachable.

- [ ] **Step 1: Write the failing test**

Add to `FilingLoopTests.cs`, after `IllegalNameWarnsInThePreviewBeforeAnyButton` (line 202):

```csharp
    /// <summary>UX-01: a filing the app refuses (here an illegal name — a
    /// colon — that Naming.RejectIllegal turns into a CommitError) used to
    /// come back with an empty name box, because the reload after the
    /// warning went through LoadCurrentAsync's unconditional reset. The
    /// document has not moved and the user is about to fix the name, not
    /// retype it, so the same document keeps what was typed.</summary>
    [Fact]
    public async Task ARefusedFilingKeepsTheTypedName()
    {
        using var fx = Started("20240115--111111.pdf");
        fx.Shell.TypedName = "A:B";

        await fx.Shell.OnRouteAsync(0);

        Assert.Single(fx.Dialogs.Warnings);
        Assert.Equal("A:B", fx.Shell.TypedName);
        Assert.Equal("20240115--111111.pdf", fx.Shell.CurrentFilename);
        Assert.Single(Directory.GetFiles(fx.Inbox));   // nothing moved
    }

    /// <summary>The other direction, so the same-document guard added for
    /// UX-01 cannot leak one document's name onto the next: a filing that
    /// succeeds still opens the next document with an empty box. (Passes
    /// before the fix too; kept because the fix touches this branch.)</summary>
    [Fact]
    public async Task TheNextDocumentStillStartsWithAnEmptyName()
    {
        using var fx = Started("20240115--111111.pdf", "20240115--222222.pdf");
        fx.Shell.TypedName = "SMITH JOHN";

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal("", fx.Shell.TypedName);
        Assert.Equal("20240115--222222.pdf", fx.Shell.CurrentFilename);
    }
```

- [ ] **Step 2: Run the tests to verify the first fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~FilingLoopTests"`
Expected: `ARefusedFilingKeepsTheTypedName` FAILS on `Assert.Equal("A:B", fx.Shell.TypedName)` (actual `""`); the other facts in the class pass.

- [ ] **Step 3: Implement**

In `ShellViewModel.cs`, add a field beside `_session` (line 48):

```csharp
    /// <summary>The document LoadCurrentAsync last put on screen. A reload of
    /// the SAME path — a filing that was refused, a set-aside that failed —
    /// keeps what was typed (UX-01); only a new path starts from an empty
    /// box. Cleared whenever the session ends or returns to Ready, so a
    /// later session never inherits a name typed in an earlier one.</summary>
    private string? _loadedPath;
```

Replace the body of `LoadCurrentAsync` (from `var path = _session.Current;` to `RequestNameFocus?.Invoke();`) with:

```csharp
        var path = _session.Current;
        if (path is null) { ShowDone(); return; }
        RaiseProgress();
        CurrentFilename = Path.GetFileName(path);
        if (path != _loadedPath)
        {
            _loadedPath = path;
            // the FIELD, not the property — so the setter's bookkeeping is skipped
            // and the frozen suggestion walk has to be cleared by hand, or the
            // next ↓ would offer this document the previous one's names
            _typedName = "";
            ResetCycle();
            Raise(nameof(TypedName));
        }
        RefreshSuggestions();
        UpdatePreview();
        RaiseUndoState();
        await _viewer.ShowAsync(path);
        RequestNameFocus?.Invoke();
```

In `ShowDone()` add `_loadedPath = null;` as the first line. Then grep `ShellViewModel.cs` for `Screen = Screen.Ready` and add `_loadedPath = null;` immediately before each assignment (there are one or two: the stop/return-to-Ready path and possibly initialisation).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~FilingLoopTests"`
Expected: all pass; read the `Passed!` count.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs tests/OrdoSort.Wpf.Tests/FilingLoopTests.cs
git commit -m "fix(shell): a refused filing keeps the name the user typed

The reload after a CommitError went through LoadCurrentAsync's
unconditional reset, so an illegal name or an unreachable destination
handed the user an empty box for the same document (UX-01). The reset
now runs only when the document changes.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 2: UX-02 — "next file needing a name" navigates by identity

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs` — beside `IndexOfNextNeedingName` (line 457)
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs` — `OnJumpToNextStray` (line 60) and `OnCellEditEnding` (line 116)
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml` — the button at line 157
- Test: `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` (VM fact), `tests/OrdoSort.Wpf.Tests/BulkRenameSortedNavigationTests.cs` (new, STA)

**Interfaces:**
- Consumes: `RenameRow.Source` (string, the full path — unique per row), `RenameRow.NeedsName`, `ObservableCollection<RenameRow> Preview`, `IndexOfNextNeedingName(int after)` (kept), `BulkRenameWindow.BeginEdit(RenameRow)`, `ToolViewModelTests.BatchWithStrays()` (line 1168) and its rows: index 0 SMITH (right), 1 "oddball one" (stray), 2 GARCIA (right), 3 "oddball two" (stray).
- Produces: `public RenameRow? NextNeedingName(string? afterSource)`; `x:Name="NextStrayButton"` on the jump button.

Background: `PreviewGrid` is sortable; the window feeds a view-order index (`PreviewGrid.Items.IndexOf`) into a walk over `Preview`'s insertion order and indexes the view again with the result. WPF sorts the view, never the source collection, so after a sort the row that opens is arbitrary.

- [ ] **Step 1: Write the failing VM test**

Add to `ToolViewModelTests.cs` after `NextStrayWrapsPastTheRowsThatAreAlreadyRight` (line 1218):

```csharp
    /// <summary>UX-02: the window's grid can be sorted, so "the next stray
    /// after this row" has to be answered by identity (the row's source
    /// path), never by a position that means one thing in the view and
    /// another in Preview.</summary>
    [Fact]
    public async Task NextStrayIsFoundByIdentityNotPosition()
    {
        var vm = await BatchWithStrays();

        Assert.Same(vm.Preview[1], vm.NextNeedingName(null));                   // from the top
        Assert.Same(vm.Preview[3], vm.NextNeedingName(vm.Preview[1].Source));  // skips row 2
        Assert.Same(vm.Preview[1], vm.NextNeedingName(vm.Preview[3].Source));  // wraps
        Assert.Same(vm.Preview[1], vm.NextNeedingName("not-a-row"));           // unknown = from the top
    }
```

- [ ] **Step 2: Write the failing window test**

Create `tests/OrdoSort.Wpf.Tests/BulkRenameSortedNavigationTests.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-02: sorting the preview grid used to desynchronise "next file
/// needing a name". The window read the selected row's VIEW index, walked
/// Preview's INSERTION order from there, and indexed the view again with the
/// answer — two index spaces that agree only while the grid is unsorted.
/// This drives the real window with a reversed view and asserts the row that
/// opens for editing is the one that actually needs a name.</summary>
[Collection(HighlightContrastTests.Name)]
public class BulkRenameSortedNavigationTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_sortednav_" + Guid.NewGuid());

    public BulkRenameSortedNavigationTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "pdf");
        return path;
    }

    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void NextStrayIgnoresTheGridsSortOrder()
    {
        // Built OUTSIDE the STA call so WaitFor can poll without starving a
        // dispatcher; the window is built inside it.
        var vm = new BulkRenameViewModel();
        vm.AddFilesAsync(new[]
        {
            Touch("SMITH_JOHN_5_5_2024_ACME_RECORDS_1-1__08_02_24_1019_X.pdf"),
            Touch("oddball one.pdf"),
            Touch("GARCIA_MARIA_8_5_2024_ACME_RECORDS_2-1__08_02_24_1020_X.pdf"),
            Touch("oddball two.pdf"),
        }).GetAwaiter().GetResult();
        vm.ReceivedDate = new DateTime(2024, 8, 2);
        vm.ReviewMode = true;
        WaitFor(() => vm.NeedsNameCount == 2, "the batch's preview should settle first");

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var win = new BulkRenameWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowActivated = false,
            };
            try
            {
                win.Show();
                win.UpdateLayout();

                // Reverse the view: insertion order [0,1,2,3] shows as [3,2,1,0].
                var view = (ListCollectionView)CollectionViewSource.GetDefaultView(vm.Preview);
                view.CustomSort = Comparer<object>.Create((a, b) =>
                    vm.Preview.IndexOf((RenameRow)b).CompareTo(vm.Preview.IndexOf((RenameRow)a)));
                win.PreviewGrid.UpdateLayout();

                // From the first stray, the next stray is the other one — the
                // pre-fix index arithmetic lands on SMITH instead.
                win.PreviewGrid.SelectedItem = vm.Preview[1];
                win.NextStrayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                var opened = Assert.IsType<RenameRow>(win.PreviewGrid.CurrentCell.Item);
                Assert.True(opened.NeedsName, $"opened {Path.GetFileName(opened.Source)}, which does not need a name");
                Assert.Same(vm.Preview[3], opened);
            }
            finally
            {
                try { win.Close(); } catch { /* best effort */ }
            }
        });
    }
}
```

- [ ] **Step 3: Run both to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~NextStrayIsFoundByIdentityNotPosition|FullyQualifiedName~BulkRenameSortedNavigationTests"`
Expected: build FAILS — `NextNeedingName` and `NextStrayButton` do not exist. (If you want to see the window test fail on the assertion alone, temporarily add only the XAML name first; not required.)

- [ ] **Step 4: Implement the view model method**

In `BulkRenameViewModel.cs`, directly after `IndexOfNextNeedingName` (ends line 465):

```csharp
    /// <summary>The next row still waiting on a name after the row whose
    /// source path this is, wrapping; from the top when the path is null or
    /// no longer in the batch. Identity, not position: the grid the window
    /// shows may be sorted, and a view index fed into Preview's insertion
    /// order opens the wrong row (UX-02). Null when nothing needs a name.</summary>
    public RenameRow? NextNeedingName(string? afterSource)
    {
        var after = -1;
        if (afterSource is not null)
        {
            for (var i = 0; i < Preview.Count; i++)
            {
                if (Preview[i].Source == afterSource) { after = i; break; }
            }
        }
        var next = IndexOfNextNeedingName(after);
        return next >= 0 ? Preview[next] : null;
    }
```

- [ ] **Step 5: Implement the window side**

In `BulkRenameWindow.xaml`, add `x:Name="NextStrayButton"` to the `<Button DockPanel.Dock="Right" Click="OnJumpToNextStray"` element at line 157.

In `BulkRenameWindow.xaml.cs`, replace `OnJumpToNextStray` (lines 60-66) with:

```csharp
    private void OnJumpToNextStray(object sender, RoutedEventArgs e)
    {
        // By source path, not by grid position: the grid may be sorted
        // (UX-02), and only the view model's insertion order is stable.
        var next = _vm.NextNeedingName((PreviewGrid.SelectedItem as RenameRow)?.Source);
        if (next is not null) BeginEdit(next);
    }
```

In `OnCellEditEnding`, delete the line `var wasAt = PreviewGrid.Items.IndexOf(row);` and replace the two lines

```csharp
            var next = _vm.IndexOfNextNeedingName(wasAt);
            if (next >= 0 && PreviewGrid.Items[next] is RenameRow target)
                BeginEdit(target);
```

with

```csharp
            var next = _vm.NextNeedingName(row.Source);
            if (next is not null) BeginEdit(next);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ToolViewModelTests|FullyQualifiedName~BulkRenameSortedNavigationTests"`
Expected: all pass, including the three existing `IndexOfNextNeedingName` facts (the method is kept).

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs tests/OrdoSort.Wpf.Tests/BulkRenameSortedNavigationTests.cs
git commit -m "fix(bulk-rename): the next stray is found by identity, so a sorted grid cannot misdirect it

The window mixed the grid's view index with Preview's insertion order;
after any header click, Enter or F2 opened an arbitrary row and seeded
it (UX-02). Navigation now keys on the row's source path.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 3: UX-03 — Enter in a Settings text box stops reaching OK

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` — the `<Window …>` element (add `KeyDown="OnWindowKeyDown"`)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs` — new handler
- Test: `tests/OrdoSort.Wpf.Tests/SettingsEnterKeyTests.cs` (new, STA)

**Interfaces:**
- Consumes: the SettingsViewModel/SettingsWindow construction in `tests/OrdoSort.Wpf.Tests/FieldClippingTests.cs:84-87` (copy the `new SettingsViewModel(cfg, new FakeDialogs(), …)` call verbatim from there); the Inbox `TextBox` (`AutomationProperties.Name="Inbox folder"`, SettingsWindow.xaml:241); the alert-term `TextBox` (`AutomationProperties.Name="New alert term"`, line 1331, whose own `KeyBinding Key="Return"` runs `AddAlertCommand`); `SettingsViewModel.NewAlertText`.
- Produces: `SettingsWindow.OnWindowKeyDown`.

Background: OK is `IsDefault` (line 156). WPF fires the default button from an UNHANDLED `KeyDown` for Enter. A text box that handles Enter itself (the hotkey capture at 656, the section editor at 995, the alert-term KeyBinding at 1334) marks the event handled, so the default button never fires for them — that is the mechanism `AcceptsReturn` text boxes use. The fix gives every other single-line text box the same protection at the window level, on the *bubbling* event, so a box that already claimed Enter is never touched. Buttons, checkboxes and combos keep Enter-means-OK.

- [ ] **Step 1: Write the failing test**

Create `tests/OrdoSort.Wpf.Tests/SettingsEnterKeyTests.cs`:

```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-03: OK carries IsDefault, and WPF fires a default button from
/// any Enter that reaches the window UNHANDLED. Only three of Settings'
/// text boxes claimed Enter themselves, so Enter in the inbox path, a
/// route's folder, poll seconds and the rest validated all seven tabs and
/// closed the dialog mid-edit. The window now handles an Enter that
/// bubbles out of a single-line text box; a box with its own Enter
/// behaviour — the alert-term KeyBinding here — still gets it first.</summary>
[Collection(HighlightContrastTests.Name)]
public class SettingsEnterKeyTests
{
    private readonly HighlightContrastFixture _fx;
    public SettingsEnterKeyTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static TextBox Named(Window win, string automationName) =>
        Descendants<TextBox>(win).Single(t => AutomationProperties.GetName(t) == automationName);

    private static KeyEventArgs EnterKeyDown(UIElement target) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, Key.Return)
        { RoutedEvent = UIElement.KeyDownEvent };

    private static (SettingsWindow win, SettingsViewModel vm) Build()
    {
        // The same construction FieldClippingTests uses: fake folder checks,
        // an inline scheduler, one route so every tab has content.
        var cfg = new Config
        {
            Inbox = @"C:\inbox", Deferred = @"C:\deferred",
            Routes = { new Route { Label = "Invoices", Path = @"C:\routes\invoices", Color = "#2e7d32" } },
        };
        var vm = new SettingsViewModel(cfg, new FakeDialogs(),
            directoryExists: _ => true, fileExists: _ => true,
            scheduler: new InlineWorkScheduler());
        var win = new SettingsWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        return (win, vm);
    }

    [Fact]
    public void EnterInThePathBoxIsSwallowedBeforeItReachesOk() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, _) = Build();
        try
        {
            var args = EnterKeyDown(Named(win, "Inbox folder"));
            Named(win, "Inbox folder").RaiseEvent(args);

            Assert.True(args.Handled, "an Enter that bubbles out of the Inbox box must be handled, or IsDefault fires OK");
            Assert.True(win.IsVisible);
        }
        finally { try { win.Close(); } catch { /* best effort */ } }
    });

    [Fact]
    public void EnterInTheAlertTermBoxStillAddsTheTerm() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, vm) = Build();
        try
        {
            vm.NewAlertText = "urgent";
            var box = Named(win, "New alert term");
            box.RaiseEvent(EnterKeyDown(box));

            // AddAlertCommand (SettingsViewModel.cs:743-746) runs from the box's
            // own KeyBinding and clears NewAlertText as its first act — the
            // only path that empties the box.
            Assert.Equal("", vm.NewAlertText);
        }
        finally { try { win.Close(); } catch { /* best effort */ } }
    });
}
```

- [ ] **Step 2: Run to verify the first test fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~SettingsEnterKeyTests"`
Expected: `EnterInThePathBoxIsSwallowedBeforeItReachesOk` FAILS on `args.Handled` (false today). `EnterInTheAlertTermBoxStillAddsTheTerm` passes today (it guards the exclusion).

- [ ] **Step 3: Implement**

In `SettingsWindow.xaml`, add `KeyDown="OnWindowKeyDown"` to the root `<Window …>` element's attributes.

In `SettingsWindow.xaml.cs`, add (needs `using System.Windows.Controls;` and `using System.Windows.Input;` if absent):

```csharp
    /// <summary>Enter in a single-line text box means "done with this
    /// field", not "OK the whole dialog": through OK's IsDefault it used to
    /// validate all seven tabs mid-edit and, when they happened to be valid,
    /// save and close with edits the user had not finished (UX-03). This
    /// runs on the BUBBLING event, so a box that handles Enter itself — the
    /// hotkey capture, the section editor, the alert-term KeyBinding — has
    /// already claimed it and never arrives here; only an unclaimed Enter
    /// is stopped from reaching the default button. Buttons, checkboxes and
    /// combos still answer Enter with OK, as every Windows dialog does.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && e.OriginalSource is TextBox { AcceptsReturn: false })
            e.Handled = true;
    }
```

- [ ] **Step 4: Run to verify both pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~SettingsEnterKeyTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/SettingsWindow.xaml src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs tests/OrdoSort.Wpf.Tests/SettingsEnterKeyTests.cs
git commit -m "fix(settings): enter in a text box no longer commits all seven tabs

Only three text boxes claimed Enter for themselves; everywhere else it
reached OK's IsDefault and validated or saved the whole dialog mid-edit
(UX-03). An Enter that bubbles out of a single-line box is now handled
at the window, so the default button fires only from controls that do
not take text.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 4: UX-07 — building a zip says so while it runs

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs:359` — `IsBusy` setter `private set` → `protected set`
- Modify: `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` — `ZipAsync` (lines 88-97)
- Test: `tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs`

**Interfaces:**
- Consumes: `ZipExtractViewModel(IDialogService, IReadOnlyList<string>, IWorkScheduler?, SynchronizationContext?, zipper: Func<IReadOnlyList<string>, string?, Zipper.ZipResult>?)`; `Zipper.ZipResult(string Status, string? Output, string Message = "")`; `TempDir` (`.Path`, `.File(name)` creates the file); `vm.AddPaths(IEnumerable<string>)` (async; completes inline with `InlineWorkScheduler`); `vm.Status`, `vm.IsBusy`.
- Produces: `IsBusy` is settable by subclasses.

- [ ] **Step 1: Write the failing test**

Add to `ZipExtractViewModelTests.cs` (inside the test class, near the other `ZipAsync` facts):

```csharp
    /// <summary>UX-07: Zip wrote Status only when the archive was finished
    /// and never raised IsBusy, so compressing a large folder was silent
    /// with every button still live — while Extract, two methods down, says
    /// "Extracting n of N…" through RunBatchAsync. The zipper stub reads the
    /// view model DURING the run, which the inline scheduler makes
    /// synchronous.</summary>
    [Fact]
    public async Task ZipSaysItIsWorkingWhileTheArchiveIsBuilt()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt");
        string? statusDuring = null;
        bool? busyDuring = null;
        ZipExtractViewModel? vm = null;
        vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipper: (_, _) =>
            {
                statusDuring = vm!.Status;
                busyDuring = vm.IsBusy;
                return new Zipper.ZipResult("ok", Path.Combine(dir.Path, "a.zip"));
            });
        await vm.AddPaths(new[] { file });

        await vm.ZipAsync(null);

        Assert.Equal("Zipping 1 item…", statusDuring);
        Assert.True(busyDuring);
        Assert.False(vm.IsBusy);
        Assert.StartsWith("Created a.zip", vm.Status);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipSaysItIsWorkingWhileTheArchiveIsBuilt"`
Expected: FAIL — `statusDuring` is `""` (or whatever Status held before), `busyDuring` false.

- [ ] **Step 3: Implement**

In `ZipListViewModel.cs` line 359 change `private set` to `protected set` on `IsBusy`, and extend its doc comment with one sentence: "Protected so a subclass operation that does not go through RunBatchAsync — ZipAsync — can still declare itself busy."

In `ZipExtractViewModel.cs` replace `ZipAsync`'s body:

```csharp
    internal async Task ZipAsync(string? outputPath)
    {
        if (Rows.Count == 0) return;
        var paths = Rows.Select(r => r.Path).ToList();
        var itemCount = paths.Count;
        // Busy and a status line BEFORE the work, as RunBatchAsync does for
        // Extract: a large folder takes seconds to compress, and the window
        // used to give no sign of it (UX-07). IsBusy also parks Remove
        // selected (IsIdle) for the duration.
        IsBusy = true;
        Status = $"Zipping {itemCount} item{(itemCount == 1 ? "" : "s")}…";
        try
        {
            var result = await Scheduler.Run(() => _zipper(paths, outputPath));
            RunOnUi(() => Status = result.Status == "ok"
                ? $"Created {System.IO.Path.GetFileName(result.Output!)} · {itemCount} item{(itemCount == 1 ? "" : "s")}"
                : result.Message);
        }
        finally
        {
            RunOnUi(() => IsBusy = false);
        }
    }
```

Check how `RunBatchAsync` (ZipListViewModel.cs, from line 584) resets `IsBusy` at its end and mirror that exactly (with or without `RunOnUi`) so the two paths agree.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ZipExtractViewModelTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs tests/OrdoSort.Wpf.Tests/ZipExtractViewModelTests.cs
git commit -m "fix(zip): say that an archive is being built while it is

ZipAsync wrote Status only when it was done and never raised IsBusy, so a
large folder compressed in silence with every button live (UX-07).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 5: UX-06 — History shows it is loading and Show all cannot double-fire

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/HistoryViewModel.cs` — `ShowAllCommand` (line 79), `LoadAsync` (line 106)
- Modify: `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml` — after `NoMatchesText` (line 309)
- Test: `tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs`

**Interfaces:**
- Consumes: `ControlledWorkScheduler` (`Queued`, `ReleaseNext()`, `ReleaseAll()`; continuations run synchronously inside Release), `HistoryViewModelTests._history`, `_dialogs`, `Seed(int)`, `HistoryViewModel(History, IDialogService, IWorkScheduler?)`, `HistoryViewModel.InitialLoad` (500).
- Produces: `public bool IsBusy { get; }`.

- [ ] **Step 1: Write the failing test**

Add to `HistoryViewModelTests.cs` after `ShowAllLoadsEverything` (line 47):

```csharp
    /// <summary>UX-06: a query on a share can take seconds, and the window
    /// showed neither rows nor an empty-state line while it ran; worse,
    /// Show all was gated on "not yet shown all" only, so a second click
    /// during the slow load started a second full query. IsBusy now covers
    /// both: the window binds a Loading line to it and Show all is parked.
    /// ControlledWorkScheduler holds each load in flight until released.</summary>
    [Fact]
    public void ShowAllIsParkedWhileALoadIsInFlight()
    {
        Seed(600);
        var scheduler = new ControlledWorkScheduler();
        var vm = new HistoryViewModel(_history, _dialogs, scheduler);   // the initial load is queued

        Assert.True(vm.IsBusy);
        Assert.False(vm.ShowAllCommand.CanExecute(null));

        scheduler.ReleaseNext();
        Assert.False(vm.IsBusy);
        Assert.Equal(HistoryViewModel.InitialLoad, vm.Rows.Count);
        Assert.True(vm.ShowAllCommand.CanExecute(null));

        vm.ShowAllCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ShowAllCommand.CanExecute(null));
        Assert.Equal(HistoryViewModel.InitialLoad, vm.Rows.Count);   // stale rows stay visible

        scheduler.ReleaseAll();
        Assert.False(vm.IsBusy);
        Assert.Equal(600, vm.Rows.Count);
        Assert.False(vm.ShowAllCommand.CanExecute(null));   // everything is shown now
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ShowAllIsParkedWhileALoadIsInFlight"`
Expected: build FAILS — `IsBusy` does not exist.

- [ ] **Step 3: Implement the view model**

In `HistoryViewModel.cs`:

Change line 79 to
```csharp
        ShowAllCommand = new RelayCommand(() => _ = LoadAsync(all: true), () => !_showedAll && !IsBusy);
```

Add after the `NoMatches` property (line 102):
```csharp
    /// <summary>True from the moment a load is handed to the scheduler until
    /// its rows have landed. The window shows a Loading line on it, and Show
    /// all is parked on it — a second click mid-load used to start a second
    /// full query (UX-06).</summary>
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ShowAllCommand.RaiseCanExecuteChanged();
        }
    }
```

Wrap the body of `LoadAsync` so it begins with `IsBusy = true;` and the scheduler call plus the row rebuild sit in a `try` whose `finally` sets `IsBusy = false;`:

```csharp
    internal async Task LoadAsync(bool all)
    {
        var history = _history;
        IsBusy = true;
        try
        {
            var (rows, total) = await _scheduler.Run(() =>
            {
                var loaded = (all ? history.Rows() : history.Rows(InitialLoad))
                    .Select(HistoryRow.From).ToList();
                return (loaded, (long)history.Count());
            });
            _total = total;
            _showedAll = all || rows.Count < InitialLoad;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }
        finally
        {
            IsBusy = false;
        }
        ShowAllCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanShowAll));
        ApplyFilter();
    }
```

Note `IsBusy`'s setter calls `ShowAllCommand.RaiseCanExecuteChanged()`, and the constructor assigns `ShowAllCommand` before it calls `LoadAsync` — keep that order.

- [ ] **Step 4: Implement the window**

In `HistoryWindow.xaml`, after the `NoMatchesText` TextBlock (ends line 309) add:

```xml
            <!-- UX-06: the load can take seconds on a share; without this the
                 grid is simply empty while it runs, which reads as "no
                 history". Show all leaves the old rows in place underneath. -->
            <TextBlock x:Name="LoadingText"
                       Text="Loading…"
                       Style="{StaticResource EmptyStateText}">
                <TextBlock.Visibility>
                    <Binding Path="IsBusy" Converter="{StaticResource BoolToVis}" />
                </TextBlock.Visibility>
            </TextBlock>
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~HistoryViewModelTests|FullyQualifiedName~HistoryWindowXamlTests"`
Expected: all pass (the XAML tests confirm the window still builds).

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/HistoryViewModel.cs src/OrdoSort.Wpf/Windows/HistoryWindow.xaml tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs
git commit -m "fix(history): show that a load is running, and park Show all until it lands

A query on a share can take seconds; the grid was blank meanwhile and a
second Show all click started a second full query (UX-06).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 6: UX-05 — Print claims its numbers off the UI thread

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs` — `PrintCommand` (line 233), `ClaimNumbers` (line 455, remove), `Print` (line 478)
- Test: `tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs`

**Interfaces:**
- Consumes: `LabelMakerViewModel(Config, string boxLabelsPath, IDialogService, Func<DateTime>? today, Action<string>? openFile, IWorkScheduler? scheduler)`; tests' `PathWith(params LabelClient[])`, `_dialogs`, `_opened`, `Today`; `PrintSheets` hook (`Func<IReadOnlyList<BoxLabels.Item>, string, bool>?`); `ClaimNumbersCore(LabelClientVm, int)` (throws `ConfigException` when the file is busy past its retry window); `SetClaimedNumber`, `RebuildFromClaim`, `BuildBatch()`.
- Produces: `public bool IsPrinting { get; }`, `internal Task PrintAsync()`; `Print()` becomes a fire-and-forget wrapper like `SavePdf()`.

Background: `Print()` calls `ClaimNumbers` synchronously; `BoxLabelStore` retries a contended file for up to 5 s (`DefaultMaxWaitMs`), so the window freezes. `SavePdfAsync` (line 499) already does the claim inside `_scheduler.Run` via `ClaimNumbersCore`.

- [ ] **Step 1: Write the failing test**

Add to `LabelMakerViewModelTests.cs` after `PrintClaimsFromTheFreshFileEvenWhenTheDialogIsThenCancelled`:

```csharp
    /// <summary>UX-05: Print claimed its numbers ON the UI thread, and the
    /// store retries a contended file for up to five seconds, so the window
    /// froze on its one primary button. The claim now runs through the
    /// scheduler like SavePdf's; while it is out, PrintCommand is parked so a
    /// second click cannot claim a second range.</summary>
    [Fact]
    public void PrintClaimsOffTheUiThreadAndParksItselfMeanwhile()
    {
        var path = PathWith(new LabelClient { Id = "ABCD", DestroyDays = 30, NextNumber = 5 });
        var scheduler = new ControlledWorkScheduler();
        var vm = new LabelMakerViewModel(new Config(), path, _dialogs, () => Today, _opened.Add, scheduler);
        scheduler.ReleaseAll();   // anything the constructor queued
        IReadOnlyList<BoxLabels.Item>? sent = null;
        vm.PrintSheets = (items, _) => { sent = items; return true; };
        var queuedBefore = scheduler.Queued;

        vm.Print();

        Assert.Equal(queuedBefore + 1, scheduler.Queued);   // the claim is in flight, not done
        Assert.Null(sent);
        Assert.True(vm.IsPrinting);
        Assert.False(vm.PrintCommand.CanExecute(null));

        scheduler.ReleaseNext();

        Assert.NotNull(sent);
        Assert.Equal("ABCD00000005", sent![0].Code);
        Assert.False(vm.IsPrinting);
        Assert.True(vm.PrintCommand.CanExecute(null));
        Assert.Equal("15", vm.Selected!.NextNumberText);
        Assert.Empty(_dialogs.Warnings);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PrintClaimsOffTheUiThreadAndParksItselfMeanwhile"`
Expected: build FAILS — `IsPrinting` does not exist.

- [ ] **Step 3: Implement**

In `LabelMakerViewModel.cs`:

Line 233: `PrintCommand = new RelayCommand(Print, () => Selected is not null && !IsPrinting);`

Add near the other properties (after `PrintSheets`, line 242):
```csharp
    /// <summary>True while Print's claim is out with the scheduler. A
    /// contended store file can hold the claim for BoxLabelStore's whole
    /// retry budget, which used to freeze the window (UX-05); this parks
    /// PrintCommand for the duration so a second click cannot claim a
    /// second range.</summary>
    private bool _isPrinting;
    public bool IsPrinting
    {
        get => _isPrinting;
        private set { if (Set(ref _isPrinting, value)) PrintCommand.RaiseCanExecuteChanged(); }
    }
```

Delete `ClaimNumbers` (the method at lines 455-467 and its doc comment at 448-454). First grep `tests/` for `ClaimNumbers(` — if any test calls it, rewrite that test against `PrintAsync`/`SavePdfAsync` behaviour instead.

Replace `Print` (lines 478-495) with:

```csharp
    internal void Print() => _ = PrintAsync();

    internal async Task PrintAsync()
    {
        if (BuildBatch() is not { } b) return;
        if (PrintSheets is null)
        {
            _dialogs.Warn("Printing isn't available here.", "OrdoSort — label maker");
            return;
        }
        // Claim from the fresh file FIRST: several stations may be printing,
        // so the sheets that actually go out must carry the claimed numbers,
        // not whatever was on screen when this window opened. Off the UI
        // thread, as SavePdfAsync already does — the store can wait seconds
        // on a contended file (UX-05).
        long start;
        IsPrinting = true;
        try
        {
            start = await _scheduler.Run(() => ClaimNumbersCore(b.Client, b.Count));
        }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
            return;
        }
        finally
        {
            IsPrinting = false;
        }
        SetClaimedNumber(b.Client, start + b.Count);
        var items = RebuildFromClaim(b, start);
        if (!PrintSheets(items, $"OrdoSort labels {items[0].Code}")) return;   // cancelled
        var sheets = (b.Count + BoxLabels.PerSheet - 1) / BoxLabels.PerSheet;
        Status = $"Sent {b.Count} label{(b.Count == 1 ? "" : "s")} "
            + $"({sheets} sheet{(sheets == 1 ? "" : "s")}) to the printer.";
    }
```

The existing `Print` facts (`PrintSendsTheSheetsAndAdvancesTheNumber`, `PrintClaimsFromTheFreshFileEvenWhenTheDialogIsThenCancelled`, `PrintWithoutAPrinterHookWarns`) build the view model with `InlineWorkScheduler`, whose completed task lets `PrintAsync` run to the end inside `Print()`; they must keep passing unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~LabelMakerViewModelTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs
git commit -m "fix(label-maker): print claims its numbers off the ui thread

The claim can wait five seconds on a contended store file and ran on the
UI thread, freezing the window on its primary button (UX-05). It now
goes through the scheduler as Save PDF's already did, and Print is
parked while it is out.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 7: UX-19 — Enter starts a session on Ready and returns from Done

**Files:**
- Modify: `src/OrdoSort.Wpf/Views/ReadyView.xaml:377` (Start processing), `src/OrdoSort.Wpf/Views/DoneView.xaml:33` (Back to inbox)
- Test: `tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs` (new, STA; Task 8 adds to it)

**Interfaces:**
- Consumes: `ReadyView()`, `DoneView()` (parameterless; `CopyAndTerminologyTests.cs:179` builds `ReadyView` the same way), `AutomationProperties.Name` "Start processing" / "Back to inbox".
- Produces: the test file and its `Descendants<T>` helper, reused by Task 8.

Background: only one of the three screens is `Visibility="Visible"` at a time (`MainWindow.xaml:413-418`); WPF's default-button lookup skips elements that are not visible, so one `IsDefault` per screen cannot collide. Processing handles Enter itself and marks it handled (`ProcessingView.xaml.cs:78-81`), so its screen is unaffected.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs`:

```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Enter reaches the one primary button on a screen or window.
/// UX-19: Ready and Done had none, although Processing makes Enter the
/// commit; a keyboard user had to Tab to Start or Back to inbox. UX-22
/// (below): three batch windows styled a primary button without wiring it
/// to Enter while their siblings did. Read off the LOGICAL tree so no
/// window needs to be shown; a button's Style resolves at parse time.</summary>
[Collection(HighlightContrastTests.Name)]
public class DefaultButtonTests
{
    private readonly HighlightContrastFixture _fx;
    public DefaultButtonTests(HighlightContrastFixture fx) => _fx = fx;

    internal static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var x in LogicalDescendants<T>(d)) yield return x;
        }
    }

    private static Button ByName(DependencyObject root, string automationName) =>
        LogicalDescendants<Button>(root).Single(b => AutomationProperties.GetName(b) == automationName);

    [Fact]
    public void StartProcessingAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var view = new ReadyView();
        Assert.True(ByName(view, "Start processing").IsDefault, "Enter on Ready must start the session");
    });

    [Fact]
    public void BackToInboxAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var view = new DoneView();
        Assert.True(ByName(view, "Back to inbox").IsDefault, "Enter on Done must return to the inbox");
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DefaultButtonTests"`
Expected: both FAIL on `IsDefault` being false.

- [ ] **Step 3: Implement**

`ReadyView.xaml` line 377-379 — add `IsDefault="True"` to the Start button's attributes, with a comment above it:
```xml
        <!-- IsDefault: Processing makes Enter the commit, so Enter on the
             bookends means "go" too (UX-19). Only the visible screen's
             default button is reachable — WPF skips collapsed ones. -->
```
`DoneView.xaml` line 33-35 — add `IsDefault="True"` to the Back to inbox button, with the same one-line comment.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DefaultButtonTests|FullyQualifiedName~CopyAndTerminologyTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Views/ReadyView.xaml src/OrdoSort.Wpf/Views/DoneView.xaml tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs
git commit -m "fix(ui): enter starts a session on ready and returns from done

Processing makes Enter the commit; the two bookend screens ignored it, so
a keyboard user had to Tab to the one button on each (UX-19).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 8: UX-22 — Zip, Merge and Match-and-merge primaries answer Enter

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml:19-24` (Zip), `MergePdfsWindow.xaml:17-22` (Merge), `MatchMergeWindow.xaml:12-24` (Merge)
- Test: `tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs` (from Task 7)

**Interfaces:**
- Consumes: `LogicalDescendants<T>` from Task 7; view-model constructors as the existing window tests build them: `new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler())`; `new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"), pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p))` (`MergePdfsWindowTests.cs:40`); `new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs())` (`AutoFitColumnTests.cs:415`). `PrimaryButton` is an application resource (`Theme/Styles.xaml:1905`). All three primaries already gate `CanExecute` on rows, so `IsDefault` cannot fire on an empty list.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Add to `DefaultButtonTests.cs` (add `using OrdoSort.Core;`, `using OrdoSort.Wpf.ViewModels;`, `using OrdoSort.Wpf.Windows;`):

```csharp
    private static Button ThePrimary(Window win)
    {
        var primary = (Style)win.FindResource("PrimaryButton");
        return Assert.Single(LogicalDescendants<Button>(win).Where(b => ReferenceEquals(b.Style, primary)));
    }

    [Fact]
    public void ZipAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var win = new ZipToolsWindow(new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler()));
        Assert.True(ThePrimary(win).IsDefault);
    });

    [Fact]
    public void MergePdfsAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));
        var win = new MergePdfsWindow(vm);
        Assert.True(ThePrimary(win).IsDefault);
    });

    [Fact]
    public void MatchAndMergeAnswersEnter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var win = new MatchMergeWindow(new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs()));
        Assert.True(ThePrimary(win).IsDefault);
    });
```

If `ReferenceEquals(b.Style, primary)` finds nothing for a window (the style may be wrapped by a `BasedOn`), compare `b.Style == primary || b.Style?.BasedOn == primary` — check what the XAML declares first.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DefaultButtonTests"`
Expected: the three new facts FAIL on `IsDefault`; Task 7's two pass.

- [ ] **Step 3: Implement**

Add `IsDefault="True"` to the three primary buttons: `ZipToolsWindow.xaml` line 19-20 (the `ZipCommand` button), `MergePdfsWindow.xaml` line 17-18 (`MergeCommand`), `MatchMergeWindow.xaml` line 12-13 (`MergeCommand`). One comment on each: `<!-- IsDefault: CanExecute already needs rows, so Enter cannot fire early; the sibling batch windows wire it (UX-22). -->`

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DefaultButtonTests|FullyQualifiedName~ZipToolsWindowTests|FullyQualifiedName~MergePdfsWindowTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml tests/OrdoSort.Wpf.Tests/DefaultButtonTests.cs
git commit -m "fix(tools): the primary button answers enter in zip, merge and match-and-merge

Unlock, Box labels, Print preview and the date prompt wire IsDefault;
these three styled a primary without it (UX-22).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 9: UX-20 — the message dialog's Copy button says what happened

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/MessageWindow.xaml.cs` — `OnCopy` (line 183), a timer field and constructor wiring
- Test: `tests/OrdoSort.Wpf.Tests/MessageWindowCopyTests.cs` (new, STA)

**Interfaces:**
- Consumes: `MessageWindow.Build(Window? owner, string message, string title, MessageKind kind)` (internal, line 113), `MessageKind.Info`, `w.CopyButton` (x:Name), `w.MessageText`.
- Produces: `internal Action<string> SetClipboardText { get; set; }` (defaults to `Clipboard.SetText`).

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/MessageWindowCopyTests.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-20: Copy swallowed both outcomes, so a user could not tell a
/// copy that worked from a clipboard another program was holding — and
/// this button sits on every error dialog in the app. The clipboard call
/// is injected so both branches are driven without touching the real
/// clipboard, which is shared, slow and flaky under a test runner.</summary>
[Collection(HighlightContrastTests.Name)]
public class MessageWindowCopyTests
{
    private readonly HighlightContrastFixture _fx;
    public MessageWindowCopyTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void ASuccessfulCopySaysCopied() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = MessageWindow.Build(null, "Couldn't save C:\\x.csv", "OrdoSort — test", MessageKind.Info);
        string? copied = null;
        w.SetClipboardText = text => copied = text;

        w.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal("Couldn't save C:\\x.csv", copied);
        Assert.Equal("Copied", w.CopyButton.Content);
    });

    [Fact]
    public void ABusyClipboardSaysTryAgain() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = MessageWindow.Build(null, "Something needs your attention.", "OrdoSort — test", MessageKind.Info);
        w.SetClipboardText = _ => throw new COMException("CLIPBRD_E_CANT_OPEN");

        w.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal("Try again", w.CopyButton.Content);
        Assert.Equal("Another program is holding the clipboard.", w.CopyButton.ToolTip);
        Assert.True(w.IsEnabled);   // the dialog itself is untouched
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MessageWindowCopyTests"`
Expected: build FAILS — `SetClipboardText` does not exist.

- [ ] **Step 3: Implement**

In `MessageWindow.xaml.cs` (add `using System.Windows.Threading;` if absent):

Add fields near the top of the class:
```csharp
    /// <summary>The clipboard write, injectable so tests can drive the busy
    /// branch without the real clipboard. Defaults to WPF's own.</summary>
    internal Action<string> SetClipboardText { get; set; } = Clipboard.SetText;

    /// <summary>Puts the Copy label back after the acknowledgment has been
    /// read — two seconds, the feedback reference's figure for a
    /// copy-to-clipboard confirmation.</summary>
    private readonly DispatcherTimer _copyLabelReset = new() { Interval = TimeSpan.FromSeconds(2) };
```

In the constructor (after `InitializeComponent();`), add:
```csharp
        _copyLabelReset.Tick += (_, _) =>
        {
            _copyLabelReset.Stop();
            CopyButton.Content = "Copy";
            CopyButton.ToolTip = null;
        };
```

Replace `OnCopy`:
```csharp
    /// <summary>Puts the message on the clipboard and says so on the button
    /// (UX-20). Clipboard access genuinely fails when another process holds
    /// it open; a dialog that threw while reporting a problem would replace
    /// the message the user came here to read, so the failure is said on the
    /// same button instead — "Try again" is true, the lock is momentary.</summary>
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            SetClipboardText(MessageText.Text);
            CopyButton.Content = "Copied";
            CopyButton.ToolTip = null;
        }
        catch (Exception)
        {
            CopyButton.Content = "Try again";
            CopyButton.ToolTip = "Another program is holding the clipboard.";
        }
        _copyLabelReset.Stop();
        _copyLabelReset.Start();
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~MessageWindowCopyTests|FullyQualifiedName~MessageWindowThemeTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/MessageWindow.xaml.cs tests/OrdoSort.Wpf.Tests/MessageWindowCopyTests.cs
git commit -m "fix(dialogs): the copy button says copied, or that the clipboard is busy

It swallowed both outcomes, on every error dialog in the app, so a user
copied again or pasted nothing (UX-20). Every sibling Copy already
reports.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 10: UX-31 — "Space after separator" disables itself where it has no effect

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ListReformatViewModel.cs` — `Shape` setter (line 50), new property after `IsCustomDelimiter` (line 73)
- Modify: `src/OrdoSort.Wpf/Windows/ListReformatWindow.xaml:45`
- Test: `tests/OrdoSort.Wpf.Tests/ListReformatViewModelTests.cs`

**Interfaces:**
- Consumes: `ListReformat.OutputShape { CommaLine, OnePerLine, CustomDelimiter }`, `new ListReformatViewModel()`.
- Produces: `public bool SpaceAfterApplies`.

- [ ] **Step 1: Write the failing test**

Add to `ListReformatViewModelTests.cs`:

```csharp
    /// <summary>UX-31: the separator switch (ListReformat.cs:108-110) reads
    /// SpaceAfterComma only for the comma and custom shapes; under one item
    /// per line the checkbox did nothing while staying live, unlike the
    /// delimiter box beside it, which already disables itself.</summary>
    [Fact]
    public void SpaceAfterSeparatorAppliesOnlyWhereThereIsASeparator()
    {
        var vm = new ListReformatViewModel();

        vm.Shape = ListReformat.OutputShape.CommaLine;
        Assert.True(vm.SpaceAfterApplies);
        vm.Shape = ListReformat.OutputShape.OnePerLine;
        Assert.False(vm.SpaceAfterApplies);
        vm.Shape = ListReformat.OutputShape.CustomDelimiter;
        Assert.True(vm.SpaceAfterApplies);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~SpaceAfterSeparatorAppliesOnlyWhereThereIsASeparator"`
Expected: build FAILS — `SpaceAfterApplies` does not exist.

- [ ] **Step 3: Implement**

`ListReformatViewModel.cs`: in the `Shape` setter add `Raise(nameof(SpaceAfterApplies));` after `Raise(nameof(IsCustomDelimiter));`. After `IsCustomDelimiter` add:

```csharp
    /// <summary>IsEnabled for "Space after separator" — one item per line has
    /// no separator to put a space after, so the box is dead weight there,
    /// exactly as the delimiter box is under the other two shapes (UX-31).</summary>
    public bool SpaceAfterApplies => Shape != ListReformat.OutputShape.OnePerLine;
```

`ListReformatWindow.xaml` line 45: add `IsEnabled="{Binding SpaceAfterApplies}"` to the `Space after separator` CheckBox.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ListReformatViewModelTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ListReformatViewModel.cs src/OrdoSort.Wpf/Windows/ListReformatWindow.xaml tests/OrdoSort.Wpf.Tests/ListReformatViewModelTests.cs
git commit -m "fix(list-reformat): space-after-separator is disabled where there is no separator

One item per line ignores the flag; the box stayed live and did nothing,
while the delimiter box beside it already disables itself (UX-31).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 11: UX-32 — Delete removes the selected rows in every tool window

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs` — `OnGridKeyDown` (line 78)
- Modify: `src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml` (`CountsGrid`, line 50) + `.xaml.cs`; `UnlockWindow.xaml` (`FileList`, line 73) + `.xaml.cs`; `ZipToolsWindow.xaml` (`ItemsGrid`, line 67) + `.xaml.cs`; `MergePdfsWindow.xaml` (`ItemsGrid`, line 96) + `.xaml.cs`; `MatchMergeWindow.xaml` (`MatchGrid`, line 149) + `.xaml.cs`
- Test: `tests/OrdoSort.Wpf.Tests/DeleteKeyTests.cs` (new: one STA behaviour fact + one XAML lint)

**Interfaces:**
- Consumes: each window's existing `OnRemoveSelected(object sender, RoutedEventArgs e)` (`KeyEventArgs` derives from `RoutedEventArgs`, so it can be forwarded); `BulkRenameViewModel(scheduler:)`, `vm.AddFilesAsync`, `vm.Preview`, `vm.RemoveSelected()`; `win.PreviewGrid`. `PreviewGrid` already has `CanUserDeleteRows="False"` (line 179), so the DataGrid's own delete never competes. The precedent is `FilenameListWindow.xaml.cs:88-95`.
- Produces: `OnGridKeyDown` in five windows; a Delete branch in Bulk rename's.

- [ ] **Step 1: Write the failing tests**

Create `tests/OrdoSort.Wpf.Tests/DeleteKeyTests.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-32: Delete removed a selected row in the Filename list alone;
/// the six other windows with a "Remove selected" button ignored the key.
/// One window is driven for real (Bulk rename, whose grid already routes
/// F2 and Enter through a key handler); the other five are held to the
/// wiring by a lint over their XAML, since each forwards to the same
/// OnRemoveSelected its button already uses.</summary>
[Collection(HighlightContrastTests.Name)]
public class DeleteKeyTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_deletekey_" + Guid.NewGuid());

    public DeleteKeyTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void DeleteRemovesTheSelectedRowInBulkRename()
    {
        var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler());
        var a = Path.Combine(_dir, "a.pdf"); File.WriteAllText(a, "pdf");
        var b = Path.Combine(_dir, "b.pdf"); File.WriteAllText(b, "pdf");
        vm.AddFilesAsync(new[] { a, b }).GetAwaiter().GetResult();
        Assert.Equal(2, vm.Preview.Count);

        _fx.Invoke(() =>
        {
            ThemeManager.Apply(_fx.App, dark: false);
            var win = new BulkRenameWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowActivated = false,
            };
            try
            {
                win.Show();
                win.UpdateLayout();
                win.PreviewGrid.SelectedItem = vm.Preview[0];
                win.PreviewGrid.UpdateLayout();

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(win.PreviewGrid)!, 0, Key.Delete)
                { RoutedEvent = UIElement.PreviewKeyDownEvent };
                win.PreviewGrid.RaiseEvent(args);

                Assert.True(args.Handled);
                Assert.Single(vm.Preview);
                Assert.EndsWith("b.pdf", vm.Preview[0].Source);
            }
            finally { try { win.Close(); } catch { /* best effort */ } }
        });
    }

    public static IEnumerable<object[]> GridsThatMustHandleDelete() => new[]
    {
        new object[] { "PageCountsWindow.xaml", "CountsGrid" },
        new object[] { "UnlockWindow.xaml", "FileList" },
        new object[] { "ZipToolsWindow.xaml", "ItemsGrid" },
        new object[] { "MergePdfsWindow.xaml", "ItemsGrid" },
        new object[] { "MatchMergeWindow.xaml", "MatchGrid" },
    };

    [Theory, MemberData(nameof(GridsThatMustHandleDelete))]
    public void TheListElementRoutesKeysToTheDeleteHandler(string xamlFile, string elementName)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Windows", xamlFile);
        var xaml = File.ReadAllText(path);
        var start = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{xamlFile} has no element named {elementName}");
        var openingTag = xaml.Substring(start, xaml.IndexOf('>', start) - start);

        Assert.Contains("PreviewKeyDown=\"OnGridKeyDown\"", openingTag);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OrdoSort.sln not found above the test output");
    }
}
```

Check `TextWrapCoverageTests.FindRepoRoot` (line 40) for the marker file it walks up to and use the same one if it is not `OrdoSort.sln`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DeleteKeyTests"`
Expected: the behaviour fact FAILS (`args.Handled` false, two rows remain); all five lint cases FAIL (no `PreviewKeyDown` on those elements).

- [ ] **Step 3: Implement Bulk rename**

In `BulkRenameWindow.xaml.cs`, `OnGridKeyDown` (line 78): add a Delete branch before the existing selection guard so it uses the view model's preserved selection (QC-11) exactly as the button does:

```csharp
    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Delete = the Remove selected button, as in the Filename list (UX-32);
        // never while a cell editor is open, where Delete edits text.
        if (!_editing && e.Key == System.Windows.Input.Key.Delete)
        {
            _vm.RemoveSelected();
            e.Handled = true;
            return;
        }
        if (PreviewGrid.SelectedItem is not RenameRow row) return;
        // (existing F2 / Enter branch unchanged)
```

- [ ] **Step 4: Implement the other five**

For each window, add `PreviewKeyDown="OnGridKeyDown"` to the named element's opening tag and this handler to the code-behind (namespaces: `System.Windows.Input` for `KeyEventArgs`/`Key`):

```csharp
    /// <summary>Delete = the Remove selected button (UX-32) — the Filename
    /// list has answered the key since its own audit; the other tool
    /// windows now match it.</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        OnRemoveSelected(sender, e);
        e.Handled = true;
    }
```

- `PageCountsWindow.xaml` line 50 (`CountsGrid`) / `PageCountsWindow.xaml.cs` (`OnRemoveSelected` at line 35)
- `UnlockWindow.xaml` line 73 (`FileList`, a ListBox) / `UnlockWindow.xaml.cs` (line 130)
- `ZipToolsWindow.xaml` line 67 (`ItemsGrid`) / `ZipToolsWindow.xaml.cs` (line 40)
- `MergePdfsWindow.xaml` line 96 (`ItemsGrid`) / `MergePdfsWindow.xaml.cs` (line 84)
- `MatchMergeWindow.xaml` line 149 (`MatchGrid`) / `MatchMergeWindow.xaml.cs` (line 34)

Unlock's `Remove selected` is gated on `IsIdle` in the XAML (see `UnlockWindow.xaml:210`); mirror that gate in the handler: `if (e.Key != Key.Delete || !_vm.IsIdle) return;` — read the exact property name from the button's binding at line 210.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~DeleteKeyTests|FullyQualifiedName~WindowOverflowTests"`
Expected: all pass (WindowOverflowTests builds every touched window and confirms the XAML still loads).

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml.cs src/OrdoSort.Wpf/Windows/UnlockWindow.xaml src/OrdoSort.Wpf/Windows/UnlockWindow.xaml.cs src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml.cs src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml.cs tests/OrdoSort.Wpf.Tests/DeleteKeyTests.cs
git commit -m "fix(tools): delete removes the selected rows in every tool window

Only the Filename list answered the key; the six other windows with a
Remove selected button ignored it (UX-32). Each now forwards Delete to
the handler its button already uses.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 12: UX-35 — Bulk rename's Prefix and Suffix are trimmed where the operation is built

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs:296` (the one place `Prefix: Prefix, Suffix: Suffix` is passed into the operation)
- Test: `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs`

**Interfaces:**
- Consumes: `new BulkRenameViewModel()`, `vm.AddFilesAsync`, `vm.Prefix`, `vm.Suffix`, `vm.Preview[0].NewName`, the file's `Touch` helper and `WaitFor` helper (both already in `ToolViewModelTests`).
- Produces: nothing new.

Why not trim in the setters: the text boxes bind with `UpdateSourceTrigger=PropertyChanged` (`BulkRenameWindow.xaml:118`); trimming the stored value would snap a trailing space back out before the user could type the next word. The raw text stays in the box; the operation gets the trimmed value, as `SetOverride` (line 427) already does for the per-row name.

- [ ] **Step 1: Write the failing test**

Add to `ToolViewModelTests.cs` near `NoStraysMeansNothingToJumpTo`:

```csharp
    /// <summary>UX-35: Prefix and Suffix went into the operation untrimmed
    /// while the per-row override three lines away trims. A prefix pasted
    /// from a spreadsheet cell with a leading space landed in every
    /// filename. The box keeps what was typed (a trailing space is the
    /// start of the next word); only the operation is trimmed.</summary>
    [Fact]
    public async Task PrefixAndSuffixAreTrimmedInTheOperationButNotInTheBox()
    {
        var vm = new BulkRenameViewModel();
        await vm.AddFilesAsync(new[] { Touch("scan_001.pdf") });
        WaitFor(() => vm.Preview.Count == 1, "the initial add's compute should land");

        vm.Prefix = " X";
        vm.Suffix = "Y \n";

        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "Xscan_001Y.pdf",
            "the trimmed prefix and suffix should reach the preview");
        Assert.Equal(" X", vm.Prefix);
        Assert.Equal("Y \n", vm.Suffix);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PrefixAndSuffixAreTrimmedInTheOperationButNotInTheBox"`
Expected: FAILS on the `WaitFor` timeout (the preview reads ` Xscan_001Y \n.pdf` or is refused).

- [ ] **Step 3: Implement**

`BulkRenameViewModel.cs` line 296: change `Prefix: Prefix, Suffix: Suffix,` to `Prefix: Prefix.Trim(), Suffix: Suffix.Trim(),` and add above that line:
```csharp
            // Trimmed here, not in the setters: the boxes update per keystroke,
            // and trimming the stored text would eat the space before the next
            // word. Find/Replace stay as typed — they match existing names (UX-35).
```
Grep the file for any second construction of the operation (`Prefix:`); there is one. If `RenameAsync`'s snapshot (around line 467) builds its own, apply the same trim there.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ToolViewModelTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs
git commit -m "fix(bulk-rename): prefix and suffix are trimmed in the operation

A leading space pasted into the prefix landed in every filename; the
per-row override already trimmed (UX-35). The box keeps what was typed.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 13: UX-37 — History's Find box takes focus when the window opens

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml:10-11` (name the TextBox), `HistoryWindow.xaml.cs` (constructor)
- Test: `tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs`

**Interfaces:**
- Consumes: `HistoryWindowXamlTests.BuildWindow()` / `Cleanup()` (lines 57-83: off-screen, `ShowActivated = false`), `FocusManager.GetFocusedElement(win)`.
- Produces: `x:Name="FindBox"`.

Why logical focus is the assertion: `UIElement.Focus()` sets keyboard focus when the window is active and otherwise records the element as the focus scope's focused element; the test window is shown inactive, so `FocusManager.GetFocusedElement(win)` is what the fix changes — from null to the box. Before the fix nothing sets it. (If this fact passes BEFORE the fix on your machine, the runner activated the window; report that rather than keeping an already-true test.)

- [ ] **Step 1: Write the failing test**

Add to `HistoryWindowXamlTests.cs`:

```csharp
    /// <summary>UX-37: the window's stated job is the Find box, and every
    /// other text-first window focuses its field explicitly (the password
    /// prompt, the date prompt, the name box); History alone left the user
    /// to Tab or click first. Asserted as the focus scope's focused element
    /// because the test window is shown inactive.</summary>
    [Fact]
    public void TheFindBoxHasFocusOnOpen() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            Assert.Same(win.FindBox, FocusManager.GetFocusedElement(win));
        }
        finally { Cleanup(win, history, dbPath); }
    });
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~TheFindBoxHasFocusOnOpen"`
Expected: build FAILS — `FindBox` does not exist.

- [ ] **Step 3: Implement**

`HistoryWindow.xaml` line 10: add `x:Name="FindBox"` to the Find `TextBox`.

`HistoryWindow.xaml.cs` constructor, after `DataGridColumnCap.Track(...)`:
```csharp
        // The window exists to find a filing, so the first keystroke goes
        // to the Find box without a Tab or a click (UX-37) — the same
        // Loaded-focus the password and date prompts already do.
        Loaded += (_, _) => FindBox.Focus();
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~HistoryWindowXamlTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/HistoryWindow.xaml src/OrdoSort.Wpf/Windows/HistoryWindow.xaml.cs tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs
git commit -m "fix(history): the find box takes focus when the window opens

Every other text-first window focuses its field on open; History left
the user to Tab or click before the first keystroke reached the filter
(UX-37).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 14: UX-38 — Filename list's Find trims a pasted term

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs` — `Reproject` (line 441)
- Test: `tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs`

**Interfaces:**
- Consumes: `MakeVm(FakeDialogs)`, `Touch(name)`, `FakeDialogs { NextFolder = _dir }`, `vm.BrowseFolderCommand`, `vm.NameFilter`, `vm.Rows`, `WaitFor` — exactly the shape of the existing filter test at lines 391-397.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Add next to the existing `NameFilter` fact (line ~391):

```csharp
    /// <summary>UX-38: the filter matched case-insensitively but never
    /// trimmed, so a term pasted with a trailing space or newline found
    /// nothing, with no indication why.</summary>
    [Fact]
    public void AFilterPastedWithWhitespaceStillMatches()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("invoice.pdf"); Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.NameFilter = " inv\n";

        Assert.Single(vm.Rows);
        Assert.Equal("invoice.pdf", vm.Rows[0].Name);
        Assert.False(vm.NoMatches);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~AFilterPastedWithWhitespaceStillMatches"`
Expected: FAILS — `Assert.Single` sees zero rows.

- [ ] **Step 3: Implement**

In `Reproject()` replace
```csharp
        if (NameFilter.Length > 0)
            visible = visible.Where(r =>
                r.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase));
```
with
```csharp
        // Trimmed at use, not in the setter: a pasted term often carries a
        // trailing space or newline, and neither is in any filename (UX-38).
        var filter = NameFilter.Trim();
        if (filter.Length > 0)
            visible = visible.Where(r =>
                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~FilenameListViewModelTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs tests/OrdoSort.Wpf.Tests/FilenameListViewModelTests.cs
git commit -m "fix(filename-list): find trims a pasted term

A trailing space or newline in the filter matched nothing, silently
(UX-38).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

### Task 15: UX-36 — the password prompt remembers the Show toggle for the rest of the run

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml.cs` — `Ask` (line 40), `Build` (line 50), the `Loaded` focus (line 31)
- Modify: `src/OrdoSort.Wpf/Services/DialogService.cs:81`
- Test: `tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs`

**Interfaces:**
- Consumes: `PasswordWindow.Build(Window? owner, PasswordRequest request)` (internal), `w.ShowPw` (CheckBox), `w.PwPlain` (TextBox), `w.PwBox` (PasswordBox), `w.OpenButton`, `w.Answer`; `PasswordWindowTests.Show(PasswordRequest)` helper (builds and shows off-screen); `DialogService._owner`.
- Produces: `internal static PasswordWindow Build(Window? owner, PasswordRequest request, bool showPassword = false)`; `public static string? Ask(Window? owner, PasswordRequest request, ref bool showPassword)` (replaces the two-argument `Ask`); `internal bool ShowPassword`.

Background: `Ask` builds a fresh window per locked item and `ShowPw` starts unchecked each time; `ZipListViewModel.AskPassword` asks once per undecided item, so a user transcribing from a note re-clicks Show every time. `DialogService` is one instance per owning window, so a field there carries the toggle for the run. Grep `PasswordWindow.Ask(` first — `DialogService.cs:81` should be the only caller; fix any other.

- [ ] **Step 1: Write the failing tests**

Add to `PasswordWindowTests.cs`:

```csharp
    /// <summary>UX-36: every prompt in a run is a fresh window, and Show
    /// started unchecked each time. The caller now hands the last state in
    /// and reads it back, so the second locked item opens the way the user
    /// left the first.</summary>
    [Fact]
    public void ShowCarriesInAndOut() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = PasswordWindow.Build(null, new PasswordRequest("a.zip", null, false), showPassword: true);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000; w.Top = 0; w.ShowActivated = false;
        w.Show();
        w.UpdateLayout();
        try
        {
            Assert.True(w.ShowPw.IsChecked);
            Assert.True(w.PwPlain.IsVisible);
            Assert.False(w.PwBox.IsVisible);
            Assert.Same(w.PwPlain, FocusManager.GetFocusedElement(w));   // focus follows the visible box
            Assert.True(w.ShowPassword);

            w.PwPlain.Text = "secret";
            w.OpenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("secret", w.Answer);

            w.ShowPw.IsChecked = false;
            Assert.False(w.ShowPassword);
        }
        finally { if (w.IsVisible) w.Close(); }
    });
```

(Add `using System.Windows.Input;` for `FocusManager` if the file lacks it.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~ShowCarriesInAndOut"`
Expected: build FAILS — no `showPassword` parameter, no `ShowPassword`.

- [ ] **Step 3: Implement the window**

In `PasswordWindow.xaml.cs`:

Line 31, replace `Loaded += (_, _) => PwBox.Focus();` with
```csharp
        // Focus lands in whichever box is showing — PwPlain when Show came in
        // checked (UX-36) — so the next keystroke is the password and Enter is
        // live immediately. Keyboard.Focus on a Collapsed element lands nowhere.
        Loaded += (_, _) => (ShowPw.IsChecked == true ? (UIElement)PwPlain : PwBox).Focus();
```

Replace `Ask`:
```csharp
    /// <summary>Owner-modal. Returns the password, or null when skipped.
    /// <paramref name="showPassword"/> carries the Show toggle in and back
    /// out, so a run that asks for several passwords opens each prompt the
    /// way the user left the last one (UX-36).</summary>
    public static string? Ask(Window? owner, PasswordRequest request, ref bool showPassword)
    {
        var w = Build(owner, request, showPassword);
        w.ShowDialog();
        showPassword = w.ShowPassword;
        return w._answer;
    }

    /// <summary>The Show toggle's state, read back by <see cref="Ask"/> after
    /// the dialog closes.</summary>
    internal bool ShowPassword => ShowPw.IsChecked == true;
```

Change `Build`'s signature to `internal static PasswordWindow Build(Window? owner, PasswordRequest request, bool showPassword = false)` and, as its last statement before `return w;`, add:
```csharp
        // Checked AFTER the text is in place: the Checked handler swaps the
        // boxes and copies the password across, which is exactly the setup
        // a prompt opened with Show already on needs.
        if (showPassword) w.ShowPw.IsChecked = true;
```

- [ ] **Step 4: Implement the service**

In `DialogService.cs`, add a field and change line 81:
```csharp
    /// <summary>The password prompt's Show toggle, carried from one prompt to
    /// the next for the life of this service — one per owning window, so
    /// one per run (UX-36).</summary>
    private bool _showPassword;

    public string? AskPassword(PasswordRequest request) =>
        PasswordWindow.Ask(_owner, request, ref _showPassword);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PasswordWindowTests|FullyQualifiedName~DialogService"`
Expected: all pass; `dotnet build` of the whole solution succeeds (no other `Ask` caller left behind).

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/PasswordWindow.xaml.cs src/OrdoSort.Wpf/Services/DialogService.cs tests/OrdoSort.Wpf.Tests/PasswordWindowTests.cs
git commit -m "fix(dialogs): the password prompt remembers show for the rest of the run

Each prompt was a fresh window with Show unchecked, so a user reading a
password off a note re-clicked it once per locked item (UX-36). The
service carries the toggle from one prompt to the next.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L4wHw4xWdVFQanbyHQMReL"
```

---

## After the last task (controller, not an implementer)

1. `git status` clean, fifteen fix commits on `fix/ux-audit-2026-09-03` above the audit commit.
2. One full run: `dotnet test` from the repo root. Read both `Passed!` lines and their counts; compare the Wpf count to the previous baseline plus the new facts. Budget for one clean run — Smart App Control gets worse with repeated rebuilds.
3. Update `docs/superpowers/audits/2026-09-03-ux-audit.md`: mark the fifteen findings `— FIXED 2026-09-03 (<commit>)` in their headings, and update the companion HTML's counts strip and the affected articles; republish the artifact from the same file path.
4. Then batches 2 and 3 get their own plan.
