# Grid Autofit-Then-Wrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every text column in the app's six capped grid windows shows its whole content — sized to the content when it fits, wrapped onto extra lines in proportion when it doesn't — with no horizontal scrollbar and nothing for the user to readjust.

**Architecture:** One shared class, `DataGridColumnCap` (already called by all six windows), changes its rule from "the message column takes what it needs, the name column gets the leftovers down to its floor" to autofit-then-wrap: it measures each participating column's content width (widest realized cell or the header, `FormattedText` in the cell's own font, cached per string), asks a new pure `ColumnShares.Compute` for each column's share of the viewport, and keeps every governed column's `MaxWidth` at that share. Star columns participate in the split but are never assigned — they take what the governed columns leave, which is their share by construction. Each window's XAML swaps `TextTrimming="CharacterEllipsis"` for `TextWrapping="Wrap"` on its text columns.

**Tech Stack:** .NET 8 WPF, xunit with the existing STA `HighlightContrastFixture`. No new dependencies.

**Spec:** none as a file — this was a bounded change designed and approved in chat on 2026-08-29. The approved design is reproduced verbatim in the next section and is the binding authority for this plan.

**Branch:** `feature/grid-autofit-wrap`, stacked on `feature/zip-window-split` at `6098114` (it edits `ZipToolsWindow`/`MergePdfsWindow`, which exist only there). Do not rebase onto `main`.

## The approved design (binding)

> **Rule.** Every text column gets its content width when the total fits the window; when it doesn't, the shortfall is shared in proportion to each column's content width — no column below its `MinWidth` — and the text in the columns that gave way wraps onto extra lines. Nothing is ever cut off, no horizontal scrollbar, and the fit is recomputed as content changes (so it also shrinks back after Clear/Remove — today WPF Auto columns only ever grow). Dragging a column border still wins for that column for the life of the window, as now.
>
> **How.** `DataGridColumnCap.Track` already sets each governed column's `MaxWidth` live and recomputes after layout; it gains a content-width measurement (widest realized cell — `FormattedText` in the cell's own font plus padding, cached per string — or the header, whichever is wider), treats the grid's star columns as participants automatically (no window needs a new call), and replaces the remainder formula with the proportional split. The XAML change is one setter per text column — `TextTrimming="CharacterEllipsis"` becomes `TextWrapping="Wrap"` — in the six windows that use the rule: Zip and unzip, Merge PDFs, PDF page counts, Match & merge, Bulk rename, History. Tooltips that only repeated the cell text go; the ones that show the full path stay.
>
> **Not doing, deliberately.** No persisted column widths. Triage keeps its own budget formula. Filename list is structured differently (several Auto columns, no cap) and is a follow-up of the same shape if wanted.

## Measured WPF facts (2026-08-29 probe on a bare grid, before this plan was written)

Every one of these was measured, not assumed; the code below leans on all of them.

| Fact | Measurement |
|---|---|
| A cell whose `TextBlock` wraps grows its row rather than clipping | Auto column capped at `MaxWidth=200`: a one-line 20px row became 94px, the `TextBlock` 93px, the short row beside it stayed 20px |
| An Auto column displays at `min(WPF's desired width, MaxWidth)` | desired 802 / `MaxWidth` 200 → `ActualWidth` 200; `MaxWidth` 60 → 60 |
| WPF's desired width keeps tracking longer content while capped | a longer row arrived while capped at 150: desired 802 → 1607, display stayed 150 |
| WPF's desired width **never shrinks** | the long row removed, cap lifted: `ActualWidth` stayed 482 and desired stayed 802; reassigning `Width = Auto` does not reset it (only `Width = 0` then `Auto` does, to 43 — the header) |
| `FormattedText` reproduces WPF's own measurement | 801.53px measured against a column WPF laid out at 802px for the same string — cell `Padding` (`8,4` in the theme) is NOT applied by the default `DataGridCell` template, so no padding enters the sum |
| Headless `Show()` clamps star columns at their `MinWidth`; a Background-priority pump reconciles them | already recorded in `AutoFitColumnTests.ShowOffscreenThenResizeTo`'s doc; re-confirmed (star at 100 = its floor after a Render pump; 382 after a Background pump once the Auto column was capped) |
| The `DataGridColumnHeader`'s `DesiredSize.Width` is its natural width only while unconstrained | 43px for "Text" — usable as-is for a header that fits; the class measures the header's own `TextBlock` instead so a capped header still counts at its natural width |

Consequences: the class caps at the **measured** content width in the fit case rather than relaxing to infinity (that is what makes shrink-back work — WPF alone would keep the old width forever), and adds 1px of slack to every measurement so a rounding difference can never wrap a one-line cell.

## Global Constraints

- **Check command** (run from the repo root before every commit; both lines must read `Passed!` and the totals must be at or above the baseline — Core **750**, Wpf **1989** at `6098114`; a `dotnet test` that exits 0 having run zero tests, or a stalled run reporting a short `Total`, is a FAILED check):
  ```
  dotnet build OrdoSort.sln -t:Rebuild -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  - Smart App Control block (`0x800711C7` / "blocked by policy") → delete every `bin/` and `obj/` and rebuild; never pass `-p:Deterministic=false`.
  - A burst of `XamlParseException` failures → `dotnet build-server shutdown`, rebuild, rerun.
  - MSB3027 (file in use) → kill the stale `testhost.exe`.
  - If the Wpf run stalls (known intermittent hang inside `HeaderLayoutTests.TheToolbarYieldsTheSpaceRatherThanTheMenu`): rerun with `--blame-hang --blame-hang-timeout 150s --blame-hang-dump-type none` and READ THE TOTAL — a stalled run reports only the tests that ran.
  - For a single test class: `dotnet test tests/OrdoSort.Wpf.Tests --no-build -v minimal --filter "FullyQualifiedName~<ClassName>"` (after `dotnet build tests/OrdoSort.Wpf.Tests -v quiet`).
- **Every measured fact must be revert-proof.** Each task lists, per fact, the production line to break; the implementer breaks it, sees the fact fail for a VALUE reason (a wrong number, not an exception), restores it, and records the failure message in the report. A fact that passes with the fix removed is a defect in the task (repo lesson: "the already-true-predicate trap bit three times").
- Tests are hermetic: STA work goes through `HighlightContrastFixture` (`[Collection(HighlightContrastTests.Name)]`, `_fx.Invoke`), windows are shown off-screen (`Left = -20000`, `ShowActivated = false`), no `Thread.Sleep`, no real filesystem beyond what a window's own builder already uses.
- No new NuGet dependencies. Do not edit `Theme/Styles.xaml`. Do not touch `TriageWindow`, `FilenameListWindow`, or the `Track(DataGrid, Func<double,double>, …)` overload's behaviour (Triage depends on it).
- C# style per the repo: XML doc comments on public/internal surfaces that explain WHY; `_camelCase` private fields; no single-letter names except loop indices.
- Every commit carries these trailer lines, after a blank line:
  ```
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc
  ```
- Commit messages say why, not just what.

---

## File structure

| File | Responsibility |
|---|---|
| `src/OrdoSort.Wpf/Views/ColumnShares.cs` (new) | Pure arithmetic: each column's share of an available width from natural widths and floors. No WPF types. |
| `src/OrdoSort.Wpf/Views/DataGridColumnCap.cs` (rewrite) | Live tracking: measures content widths, applies `ColumnShares`, keeps `MaxWidth` current; pinning and the Triage overload unchanged. |
| `src/OrdoSort.Wpf/Windows/{ZipTools,MergePdfs,PageCounts,MatchMerge,BulkRename,History}Window.xaml` | Text columns wrap instead of trim; comments say why. |
| `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml.cs` | `When` leaves the governed set (bounded content). |
| `tests/OrdoSort.Wpf.Tests/ColumnSharesTests.cs` (new) | Plain facts for the arithmetic. |
| `tests/OrdoSort.Wpf.Tests/DataGridColumnCapTests.cs` (new) | Measured facts on a bare grid: fit, proportional split, floors, wrap grows the row, shrink-back, header, star never assigned. |
| `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs` | Window-level facts rewritten from "stops at the cap with ellipsis" to "wraps inside its cap"; two new ZipTools facts. |
| `tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs` | The trimming theory becomes a wrapping theory; `When` uncapped fact. |

---

### Task 1: `ColumnShares` — the split, as a pure function

**Files:**
- Create: `src/OrdoSort.Wpf/Views/ColumnShares.cs`
- Test: `tests/OrdoSort.Wpf.Tests/ColumnSharesTests.cs`

**Interfaces:**
- Produces: `internal static class ColumnShares { public static double[] Compute(double available, IReadOnlyList<double> natural, IReadOnlyList<double> floors) }` — Task 2 calls it with the participants' measured widths and `MinWidth` floors.

- [ ] **Step 1: Write the failing tests**

```csharp
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>The arithmetic DataGridColumnCap applies, proven without a
/// DataGrid: what each text column gets of the width the viewport has
/// left. "Natural" is a column's content width; a "floor" is its MinWidth.
/// Numbers are chosen so the expected shares are exact.</summary>
public class ColumnSharesTests
{
    [Fact]
    public void WhenTheWidthsFitEachColumnGetsItsNaturalWidth()
    {
        var shares = ColumnShares.Compute(1000, new[] { 300.0, 200.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 300.0, 200.0 }, shares);
    }

    [Fact]
    public void WidthsThatFitExactlyTakeTheFittingBranchRatherThanBeingSplit()
    {
        // 100 + 200 == 300: the boundary between the two branches, where a
        // < instead of <= would needlessly split widths that already fit.
        var shares = ColumnShares.Compute(300, new[] { 100.0, 200.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 100.0, 200.0 }, shares);
    }

    [Fact]
    public void AFloorAboveTheNaturalWidthWinsEvenWhenEverythingFits()
    {
        var shares = ColumnShares.Compute(1000, new[] { 50.0, 200.0 }, new[] { 180.0, 20.0 });
        Assert.Equal(new[] { 180.0, 200.0 }, shares);
    }

    [Fact]
    public void WhenTheyDoNotFitTheShortfallIsSharedInProportion()
    {
        var shares = ColumnShares.Compute(600, new[] { 400.0, 800.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(200.0, shares[0], 6);
        Assert.Equal(400.0, shares[1], 6);
    }

    [Fact]
    public void AColumnWhoseShareWouldFallUnderItsFloorIsHeldThereAndTheRestIsResplit()
    {
        // Shares go by WANTED width — max(natural, floor) — so the first
        // column's first-pass share is 600 × 180/980 = 110, under its own
        // 180 floor: it is held at 180, and the 420 that leaves is less
        // than the 490 (600 × 800/980) the second column would have taken
        // had the first not been held at all.
        var shares = ColumnShares.Compute(600, new[] { 100.0, 800.0 }, new[] { 180.0, 20.0 });
        Assert.Equal(180.0, shares[0], 6);
        Assert.Equal(420.0, shares[1], 6);
    }

    [Fact]
    public void EveryColumnUnderItsFloorIsHeldAndTheRestTakesWhatIsLeft()
    {
        // First pass: 400 × 150/1100 = 54.5 for each short column (their
        // wanted width is their floor) and 290.9 for the third. Both short
        // columns are under their 150 floors, so both are held in that one
        // pass, and the 100 left over is all the third can have.
        var shares = ColumnShares.Compute(400, new[] { 100.0, 100.0, 800.0 }, new[] { 150.0, 150.0, 20.0 });
        Assert.Equal(new[] { 150.0, 150.0, 100.0 }, shares.Select(s => Math.Round(s, 6)).ToArray());
    }

    [Fact]
    public void HoldingOneColumnCanPushAnotherUnderItsFloorInALaterPass()
    {
        // wanted = 700 / 300 / 300 / 5000 against 1000.
        // Pass 1: 111 / 47.6 / 47.6 / 793.7 — only the first is under its
        //         floor (700), so only it is held. The second clears its 40.
        // Pass 2: 300 left over 5600 of pool — the second is now 16.1, under
        //         the 40 it cleared a pass ago, so it is held too.
        // Pass 3: 260 left over 5300 of pool — 14.7 and 245.3, both above
        //         their floors, so the loop stops. This is the case a single
        //         proportional pass with floors clamped afterwards gets wrong.
        var shares = ColumnShares.Compute(
            1000, new[] { 10.0, 300.0, 300.0, 5000.0 }, new[] { 700.0, 40.0, 5.0, 10.0 });
        Assert.Equal(700.0, shares[0], 6);
        Assert.Equal(40.0, shares[1], 6);
        Assert.Equal(14.716981, shares[2], 6);
        Assert.Equal(245.283019, shares[3], 6);
        Assert.Equal(1000.0, shares.Sum(), 6);
    }

    [Fact]
    public void FloorsThatAloneExceedTheWidthAreReturnedAsTheyAre()
    {
        // 300px of floors in 200px: the floors stand and the overflow is
        // WPF's to resolve (a horizontal scrollbar), exactly as the window's
        // own MinWidth is supposed to make impossible.
        var shares = ColumnShares.Compute(200, new[] { 500.0, 500.0 }, new[] { 150.0, 150.0 });
        Assert.Equal(new[] { 150.0, 150.0 }, shares);
    }

    [Fact]
    public void ColumnsWithNoContentSitAtTheirFloors()
    {
        var shares = ColumnShares.Compute(100, new[] { 0.0, 0.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 20.0, 20.0 }, shares);
    }

    [Fact]
    public void NoColumnsMeansNoShares()
    {
        Assert.Empty(ColumnShares.Compute(500, Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void MismatchedListsAreRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            ColumnShares.Compute(500, new[] { 1.0, 2.0 }, new[] { 1.0 }));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -v quiet` — expected: a compile error, `ColumnShares` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace OrdoSort.Wpf.Views;

/// <summary>The arithmetic behind <see cref="DataGridColumnCap"/>'s autofit:
/// how much of the width a grid has left each of its text columns gets.
/// Pure, so it is provable without a DataGrid — the WPF half of the rule
/// lives in DataGridColumnCap, this is the part a person can check with a
/// calculator.</summary>
internal static class ColumnShares
{
    /// <summary>One share per column, in the order given.
    ///
    /// When every column's wanted width — its natural (content) width, or
    /// its floor if that is larger — fits in <paramref name="available"/>,
    /// each column gets exactly that: content-sized, nothing wraps. When
    /// they don't fit, the width is split in proportion to wanted width,
    /// and a column whose proportional share would fall under its floor is
    /// held AT the floor with the rest re-split among the others — so a
    /// long message wraps rather than squeezing a file name below the
    /// floor its window declared. Floors are honoured even when they alone
    /// exceed the width; that overflow is WPF's to resolve (a horizontal
    /// scrollbar), and every window's own MinWidth is what makes it
    /// unreachable in practice.</summary>
    /// <param name="available">Width the columns may use between them.</param>
    /// <param name="natural">Each column's content width.</param>
    /// <param name="floors">Each column's MinWidth; same length as <paramref name="natural"/>.</param>
    /// <exception cref="ArgumentException">The two lists differ in length.</exception>
    public static double[] Compute(double available, IReadOnlyList<double> natural, IReadOnlyList<double> floors)
    {
        if (natural.Count != floors.Count)
            throw new ArgumentException(
                $"{natural.Count} natural widths against {floors.Count} floors", nameof(floors));

        var count = natural.Count;
        var wanted = new double[count];
        for (var i = 0; i < count; i++) wanted[i] = Math.Max(natural[i], floors[i]);

        var shares = new double[count];
        if (wanted.Sum() <= available)
        {
            Array.Copy(wanted, shares, count);
            return shares;
        }

        // Proportional split with floors. Holding a column at its floor
        // changes what is left for the others, which can push another
        // column under ITS floor, so this repeats until a pass holds nobody
        // new — at most one pass per column.
        var held = new bool[count];
        while (true)
        {
            var remaining = available;
            var pool = 0.0;
            for (var i = 0; i < count; i++)
            {
                if (held[i]) remaining -= floors[i];
                else pool += wanted[i];
            }

            var newlyHeld = false;
            for (var i = 0; i < count; i++)
            {
                if (held[i])
                {
                    shares[i] = floors[i];
                    continue;
                }
                shares[i] = pool > 0 ? remaining * wanted[i] / pool : floors[i];
                if (shares[i] < floors[i])
                {
                    held[i] = true;
                    newlyHeld = true;
                }
            }
            if (!newlyHeld) return shares;
        }
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -v quiet && dotnet test tests/OrdoSort.Wpf.Tests --no-build -v minimal --filter "FullyQualifiedName~ColumnSharesTests"` — expected: `Passed! … Total: 11`.

- [ ] **Step 5: Revert-proof check**

Change `if (shares[i] < floors[i])` to `if (false)` → `AColumnWhoseShareWouldFallUnderItsFloor…`, `HoldingOneColumn…` and `FloorsThatAloneExceed…` fail with wrong numbers. Change `Math.Max(natural[i], floors[i])` to `natural[i]` → `AFloorAboveTheNaturalWidthWins…` fails. Restore both; rerun; record the messages in the report.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Views/ColumnShares.cs tests/OrdoSort.Wpf.Tests/ColumnSharesTests.cs
git commit -m "feat(grid): the share arithmetic behind autofit, as a pure function

Columns get their content width when it fits and a proportional share
held at their floors when it doesn't. Kept free of WPF so the rule can be
checked with a calculator before DataGridColumnCap wires it to a grid.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc"
```

---

### Task 2: `DataGridColumnCap` measures content and applies the split

**Files:**
- Rewrite: `src/OrdoSort.Wpf/Views/DataGridColumnCap.cs` (the whole file — its 200-line doc comment describes six fix rounds of a rule that no longer exists; git history keeps them)
- Test: `tests/OrdoSort.Wpf.Tests/DataGridColumnCapTests.cs` (new)

**Interfaces:**
- Consumes: `ColumnShares.Compute` (Task 1).
- Produces (unchanged signatures, so no window's code-behind changes): `DataGridColumnCap.Track(DataGrid grid, params DataGridColumn[] columns)` and `Track(DataGrid grid, Func<double, double> computeCap, params DataGridColumn[] columns)`. New behaviour of the first overload: governed columns' `MaxWidth` = their `ColumnShares` share, with the grid's star columns as silent participants. The second overload (Triage) is behaviourally unchanged: every governed column gets `computeCap(viewport)`.

- [ ] **Step 1: Write the failing measured facts**

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-29, "the columns don't autofit, hiding text, and I have
/// to readjust every time": DataGridColumnCap's rule became autofit-then-
/// wrap. These facts prove the mechanism on a bare grid built in code — a
/// star Name column with a floor and a tracked Auto Note column, both
/// wrapping, the shape every tool window has — so each one is about the
/// class, not about any window's XAML (AutoFitColumnTests covers the
/// windows).
///
/// The WPF facts they lean on were measured before the class was written
/// (see the plan, docs/superpowers/plans/2026-08-29-grid-autofit-wrap.md):
/// a wrapped cell grows its row; an Auto column displays at min(desired,
/// MaxWidth); WPF's desired width never shrinks, which is why the class
/// caps at the MEASURED width even when everything fits — relaxing to
/// infinity would leave a column at its old width forever.</summary>
[Collection(HighlightContrastTests.Name)]
public class DataGridColumnCapTests
{
    private readonly HighlightContrastFixture _fx;
    public DataGridColumnCapTests(HighlightContrastFixture fx) => _fx = fx;

    private sealed class Row
    {
        public string Name { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private sealed record BareGrid(
        Window Window, DataGrid DataGrid, DataGridTextColumn Name, DataGridTextColumn Note,
        ObservableCollection<Row> Rows);

    /// <summary>Track is called the way the windows call it — only the Auto
    /// column is passed; the star column is discovered.</summary>
    private static BareGrid Build(double windowWidth, double nameFloor, params Row[] rows)
    {
        var items = new ObservableCollection<Row>(rows);
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        var name = new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = nameFloor,
            ElementStyle = Wrapping(),
        };
        var note = new DataGridTextColumn
        {
            Header = "Note", Binding = new Binding("Note"), Width = DataGridLength.Auto,
            ElementStyle = Wrapping(),
        };
        grid.Columns.Add(name);
        grid.Columns.Add(note);
        DataGridColumnCap.Track(grid, note);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false,
        };
        return new BareGrid(window, grid, name, note, items);
    }

    private static Style Wrapping()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        return style;
    }

    private static void ShowAndSettle(Window window)
    {
        window.Show();
        Settle(window);
    }

    /// <summary>UpdateLayout, then drain Background priority: WPF reconciles
    /// star-column widths there, and a headless window never gets the
    /// WM_SIZE that does it for free (AutoFitColumnTests.ShowOffscreenThenResizeTo
    /// records that measurement).</summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static DataGridRow RowAt(DataGrid grid, int index) =>
        (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);

    private static TextBlock CellText(DataGrid grid, DataGridColumn column, int rowIndex) =>
        (TextBlock)column.GetCellContent(RowAt(grid, rowIndex));

    /// <summary>What the class measures for one cell, restated so a fact
    /// can say "as wide as its content" with a number.</summary>
    private static double ContentWidthOf(TextBlock text) =>
        Math.Ceiling(new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, null!, TextOptions.GetTextFormattingMode(text),
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace);

    private static double LineHeightOf(TextBlock text) => text.FontSize * text.FontFamily.LineSpacing;

    /// <summary>The class's own budget, restated: the grid's width less the
    /// vertical-scrollbar reservation and its 20px safety margin. The same
    /// pattern as AutoFitColumnTests.ExpectedTriageColumnCap — a fact that
    /// uses it doubles as a check that the constants haven't drifted.</summary>
    private static double AvailableWidthOf(DataGrid grid) =>
        grid.ActualWidth - SystemParameters.VerticalScrollBarWidth - 20;

    private static Visibility HorizontalScrollbarOf(DataGrid grid) =>
        FindDescendant<ScrollViewer>(grid)!.ComputedHorizontalScrollBarVisibility;

    private static string Ms(int count) => new('M', count);

    [Fact]
    public void WhenEverythingFitsTheTrackedColumnIsExactlyAsWideAsItsContent() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = "merged 3 PDFs" });
        try
        {
            ShowAndSettle(g.Window);
            var text = CellText(g.DataGrid, g.Note, 0);
            var content = ContentWidthOf(text);
            Assert.True(Math.Abs(g.Note.ActualWidth - content) <= 2,
                $"Note is {g.Note.ActualWidth}px for {content}px of content — expected the content width, give or take a pixel of slack");
            Assert.True(RowAt(g.DataGrid, 0).ActualHeight < 1.5 * LineHeightOf(text),
                "nothing should wrap when everything fits");
            Assert.True(g.Name.ActualWidth > 400,
                $"the star column should take the rest, not sit at its floor: {g.Name.ActualWidth}px");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void WhenTheContentDoesNotFitTheWidthIsSplitInProportionAndTheTextWraps() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        // Thirty M's against sixty: content widths in a 1:2 ratio, both far
        // wider than a 500px window can show.
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
            var nameContent = ContentWidthOf(CellText(g.DataGrid, g.Name, 0)) + 1;
            var noteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0)) + 1;
            var expectedNoteShare = AvailableWidthOf(g.DataGrid) * noteContent / (nameContent + noteContent);
            Assert.True(Math.Abs(g.Note.MaxWidth - expectedNoteShare) <= 3,
                $"Note's cap is {g.Note.MaxWidth}px; splitting {AvailableWidthOf(g.DataGrid)}px between " +
                $"{nameContent}px and {noteContent}px of content in proportion gives {expectedNoteShare}px");
            Assert.True(Math.Abs(g.Note.ActualWidth - g.Note.MaxWidth) <= 1,
                $"Note should sit at its cap: {g.Note.ActualWidth}px against {g.Note.MaxWidth}px");
            Assert.True(g.Name.ActualWidth >= 150,
                $"Name should get its share rather than be starved to its 40px floor: {g.Name.ActualWidth}px");
            var noteText = CellText(g.DataGrid, g.Note, 0);
            Assert.True(noteText.ActualHeight >= 2 * LineHeightOf(noteText),
                $"Note should wrap: {noteText.ActualHeight}px against a {LineHeightOf(noteText)}px line");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AColumnWhoseShareWouldFallUnderItsFloorIsHeldThereAndTheOtherTakesTheRest() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 180, new Row { Name = "a.pdf", Note = Ms(80) });
        try
        {
            ShowAndSettle(g.Window);
            // A proportional share of a short name would be ~70px; the 180px
            // floor holds, and Note gets exactly what the floor leaves.
            var expectedNoteCap = AvailableWidthOf(g.DataGrid) - 180;
            Assert.True(Math.Abs(g.Note.MaxWidth - expectedNoteCap) <= 2,
                $"Note's cap is {g.Note.MaxWidth}px; with Name held at its 180px floor it should be {expectedNoteCap}px");
            Assert.True(g.Name.ActualWidth >= 180,
                $"Name must never drop under its floor: {g.Name.ActualWidth}px");
            Assert.True(Math.Abs(g.Note.ActualWidth - g.Note.MaxWidth) <= 1, "Note should sit at its cap");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AWrappedCellGrowsItsRowRatherThanClippingTheText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) }, new Row { Name = "b.pdf", Note = "ok" });
        try
        {
            ShowAndSettle(g.Window);
            var wrapped = CellText(g.DataGrid, g.Note, 0);
            var lineHeight = LineHeightOf(wrapped);
            Assert.True(wrapped.ActualHeight >= 2 * lineHeight,
                $"the long cell should be at least two lines: {wrapped.ActualHeight}px against {lineHeight}px");
            Assert.True(RowAt(g.DataGrid, 0).ActualHeight >= wrapped.ActualHeight - 1,
                $"the row ({RowAt(g.DataGrid, 0).ActualHeight}px) must grow to hold the wrapped text ({wrapped.ActualHeight}px)");
            Assert.True(RowAt(g.DataGrid, 1).ActualHeight < 1.5 * lineHeight,
                $"the short row beside it should stay one line: {RowAt(g.DataGrid, 1).ActualHeight}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void ATrackedColumnShrinksBackWhenItsLongContentIsRemoved() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = Ms(60) }, new Row { Name = "b.pdf", Note = "ok" });
        try
        {
            ShowAndSettle(g.Window);
            Assert.True(g.Note.ActualWidth > 500, $"precondition: the long row should make Note wide ({g.Note.ActualWidth}px)");
            g.Rows.RemoveAt(0);
            Settle(g.Window);
            // WPF alone would leave it at the old width — its desired width
            // never shrinks (measured: 802px after the long row was removed).
            Assert.True(g.Note.ActualWidth < 80,
                $"Note should shrink back to its remaining content once the long row is gone: {g.Note.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AHeaderWiderThanEveryCellSetsTheTrackedColumnsWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = "ok" });
        g.Note.Header = "A considerably longer column header";
        try
        {
            ShowAndSettle(g.Window);
            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Note);
            var headerText = FindDescendant<TextBlock>(header)!;
            // Theme/Styles.xaml gives DataGridColumnHeader Padding="8,6".
            var expected = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;
            Assert.True(Math.Abs(g.Note.ActualWidth - expected) <= 3,
                $"Note is {g.Note.ActualWidth}px; its header needs {expected}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AStarColumnIsNeverGivenACapOfItsOwn() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
            // It takes what the governed columns leave; capping it too would
            // fight WPF's own star reconciliation.
            Assert.True(double.IsPositiveInfinity(g.Name.MaxWidth),
                $"the star column's MaxWidth should be untouched: {g.Name.MaxWidth}");
        }
        finally { g.Window.Close(); }
    });

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -v quiet && dotnet test tests/OrdoSort.Wpf.Tests --no-build -v minimal --filter "FullyQualifiedName~DataGridColumnCapTests"`

Expected with the OLD class: `WhenEverythingFits…` fails (the remainder rule caps Note at the leftover, not its content — the number is wrong), `WhenTheContentDoesNotFit…` fails (equal-share cap ≠ proportional), `AColumnWhoseShare…` may pass by coincidence (record it either way), `ATrackedColumnShrinksBack…` fails (Note stays wide), `AHeaderWider…` may pass (WPF's own desired includes the header), `AStarColumn…` passes. At least four must fail for a value reason before Step 3.

- [ ] **Step 3: Rewrite `DataGridColumnCap.cs`**

Replace the whole file with this:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrdoSort.Wpf.Views;

/// <summary>Autofit for a DataGrid's text columns: every column gets its
/// content width when the total fits the grid, and when it doesn't, the
/// shortfall is shared in proportion to content width — no column below
/// its MinWidth — with the text in the columns that gave way wrapping onto
/// extra lines (each column's own ElementStyle carries the TextWrapping).
/// No horizontal scrollbar, nothing hidden behind an ellipsis, nothing
/// for the user to readjust.
///
/// Until 2026-08-29 this class capped the message column at whatever was
/// left after the name column's floor, and the name column got the
/// leftovers — six fix rounds of it, all in git history. The owner's
/// report that ended it: "the columns don't autofit, hiding text, and I
/// have to readjust every time." Both halves were that rule: the message
/// column cut names down to their floor and trimmed itself with an
/// ellipsis, and every window reopened with the same squeeze.
///
/// SIX windows depend on this class through <see cref="Track(DataGrid,
/// DataGridColumn[])"/>: History, MatchMerge, BulkRename, PageCounts,
/// ZipTools and MergePdfs. Triage supplies its own budget through the
/// <see cref="Func{Double,Double}"/> overload and is untouched by the
/// autofit rule. FilenameListWindow calls neither.
///
/// HOW. Track keeps every governed column's MaxWidth at its share,
/// recomputed after every layout pass (SizeChanged fires before the other
/// columns have laid out, so LayoutUpdated is the one that reads settled
/// numbers; assignments happen only when a cap actually moves, which is
/// what lets the LayoutUpdated cycle end). The share comes from
/// <see cref="ColumnShares.Compute"/> over the PARTICIPANTS — the governed
/// columns plus every visible star column in the grid — against what the
/// viewport has left after the fixed claims: absolute widths, and every
/// other column at its live width (an untracked Auto column has bounded
/// content — a date, a count, a tag — so what it measures is what it
/// needs; a column the user has dragged is absolute, and theirs).
///
/// A star column is a participant but is never assigned: it takes what
/// the governed columns leave, which is its share by construction once
/// every governed column sits at its own. (The vertical-scrollbar
/// reservation and the safety margin land in it too, so a star column is
/// a few pixels wider than its share, never narrower.)
///
/// MEASURED, NOT READ BACK. A column's content width is the widest realized
/// cell's text — FormattedText in that cell's own font, cached per string —
/// or its header, whichever is wider. WPF's own Width.DesiredValue would be
/// cheaper, but it only ever grows (measured 2026-08-29: 802px after the
/// long row was removed), so a class that read it could never shrink a
/// column back. Measuring is exact (801.53px against WPF's 802px for the
/// same string; the default DataGridCell template applies no padding), and
/// <see cref="MeasureSlack"/> covers the rounding so a one-line cell is
/// never wrapped by its own cap. Capping at the measured width in the fit
/// case — rather than relaxing to infinity — is the whole shrink-back
/// mechanism: an Auto column displays at min(desired, MaxWidth).
///
/// Only realized rows are measured, the same population WPF sizes an Auto
/// column from, so a column can still widen as longer rows scroll into
/// view — the pre-existing behaviour docs/superpowers/plans/
/// 2026-08-14-column-stability-while-scrolling.md measured and left alone.
///
/// A user's drag still wins: DragStarted relaxes every not-yet-pinned
/// governed column so WPF's live clamp can't block the gesture;
/// DragCompleted reads "Width became absolute" as "this one was dragged"
/// and pins it for the window's lifetime — out of the governed set, in
/// with the fixed claims. Track is idempotent per grid: a second call
/// detaches the first call's handlers before subscribing its own.</summary>
internal static class DataGridColumnCap
{
    /// <summary>Keeps the arithmetic off the exact viewport edge, where a
    /// rounding difference decides whether a scrollbar appears, and
    /// absorbs an untracked Auto column growing between recomputes.</summary>
    private const double SafetyMargin = 20;

    /// <summary>Floor under any cap, matching the floor every window's own
    /// column MinWidths already respect; WPF's own space-fitting resolves
    /// the layout below it.</summary>
    private const double MinimumCap = 20;

    /// <summary>Added to every measured width so a rounding difference
    /// between FormattedText and the layout engine can never wrap a cell
    /// that fits on one line.</summary>
    private const double MeasureSlack = 1;

    /// <summary>Autofit-then-wrap for <paramref name="columns"/> — the
    /// grid's Auto text columns that may wrap. Star columns are found from
    /// the grid itself and need not be passed.</summary>
    public static void Track(DataGrid grid, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap: null, columns);

    /// <summary>Same live tracking, but every governed column's cap is
    /// <paramref name="computeCap"/> applied to the grid's live column
    /// viewport width (net of the vertical-scrollbar reservation) — for
    /// TriageWindow, whose budget depends on how many roster columns exist
    /// and whether its fixed "Why" column might appear.</summary>
    public static void Track(DataGrid grid, Func<double, double> computeCap, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap, columns);

    private static void TrackCore(DataGrid grid, Func<double, double>? computeCap, DataGridColumn[] columns)
    {
        var pinned = new HashSet<DataGridColumn>();
        var widths = new ContentWidths();

        void Recalculate()
        {
            // 0 before the grid's first layout pass — nothing to size
            // against yet; SizeChanged fires the moment a real width exists.
            if (grid.ActualWidth <= 0) return;
            var columnViewportWidth = Math.Max(0, grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);

            var governed = columns.Where(column => !pinned.Contains(column)).ToArray();
            if (governed.Length == 0) return;

            var caps = computeCap is not null
                ? Enumerable.Repeat(computeCap(columnViewportWidth), governed.Length).ToArray()
                : AutofitCaps(grid, columnViewportWidth, governed, widths);

            // Assign only what moved: an assignment invalidates layout, which
            // raises LayoutUpdated, which recomputes — so unconditional
            // assignment would never let the cycle end.
            for (var i = 0; i < governed.Length; i++)
                if (Math.Abs(governed[i].MaxWidth - caps[i]) > 0.5) governed[i].MaxWidth = caps[i];
        }

        (grid.GetValue(DetachProperty) as Action)?.Invoke();

        void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Recalculate();
        grid.SizeChanged += OnSizeChanged;

        var recomputing = false;
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (recomputing) return;
            recomputing = true;
            try { Recalculate(); }
            finally { recomputing = false; }
        }
        grid.LayoutUpdated += OnLayoutUpdated;

        Recalculate();

        // handledEventsToo: nothing in this app's column templates marks
        // these handled today; a future template that did would silently
        // break the drag fix otherwise.
        var dragStarted = new DragStartedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column)) column.MaxWidth = double.PositiveInfinity;
        });
        grid.AddHandler(Thumb.DragStartedEvent, dragStarted, true);

        var dragCompleted = new DragCompletedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column) && column.Width.IsAbsolute) pinned.Add(column);
            Recalculate();
        });
        grid.AddHandler(Thumb.DragCompletedEvent, dragCompleted, true);

        grid.SetValue(DetachProperty, new Action(() =>
        {
            grid.SizeChanged -= OnSizeChanged;
            grid.LayoutUpdated -= OnLayoutUpdated;
            grid.RemoveHandler(Thumb.DragStartedEvent, dragStarted);
            grid.RemoveHandler(Thumb.DragCompletedEvent, dragCompleted);
        }));
    }

    /// <summary>Holds the Action that detaches the current Track call's
    /// handlers, so the next Track call on the same grid can run it first.
    /// An attached property rather than a static table because it lives
    /// and dies with the grid.</summary>
    private static readonly DependencyProperty DetachProperty =
        DependencyProperty.RegisterAttached(
            "Detach", typeof(Action), typeof(DataGridColumnCap), new PropertyMetadata(null));

    /// <summary>One cap per governed column, in the same order.</summary>
    private static double[] AutofitCaps(
        DataGrid grid, double viewportWidth, DataGridColumn[] governed, ContentWidths widths)
    {
        var governedSet = new HashSet<DataGridColumn>(governed);
        var participants = new List<DataGridColumn>(governed);
        var claimed = 0.0;
        foreach (var column in grid.Columns)
        {
            if (governedSet.Contains(column) || column.Visibility != Visibility.Visible) continue;
            if (column.Width.IsStar) participants.Add(column);
            else claimed += column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        }

        var available = viewportWidth - claimed - SafetyMargin;
        var rows = RealizedRows(grid);
        var natural = participants.Select(column => widths.Of(grid, column, rows)).ToList();
        var floors = participants.Select(column => Math.Max(MinimumCap, column.MinWidth)).ToList();
        var shares = ColumnShares.Compute(available, natural, floors);
        return shares.Take(governed.Length).ToArray();
    }

    /// <summary>The rows WPF currently has containers for — the same
    /// population it sizes an Auto column from. One pass per recompute,
    /// shared by every participant.</summary>
    private static List<DataGridRow> RealizedRows(DataGrid grid)
    {
        var rows = new List<DataGridRow>();
        var count = grid.Items.Count;
        for (var i = 0; i < count; i++)
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row) rows.Add(row);
        return rows;
    }

    /// <summary>Content widths from the realized cells and the header,
    /// cached per string so a layout pass costs a dictionary lookup per
    /// visible cell. Font properties are part of the key: BulkRename bolds
    /// a hand-edited row, and Settings can change the app's type scale
    /// while a window is open.</summary>
    private sealed class ContentWidths
    {
        private readonly Dictionary<(string Text, string Family, double Size, FontWeight Weight, FontStyle Style), double> _measured = new();
        private DataGridColumnHeadersPresenter? _headers;

        public double Of(DataGrid grid, DataGridColumn column, List<DataGridRow> rows)
        {
            var widest = HeaderWidthOf(grid, column);
            foreach (var row in rows)
            {
                var width = column.GetCellContent(row) switch
                {
                    TextBlock text => TextWidthOf(text),
                    FrameworkElement other => other.DesiredSize.Width,
                    _ => 0,
                };
                widest = Math.Max(widest, width);
            }
            return widest;
        }

        /// <summary>The header's own text plus the header's padding, measured
        /// rather than read from its DesiredSize, which is clipped to the
        /// column once the column is capped.</summary>
        private double HeaderWidthOf(DataGrid grid, DataGridColumn column)
        {
            _headers ??= FindDescendant<DataGridColumnHeadersPresenter>(grid);
            if (_headers is null) return 0;
            var header = FindDescendants<DataGridColumnHeader>(_headers).FirstOrDefault(h => h.Column == column);
            if (header is null) return 0;
            var text = FindDescendant<TextBlock>(header);
            return text is null
                ? header.DesiredSize.Width
                : TextWidthOf(text) + header.Padding.Left + header.Padding.Right;
        }

        private double TextWidthOf(TextBlock text)
        {
            var key = (text.Text, text.FontFamily.Source, text.FontSize, text.FontWeight, text.FontStyle);
            if (!_measured.TryGetValue(key, out var width))
            {
                width = text.Text.Length == 0 ? 0 : Math.Ceiling(new FormattedText(
                    text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                    text.FontSize, Brushes.Black,
                    // no number substitution — the same default a TextBlock
                    // formats with; the parameter is unannotated, ! keeps the
                    // nullable analysis quiet either way
                    null!, TextOptions.GetTextFormattingMode(text),
                    VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace) + MeasureSlack;
                _measured[key] = width;
            }
            return width + text.Margin.Left + text.Margin.Right + text.Padding.Left + text.Padding.Right;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindDescendants<T>(child));
        }
        return results;
    }
}
```

- [ ] **Step 4: Run the new facts and the whole Wpf suite**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -v quiet && dotnet test tests/OrdoSort.Wpf.Tests --no-build -v minimal --filter "FullyQualifiedName~DataGridColumnCapTests"` — expected `Passed! … Total: 7`.

Then the full check (Global Constraints). EXPECTED FAILURES at this point, all in `AutoFitColumnTests`, and only these: the five `*_Long…ValueStopsAtTheCapWithEllipsisAndTooltip` facts for MatchMerge, BulkRename, PageCounts, ZipTools and MergePdfs may still PASS (the columns still trim — Tasks 3–5 change that) — fine; `Triage_*` must all pass (the overload is untouched). If anything else fails, it is a defect in this task: fix it here. Record the exact list of failures in the report.

- [ ] **Step 5: Revert-proof each fact**

One at a time, restore between:
- `WhenEverythingFits…`: in `TextWidthOf`, return `0` instead of the measurement → Note collapses to its floor → fails with a wrong number.
- `WhenTheContentDoesNotFit…`: replace `ColumnShares.Compute(available, natural, floors)` with an equal split (`Enumerable.Repeat(available / participants.Count, participants.Count).ToArray()`) → the cap is wrong → fails.
- `AColumnWhoseShare…`: pass `Enumerable.Repeat(MinimumCap, participants.Count).ToList()` as floors → Name's share is ~70 → Note's cap is wrong → fails.
- `AWrappedCell…`: temporarily make `Build` use no `ElementStyle` → single-line clipped text → fails.
- `ATrackedColumnShrinksBack…`: in `ColumnShares.Compute`'s fit branch, return `double.PositiveInfinity` shares (simulating "relax to infinity") → Note stays wide → fails.
- `AHeaderWider…`: make `HeaderWidthOf` return 0 → the cap is the cell's 20px → fails.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Views/DataGridColumnCap.cs tests/OrdoSort.Wpf.Tests/DataGridColumnCapTests.cs
git commit -m "feat(grid): columns autofit to their content and wrap in proportion when it doesn't fit

The cap used to hand the message column whatever the name column's floor
left over, and both trimmed with an ellipsis; the owner had to widen
columns by hand every time a window opened. Now every text column gets
its measured content width when the total fits, and a proportional share
- held at its MinWidth - when it doesn't, so the ElementStyle's wrapping
takes over instead of an ellipsis. Measured rather than read from WPF's
desired width because that only ever grows; capping at the measured width
is what lets a column shrink back after a Clear.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc"
```

---

### Task 3: Zip and unzip + Merge PDFs wrap

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml` (Item and Result columns, lines ~86–178)
- Modify: `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml` (Item and Result columns, lines ~84–150)
- Test: `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs`

**Interfaces:**
- Consumes: `DataGridColumnCap.Track` (Task 2) — the windows' code-behind calls are unchanged.

- [ ] **Step 1: Write the failing facts**

In `AutoFitColumnTests.cs`:

(a) Add this helper next to `AssertTrimmingAndTooltip` (which stays, for Triage only):

```csharp
    /// <summary>2026-08-29 successor to AssertStoppedAtItsCap +
    /// AssertTrimmingAndTooltip for every window except Triage: a long
    /// value is still stopped BY its cap, but the cap now makes it wrap —
    /// the column's ElementStyle says so, no TextTrimming setter competes,
    /// and the realized cell is taller than one line with the row grown to
    /// hold it. Nothing is hidden, so no tooltip repeats the text.</summary>
    private static void AssertWrapsInsideItsCap(Window win, DataGridBoundColumn column, string name)
    {
        AssertStoppedAtItsCap(win, column, name);
        Assert.NotNull(column.ElementStyle);
        var setters = column.ElementStyle!.Setters.OfType<Setter>().ToList();
        Assert.Contains(setters, s => s.Property == TextBlock.TextWrappingProperty && Equals(s.Value, TextWrapping.Wrap));
        Assert.DoesNotContain(setters, s => s.Property == TextBlock.TextTrimmingProperty);

        var grid = FindDescendant<DataGrid>(win)!;
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        var text = Assert.IsType<TextBlock>(column.GetCellContent(row));
        var lineHeight = text.FontSize * text.FontFamily.LineSpacing;
        Assert.True(text.ActualHeight >= 2 * lineHeight,
            $"{name}: a value wider than its {column.MaxWidth}px cap should wrap onto more lines; " +
            $"the cell is {text.ActualHeight}px against a {lineHeight}px line");
        Assert.True(row.ActualHeight >= text.ActualHeight - 1,
            $"{name}: the row ({row.ActualHeight}px) must grow to hold the wrapped text ({text.ActualHeight}px)");
    }

    /// <summary>ShowOffscreen plus the Background-priority drain that
    /// reconciles star columns headlessly (see ShowOffscreenThenResizeTo).</summary>
    private static void SettleStarColumns(Window win)
    {
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        win.UpdateLayout();
    }
```

(b) Rename and rewrite the two zip-window long-value facts:

```csharp
    [Fact]
    public void ZipTools_LongResultValueWrapsInsideItsCap() => _fx.Invoke(() =>
    {
        var win = BuildZipToolsWindow(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            AssertWrapsInsideItsCap(win, (DataGridBoundColumn)column, "Zip and unzip Result");
        }
        finally { win.Close(); }
    });
```
and the same for `MergePdfs_LongResultValueWrapsInsideItsCap` with `BuildMergePdfsWindow` / `"Merge PDFs Result"`. Delete the two `…StopsAtTheCapWithEllipsisAndTooltip` facts they replace.

(c) Extend `BuildZipToolsWindow` with an optional display name so a fact can make the Item column long:

```csharp
    private static ZipToolsWindow BuildZipToolsWindow(string resultValue, int rowCount = 1, string? fileName = null)
    {
        var vm = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new ZipItemRow($@"C:\inbox\{fileName ?? $"f{i}.zip"}", "zip");
            row.Apply(new Zipper.UnzipResult(row.Path, "error", null, resultValue));
            vm.Rows.Add(row);
        }
        return new ZipToolsWindow(vm);
    }
```

(d) Two new ZipTools facts — the window-level proof of the two halves of the owner's report:

```csharp
    /// <summary>The "hiding text" half of the 2026-08-29 report, at the
    /// window's own MinWidth: a long file name AND a long result. The old
    /// remainder rule pinned Item at exactly its 180px floor and gave
    /// Result everything else; the proportional split gives Item a share
    /// well above the floor while Result still wraps inside its cap and
    /// no horizontal scrollbar appears. Star columns only resolve headlessly
    /// after the Background drain, hence SettleStarColumns.</summary>
    [Fact]
    public void ZipTools_ALongNameAndALongResultShareTheWidthRatherThanStarvingTheName() => _fx.Invoke(() =>
    {
        var win = BuildZipToolsWindow(VeryLongValue, rowCount: 1, fileName: VeryLongValue);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            SettleStarColumns(win);
            var item = FindColumnByHeader(win, "Item");
            var result = FindColumnByHeader(win, "Result");
            Assert.True(item.ActualWidth >= item.MinWidth + 40,
                $"Item should get a share of the width, not sit at its {item.MinWidth}px floor: {item.ActualWidth}px");
            AssertWrapsInsideItsCap(win, (DataGridBoundColumn)result, "Zip and unzip Result");
            AssertNoHorizontalScrollbar(win, $"Zip and unzip (at MinWidth {win.MinWidth}, long name and long result)");
        }
        finally { win.Close(); }
    });

    /// <summary>The "readjust every time" half: once the long results are
    /// gone the Result column shrinks back to its header, instead of
    /// keeping the width WPF's desired width would hold forever.</summary>
    [Fact]
    public void ZipTools_ClearingTheListLetsTheResultColumnShrinkBack() => _fx.Invoke(() =>
    {
        var win = BuildZipToolsWindow(VeryLongValue, rowCount: 3);
        try
        {
            ShowOffscreen(win);
            var result = FindColumnByHeader(win, "Result");
            Assert.True(result.ActualWidth > 200, $"precondition: long results should make Result wide ({result.ActualWidth}px)");
            ((ZipExtractViewModel)win.DataContext).Rows.Clear();
            SettleStarColumns(win);
            Assert.True(result.ActualWidth < 100,
                $"Result should shrink back to its header once the list is cleared: {result.ActualWidth}px");
        }
        finally { win.Close(); }
    });
```

- [ ] **Step 2: Run to see them fail**

`dotnet build tests/OrdoSort.Wpf.Tests -v quiet && dotnet test tests/OrdoSort.Wpf.Tests --no-build -v minimal --filter "FullyQualifiedName~AutoFitColumnTests.ZipTools|FullyQualifiedName~AutoFitColumnTests.MergePdfs"` — expected: the two `…WrapsInsideItsCap` facts fail on the `TextWrapping` setter assertion (the XAML still trims); `ZipTools_ALongName…` fails at the wrap assertion for the same reason; `ZipTools_Clearing…` passes already (Task 2's mechanism) — record that.

- [ ] **Step 3: Edit the XAML**

`ZipToolsWindow.xaml`, Item column — replace the comment sentence beginning "MinWidth is load-bearing arithmetic, not a nicety:" through "See PageCountsWindow.xaml." with:

```
                             MinWidth is this column's floor in
                             DataGridColumnCap's split: when Item and
                             Result can't both have their content width,
                             each gets a proportional share, never less
                             than this. Wrap, not trim — a name that has
                             to give way goes onto a second line rather
                             than behind an ellipsis (2026-08-29). The
                             ToolTip stays because it shows the full path,
                             which no column does.
```
and in its ElementStyle change `<Setter Property="TextTrimming" Value="CharacterEllipsis" />` to `<Setter Property="TextWrapping" Value="Wrap" />` (keep the `ToolTip` setter bound to `Path`).

Result column — replace "Result: content-sized, capped from the code-behind by DataGridColumnCap." with "Result: content-sized; DataGridColumnCap (code-behind) gives it its content width when that fits beside Item, and a proportional share — wrapped, never trimmed — when it doesn't." In its ElementStyle change the `TextTrimming` setter to `<Setter Property="TextWrapping" Value="Wrap" />` and DELETE `<Setter Property="ToolTip" Value="{Binding Note}" />` (the whole note is visible now).

`MergePdfsWindow.xaml`: the same two edits — Item (wrap; keep the `Path` tooltip; the comment above it that ends "…squeezes this column to WPF's 20px default." gets the same replacement sentence as ZipTools's), Result (wrap; delete the `Note` tooltip; replace "Result: content-sized, capped from the code-behind by DataGridColumnCap." the same way).

- [ ] **Step 4: Run the facts, then the full check**

The filtered run: all ZipTools/MergePdfs facts pass (`ShortResultValueMeasuresNarrow`, `…WrapsInsideItsCap`, `AtMinWidthNoHorizontalScrollbar`, the two new ones). Then the full check per Global Constraints — everything green except, possibly, the three `…StopsAtTheCapWithEllipsisAndTooltip` facts for MatchMerge/BulkRename/PageCounts, which must still PASS (those windows still trim until Task 4).

- [ ] **Step 5: Revert-proof**

Revert the Result column's `TextWrapping` setter back to `TextTrimming` in ZipToolsWindow.xaml → `ZipTools_LongResultValueWrapsInsideItsCap` and `ZipTools_ALongName…` fail on the setter assertion; restore. Change `Track(ItemsGrid, ItemsResultColumn)` in `ZipToolsWindow.xaml.cs` to `Track(ItemsGrid, viewport => viewport - 250, ItemsResultColumn)` (the Triage-style flat cap) → `ZipTools_ALongName…` fails because Item sits at its floor; restore.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs
git commit -m "feat(zip): Item and Result wrap instead of trimming, and share the width

The two zip windows are where the owner hit the squeeze: a long result
cut the file name down to its floor and hid the rest of both behind
ellipses. With DataGridColumnCap's autofit the columns split the width
in proportion and wrap, so nothing is hidden and nothing needs dragging.
The Note tooltips go - the whole note is on screen - while the Path
tooltip stays, since no column shows the full path.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc"
```

---

### Task 4: PDF page counts, Match & merge, Bulk rename wrap

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml` (File star column ~line 80, Note column ~line 134)
- Modify: `src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml` (File ~195, Becomes ~220, Note ~238)
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml` (Current name ~243, New name ~272, Note ~317)
- Test: `tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs`

**Interfaces:** consumes Task 3's `AssertWrapsInsideItsCap`.

- [ ] **Step 1: Rewrite the three facts**

Replace `MatchMerge_LongFileValueStopsAtTheCapWithEllipsisAndTooltip`, `BulkRename_LongCurrentValueStopsAtTheCapWithEllipsisAndTooltip` and `PageCounts_LongNoteValueStopsAtTheCapWithEllipsisAndTooltip` with `MatchMerge_LongFileValueWrapsInsideItsCap`, `BulkRename_LongCurrentValueWrapsInsideItsCap`, `PageCounts_LongNoteValueWrapsInsideItsCap` — each keeps its builder and `ShowOffscreen`, then:

```csharp
            var column = FindColumnByHeader(win, "File");        // "Current name" / "Note" respectively
            AssertWrapsInsideItsCap(win, (DataGridBoundColumn)column, "MatchMerge File");   // "BulkRename Current name" / "PageCounts Note"
```

Run the filtered suite (`--filter "FullyQualifiedName~AutoFitColumnTests"`): exactly those three fail on the `TextWrapping` setter assertion.

- [ ] **Step 2: Edit the XAML**

For every column below: in its ElementStyle, change `<Setter Property="TextTrimming" Value="CharacterEllipsis" />` to `<Setter Property="TextWrapping" Value="Wrap" />`, and delete the `ToolTip` setter when it binds the SAME property the column displays (it would only repeat the visible text). Keep a `ToolTip` that shows something else.

| Window | Column | ToolTip |
|---|---|---|
| PageCounts | File (star) | keep — `{Binding Path}` shows the full path |
| PageCounts | Note | delete `{Binding Note}` |
| MatchMerge | File | delete `{Binding File}` |
| MatchMerge | Becomes (star) | delete `{Binding Becomes}` |
| MatchMerge | Note | delete `{Binding Note}` |
| BulkRename | Current name | delete `{Binding Current}` |
| BulkRename | New name (star, editable) | delete `{Binding NewName}` — the editing TextBox is untouched (single-line editing is right for a file name); only the display TextBlock wraps |
| BulkRename | Note | delete `{Binding Note}` |

Comments to update, each to one or two sentences in the same voice as the file:
- PageCounts File: the comment ending "180 is the app's existing floor for a filename column." → add "Since 2026-08-29 the floor is this column's share floor in DataGridColumnCap's proportional split, and the name wraps rather than trims when it gives way."
- PageCounts Note / MatchMerge File / MatchMerge Note / BulkRename Current name / BulkRename Note: wherever a comment says "content-sized and capped" or "capped from the code-behind", say "content-sized; DataGridColumnCap gives it its content width when that fits and a proportional, wrapped share when it doesn't (2026-08-29)".
- MatchMerge Becomes and BulkRename New name (the star fillers): after "…so it gets whatever width File and Note don't need." / "…don't need — now ALL of the leftover…" add: "It is also a participant in DataGridColumnCap's split: when the columns can't all have their content width it gets a proportional share, never under this floor, and wraps."

- [ ] **Step 3: Run the facts, then the full check**

Filtered: every AutoFitColumnTests fact passes (Triage's `…StopsAtTheCapWithEllipsisAndTooltip` included — it is untouched). Then the full check.

- [ ] **Step 4: Revert-proof**

Revert MatchMergeWindow.xaml's File setter to `TextTrimming` → `MatchMerge_LongFileValueWrapsInsideItsCap` fails on the setter; restore. Do the same once for BulkRename's Current name and PageCounts's Note.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs
git commit -m "feat(tools): page counts, match & merge and bulk rename wrap instead of trimming

Same rule as the zip windows, same reason: a column that gives way now
puts its text on a second line instead of behind an ellipsis, and the
tooltips that only repeated the cell go. The full-path tooltip on page
counts' File column stays.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc"
```

---

### Task 5: History wraps; `When` leaves the governed set

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml` (When ~133, Original ~193, Filed as ~215, Name ~243, Destination ~276)
- Modify: `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml.cs:20`
- Test: `tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs`

**Interfaces:** none new.

- [ ] **Step 1: Rewrite the theory and add the `When` fact**

In `HistoryWindowXamlTests.cs`, replace `NameAndDestinationColumnsTrimWithEllipsisAndCarryATooltip` (keep its `[InlineData]` rows for Name, Destination, Original, Filed as; drop the `bindingPath` parameter) with:

```csharp
    /// <summary>2026-08-29: the four text columns wrap instead of trimming
    /// — DataGridColumnCap's autofit gives each its content width when that
    /// fits and a proportional share when it doesn't, and a share is only
    /// honest if the text it can't show on one line goes onto the next.
    /// No tooltip repeats the cell: the whole value is on screen.</summary>
    [Theory]
    [InlineData("Name")]
    [InlineData("Destination")]
    [InlineData("Original")]
    [InlineData("Filed as")]
    public void TextColumnsWrapRatherThanTrim(string header) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("No DataGrid descendant found");
            var column = grid.Columns.OfType<DataGridTextColumn>()
                .FirstOrDefault(c => (string)c.Header == header)
                ?? throw new InvalidOperationException($"No '{header}' column found");

            Assert.NotNull(column.ElementStyle);
            var setters = column.ElementStyle!.Setters.OfType<Setter>().ToList();
            Assert.Contains(setters, s => s.Property == TextBlock.TextWrappingProperty && Equals(s.Value, TextWrapping.Wrap));
            Assert.DoesNotContain(setters, s => s.Property == TextBlock.TextTrimmingProperty);
            Assert.DoesNotContain(setters, s => s.Property == FrameworkElement.ToolTipProperty);
        }
        finally { Cleanup(win, history, dbPath); }
    });

    /// <summary>When holds a timestamp History formats itself — bounded, 16
    /// characters — so it is no longer one of the governed columns: sized
    /// to its content, never asked to give way, never wrapped mid-date.
    /// An uncapped column's MaxWidth is WPF's default, infinity.</summary>
    [Fact]
    public void WhenIsNotCappedBecauseItsContentIsBounded() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -20000;
            win.Top = 0;
            win.ShowActivated = false;
            win.Show();
            win.UpdateLayout();
            var grid = FindDescendant<DataGrid>(win)!;
            var when = grid.Columns.First(c => (string)c.Header == "When");
            Assert.True(double.IsPositiveInfinity(when.MaxWidth),
                $"When should not be governed by DataGridColumnCap: MaxWidth is {when.MaxWidth}");
        }
        finally { Cleanup(win, history, dbPath); }
    });
```
(Check how `BuildWindow`/`Cleanup` in this file show the window; if `BuildWindow` already shows it, drop the Show lines. `Cleanup` closes it.)

Run `--filter "FullyQualifiedName~HistoryWindowXamlTests"`: the four theory rows fail on the setter assertions; `WhenIsNotCapped…` fails because the code-behind still tracks When.

- [ ] **Step 2: Edit the XAML and the code-behind**

`HistoryWindow.xaml.cs:20` → `DataGridColumnCap.Track(HistoryGrid, NameColumn, DestinationColumn);`

`HistoryWindow.xaml`:
- When: delete BOTH the `TextTrimming` and the `ToolTip` setters (keep the style, its `Reverted` trigger and its selection trigger); replace the comment immediately above the column (the one ending "…has no realistic way to happen for them.") with a short one: "When: a 16-character timestamp History formats itself — bounded content, so it is sized to itself and left out of DataGridColumnCap's split (2026-08-29); the four text columns below are the participants."
- Original, Filed as, Name, Destination: `TextTrimming` → `<Setter Property="TextWrapping" Value="Wrap" />`; delete each `ToolTip` setter.
- Replace the comment "TextTrimming/ToolTip: Name and Destination clip without ellipsis or any way to recover the full value if they didn't have this (2026-08-02 audit-remediation, Task 7 Step 3) — still true now that they're capped-Auto rather than fixed-width, so it stays." with: "Name and Destination: content-sized; DataGridColumnCap gives each its content width when the four text columns fit and a proportional share — wrapped, never trimmed — when they don't (2026-08-29)."
- In the long MinWidth="120" comment above Original, leave the measurements; append one sentence at its end: "Since 2026-08-29 the same 120px is also the floor of these two columns' share in DataGridColumnCap's proportional split, and they wrap rather than trim when they give way."

- [ ] **Step 3: Run the History suites, then the full check**

`--filter "FullyQualifiedName~HistoryWindowXamlTests|FullyQualifiedName~DataGridStarColumnTests|FullyQualifiedName~AutoFitColumnTests.History"` all green (DataGridStarColumnTests' "exactly two star columns at exactly their floor" fact is unaffected: star columns are still never assigned). Then the full check: Core 750, Wpf ≥ 1989 + the new facts (7 Task 2 + 9 Task 1 + 2 Task 3 + 1 here = expect **2008**, minus nothing — the renamed facts are one-for-one).

- [ ] **Step 4: Revert-proof**

Restore `WhenColumn` into the Track call → `WhenIsNotCappedBecauseItsContentIsBounded` fails; remove again. Put Name's `TextTrimming` setter back → the Name theory row fails; restore.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/HistoryWindow.xaml src/OrdoSort.Wpf/Windows/HistoryWindow.xaml.cs tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs
git commit -m "feat(history): the four text columns wrap; When is sized to itself

Original, Filed as, Name and Destination now share the width in
proportion and wrap instead of trimming. When holds a 16-character
timestamp the app formats itself, so it leaves the governed set rather
than ever being asked to wrap a date.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018PfXp3Zud9Retu1DkdBSbc"
```

---

## Self-review (done while writing)

- **Design coverage:** rule → Task 1 + 2; measurement, star participation, shrink-back → Task 2; six windows' XAML → Tasks 3–5; tooltips → Tasks 3–5 table; drag still wins → unchanged code, existing `*_UserDragged…` facts; Triage untouched → overload path preserved and its facts must stay green (Task 2 Step 4).
- **Placeholders:** none — every step has its code or its exact edit.
- **Type consistency:** `ColumnShares.Compute(double, IReadOnlyList<double>, IReadOnlyList<double>) : double[]` used identically in Task 2; `AssertWrapsInsideItsCap(Window, DataGridBoundColumn, string)` defined in Task 3, used in Task 4; `SettleStarColumns(Window)` defined in Task 3, used in Task 3 only; `BuildZipToolsWindow(string, int, string?)` extended in Task 3 with defaults so existing callers compile.
- **Known coincidences to report, not hide:** Task 2 Step 2 lists which new facts may pass against the old class; Task 3 Step 2 says `ZipTools_Clearing…` passes before the XAML edit (its mechanism is Task 2's). Neither is vacuous — each has a named break in its revert-proof step.
