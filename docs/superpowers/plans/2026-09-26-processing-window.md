# Processing Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Start processing opens a separate Processing window, fitted so the whole first page shows, while the dashboard (`MainWindow`) stays visible and live.

**Architecture:** Two windows, one brain. `MainWindow` becomes the dashboard only: it stays compact and loses the compact/normal reshaping. A new `ProcessingWindow` owns the WebView2 viewer and hosts `ProcessingView` and `DoneView`. It is created once at start-up, hidden between sessions, and bound to the same `ShellViewModel`. The view model gains a hook it awaits before the first document (so the window can show and start the viewer), a "show the session" request, and separate Done text so the dashboard's Ready text is never overwritten.

**Tech Stack:** C# / .NET 8, WPF, WebView2, xUnit. `check.bat` is the one check command.

**Spec:** `docs/superpowers/specs/2026-09-26-processing-window-design.md`

## Global Constraints

- Two windows, one `ShellViewModel`; the filing rules (rename, move, audit log, Undo, completer) do not change.
- Dashboard while filing: stays live (counts, alerts, tiles keep updating); its button reads "Processing… (show)" and brings the Processing window forward; no second session starts.
- X on the Processing window mid-session stops the session, exactly as Stop / Esc does.
- Last document filed: the Processing window shows Done (with Undo); Back to dashboard or X hides it.
- Fit on open: the viewer takes the page's shape at the full height of the work area of the dashboard's monitor; if that is wider than the work area, the height shrinks; centred on that monitor.
- The dashboard's Refresh button must never end an open session.
- One way to do a thing: the old compact/normal switch (`EnterCompact`/`EnterNormal`/`FitViewerTo`) and `FitMath.WindowWidthFor`/`LeftFor` are removed, not kept alongside.
- CRLF line endings in every `.cs`/`.xaml` (scratchpad `crlf.py`); `check.bat` runs `dotnet format` verify.

## Review Focus

1. **The viewer fails to start** (no WebView2 runtime). Start must still open the Processing window and run the session with a blank viewer, warning once, as today's start-up warning does. Pinned in Task 3.
2. **The dashboard sits on a second monitor.** The Processing window must fit and centre on that monitor, not the primary. Pinned in Task 1 (`FitMath` with an offset work area).
3. **A very wide page** (landscape, or 2:1). The width is capped at the work area and the height shrinks; the window never hangs off-screen. Pinned in Task 1.
4. **X pressed while a document is mid-commit** (`_busy`). `StopSession` refuses, so the window must stay open showing the session, not hide over a half-finished move. Pinned in Task 3.
5. **Refresh on the dashboard during a session or on Done.** It must not end the session or wipe the Done summary. The button is hidden while a session is open. Pinned in Task 2 (`IsSessionOpen`) and Task 3 (binding).

---

### Task 1: Fit the whole page (`FitMath.SessionBounds`)

**Files:**
- Modify: `src/OrdoSort.Wpf/Services/FitMath.cs` (replace `WindowWidthFor` and `LeftFor` with `SessionBounds`)
- Modify: `tests/OrdoSort.Wpf.Tests/ViewerFitTests.cs` (replace the `WindowWidthFor`/`LeftFor` facts; keep the `FitViewerToPage` event facts)

**Interfaces:**
- Produces: `public static Rect? FitMath.SessionBounds(Rect workArea, double chromeWidth, double chromeHeight, double aspect, double minWidth, double minHeight)` returns the window's bounds, or null when the inputs can't give a sane answer. `chromeWidth`/`chromeHeight` are the window's size minus the viewer pane's size.

- [ ] **Step 1: Write the failing tests** (in `ViewerFitTests.cs`, replacing the facts that call `WindowWidthFor`/`LeftFor`)

```csharp
    // ---- SessionBounds: the whole first page fits (2026-09-26) -----------
    // Work area 1920x1040 at the origin; chrome 470 wide (panel + splitter +
    // borders) and 40 tall. Edge's toolbar (56) and scrollbar (24) are part
    // of the pane but not of the page.

    private static readonly Rect Primary = new(0, 0, 1920, 1040);

    [Fact]
    public void APortraitPageTakesTheFullHeightAndItsOwnShape()
    {
        var r = FitMath.SessionBounds(Primary, 470, 40, 612d / 792d, 900, 600)!.Value;

        Assert.Equal(1040, r.Height, 1);
        Assert.Equal(470 + (1040 - 40 - 56) * (612d / 792d) + 24, r.Width, 1);
        Assert.Equal((1920 - r.Width) / 2, r.Left, 1);
        Assert.Equal(0, r.Top, 1);
    }

    [Fact]
    public void APageTooWideForTheScreenCapsTheWidthAndShrinksTheHeight()
    {
        var r = FitMath.SessionBounds(Primary, 470, 40, 2.0, 900, 600)!.Value;

        Assert.Equal(1920, r.Width, 1);
        Assert.Equal(40 + 56 + (1920 - 470 - 24) / 2.0, r.Height, 1);
        Assert.Equal((1040 - r.Height) / 2, r.Top, 1);
    }

    [Fact]
    public void ItFitsAndCentresOnTheDashboardsOwnMonitor()
    {
        var second = new Rect(1920, 0, 2560, 1400);

        var r = FitMath.SessionBounds(second, 470, 40, 612d / 792d, 900, 600)!.Value;

        Assert.Equal(1400, r.Height, 1);
        Assert.Equal(1920 + (2560 - r.Width) / 2, r.Left, 1);
    }

    [Fact]
    public void ANarrowPageStillGetsTheWindowsMinimumWidth()
    {
        var r = FitMath.SessionBounds(Primary, 470, 40, 0.2, 900, 600)!.Value;

        Assert.Equal(900, r.Width, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsensePageShapeLeavesTheWindowAlone(double aspect) =>
        Assert.Null(FitMath.SessionBounds(Primary, 470, 40, aspect, 900, 600));

    [Fact]
    public void AWorkAreaTooSmallForTheChromeLeavesTheWindowAlone() =>
        Assert.Null(FitMath.SessionBounds(new Rect(0, 0, 400, 80), 470, 40, 0.77, 900, 600));
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt --filter "FullyQualifiedName~ViewerFitTests"`
Expected: build error, `FitMath` has no `SessionBounds`.

- [ ] **Step 3: Implement** (replace the body of `FitMath` below its class doc; update the class doc to describe whole-page fitting)

```csharp
    /// <summary>The Processing window's bounds when a session opens (spec
    /// 2026-09-26): the viewer's DOCUMENT area takes the page's shape at the
    /// full height of <paramref name="workArea"/>; if that is wider than the
    /// work area, the height shrinks instead. Centred on the work area, and
    /// never below the window's minimum size. Edge spends the pane's top
    /// <see cref="PanMath.ToolbarDip"/> on its toolbar and its right
    /// <see cref="PanMath.ScrollbarDip"/> on a scrollbar, so those are added
    /// around the page. Null for input that cannot give a sane size: a window
    /// that stays put beats one sized from garbage.</summary>
    public static Rect? SessionBounds(Rect workArea, double chromeWidth, double chromeHeight,
        double aspect, double minWidth, double minHeight)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect)) return null;
        if (workArea.Width <= 0 || workArea.Height <= 0) return null;

        var height = workArea.Height;
        var documentHeight = height - chromeHeight - PanMath.ToolbarDip;
        if (documentHeight <= 0) return null;
        var width = chromeWidth + documentHeight * aspect + PanMath.ScrollbarDip;
        if (width > workArea.Width)
        {
            width = workArea.Width;
            var documentWidth = width - chromeWidth - PanMath.ScrollbarDip;
            if (documentWidth <= 0) return null;
            height = chromeHeight + PanMath.ToolbarDip + documentWidth / aspect;
        }
        width = Math.Max(width, Math.Min(minWidth, workArea.Width));
        height = Math.Max(height, Math.Min(minHeight, workArea.Height));
        return new Rect(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }
```

`WindowWidthFor` and `LeftFor` are deleted; `MainWindow.FitViewerTo` (their only caller) goes in Task 3. Until then, delete `FitViewerTo`'s body and leave `private void FitViewerTo(double aspect) { }` with a comment `// replaced by ProcessingWindow.FitToPage (Task 3)`, so the build stays green between tasks.

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Services/FitMath.cs src/OrdoSort.Wpf/MainWindow.xaml.cs tests/OrdoSort.Wpf.Tests/ViewerFitTests.cs
git commit -m "feat(processing-window): fit the whole first page (FitMath.SessionBounds)"
```

---

### Task 2: The view model keeps the dashboard live

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`
- Modify: `src/OrdoSort.Wpf/Views/DoneView.xaml` (bind `DoneTitle`/`DoneDetail`)
- Modify: `src/OrdoSort.Wpf/Views/ReadyView.xaml` (Start button caption binds `StartButtonText`)
- Modify: `tests/OrdoSort.Wpf.Tests/DashboardTests.cs`
- Create: `tests/OrdoSort.Wpf.Tests/SessionWindowShellTests.cs`

**Interfaces:**
- Produces on `ShellViewModel`:
  - `public bool IsSessionOpen` (`Screen != Screen.Ready`, raised with `Screen`)
  - `public string StartButtonText` ("Start processing" / "Processing… (show)")
  - `public event Action? ShowSessionRequested` (the Start button while a session is open)
  - `public Func<Task>? PrepareSessionView { get; set; }` (awaited by `StartProcessingAsync` after `Screen = Processing`, before the first document loads)
  - `public string DoneTitle`, `public string DoneDetail` (the Done summary; `CountLine`/`DetailLine` stay the dashboard's)

- [ ] **Step 1: Write the failing tests**

`tests/OrdoSort.Wpf.Tests/SessionWindowShellTests.cs`:

```csharp
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The view model side of "processing in its own window"
/// (2026-09-26): the dashboard's Ready content stays true while a session
/// runs, the Start button shows the session instead of starting a second one,
/// and the window gets a moment to open before the first document loads.</summary>
public class SessionWindowShellTests
{
    [Fact]
    public void StartWaitsForTheSessionWindowBeforeLoadingTheFirstDocument()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        var order = new List<string>();
        fx.Shell.PrepareSessionView = () => { order.Add($"prepare, {fx.Viewer.Shown.Count} shown"); return Task.CompletedTask; };
        fx.Shell.Initialize();

        fx.Shell.StartProcessing();

        Assert.Equal(new[] { "prepare, 0 shown" }, order);
        Assert.Single(fx.Viewer.Shown);
    }

    [Fact]
    public void DuringASessionTheStartButtonShowsTheSessionInsteadOfStartingAnother()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        Assert.Equal("Start processing", fx.Shell.StartButtonText);
        fx.Shell.StartProcessing();
        var shown = 0;
        fx.Shell.ShowSessionRequested += () => shown++;

        Assert.True(fx.Shell.IsSessionOpen);
        Assert.Equal("Processing… (show)", fx.Shell.StartButtonText);
        Assert.True(fx.Shell.StartCommand.CanExecute(null));
        fx.Shell.StartCommand.Execute(null);

        Assert.Equal(1, shown);
        Assert.True(fx.Shell.IsProcessing);   // still the same session
    }

    [Fact]
    public void TheDoneSummaryNeverOverwritesTheDashboardsCountLine()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.SkipCommand.Execute(null);   // the only document: the session ends

        Assert.True(fx.Shell.IsDone);
        Assert.Equal("Session complete", fx.Shell.DoneTitle);
        Assert.Contains("set aside", fx.Shell.DoneDetail);
        Assert.NotEqual("Session complete", fx.Shell.CountLine);
    }

    [Fact]
    public void StoppingClosesTheSessionAgain()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.StopCommand.Execute(null);

        Assert.False(fx.Shell.IsSessionOpen);
        Assert.Equal("Start processing", fx.Shell.StartButtonText);
    }
}
```

If `SkipCommand` needs a parameter or the fixture's single file is not the whole session, read `ShellViewModel`'s skip path and pick the call that files or sets aside the only document; the assertion stays the same.

In `DashboardTests.cs`, flip the three facts that pin the old "dashboard hides while filing" behaviour:

```csharp
    [Fact]
    public void TileControlsHideWithoutWatchFoldersButStayWhileFiling()
    {
        using var bare = new ShellFixture();
        bare.Shell.Initialize();
        Assert.False(bare.Shell.TileControlsVisible);   // nothing to control

        using var fx = WithWatchFolder(out _);
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.True(fx.Shell.TileControlsVisible);      // the dashboard stays live (2026-09-26)
    }

    [Fact]
    public void TheDashboardStaysLiveDuringASession()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.AddInboxFile();
        fx.Shell.Initialize();
        Assert.True(fx.Shell.FlashRunning);

        fx.Shell.StartProcessing();
        Assert.True(fx.Shell.DashboardVisible);
        Assert.True(fx.Shell.FlashRunning);

        File.WriteAllText(Path.Combine(watched, "second.pdf"), "x");
        fx.Shell.OnFolderActivity();
        Assert.Equal(2, Assert.Single(fx.Shell.Tiles).Count);
    }
```

This replaces `StartingASessionHidesTheDashboardAndStopsTheFlash` and `TileControlsHideWithoutWatchFoldersAndWhileFiling`. In the AllQuiet fact, change the filing case to `Assert.True(filing.Shell.AllQuiet);` with the comment `// the dashboard stays live while filing (2026-09-26)`. If `TileViewModel` names its count differently from `Count`, use its real property.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt --filter "FullyQualifiedName~SessionWindowShellTests|FullyQualifiedName~DashboardTests"`
Expected: build errors for `PrepareSessionView`, `StartButtonText`, `IsSessionOpen`, `ShowSessionRequested`, `DoneTitle`, `DoneDetail`.

- [ ] **Step 3: Implement** in `ShellViewModel.cs`

- In the `Screen` setter, add `Raise(nameof(IsSessionOpen)); Raise(nameof(StartButtonText)); StartCommand.RaiseCanExecuteChanged();`.
- Add after `IsDone`:

```csharp
    /// <summary>A session is on screen (Processing or Done), in its own
    /// window (2026-09-26). The dashboard stays up beside it.</summary>
    public bool IsSessionOpen => Screen != Screen.Ready;

    /// <summary>The dashboard's Start button: while a session is open it
    /// brings the session's window forward rather than starting another.</summary>
    public string StartButtonText => IsSessionOpen ? "Processing… (show)" : "Start processing";

    /// <summary>Raised by the Start button while a session is open.</summary>
    public event Action? ShowSessionRequested;

    /// <summary>Awaited as a session starts, before the first document
    /// loads: the session's window opens and its viewer starts here.</summary>
    public Func<Task>? PrepareSessionView { get; set; }
```

- `StartCommand = new RelayCommand(OnStart, () => IsSessionOpen || StartEnabled);` with

```csharp
    private void OnStart()
    {
        if (IsSessionOpen) ShowSessionRequested?.Invoke();
        else StartProcessing();
    }
```

  and make `StartEnabled`'s setter keep raising `StartCommand.RaiseCanExecuteChanged()`.
- `StartProcessingAsync`: delete `DashboardVisible = false;`, `AllQuiet = false;` and `StopFlash();`. Keep `ApplyFlashAll();` only if it re-applies tile colours; otherwise delete it too. Insert `if (PrepareSessionView is { } prepare) await prepare();` immediately before `await RefreshCompleterAsync();`.
- Refresh during a session: in the snapshot loop, `var wantStatuses = mode != "hidden";` (drop the `Screen != Screen.Processing` part). Split `ShowReady` into:

```csharp
    private void ShowReady(FolderSnapshot snap)
    {
        _viewer.Blank();
        StartEnabled = snap.Scan.Count > 0;
        ShowDashboard(snap);
    }

    /// <summary>The dashboard's counts and tiles, kept current in every
    /// screen: the dashboard stays up beside a session (2026-09-26).</summary>
    private void ShowDashboard(FolderSnapshot snap)
    {
        // the CountLine / BigCount / CountCaption / DetailLine assignments and
        // RefreshDashboard(scan, snap.Statuses) move here unchanged from ShowReady
    }
```

  In `ApplySnapshot`, call `ShowDashboard(snap)` from the Processing branch (after the `Extend`) and the Done branch too. Guard `if (snap.Statuses is null)` only where `RefreshDashboard` already handles null.
- `RefreshDashboard`: `DashboardVisible = statuses.Count > 0;` and `AllQuiet = statuses.Count == 0 && _cfg.WatchFolders.Count > 0 && TileMode == "active";` (drop both `Screen == Screen.Ready &&`).
- `TileControlsVisible => _cfg.WatchFolders.Count > 0;` (drop `IsReady &&`).
- `ShowDone`: set `DoneTitle = "Session complete";` and `DoneDetail = <the text it gave DetailLine>`; stop writing `CountLine`/`DetailLine`. Add both as `private set` properties with `Set(ref …)`.

`DoneView.xaml`: `{Binding CountLine}` → `{Binding DoneTitle}`, `{Binding DetailLine}` → `{Binding DoneDetail}`.

`ReadyView.xaml`: the Start button's caption `Text="Start processing"` → `Text="{Binding StartButtonText}"` and `AutomationProperties.Name="{Binding StartButtonText}"` on the Button.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt --filter "FullyQualifiedName~SessionWindowShellTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~Shell|FullyQualifiedName~ViewerFit|FullyQualifiedName~UnexpectedError"`
Expected: PASS. Any other Shell fact that pinned `CountLine == "Session complete"` moves to `DoneTitle`.

- [ ] **Step 5: `check.bat`, then commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs src/OrdoSort.Wpf/Views/DoneView.xaml src/OrdoSort.Wpf/Views/ReadyView.xaml tests/OrdoSort.Wpf.Tests/DashboardTests.cs tests/OrdoSort.Wpf.Tests/SessionWindowShellTests.cs
git commit -m "feat(processing-window): the dashboard stays live beside a session"
```

---

### Task 3: `ProcessingWindow`, and `MainWindow` becomes the dashboard

**Files:**
- Create: `src/OrdoSort.Wpf/ProcessingWindow.xaml`, `src/OrdoSort.Wpf/ProcessingWindow.xaml.cs`
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml` (remove the viewer grid; the ReadyView fills the window; Refresh hides on `IsSessionOpen`)
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml.cs` (remove compact/normal modes, `FitViewerTo`, the pan zone, route hotkeys, viewer init; create and wire the Processing window)
- Create: `tests/OrdoSort.Wpf.Tests/ProcessingWindowTests.cs`

**Interfaces:**
- Consumes: Task 1 `FitMath.SessionBounds`; Task 2 `PrepareSessionView`, `ShowSessionRequested`, `IsSessionOpen`.
- Produces:
  - `ProcessingWindow(Func<Rect> dashboardWorkArea)`
  - `IPdfViewer Pdf`, `IDialogService Dialogs { get; set; }`
  - `void Attach(ShellViewModel shell)`, `Task OpenForSessionAsync()`, `void BringToFront()`, `void CloseForReal()`
  - `internal void FitToPage(double aspect)`
  - `MainWindow.Processing` (internal, for tests and the smoke harness)

- [ ] **Step 1: Write the failing tests** (`ProcessingWindowTests.cs`, on the shared STA fixture, the way `ShutdownDuringCommitTests` drives a real `MainWindow`; read its setup and copy its config/scheduler pattern)

```csharp
    [Fact] public void StartOpensTheProcessingWindowAndTheDashboardStaysOnTheReadyScreen()
    //  open a real MainWindow over a scratch inbox with one PDF; Shell.StartProcessing();
    //  Assert.True(window.Processing.IsVisible); Assert.True(window.IsVisible);
    //  Assert.Equal(Visibility.Visible, the dashboard's ReadyView.Visibility)

    [Fact] public void XOnTheProcessingWindowMidSessionStopsTheSession()
    //  start; window.Processing.Close(); Assert.True(Shell.IsReady); Assert.False(window.Processing.IsVisible)

    [Fact] public void XWhileADocumentIsMidCommitKeepsTheWindowOpen()   // Review Focus 4
    //  start; hold a commit open (ShutdownDuringCommitTests' gate); window.Processing.Close();
    //  Assert.True(window.Processing.IsVisible); Assert.True(Shell.IsProcessing); release the gate

    [Fact] public void ClosingTheDoneSummaryReturnsToReadyAndHidesTheWindow()
    //  start; file or set aside the only document (Done); window.Processing.Close();
    //  Assert.True(Shell.IsReady); Assert.False(window.Processing.IsVisible)

    [Fact] public void TheDashboardsButtonBringsTheSessionForward()
    //  start; window.Processing.WindowState = Minimized; Shell.StartCommand.Execute(null);
    //  Assert.Equal(WindowState.Normal, window.Processing.WindowState)

    [Fact] public void RouteHotkeysLiveOnTheProcessingWindow()
    //  config route with Hotkey "Ctrl+1"; start;
    //  Assert.Contains(window.Processing.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.D1)
    //  Assert.DoesNotContain(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.D1)

    [Fact] public void AViewerThatFailsToStartWarnsOnceAndTheSessionStillRuns()   // Review Focus 1
    //  a ProcessingWindow whose viewer init returns false (WebViewPdfViewer.InitAsync
    //  returns false when the runtime is missing: give ProcessingWindow an internal ctor
    //  seam `Func<Task<bool>>? initViewer` for this test); two sessions in a row;
    //  Assert.Single(dialogs.Warnings); Assert.True(Shell.IsProcessing) after each Start

    [Fact] public void TheRefreshButtonIsHiddenWhileASessionIsOpen()   // Review Focus 5
    //  start; the dashboard header's Rescan button: Visibility == Collapsed; finish to Done: still Collapsed
```

Write each as full test code against those windows. The comments above give the arrangement, act and assertions; the fixture setup is `ShutdownDuringCommitTests`' own. Give the windows `ShowActivated = false` and `Left = -20000`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt --filter "FullyQualifiedName~ProcessingWindowTests"`
Expected: build errors (`ProcessingWindow`, `MainWindow.Processing` missing).

- [ ] **Step 3: Implement**

`ProcessingWindow.xaml` (namespace `OrdoSort.Wpf`, same `xmlns` lines as `MainWindow.xaml`, including `wv2` and `views`):

```xml
<Window x:Class="OrdoSort.Wpf.ProcessingWindow"
        ...same xmlns as MainWindow.xaml...
        Title="OrdoSort — Processing" Width="1280" Height="860" MinWidth="900" MinHeight="600"
        WindowStartupLocation="Manual" Style="{StaticResource {x:Type Window}}">
    <!-- A session in its own window (spec 2026-09-26): the viewer, the
         Processing screen and the Done summary. The dashboard (MainWindow)
         stays up beside it. -->
    <Grid>
        <!-- the Grid with ViewerCol / SplitterCol / PanelCol, Viewer, Split and the
             ScrollViewer moves here verbatim from MainWindow.xaml, minus ReadyView -->
    </Grid>
</Window>
```

`ProcessingWindow.xaml.cs`:

```csharp
using System.Windows;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf;

/// <summary>A filing session in its own window (spec 2026-09-26). Created
/// once, hidden between sessions; owns the one PDF viewer the view model is
/// given. Closing it stops the session (or leaves Done) and hides it; only
/// <see cref="CloseForReal"/>, from the dashboard's own close, destroys it.</summary>
public partial class ProcessingWindow : Window
{
    private readonly WebViewPdfViewer _pdf;
    private readonly Func<Rect> _dashboardWorkArea;
    private readonly Func<Task<bool>> _initViewer;
    private readonly Func<Rect?> _panZone;
    private Task<bool>? _viewerStart;
    private bool _warned;
    private bool _reallyClosing;
    private ShellViewModel? _shell;

    public ProcessingWindow(Func<Rect> dashboardWorkArea) : this(dashboardWorkArea, null) { }

    internal ProcessingWindow(Func<Rect> dashboardWorkArea, Func<Task<bool>>? initViewer)
    {
        InitializeComponent();
        _dashboardWorkArea = dashboardWorkArea;
        Viewer.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            AdditionalBrowserArguments = "--disable-smooth-scrolling",
        };
        _pdf = new WebViewPdfViewer(Viewer);
        _initViewer = initViewer ?? _pdf.InitAsync;
        Dialogs = new DialogService(this);
        _panZone = ViewerPanZone;
        Loaded += (_, _) => ViewerInputEnhancer.Register(_panZone);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ViewerInputEnhancer.Unregister(_panZone);
            Viewer.Dispose();
        };
    }

    public IPdfViewer Pdf => _pdf;
    internal IDialogService Dialogs { get; set; }

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        shell.RoutesRebuilt += RebindRouteHotkeys;
        shell.FitViewerToPage += FitToPage;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Screen) && shell.IsReady) Hide();
        };
    }

    /// <summary>Shows the window and waits for its viewer, once per session,
    /// before the first document loads. A viewer that cannot start is
    /// reported once; the session still runs, with nothing in the pane.</summary>
    public async Task OpenForSessionAsync()
    {
        BringToFront();
        _viewerStart ??= _initViewer();
        if (!await _viewerStart && !_warned)
        {
            _warned = true;
            Dialogs.Warn("The PDF viewer (WebView2) failed to start:\n\n" + _pdf.InitError, "OrdoSort");
        }
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing || _shell is null) return;
        e.Cancel = true;
        // StopSession refuses mid-commit; the window then stays, showing it
        if (_shell.IsProcessing) _shell.StopSession();
        else if (_shell.IsDone) _shell.RescanCommand.Execute(null);
    }

    /// <summary>Fits the whole first page (spec 2026-09-26), once per
    /// session. A maximized window is left alone.</summary>
    internal void FitToPage(double aspect)
    {
        if (WindowState != WindowState.Normal) return;
        UpdateLayout();
        if (Viewer.ActualHeight <= 0) return;
        if (FitMath.SessionBounds(_dashboardWorkArea(), ActualWidth - Viewer.ActualWidth,
                ActualHeight - Viewer.ActualHeight, aspect, MinWidth, MinHeight) is { } r)
        {
            Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
        }
    }

    // RebindRouteHotkeys and ViewerPanZone move here verbatim from MainWindow.xaml.cs
    // (Shell → _shell!; ViewerPanZone drops the _compact test: this window has no compact mode).
}
```

`MainWindow.xaml`: delete the viewer `Grid` (ViewerCol through the ScrollViewer). In its place put `<ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False" Background="{DynamicResource Theme.WindowBg}"><views:ReadyView Margin="16,12" /></ScrollViewer>`. Drop the `wv2` xmlns. On the header Rescan button's style, change the `DataTrigger Binding="{Binding IsProcessing}"` to `Binding="{Binding IsSessionOpen}"`.

`MainWindow.xaml.cs`:
- Constructor: `Processing = new ProcessingWindow(() => MonitorWorkArea.For(this));` before the Shell. Construct the Shell with `Processing.Pdf` and `new DialogRelay(() => Processing.IsVisible ? Processing.Dialogs : Dialogs)`. Then `Processing.Attach(Shell); Shell.PrepareSessionView = Processing.OpenForSessionAsync; Shell.ShowSessionRequested += Processing.BringToFront;`
- Delete: `Viewer.CreationProperties`, `_pdf`, `ApplyWindowMode` and its `Screen` subscription, `EnterCompact`/`EnterNormal`/`FitViewerTo` and the bounds fields, `ViewerPanZone`/`_panZone`/`ViewerInputEnhancer` use, `RebindRouteHotkeys`/`_routeBindings`/the `RoutesRebuilt` subscription, and the viewer-init warning in `Loaded` (keep `Shell.Initialize()`).
- The one-time parking that `EnterCompact(initial: true)` did (Width 470, top-right of the work area, `SizeToContent.Height`, `MaxHeight` = work area − 24, `MinWidth` 400) becomes a small `ParkDashboard()` called from the constructor; keep the `Tiles.CollectionChanged` re-fit without the `_compact` test.
- `Closing`: keep the logic (a session is stopped, Done goes back to Ready); `Closed`: add `Processing.CloseForReal();` first.
- `internal ProcessingWindow Processing { get; }`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt --filter "FullyQualifiedName~ProcessingWindowTests|FullyQualifiedName~ShutdownDuringCommit|FullyQualifiedName~HeaderLayout|FullyQualifiedName~ViewerFit"`
Expected: PASS.

- [ ] **Step 5: `check.bat`, then commit**

```bash
git add src/OrdoSort.Wpf/ProcessingWindow.xaml src/OrdoSort.Wpf/ProcessingWindow.xaml.cs src/OrdoSort.Wpf/MainWindow.xaml src/OrdoSort.Wpf/MainWindow.xaml.cs tests/OrdoSort.Wpf.Tests/ProcessingWindowTests.cs
git commit -m "feat(processing-window): sessions open in their own window; the dashboard stays"
```

---

### Task 4: Re-point the suites, docs and live check

**Files:**
- Modify: `tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs` (register `ProcessingWindow`; `MainWindow`'s entry no longer mentions `EnterCompact`)
- Modify: `tests/OrdoSort.Wpf.Tests/TextWrapCoverageTests.cs` (add `ProcessingWindow.xaml` to the XAML walk if it lists files by hand)
- Modify: `tools/OrdoSort.Smoke/E2E/RoutingLoop.cs`, `tools/OrdoSort.Smoke/E2E/Scenarios/RoutingScenarios.cs`, `tools/OrdoSort.Smoke/Reentrancy.cs` (a recording dialog service goes on `window.Processing.Dialogs` too; waits and captures look at `window.Processing`)
- Modify: `tools/OrdoSort.Smoke/Screenshots.cs` (`CaptureMainWindow` for the processing shot and `CaptureMainWindowDone` capture `window.Processing`)
- Modify: `README.md`, `STATUS.md`

- [ ] **Step 1:** Run the whole suite: `dotnet test tests/OrdoSort.Wpf.Tests -c Debug --artifacts-path artifacts/alt`. For each failure caused by the move, change setup or where the test looks (the Processing window instead of `MainWindow`). Do not weaken an assertion. Record each change that is more than a re-point as a ledger ruling.
- [ ] **Step 2:** Build the smoke tool: `dotnet build tools/OrdoSort.Smoke -c Release` (0 errors). Run the E2E harness tests in the Wpf suite.
- [ ] **Step 3:** README: in Features, the routing-loop line says Start processing opens the session in its own window, fitted to the page, while the dashboard stays up. STATUS: a Now row for this change, with the branch named.
- [ ] **Step 4:** `check.bat`, then commit:

```bash
git add tests/OrdoSort.Wpf.Tests/WindowOverflowTests.cs tests/OrdoSort.Wpf.Tests/TextWrapCoverageTests.cs tools/OrdoSort.Smoke/E2E/RoutingLoop.cs tools/OrdoSort.Smoke/E2E/Scenarios/RoutingScenarios.cs tools/OrdoSort.Smoke/Reentrancy.cs tools/OrdoSort.Smoke/Screenshots.cs README.md STATUS.md
git commit -m "test(processing-window): suites and smoke harness follow the session into its window; docs"
```

- [ ] **Step 5: Live check** on a scratch copy of the Release build with `live/dev/config.json`:
  - [ ] Start opens the Processing window fitted to the first page; the dashboard stays in its corner.
  - [ ] The dashboard reads "Processing… (show)"; minimise the Processing window and press it to bring it back.
  - [ ] File one document; X mid-session stops the session.
  - [ ] Start again, finish; the Done summary shows; X returns the dashboard to Ready.
  - [ ] A route hotkey files a document while the Processing window has focus.

  Record the result in STATUS.
