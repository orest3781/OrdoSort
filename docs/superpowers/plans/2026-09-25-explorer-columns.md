# Explorer-style tables Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every table (DataGrid) in OrdoSort behaves like the Details view in Windows File Explorer, per table rules v2.

**Architecture:**
- **`ExplorerColumns`** is one shared behaviour class in `src/OrdoSort.Wpf/Views/`, attached to each grid by its window with a key. It owns fixed widths, fit-to-content, the header menu, reordering, the remembered layout, type-to-jump and click-to-clear.
- **`TableLayoutStore`** reads and writes the remembered layouts to `%LOCALAPPDATA%\OrdoSort\table-columns.json`.
- **Shared styles in `Theme/Styles.xaml`** carry the look: no row lines, no striping, a sort chevron and right-aligned numbers.
- **Removed:** the old auto-fit (`DataGridColumnCap`, `ColumnShares`).

**Tech Stack:** C# / .NET 8, WPF, System.Text.Json, xUnit. Check command: `check.bat` (format verify, Release build, all tests).

**Spec:** `docs/superpowers/specs/2026-09-25-explorer-columns-design.md`

## Global Constraints

- **The 9 grids:**
  - Bulk rename `PreviewGrid`
  - File list `NamesGrid`
  - History `HistoryGrid`
  - Match & merge `MatchGrid`
  - Merge PDFs `ItemsGrid`
  - Page counts `CountsGrid`
  - Standardise names `ResultsGrid`
  - Zip tools `ItemsGrid`
  - Triage `Candidates`
- **Starting widths (px):**

  | Window | Starting widths |
  |---|---|
  | Bulk rename | Current name 300 · New name 300 · Note 160 |
  | File list | # 50 · File name 320 · Pages 70 · Size 80 · Modified 140 · Folder 180 · Full path 320 |
  | History | When 140 · Original 240 · Filed as 240 · Name 140 · Destination 160 · Undone 80 |
  | Match & merge | File 280 · Becomes 280 · Note 200 |
  | Merge PDFs | Item 360 · Kind 90 · Result 200 |
  | Page counts | File 360 · Pages 70 · Note 200 |
  | Standardise names | Current name 380 · Result 300 |
  | Zip tools | Item 360 · Kind 90 · Result 200 |
  | Triage | each roster column 140 · Why 150 |

- **Minimum column width 40px.**
- **Type-ahead window: 1 second.**
- **Saved layout file:** `%LOCALAPPDATA%\OrdoSort\table-columns.json`. Never in `config.json`, because several stations share it.
- **Damaged or missing layout file:** open at the defaults. A failed save goes to `crash.log` via `App.LogCrash`, not to a dialog.
- **The first column** (or an explicit anchor column) can never be hidden. When it starts first, it stays first.
- **Sorting:**
  - The File list stays unsortable (`CanUserSortColumns="False"`).
  - Clicking a header cycles ascending, then descending.
- **Alignment:** number columns right-align their values (File list #, Pages, Size; Page counts Pages). Every header label left-aligns.
- **Colours** come from `Theme.*` brushes. Text stays at ≥ 4.5:1 and the sort chevron at ≥ 3:1, in both Light and Dark.
- **Files and tooling:**
  - Edit `.cs`/`.xaml` with the Edit tool. If a script is used, keep CRLF line endings: `sed -i` and Python text-mode writes strip CRs, and `dotnet format` then fails check.bat.
  - Tests must never touch the real `%LOCALAPPDATA%`. The test assembly redirects `TableLayoutStore.DefaultPath` (Task 1).
- **Commits:** each task ends with `check.bat` green and one commit. Stage named files only, never `git add -A`.

## Review Focus

1. **A column header that isn't a plain string** (e.g. a future template header). It must be skipped by save and load, not crash. Test in Task 2.
2. **The saved layout names a column the grid no longer has, or lacks one it has** (a Triage roster changed, a column renamed). Unknown entries are ignored, and new columns keep their XAML width. Test in Task 2.
3. **The saved DisplayIndex set is out of range or has duplicates** (hand-edited file). Apply must clamp and not throw. Test in Task 2.
4. **Fit on a grid with thousands of rows.** It must measure every item without realizing rows and finish quickly: 5,000 rows under 2s. Test in Task 3.
5. **Typing while a cell is being edited** (Bulk rename New name TextBox). Type-ahead must not steal the keystrokes. Test in Task 6.

---

### Task 1: TableLayoutStore (remembered layouts) and the test redirect

**Files:**
- Create: `src/OrdoSort.Wpf/Services/TableLayoutStore.cs`
- Create: `tests/OrdoSort.Wpf.Tests/TableLayoutStoreTests.cs`
- Create: `tests/OrdoSort.Wpf.Tests/TestAssemblySetup.cs`

**Interfaces:**
- Produces:
  - `record ColumnLayout(string Header, double Width, bool Visible, int DisplayIndex)`
  - `record TableLayout(IReadOnlyList<ColumnLayout> Columns, string? SortHeader, ListSortDirection? SortDirection)`
  - `class TableLayoutStore(string path)` with `TableLayout? Load(string key)` and `void Save(string key, TableLayout layout)`
  - `static string TableLayoutStore.DefaultPath { get; internal set; }`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Wpf.Tests/TableLayoutStoreTests.cs
using System.ComponentModel;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Where each table's column layout is remembered on this PC.
/// Pure file I/O, no WPF.</summary>
public sealed class TableLayoutStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_layouts_" + Guid.NewGuid().ToString("N"))).FullName;

    private string FilePath => Path.Combine(_dir, "table-columns.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static TableLayout Sample() => new(
        new[]
        {
            new ColumnLayout("Current name", 310, true, 0),
            new ColumnLayout("Note", 90, false, 2),
        },
        "Current name", ListSortDirection.Descending);

    [Fact]
    public void ALayoutComesBackUnderItsKey()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("BulkRename", Sample());

        var loaded = new TableLayoutStore(FilePath).Load("BulkRename")!;

        Assert.Equal(Sample().Columns, loaded.Columns);
        Assert.Equal("Current name", loaded.SortHeader);
        Assert.Equal(ListSortDirection.Descending, loaded.SortDirection);
    }

    [Fact]
    public void NothingSavedIsNull() =>
        Assert.Null(new TableLayoutStore(FilePath).Load("BulkRename"));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void ADamagedFileIsTreatedAsNothingSaved(string contents)
    {
        File.WriteAllText(FilePath, contents);

        Assert.Null(new TableLayoutStore(FilePath).Load("BulkRename"));
    }

    [Fact]
    public void SavingOneWindowKeepsTheOthers()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("History", Sample());

        store.Save("BulkRename", Sample() with { SortHeader = null, SortDirection = null });

        Assert.NotNull(store.Load("History"));
        Assert.Null(store.Load("BulkRename")!.SortHeader);
    }

    /// <summary>Triage builds different columns per roster; saving one
    /// roster's columns must not forget another's.</summary>
    [Fact]
    public void SavingMergesColumnsByHeader()
    {
        var store = new TableLayoutStore(FilePath);
        store.Save("Triage", new TableLayout(new[] { new ColumnLayout("DOB", 120, true, 1) }, null, null));

        store.Save("Triage", new TableLayout(new[] { new ColumnLayout("MRN", 90, true, 1) }, null, null));

        var headers = store.Load("Triage")!.Columns.Select(c => c.Header).OrderBy(h => h);
        Assert.Equal(new[] { "DOB", "MRN" }, headers);
    }

    [Fact]
    public void SavingCreatesTheFolder()
    {
        var nested = Path.Combine(_dir, "a", "b", "table-columns.json");

        new TableLayoutStore(nested).Save("BulkRename", Sample());

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void TheTestRunNeverTouchesTheRealUserProfile() =>
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TableLayoutStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
}
```

```csharp
// tests/OrdoSort.Wpf.Tests/TestAssemblySetup.cs
using System.Runtime.CompilerServices;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs once when the test assembly loads, before any test: every
/// window a test builds attaches ExplorerColumns, which saves its layout on
/// close, and that must never reach the real user's %LOCALAPPDATA%.</summary>
internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void RedirectTableLayouts() =>
        TableLayoutStore.DefaultPath = Path.Combine(Path.GetTempPath(),
            "ordo_test_layouts_" + Environment.ProcessId, "table-columns.json");
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Release`
Expected: build errors, "The type or namespace name 'TableLayoutStore' could not be found".

- [ ] **Step 3: Implement**

```csharp
// src/OrdoSort.Wpf/Services/TableLayoutStore.cs
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Wpf.Services;

/// <summary>One column's remembered state.</summary>
/// <param name="Header">The column's header text: its identity.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Visible">False when the user hid it.</param>
/// <param name="DisplayIndex">Its position after any drag-reorder.</param>
public sealed record ColumnLayout(string Header, double Width, bool Visible, int DisplayIndex);

/// <summary>A table's remembered layout: its columns and its sort.</summary>
public sealed record TableLayout(IReadOnlyList<ColumnLayout> Columns, string? SortHeader,
    ListSortDirection? SortDirection);

/// <summary>Remembers each window's table layout on this PC, the way
/// Explorer remembers a folder's view (table rules v2, rule 7).
///
/// Lives under %LOCALAPPDATA%, never in config.json: several stations
/// share one config.json, and one person's column widths are not
/// everyone's. A missing or damaged file reads as "nothing saved": a
/// layout preference must never stop a window opening (the same call Box
/// Labels makes for its own settings file).</summary>
public sealed class TableLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public TableLayoutStore(string path) => _path = path;

    /// <summary>Where the app's own layouts live. Settable only so the test
    /// assembly can point it at a temp folder.</summary>
    public static string DefaultPath { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "table-columns.json");

    /// <summary>The saved layout for <paramref name="key"/>, or null when
    /// there is none or the file can't be read.</summary>
    public TableLayout? Load(string key) =>
        ReadAll().TryGetValue(key, out var layout) ? layout : null;

    /// <summary>Saves <paramref name="layout"/> under <paramref name="key"/>,
    /// keeping every other key and every column of this key the new layout
    /// doesn't mention.</summary>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to write it.</exception>
    public void Save(string key, TableLayout layout)
    {
        var all = ReadAll();
        var columns = new Dictionary<string, ColumnLayout>();
        if (all.TryGetValue(key, out var old))
            foreach (var column in old.Columns) columns[column.Header] = column;
        foreach (var column in layout.Columns) columns[column.Header] = column;
        all[key] = layout with { Columns = columns.Values.ToList() };

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, Options));
    }

    private Dictionary<string, TableLayout> ReadAll()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, TableLayout>>(File.ReadAllText(_path), Options)
                ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~TableLayoutStoreTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: check.bat, then commit**

Run: `cmd /c "A:\DEV\OrdoSort\check.bat"`. Expected: exit 0.

```bash
git add src/OrdoSort.Wpf/Services/TableLayoutStore.cs tests/OrdoSort.Wpf.Tests/TableLayoutStoreTests.cs tests/OrdoSort.Wpf.Tests/TestAssemblySetup.cs
git commit -m "feat(tables): remember each table's layout per PC (table rules v2, rule 7)"
```

---

### Task 2: ExplorerColumns core: fixed widths, remembered layout, reorder rules; Bulk rename attached

**Files:**
- Create: `src/OrdoSort.Wpf/Views/ExplorerColumns.cs`
- Create: `tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs`
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml`:
  - `CurrentColumn` `Width="Auto"` becomes `Width="300"`;
  - New name `Width="*" MinWidth="120"` becomes `Width="300"`;
  - `NoteColumn` `Width="Auto"` becomes `Width="160"`;
  - rewrite those three columns' sizing comments to "fixed width (table rules v2, rule 1)".
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs:17-21`: replace the `SetShrinkWhenEmpty` and `Track` lines with `ExplorerColumns.Attach(PreviewGrid, "BulkRename");`.

**Interfaces:**
- Consumes: `TableLayoutStore`, `TableLayout`, `ColumnLayout` (Task 1).
- Produces:
  - `interface IColumnVisibility { bool IsShown(DataGridColumn c); void SetShown(DataGridColumn c, bool shown); }`
  - `ExplorerColumns.Attach(DataGrid grid, string key, DataGridColumn? anchor = null, IColumnVisibility? visibility = null, TableLayoutStore? store = null, Func<DateTime>? clock = null, Action<Exception>? reportSaveError = null)` returns `ExplorerColumns`.
  - `static ExplorerColumns? ExplorerColumns.For(DataGrid grid)`
  - Instance members: `void ApplySaved()`, `void Save()`, `DataGridColumn? Anchor`
  - `const double MinColumnWidth = 40`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Table rules v2: tables behave like File Explorer's Details
/// view. A bare grid with three text columns, so each fact is about the
/// behaviour and not about one window.</summary>
[Collection(HighlightContrastTests.Name)]
public sealed class ExplorerColumnsTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_explorer_" + Guid.NewGuid().ToString("N"))).FullName;

    public ExplorerColumnsTests(HighlightContrastFixture fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public sealed class Row
    {
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Note { get; init; } = "";
    }

    internal sealed record Bed(Window Window, DataGrid Grid, ObservableCollection<Row> Rows, ExplorerColumns Explorer,
        TableLayoutStore Store)
    {
        public DataGridColumn Column(string header) => Grid.Columns.Single(c => (string)c.Header == header);
    }

    internal Bed Build(double windowWidth = 900, TableLayoutStore? store = null, Func<DateTime>? clock = null,
        params Row[] rows)
    {
        var items = new ObservableCollection<Row>(rows);
        var grid = new DataGrid { ItemsSource = items, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var (header, width) in new[] { ("Name", 200.0), ("Kind", 90.0), ("Note", 150.0) })
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(header), Width = new DataGridLength(width),
                ElementStyle = (Style)_fx.App.FindResource("GridCellText"),
            });
        store ??= new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        var explorer = ExplorerColumns.Attach(grid, "Test", store: store, clock: clock);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        return new Bed(window, grid, items, explorer, store);
    }

    internal static void Settle(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    internal static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    internal static Row Long(int i) => new() { Name = new string('W', 60) + i, Kind = "pdf", Note = "" };

    // ---- rule 1: fixed widths --------------------------------------------

    [Fact]
    public void ColumnsOpenAtTheirDeclaredWidths() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(200, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
            Assert.Equal(150, bed.Column("Note").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AWidthNeverChangesWhenRowsArriveOrTheWindowResizes() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);
            bed.Column("Name").Width = new DataGridLength(260);   // as a drag leaves it

            for (var i = 0; i < 200; i++) bed.Rows.Add(Long(i));
            bed.Window.Width = 600;
            Settle(bed.Window);

            Assert.Equal(260, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void ColumnsWiderThanTheTableScrollSideways() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(windowWidth: 300, rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);

            var scroller = FindDescendant<ScrollViewer>(bed.Grid)!;
            Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AColumnCannotBeDraggedNarrowerThanTheMinimum() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);
            bed.Column("Kind").Width = new DataGridLength(5);
            Settle(bed.Window);

            Assert.Equal(ExplorerColumns.MinColumnWidth, bed.Column("Kind").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    // ---- rule 5: reorder, first column stays first --------------------------

    [Fact]
    public void TheFirstColumnStaysFirstAfterAReorder() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            bed.Column("Note").DisplayIndex = 0;   // what dropping Note in front of Name does
            bed.Explorer.KeepAnchorFirst();

            Assert.Equal(0, bed.Column("Name").DisplayIndex);
            Assert.Equal(1, bed.Column("Note").DisplayIndex);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void OtherColumnsCanBeReordered() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            bed.Column("Note").DisplayIndex = 1;
            bed.Explorer.KeepAnchorFirst();

            Assert.Equal(new[] { "Name", "Note", "Kind" },
                bed.Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header));
        }
        finally { bed.Window.Close(); }
    });

    // ---- rule 7: remembered ------------------------------------------------

    [Fact]
    public void WidthsOrderHiddenColumnsAndSortComeBackInANewWindow() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var first = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(first.Window);
            first.Column("Name").Width = new DataGridLength(310);
            first.Column("Note").DisplayIndex = 1;
            first.Column("Kind").Visibility = Visibility.Collapsed;
            first.Column("Name").SortDirection = ListSortDirection.Descending;
            first.Grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            Settle(first.Window);
        }
        finally { first.Window.Close(); }   // closing saves

        var second = Build(store: first.Store, rows: new[] { new Row { Name = "a" }, new Row { Name = "b" } });
        try
        {
            Settle(second.Window);

            Assert.Equal(310, second.Column("Name").ActualWidth, 0.5);
            Assert.Equal(1, second.Column("Note").DisplayIndex);
            Assert.Equal(Visibility.Collapsed, second.Column("Kind").Visibility);
            Assert.Equal(ListSortDirection.Descending, second.Column("Name").SortDirection);
            Assert.Equal("b", ((Row)second.Grid.Items[0]).Name);
        }
        finally { second.Window.Close(); }
    });

    [Fact]
    public void ADamagedLayoutFileOpensAtTheDefaults() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var path = Path.Combine(_dir, "table-columns.json");
        File.WriteAllText(path, "{ not json");
        var bed = Build(store: new TableLayoutStore(path), rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(200, bed.Column("Name").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 2: a saved column the grid no longer has is
    /// ignored, and a column the save doesn't mention keeps its XAML width.</summary>
    [Fact]
    public void UnknownSavedColumnsAreIgnoredAndNewColumnsKeepTheirWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var store = new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        store.Save("Test", new TableLayout(new[]
        {
            new ColumnLayout("Gone", 500, true, 0),
            new ColumnLayout("Name", 222, true, 0),
        }, "Gone", ListSortDirection.Ascending));
        var bed = Build(store: store, rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(222, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
            Assert.All(bed.Grid.Columns, c => Assert.Null(c.SortDirection));
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 3: a hand-edited file with display indexes out of
    /// range or repeated must not throw.</summary>
    [Fact]
    public void OutOfRangeOrRepeatedDisplayIndexesAreClamped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var store = new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        store.Save("Test", new TableLayout(new[]
        {
            new ColumnLayout("Name", 200, true, 7),
            new ColumnLayout("Kind", 90, true, -3),
            new ColumnLayout("Note", 150, true, 1),
        }, null, null));
        var bed = Build(store: store, rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(new[] { 0, 1, 2 }, bed.Grid.Columns.Select(c => c.DisplayIndex).OrderBy(i => i));
            Assert.Equal(0, bed.Column("Name").DisplayIndex);   // the anchor stays first
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 1: a column whose header isn't text has no
    /// identity to save under, and is left alone.</summary>
    [Fact]
    public void AColumnWithANonTextHeaderIsSkipped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        bed.Grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Odd" }, Width = new DataGridLength(70) });
        try
        {
            Settle(bed.Window);
            bed.Explorer.Save();

            Assert.DoesNotContain(bed.Store.Load("Test")!.Columns, c => c.Header.Contains("Odd"));
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AFailedSaveIsReportedNotThrown() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "table-columns.json"));   // a folder where the file should be
        var reported = new List<Exception>();
        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(100) });
        var explorer = ExplorerColumns.Attach(grid, "Test",
            store: new TableLayoutStore(Path.Combine(blocked, "table-columns.json")),
            reportSaveError: reported.Add);

        explorer.Save();

        Assert.Single(reported);
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Release`
Expected: build errors, "'ExplorerColumns' could not be found".

- [ ] **Step 3: Implement**

```csharp
// src/OrdoSort.Wpf/Views/ExplorerColumns.cs
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Views;

/// <summary>Who decides whether a column is shown. The default flips the
/// column's own Visibility; the File list supplies one that flips its
/// view-model flags, so its export follows what the table shows.</summary>
internal interface IColumnVisibility
{
    bool IsShown(DataGridColumn column);
    void SetShown(DataGridColumn column, bool shown);
}

/// <summary>Makes one DataGrid behave like File Explorer's Details view
/// (table rules v2, docs/superpowers/specs/2026-09-25-explorer-columns-design.md):
/// fixed widths only the user changes, fit to content on demand, a header
/// menu, drag-to-reorder with the first column pinned, a remembered layout,
/// type-to-jump and click-empty-space-to-clear. One instance per grid,
/// attached by the window that owns it.</summary>
internal sealed partial class ExplorerColumns
{
    /// <summary>No column can be dragged narrower than this.</summary>
    public const double MinColumnWidth = 40;

    private static readonly DependencyProperty InstanceProperty = DependencyProperty.RegisterAttached(
        "Instance", typeof(ExplorerColumns), typeof(ExplorerColumns));

    private sealed class OwnVisibility : IColumnVisibility
    {
        public bool IsShown(DataGridColumn column) => column.Visibility == Visibility.Visible;
        public void SetShown(DataGridColumn column, bool shown) =>
            column.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly DataGrid _grid;
    private readonly string _key;
    private readonly TableLayoutStore _store;
    private readonly IColumnVisibility _visibility;
    private readonly Func<DateTime> _clock;
    private readonly Action<Exception> _reportSaveError;
    private readonly DataGridColumn? _explicitAnchor;
    private bool _applied;

    private ExplorerColumns(DataGrid grid, string key, DataGridColumn? anchor, IColumnVisibility? visibility,
        TableLayoutStore? store, Func<DateTime>? clock, Action<Exception>? reportSaveError)
    {
        _grid = grid;
        _key = key;
        _explicitAnchor = anchor;
        _visibility = visibility ?? new OwnVisibility();
        _store = store ?? new TableLayoutStore(TableLayoutStore.DefaultPath);
        _clock = clock ?? (() => DateTime.UtcNow);
        _reportSaveError = reportSaveError ?? (ex => App.LogCrash(ex));
    }

    /// <summary>Attaches the behaviour to <paramref name="grid"/>.</summary>
    /// <param name="key">The window's name in the saved-layout file.</param>
    /// <param name="anchor">The column that can never be hidden or dragged;
    /// defaults to whichever column is first. It is also kept first when it
    /// starts first.</param>
    /// <param name="visibility">Who shows and hides columns; defaults to the
    /// column's own Visibility.</param>
    /// <param name="store">Where the layout is remembered; defaults to
    /// <see cref="TableLayoutStore.DefaultPath"/>.</param>
    /// <param name="clock">Time source for type-ahead; tests pass their own.</param>
    /// <param name="reportSaveError">Where a failed save is reported;
    /// defaults to crash.log.</param>
    public static ExplorerColumns Attach(DataGrid grid, string key, DataGridColumn? anchor = null,
        IColumnVisibility? visibility = null, TableLayoutStore? store = null, Func<DateTime>? clock = null,
        Action<Exception>? reportSaveError = null)
    {
        var explorer = new ExplorerColumns(grid, key, anchor, visibility, store, clock, reportSaveError);
        grid.SetValue(InstanceProperty, explorer);
        grid.MinColumnWidth = MinColumnWidth;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.Loaded += (_, _) => explorer.ApplySaved();
        grid.Unloaded += (_, _) => explorer.Save();
        grid.Columns.CollectionChanged += explorer.OnColumnsChanged;
        grid.ColumnReordering += explorer.OnColumnReordering;
        grid.ColumnReordered += (_, _) => explorer.KeepAnchorFirst();
        explorer.WireInput();
        return explorer;
    }

    /// <summary>The behaviour attached to <paramref name="grid"/>, if any.</summary>
    public static ExplorerColumns? For(DataGrid grid) => (ExplorerColumns?)grid.GetValue(InstanceProperty);

    /// <summary>The column that is never hidden or dragged.</summary>
    public DataGridColumn? Anchor =>
        _explicitAnchor ?? _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault();

    // The anchor is "kept first" only if it was first when the grid opened;
    // an explicit anchor elsewhere (the File list's File name) just stays put.
    private DataGridColumn? _pinnedFirst;

    private static string? HeaderOf(DataGridColumn column) => column.Header as string;

    /// <summary>Applies the saved layout once, when the grid first loads:
    /// widths, visibility, order, then sort. Columns the save doesn't
    /// mention keep their XAML width; saved columns the grid lacks are
    /// ignored.</summary>
    public void ApplySaved()
    {
        if (_applied) return;
        _applied = true;
        _pinnedFirst = _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault();
        if (_explicitAnchor is not null && _explicitAnchor != _pinnedFirst) _pinnedFirst = null;

        var saved = _store.Load(_key);
        if (saved is null) return;
        var byHeader = saved.Columns.GroupBy(c => c.Header).ToDictionary(g => g.Key, g => g.Last());

        foreach (var column in _grid.Columns)
            if (HeaderOf(column) is { } header && byHeader.TryGetValue(header, out var layout))
            {
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
                if (column != Anchor) _visibility.SetShown(column, layout.Visible);
            }

        // Order: saved positions first (stable for ties), unknown columns after.
        var ordered = _grid.Columns
            .Select((column, declared) => (column, declared,
                rank: HeaderOf(column) is { } h && byHeader.TryGetValue(h, out var l) ? l.DisplayIndex : int.MaxValue))
            .OrderBy(t => t.rank).ThenBy(t => t.declared)
            .Select(t => t.column)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        KeepAnchorFirst();

        if (saved.SortHeader is { } sortHeader && saved.SortDirection is { } direction
            && _grid.CanUserSortColumns
            && _grid.Columns.FirstOrDefault(c => HeaderOf(c) == sortHeader) is { SortMemberPath: { Length: > 0 } path } sortColumn)
        {
            foreach (var column in _grid.Columns) column.SortDirection = null;
            _grid.Items.SortDescriptions.Clear();
            _grid.Items.SortDescriptions.Add(new SortDescription(path, direction));
            sortColumn.SortDirection = direction;
        }
    }

    /// <summary>A column added after the grid opened (Triage builds its
    /// columns per roster) takes its saved width, if it has one.</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_applied || e.NewItems is null) return;
        var saved = _store.Load(_key);
        if (saved is null) return;
        foreach (DataGridColumn column in e.NewItems)
            if (HeaderOf(column) is { } header && saved.Columns.LastOrDefault(c => c.Header == header) is { } layout)
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
    }

    private void OnColumnReordering(object? sender, DataGridColumnReorderingEventArgs e)
    {
        if (e.Column == Anchor) e.Cancel = true;
    }

    /// <summary>After any reorder, puts the pinned first column back first.</summary>
    public void KeepAnchorFirst()
    {
        var pinned = _pinnedFirst ?? (_applied ? null : _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault());
        if (pinned is not null && pinned.DisplayIndex != 0) pinned.DisplayIndex = 0;
    }

    /// <summary>Writes the current layout to the store. Called when the grid
    /// unloads (its window closed). A failed write is reported, never thrown:
    /// losing a column width must not break closing a window.</summary>
    public void Save()
    {
        var columns = _grid.Columns
            .Where(c => HeaderOf(c) is not null)
            .Select(c => new ColumnLayout(HeaderOf(c)!, c.ActualWidth > 0 ? c.ActualWidth : c.Width.DisplayValue,
                _visibility.IsShown(c), c.DisplayIndex))
            .ToList();
        var sorted = _grid.Columns.FirstOrDefault(c => c.SortDirection is not null && HeaderOf(c) is not null);
        try
        {
            _store.Save(_key, new TableLayout(columns, sorted is null ? null : HeaderOf(sorted), sorted?.SortDirection));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _reportSaveError(ex);
        }
    }

    // Input wiring (fit, menu, type-ahead, empty space) lives in
    // ExplorerColumns.Input.cs (Tasks 3 and 6).
    partial void WireInputCore();

    private void WireInput() => WireInputCore();
}
```

Note: `KeepAnchorFirst` must work before `ApplySaved` has run in the unit tests. The tests call it after `Settle`, and by then `Loaded` has run `ApplySaved`, so `_pinnedFirst` is set. Keep the fallback anyway.

- [ ] **Step 4: Attach Bulk rename**

Apply the XAML width changes listed under Files. In `BulkRenameWindow.xaml.cs`, replace:

```csharp
        // Note is blank on most batches; it keeps its header width rather
        // than taking room the file names need (owner's call, 2026-09-25).
        DataGridColumnCap.SetShrinkWhenEmpty(NoteColumn, true);
        DataGridColumnCap.Track(PreviewGrid, CurrentColumn, NoteColumn);
```

with:

```csharp
        // Explorer-style columns (table rules v2): fixed widths the user
        // sets, remembered per PC.
        ExplorerColumns.Attach(PreviewGrid, "BulkRename");
```

- [ ] **Step 5: Run the new tests, then the Bulk rename suites**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~ExplorerColumnsTests|FullyQualifiedName~BulkRename"`

Expected: the ExplorerColumnsTests pass. Bulk rename facts in `AutoFitColumnTests` that assert auto-fit now fail, because the Bulk rename columns no longer auto-fit. Replace those Bulk rename facts in `AutoFitColumnTests` (`BulkRename_*`, and the `BulkRename` entry in the star/filler checks of `DataGridStarColumnTests`) with nothing. They are superseded by `ExplorerColumnsTests`. The trim + tooltip facts for Bulk rename move to Task 7's coverage. Remove them in this task so check.bat stays green, and list each removed test name in the commit message.

- [ ] **Step 6: check.bat, then commit**

Run: `cmd /c "A:\DEV\OrdoSort\check.bat"`. Expected: exit 0.

```bash
git add src/OrdoSort.Wpf/Views/ExplorerColumns.cs src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml.cs tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs tests/OrdoSort.Wpf.Tests/DataGridStarColumnTests.cs
git commit -m "feat(tables): Explorer-style fixed widths and remembered layout; Bulk rename first"
```

---

### Task 3: Fit to content: divider double-click, Ctrl + Plus, and the header menu

**Files:**
- Create: `src/OrdoSort.Wpf/Views/ExplorerColumns.Input.cs`
- Modify: `tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs` (append tests)

**Interfaces:**
- Consumes: `ExplorerColumns` (Task 2).
- Produces:
  - `double MeasureFit(DataGridColumn column)`
  - `void FitColumn(DataGridColumn column)` and `void FitAll()`
  - `ContextMenu BuildHeaderMenu(DataGridColumn clicked)`
  - `const double CellHorizontalPadding = 24` (must equal 2 × `DataGridCell` Padding.Left in Styles.xaml)

- [ ] **Step 1: Write the failing tests (append to ExplorerColumnsTests)**

```csharp
    // ---- rule 3: fit to content ------------------------------------------

    private static double TextWidth(DataGrid grid, string text, FontWeight weight)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(grid.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            grid.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(grid).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    [Fact]
    public void FittingAColumnUsesItsWidestValueIncludingRowsScrolledOutOfView() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var rows = Enumerable.Range(0, 300).Select(i => new Row { Name = "short" + i }).ToList();
        rows.Add(new Row { Name = "the one very long name at the very bottom of the list.pdf" });
        var bed = Build(rows: rows.ToArray());
        try
        {
            Settle(bed.Window);

            bed.Explorer.FitColumn(bed.Column("Name"));

            var longest = TextWidth(bed.Grid, rows[^1].Name, FontWeights.Normal) + ExplorerColumns.CellHorizontalPadding;
            Assert.True(bed.Column("Name").ActualWidth >= longest,
                $"fit gave {bed.Column("Name").ActualWidth}px, the bottom row needs {longest}px");
            Assert.True(bed.Column("Name").ActualWidth <= longest + 12, "fit should not pad far past the text");
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void FittingAnEmptyColumnFitsItsHeader() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            bed.Explorer.FitColumn(bed.Column("Note"));

            var header = TextWidth(bed.Grid, "Note", FontWeights.SemiBold) + ExplorerColumns.CellHorizontalPadding;
            Assert.True(bed.Column("Note").ActualWidth >= Math.Max(header, ExplorerColumns.MinColumnWidth));
            Assert.True(bed.Column("Note").ActualWidth < 100);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void CtrlPlusFitsEveryVisibleColumn() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = new string('W', 80), Kind = "pdf", Note = "merged" });
        try
        {
            Settle(bed.Window);

            bed.Explorer.FitAll();

            Assert.True(bed.Column("Name").ActualWidth > 200);
            Assert.True(bed.Column("Kind").ActualWidth < 90);
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 4: fit measures every item without realizing
    /// rows, fast enough for a big History.</summary>
    [Fact]
    public void FittingFiveThousandRowsIsQuick() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: Enumerable.Range(0, 5000).Select(i => new Row { Name = "file " + i }).ToArray());
        try
        {
            Settle(bed.Window);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            bed.Explorer.FitColumn(bed.Column("Name"));

            Assert.True(clock.ElapsedMilliseconds < 2000, $"fit took {clock.ElapsedMilliseconds}ms");
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void TheCellPaddingFitUsesIsTheOneTheStyleDraws() => _fx.Invoke(() =>
    {
        var style = (Style)_fx.App.FindResource(typeof(DataGridCell));
        var padding = style.Setters.OfType<Setter>().Single(s => s.Property == Control.PaddingProperty).Value;
        var thickness = padding is Thickness t ? t : (Thickness)new ThicknessConverter().ConvertFrom(padding)!;

        Assert.Equal(ExplorerColumns.CellHorizontalPadding, thickness.Left + thickness.Right);
    });

    // ---- rule 4: header menu -----------------------------------------------

    [Fact]
    public void TheHeaderMenuFitsAndHidesColumnsButNeverTheFirst() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = new string('W', 80), Kind = "pdf" });
        try
        {
            Settle(bed.Window);

            var menu = bed.Explorer.BuildHeaderMenu(bed.Column("Name"));
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Size column to fit", "Size all columns to fit", "Name", "Kind", "Note" },
                items.Select(i => (string)i.Header));
            Assert.IsType<Separator>(menu.Items[2]);
            Assert.False(items.Single(i => (string)i.Header == "Name").IsEnabled);

            items.Single(i => (string)i.Header == "Kind").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(Visibility.Collapsed, bed.Column("Kind").Visibility);

            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));   // Size column to fit (Name)
            Settle(bed.Window);
            Assert.True(bed.Column("Name").ActualWidth > 200);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void ADoubleClickOnADividerFitsTheColumnToItsLeft() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = new string('W', 80) });
        try
        {
            Settle(bed.Window);
            var header = FindHeader(bed.Grid, "Name");
            var gripper = (System.Windows.Controls.Primitives.Thumb)header.Template.FindName("PART_RightHeaderGripper", header);

            Assert.True(bed.Explorer.TryFitFromGripper(gripper));

            Assert.True(bed.Column("Name").ActualWidth > 200);
        }
        finally { bed.Window.Close(); }
    });

    internal static System.Windows.Controls.Primitives.DataGridColumnHeader FindHeader(DataGrid grid, string text)
    {
        var headers = new List<System.Windows.Controls.Primitives.DataGridColumnHeader>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.Primitives.DataGridColumnHeader h) headers.Add(h);
                Walk(child);
            }
        }
        Walk(grid);
        return headers.First(h => h.Column is not null && (string)h.Column.Header == text);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build tests/OrdoSort.Wpf.Tests -c Release`
Expected: errors for `FitColumn`, `FitAll`, `BuildHeaderMenu`, `TryFitFromGripper` and `CellHorizontalPadding`.

- [ ] **Step 3: Implement**

```csharp
// src/OrdoSort.Wpf/Views/ExplorerColumns.Input.cs
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace OrdoSort.Wpf.Views;

internal sealed partial class ExplorerColumns
{
    /// <summary>Left plus right cell padding, as Theme/Styles.xaml's
    /// DataGridCell style draws it (12 each side). A test keeps the two equal.</summary>
    public const double CellHorizontalPadding = 24;

    // A little room so a fitted value never trims on rounding.
    private const double FitSlack = 4;

    // Reused to evaluate a column's binding against each item without
    // realizing a row.
    private readonly TextBlock _probe = new();

    partial void WireInputCore()
    {
        _grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        _grid.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        _grid.PreviewKeyDown += OnPreviewKeyDown;
        WireTypingCore();
    }

    partial void WireTypingCore();

    /// <summary>The width that shows every value of <paramref name="column"/>
    /// whole, and its header, measured with the grid's font. Reads values
    /// through the column's own binding, so formats and indexers come out
    /// as the cells show them.</summary>
    public double MeasureFit(DataGridColumn column)
    {
        var widest = TextWidth(HeaderOf(column) ?? "", FontWeights.SemiBold);
        if (column is DataGridTextColumn { Binding: BindingBase binding })
            foreach (var item in _grid.Items)
            {
                if (item == CollectionView.NewItemPlaceholder) continue;
                widest = Math.Max(widest, TextWidth(ValueOf(item, binding), FontWeights.Normal));
            }
        return Math.Max(MinColumnWidth, Math.Ceiling(widest + CellHorizontalPadding + FitSlack));
    }

    /// <summary>Sizes one column to its content (the divider double-click).</summary>
    public void FitColumn(DataGridColumn column) => column.Width = new DataGridLength(MeasureFit(column));

    /// <summary>Sizes every visible column to its content (Ctrl + Plus).</summary>
    public void FitAll()
    {
        foreach (var column in _grid.Columns.Where(c => c.Visibility == Visibility.Visible)) FitColumn(column);
    }

    private string ValueOf(object item, BindingBase binding)
    {
        _probe.DataContext = item;
        _probe.SetBinding(TextBlock.TextProperty, binding);
        var text = _probe.Text;
        BindingOperations.ClearBinding(_probe, TextBlock.TextProperty);
        return text ?? "";
    }

    private double TextWidth(string text, FontWeight weight)
    {
        if (text.Length == 0) return 0;
        var typeface = new Typeface(_grid.FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            _grid.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(_grid).PixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    /// <summary>A double-click on a header divider fits the column to the
    /// divider's LEFT, as in Explorer. Returns false when the thumb isn't a
    /// header divider.</summary>
    internal bool TryFitFromGripper(Thumb thumb)
    {
        if (FindAncestor<DataGridColumnHeader>(thumb) is not { Column: { } column }) return false;
        if (thumb.Name == "PART_RightHeaderGripper") { FitColumn(column); return true; }
        if (thumb.Name == "PART_LeftHeaderGripper")
        {
            var left = _grid.Columns.Where(c => c.Visibility == Visibility.Visible && c.DisplayIndex < column.DisplayIndex)
                .OrderByDescending(c => c.DisplayIndex).FirstOrDefault();
            if (left is not null) FitColumn(left);
            return left is not null;
        }
        return false;
    }

    // WPF's own divider double-click sets the column to Auto (which then
    // re-grows as rows arrive, breaking rule 1). Handling the second press
    // here, before the thumb sees it, replaces that with a fixed-width fit.
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is { } thumb
            && TryFitFromGripper(thumb))
        {
            e.Handled = true;
            return;
        }
        ClearSelectionOnEmptySpace(e);
    }

    partial void ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs e);

    private void ClearSelectionOnEmptySpace(MouseButtonEventArgs e) => ClearSelectionOnEmptySpaceCore(e);

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column } header)
            return;
        var menu = BuildHeaderMenu(column);
        menu.PlacementTarget = header;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Explorer's header menu: fit this column, fit all, then a
    /// checklist of columns. The anchor column's entry is disabled.</summary>
    public ContextMenu BuildHeaderMenu(DataGridColumn clicked)
    {
        var menu = new ContextMenu();
        var fitOne = new MenuItem { Header = "Size column to fit" };
        fitOne.Click += (_, _) => FitColumn(clicked);
        var fitAll = new MenuItem { Header = "Size all columns to fit" };
        fitAll.Click += (_, _) => FitAll();
        menu.Items.Add(fitOne);
        menu.Items.Add(fitAll);
        menu.Items.Add(new Separator());
        foreach (var column in _grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (HeaderOf(column) is not { } header) continue;
            var item = new MenuItem
            {
                Header = header, IsCheckable = true, IsChecked = _visibility.IsShown(column),
                IsEnabled = column != Anchor,
            };
            var target = column;
            item.Click += (_, _) => _visibility.SetShown(target, !_visibility.IsShown(target));
            menu.Items.Add(item);
        }
        return menu;
    }

    // Ctrl + Plus, on the main keyboard or the keypad.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add)
        {
            FitAll();
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
```

Note for the implementer: `IsChecked` on a checkable `MenuItem` flips itself on click. Setting visibility from the current state (not from `IsChecked`) keeps it right, whichever runs first.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~ExplorerColumnsTests"`
Expected: PASS.

- [ ] **Step 5: check.bat, then commit**

```bash
git add src/OrdoSort.Wpf/Views/ExplorerColumns.Input.cs tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs
git commit -m "feat(tables): fit to content by divider double-click, Ctrl+Plus and the header menu"
```

---

### Task 4: The other eight grids

**Files (modify):**
- `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/StandardiseNamesWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml` and `.xaml.cs`
- `src/OrdoSort.Wpf/Windows/TriageWindow.xaml.cs`

**Test files:**
- Create `tests/OrdoSort.Wpf.Tests/ExplorerColumnsCoverageTests.cs`
- Modify `tests/OrdoSort.Wpf.Tests/FilenameListWindowTests.cs`
- Remove superseded facts from `AutoFitColumnTests.cs`, `DataGridStarColumnTests.cs` and `HistoryWindowXamlTests.cs`

**Interfaces:**
- Consumes: `ExplorerColumns.Attach`, `IColumnVisibility` (Task 2).

- [ ] **Step 1: Write the failing coverage test**

```csharp
// tests/OrdoSort.Wpf.Tests/ExplorerColumnsCoverageTests.cs
using System.Text.RegularExpressions;

namespace OrdoSort.Wpf.Tests;

/// <summary>Table rules v2 hold for every table, not just the ones someone
/// remembered: every DataGrid in src is attached to ExplorerColumns, and
/// every column declares a pixel width (no Auto, no *). A source scan, so
/// a new window can't slip past it.</summary>
public class ExplorerColumnsCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("couldn't find OrdoSort.sln");
    }

    public static TheoryData<string> GridWindows()
    {
        var data = new TheoryData<string>();
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", SearchOption.AllDirectories))
            if (File.ReadAllText(xaml).Contains("<DataGrid ")) data.Add(Path.GetFileName(xaml));
        return data;
    }

    private static string PathOf(string xamlName) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), xamlName, SearchOption.AllDirectories).Single();

    [Theory, MemberData(nameof(GridWindows))]
    public void EveryGridIsAttached(string xamlName)
    {
        var xaml = File.ReadAllText(PathOf(xamlName));
        var code = File.ReadAllText(PathOf(xamlName) + ".cs");
        foreach (Match grid in Regex.Matches(xaml, "<DataGrid [^>]*x:Name=\"(\\w+)\""))
            Assert.Contains($"ExplorerColumns.Attach({grid.Groups[1].Value}", code);
    }

    [Theory, MemberData(nameof(GridWindows))]
    public void EveryColumnHasAPixelWidth(string xamlName)
    {
        var xaml = File.ReadAllText(PathOf(xamlName));
        foreach (Match column in Regex.Matches(xaml, "<DataGrid(Text|CheckBox)Column\\b[^>]*>"))
            Assert.Matches("Width=\"\\d+\"", column.Value);
    }

    [Fact]
    public void TheOldAutoFitIsNotUsedAnywhere()
    {
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("DataGridColumnCap.Track", File.ReadAllText(cs));
    }
}
```

Add to `FilenameListWindowTests.cs` (it already builds the window with its view model; reuse the file's existing builder):

```csharp
    /// <summary>The header checklist and the export share one switch: hiding
    /// Size from the header menu also drops it from the export.</summary>
    [Fact]
    public void TheHeaderMenuFlipsTheSameFlagTheExportReads() => _fx.Invoke(() =>
    {
        var (window, vm) = BuildWindow();   // the file's existing helper
        try
        {
            vm.ShowSize = true;
            var grid = FindDescendant<DataGrid>(window)!;
            var explorer = ExplorerColumns.For(grid)!;
            var size = grid.Columns.Single(c => (string)c.Header == "Size");

            var menu = explorer.BuildHeaderMenu(size);
            menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Size")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.False(vm.ShowSize);
            Assert.Equal(Visibility.Collapsed, size.Visibility);
        }
        finally { window.Close(); }
    });
```

(If `FilenameListWindowTests` names its builder differently, use that name; the assertions stay as written.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~ExplorerColumnsCoverageTests|FullyQualifiedName~TheHeaderMenuFlipsTheSameFlag"`
Expected: FAIL, listing the eight windows not attached and their `Auto`/`*` columns.

- [ ] **Step 3: Pixel widths in XAML**

In each window, replace each column's `Width="Auto"` or `Width="*"`, and delete its `MinWidth`, with the widths from Global Constraints:
- File list: `NumberColumn` 50, File name 320, `PagesColumn` 70, `SizeColumn` 80, `ModifiedColumn` 140, `FolderColumn` 180, `FullPathColumn` 320.
- History: `WhenColumn` 140, Original 240, Filed as 240, `NameColumn` 140, `DestinationColumn` 160, Undone 80.
- Match & merge: `FileColumn` 280, Becomes 280, `NoteColumn` 200.
- Merge PDFs: Item 360, Kind 90, `ResultColumn` 200.
- Page counts: File 360, Pages 70, `NoteColumn` 200.
- Standardise names: Current name 380, `ResultColumn` 300.
- Zip tools: Item 360, Kind 90, `ItemsResultColumn` 200.

Rewrite each column's sizing comment to one line: `<!-- fixed width; the user sizes it (table rules v2, rule 1) -->`. Delete paragraphs about caps, percentiles, star fillers and DataGridColumnCap.

- [ ] **Step 4: Attach in each window's constructor**

Replace each `DataGridColumnCap.Track(...)` line:

| File | New line |
|---|---|
| `HistoryWindow.xaml.cs:13` | `ExplorerColumns.Attach(HistoryGrid, "History");` |
| `MatchMergeWindow.xaml.cs:19` | `ExplorerColumns.Attach(MatchGrid, "MatchMerge");` |
| `MergePdfsWindow.xaml.cs:19` | `ExplorerColumns.Attach(ItemsGrid, "MergePdfs");` |
| `PageCountsWindow.xaml.cs:18` | `ExplorerColumns.Attach(CountsGrid, "PageCounts");` |
| `StandardiseNamesWindow.xaml.cs:19` | `ExplorerColumns.Attach(ResultsGrid, "StandardiseNames");` |
| `ZipToolsWindow.xaml.cs:18` | `ExplorerColumns.Attach(ItemsGrid, "ZipTools");` |

File list: add to its constructor, after `InitializeComponent()` and `SyncColumnVisibility()`:

```csharp
        // The header checklist flips the same view-model flags as the
        // column toggles, so the export always matches what's shown. File
        // name is the anchor: always shown, never dragged.
        ExplorerColumns.Attach(NamesGrid, "FilenameList", anchor: FileNameColumn,
            visibility: new FlagVisibility(this));
```

Give the File name column `x:Name="FileNameColumn"` in the XAML. Add this nested class to `FilenameListWindow.xaml.cs`:

```csharp
    /// <summary>Maps a column to the view-model flag that shows it.</summary>
    private sealed class FlagVisibility(FilenameListWindow window) : IColumnVisibility
    {
        public bool IsShown(DataGridColumn column) => Flag(column) is { } get ? get() : true;

        public void SetShown(DataGridColumn column, bool shown)
        {
            var vm = window._vm;
            if (column == window.NumberColumn) vm.ShowNumber = shown;
            else if (column == window.SizeColumn) vm.ShowSize = shown;
            else if (column == window.ModifiedColumn) vm.ShowModified = shown;
            else if (column == window.FolderColumn) vm.ShowFolder = shown;
            else if (column == window.FullPathColumn) vm.ShowFullPath = shown;
            else if (column == window.PagesColumn) vm.ShowPages = shown;
        }

        private Func<bool>? Flag(DataGridColumn column)
        {
            var vm = window._vm;
            if (column == window.NumberColumn) return () => vm.ShowNumber;
            if (column == window.SizeColumn) return () => vm.ShowSize;
            if (column == window.ModifiedColumn) return () => vm.ShowModified;
            if (column == window.FolderColumn) return () => vm.ShowFolder;
            if (column == window.FullPathColumn) return () => vm.ShowFullPath;
            if (column == window.PagesColumn) return () => vm.ShowPages;
            return null;
        }
    }
```

Triage (`TriageWindow.xaml.cs`):
- In the constructor, after `InitializeComponent()`, add `ExplorerColumns.Attach(Candidates, "Triage");`.
- In the roster-column builder (around :291-339):
  - build every roster column with `Width = new DataGridLength(140)`;
  - delete the `*` filler (column 0 gets 140 too), `FillerMinWidth`, `SafetyMargin`, `ComputeRosterColumnCap` and the `DataGridColumnCap.Track(...)` call.
- Keep `WhyColumnWidth` = 150 for the Why column.

ExplorerColumns' `OnColumnsChanged` gives each rebuilt column its saved width.

- [ ] **Step 5: Remove the superseded tests**

Delete from `AutoFitColumnTests.cs` every fact for these windows: MatchMerge, History, Triage, StandardiseNames, PageCounts, ZipTools and MergePdfs. That covers `*NoHorizontalScrollbar*`, `*StarFiller*`, `*Cap*`, `*UserDragged*`, `*ShareWidth*`, `*ShrinkBack*` and the Track-twice fact. If the file is then empty, delete it.

Also delete:
- `DataGridStarColumnTests.cs` (no star columns remain);
- `HistoryWindowXamlTests.WhenIsNotCappedBecauseItsContentIsBounded`;
- the star-column checks in `DataGridSizingCoverageTests.cs` (`EveryStarColumnDeclaresItsOwnFloor`, `EveryStarDataGridTextColumnTrimsInsteadOfWrapping`). If the file is left with nothing but its registry, delete it too, because `ExplorerColumnsCoverageTests` replaces it.

Then run the per-window colour suites (`DataGridSelectionContrastTests`, `DataGridNoteColourTests`, `TrimmedTextTooltipTests`, `WindowOverflowTests`). Fix any test that looked up a column by a removed width assumption. Update the test; don't loosen it.

- [ ] **Step 6: Run and check**

Run: `cmd /c "A:\DEV\OrdoSort\check.bat"`. Expected: exit 0.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/*.xaml src/OrdoSort.Wpf/Windows/*.xaml.cs tests/OrdoSort.Wpf.Tests/ExplorerColumnsCoverageTests.cs tests/OrdoSort.Wpf.Tests/FilenameListWindowTests.cs tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs tests/OrdoSort.Wpf.Tests/DataGridStarColumnTests.cs tests/OrdoSort.Wpf.Tests/HistoryWindowXamlTests.cs tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs
git commit -m "feat(tables): every grid gets Explorer-style columns; File list's checklist drives its export flags"
```

(Stage deletions with `git rm` for any test file deleted outright.)

---

### Task 5: Explorer look: no row lines or striping, sort chevron, right-aligned numbers, reorder on

**Files:**
- Modify: `src/OrdoSort.Ui/Theme/Styles.xaml`:
  - the DataGrid style around :1672;
  - the DataGridColumnHeader style around :1695;
  - add `GridCellNumber` after `GridCellText` around :1796.
- Modify: `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml` (#, Pages and Size columns' ElementStyle) and `PageCountsWindow.xaml` (Pages)
- Modify: `tests/OrdoSort.Wpf.Tests/SharedGridStyleTests.cs`

**Interfaces:**
- Produces: resource key `GridCellNumber` (TextBlock style, BasedOn `GridCellTextSelectionAware`).

- [ ] **Step 1: Write the failing tests (SharedGridStyleTests)**

Replace `TablesDoNotLetColumnsBeReorderedOrRowsResized` and `PageCountsPagesColumnAlignsLeftNotRight` with:

```csharp
    /// <summary>Table rules v2, rule 5: columns can be dragged to a new
    /// place; rows keep one height.</summary>
    [Fact]
    public void ColumnsCanBeReorderedButRowsKeepOneHeight() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            Assert.True(grid.CanUserReorderColumns);
            Assert.False(grid.CanUserResizeRows);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Rule 8: no lines between rows and no alternating shading,
    /// as in Explorer's Details view.</summary>
    [Fact]
    public void RowsHaveNoLinesAndNoStriping() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            Assert.Equal(DataGridGridLinesVisibility.None, grid.GridLinesVisibility);
            Assert.Equal(((SolidColorBrush)grid.RowBackground).Color, ((SolidColorBrush)grid.AlternatingRowBackground).Color);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Rule 9: a number column's values sit on the right edge,
    /// as in Explorer's Size column; header labels stay left. The shared
    /// number style right-aligns (and stretches, so the ellipsis still
    /// works), and every number column in the app uses it.</summary>
    [Fact]
    public void NumberColumnsRightAlignTheirValues() => _fx.Invoke(() =>
    {
        var style = (Style)_fx.App.FindResource("GridCellNumber");
        var setters = style.Setters.OfType<Setter>().ToList();
        Assert.Contains(setters, s => s.Property == TextBlock.TextAlignmentProperty
            && (TextAlignment)s.Value == TextAlignment.Right);
        Assert.Contains(setters, s => s.Property == FrameworkElement.HorizontalAlignmentProperty
            && (HorizontalAlignment)s.Value == HorizontalAlignment.Stretch);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "OrdoSort.sln"))) root = root.Parent!;
        var windows = Path.Combine(root.FullName, "src", "OrdoSort.Wpf", "Windows");
        foreach (var (file, header) in new[]
        {
            ("PageCountsWindow.xaml", "Pages"), ("FilenameListWindow.xaml", "Pages"),
            ("FilenameListWindow.xaml", "Size"), ("FilenameListWindow.xaml", "#"),
        })
        {
            var xaml = File.ReadAllText(Path.Combine(windows, file));
            var start = xaml.IndexOf($"Header=\"{header}\"", StringComparison.Ordinal);
            var end = xaml.IndexOf("</DataGridTextColumn>", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, $"{file} has no {header} column");
            Assert.Contains("GridCellNumber", xaml[start..end]);
        }
    });

    [Fact]
    public void HeaderLabelsAlignLeft() => _fx.Invoke(() =>
    {
        var style = (Style)_fx.App.FindResource(typeof(DataGridColumnHeader));
        Assert.Contains(style.Setters.OfType<Setter>(), s => s.Property == Control.HorizontalContentAlignmentProperty
            && (HorizontalAlignment)s.Value == HorizontalAlignment.Left);
    });

    /// <summary>Rule 6: the sorted header shows a chevron at 3:1 or better in
    /// both themes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSortChevronIsVisible(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var column = grid.Columns.First(c => c.CanUserSort);
            column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            win.UpdateLayout();
            var header = FindAllDescendants<DataGridColumnHeader>(grid).First(h => h.Column == column);
            var chevron = (System.Windows.Shapes.Path)header.Template.FindName("SortChevron", header);

            Assert.Equal(Visibility.Visible, chevron.Visibility);
            var stroke = ((SolidColorBrush)chevron.Stroke).Color;
            var background = ((SolidColorBrush)header.Background).Color;
            Assert.True(ThemePalette.ContrastRatio(new Rgb(stroke.R, stroke.G, stroke.B),
                new Rgb(background.R, background.G, background.B)) >= 3);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });
```

Each number column's `ElementStyle` must name `GridCellNumber` (directly, or as the `BasedOn` of the column's own style) inside its `<DataGridTextColumn>` element. That's what the scan in `NumberColumnsRightAlignTheirValues` reads.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~SharedGridStyleTests"`
Expected: the four new facts fail.

- [ ] **Step 3: Styles**

In the `DataGrid` style:
- replace `GridLinesVisibility` `Horizontal` with `None`;
- set `AlternatingRowBackground` to `{DynamicResource Theme.Surface}` (same as `RowBackground`);
- replace the `CanUserReorderColumns` `False` setter with `True`;
- keep `CanUserResizeRows` `False`.

Replace the comment above them with: `<!-- Table rules v2 (Explorer Details view): no row lines, no striping, drag-to-reorder on, one row height. -->`

In the `DataGridColumnHeader` style, add a `Template` setter:

```xml
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="DataGridColumnHeader">
                    <Grid>
                        <Border Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}">
                            <Grid>
                                <!-- Explorer's sort chevron: centred on the header's top edge -->
                                <Path x:Name="SortChevron" Visibility="Collapsed"
                                      HorizontalAlignment="Center" VerticalAlignment="Top" Margin="0,2,0,0"
                                      Stroke="{DynamicResource Theme.SubtleText}" StrokeThickness="1.2"
                                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                      Data="M 0,4 L 4,0 L 8,4" />
                                <ContentPresenter Margin="{TemplateBinding Padding}"
                                                  HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                  VerticalAlignment="Center" RecognizesAccessKey="True" />
                            </Grid>
                        </Border>
                        <Thumb x:Name="PART_LeftHeaderGripper" HorizontalAlignment="Left" Width="8"
                               Cursor="SizeWE" Style="{StaticResource HeaderGripper}" />
                        <Thumb x:Name="PART_RightHeaderGripper" HorizontalAlignment="Right" Width="8"
                               Cursor="SizeWE" Style="{StaticResource HeaderGripper}" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="SortDirection" Value="Ascending">
                            <Setter TargetName="SortChevron" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="SortDirection" Value="Descending">
                            <Setter TargetName="SortChevron" Property="Visibility" Value="Visible" />
                            <Setter TargetName="SortChevron" Property="Data" Value="M 0,0 L 4,4 L 8,0" />
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Background" Value="{DynamicResource Theme.SurfaceHover}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
```

Add, before the header style:

```xml
    <!-- The invisible, 8px-wide divider handles a column header resizes by. -->
    <Style x:Key="HeaderGripper" TargetType="Thumb">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                    <Border Background="Transparent" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

Add after `GridCellTextSelectionAware`:

```xml
    <!-- Table rules v2, rule 9: a number column (sizes and counts) puts its
         values on the right edge, as Explorer's Size column does. Stretch
         plus TextAlignment keeps the ellipsis working. -->
    <Style x:Key="GridCellNumber" TargetType="TextBlock" BasedOn="{StaticResource GridCellTextSelectionAware}">
        <Setter Property="HorizontalAlignment" Value="Stretch" />
        <Setter Property="TextAlignment" Value="Right" />
    </Style>
```

Point the File list's #, Pages and Size columns, and Page counts' Pages column, at `GridCellNumber`. Change their `BasedOn`, or set `ElementStyle="{StaticResource GridCellNumber}"` where the column has no own triggers. Keep any column-local triggers by switching their `BasedOn` to `GridCellNumber`.

- [ ] **Step 4: Run SharedGridStyleTests and the colour suites**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~SharedGridStyleTests|FullyQualifiedName~DataGridSelectionContrast|FullyQualifiedName~HighlightContrast|FullyQualifiedName~FocusRing"`
Expected: PASS. If a hover-contrast fact fails on the header's new `SurfaceHover` background, keep the colour and add the header to that suite's list (SurfaceHover is the chrome-tier hover already cleared for `Theme.Text`).

- [ ] **Step 5: check.bat, then commit**

```bash
git add src/OrdoSort.Ui/Theme/Styles.xaml src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml src/OrdoSort.Wpf/Windows/PageCountsWindow.xaml tests/OrdoSort.Wpf.Tests/SharedGridStyleTests.cs
git commit -m "feat(tables): Explorer look: no row lines or striping, sort chevron, numbers right-aligned, reorder on"
```

---

### Task 6: Type-to-jump, F2-only editing, click empty space to clear

**Files:**
- Create: `src/OrdoSort.Wpf/Views/ExplorerColumns.Typing.cs`
- Modify: `tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs` (append)
- Modify: `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml` (help line: "Type to jump to a file; F2 or Enter edits a New name…")

**Interfaces:**
- Consumes: `ExplorerColumns` partial hooks `WireTypingCore()` and `ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs)` (Task 3).
- Produces: `internal bool TypeAhead(string text)`, which returns true when it handled the keystroke.

- [ ] **Step 1: Write the failing tests (append)**

```csharp
    // ---- rule 12: type to jump -------------------------------------------

    [Fact]
    public void TypingJumpsToTheNextRowStartingWithTheLetter() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        var bed = Build(clock: () => now, rows: new[]
        {
            new Row { Name = "alpha" }, new Row { Name = "bravo" }, new Row { Name = "beta" }, new Row { Name = "charlie" },
        });
        try
        {
            Settle(bed.Window);

            Assert.True(bed.Explorer.TypeAhead("b"));
            Assert.Equal("bravo", ((Row)bed.Grid.SelectedItem).Name);

            now = now.AddSeconds(2);                // a second press after the window: next "b"
            bed.Explorer.TypeAhead("b");
            Assert.Equal("beta", ((Row)bed.Grid.SelectedItem).Name);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void LettersTypedWithinASecondRefineTheMatch() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        var bed = Build(clock: () => now, rows: new[] { new Row { Name = "bravo" }, new Row { Name = "beta" } });
        try
        {
            Settle(bed.Window);

            bed.Explorer.TypeAhead("b");
            now = now.AddMilliseconds(300);
            bed.Explorer.TypeAhead("e");

            Assert.Equal("beta", ((Row)bed.Grid.SelectedItem).Name);
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 5: while a cell is being edited, typing is the
    /// edit, not a jump.</summary>
    [Fact]
    public void TypingInsideAnEditIsLeftToTheEditor() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new[] { new Row { Name = "alpha" }, new Row { Name = "bravo" } });
        try
        {
            Settle(bed.Window);
            bed.Grid.SelectedIndex = 0;
            bed.Grid.CurrentCell = new DataGridCellInfo(bed.Grid.Items[0], bed.Column("Note"));
            bed.Grid.BeginEdit();
            Settle(bed.Window);

            Assert.False(bed.Explorer.TypeAhead("b"));
            Assert.Equal("alpha", ((Row)bed.Grid.SelectedItem).Name);
        }
        finally { bed.Window.Close(); }
    });

    // ---- rule 13: empty space clears the selection ------------------------

    [Fact]
    public void ClickingBelowTheLastRowClearsTheSelection() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new[] { new Row { Name = "alpha" }, new Row { Name = "bravo" } });
        try
        {
            Settle(bed.Window);
            bed.Grid.SelectedIndex = 1;
            var presenter = FindDescendant<ItemsPresenter>(bed.Grid)!;

            Assert.True(bed.Explorer.ClearIfEmptySpace(presenter));

            Assert.Null(bed.Grid.SelectedItem);
        }
        finally { bed.Window.Close(); }
    });
```

Add to `BulkRenameSegmentChipTests.cs` (same collection, fixture and temp dir):

```csharp
    /// <summary>Rule 12 in Bulk rename: typing jumps to a file instead of
    /// starting an edit on its New name; the existing F2 path still edits.</summary>
    [Fact]
    public void TypingJumpsInsteadOfStartingAnEdit() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var files = new[] { "alpha_one.pdf", "bravo_two.pdf" }.Select(n =>
        {
            var path = Path.Combine(_dir, n);
            File.WriteAllText(path, "x");
            return path;
        }).ToList();
        var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler());
        vm.AddFilesAsync(files).GetAwaiter().GetResult();
        var win = new BulkRenameWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        try
        {
            win.Show();
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var grid = FindAllDescendants<DataGrid>(win).Single();
            grid.SelectedIndex = 0;

            Assert.True(ExplorerColumns.For(grid)!.TypeAhead("b"));

            Assert.Equal(1, grid.SelectedIndex);
            Assert.False(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase tb
                && FindAllDescendants<System.Windows.Controls.Primitives.TextBoxBase>(grid).Contains(tb));
        }
        finally { win.Close(); }
    });
```

Add `using System.Windows.Input;` and `using OrdoSort.Wpf.Views;` to that file if missing. F2 editing is unchanged code (`BulkRenameWindow.OnGridKeyDown`), and its existing tests keep covering it.

- [ ] **Step 2: Run to verify they fail**

Expected: build errors for `TypeAhead` and `ClearIfEmptySpace`.

- [ ] **Step 3: Implement**

```csharp
// src/OrdoSort.Wpf/Views/ExplorerColumns.Typing.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace OrdoSort.Wpf.Views;

internal sealed partial class ExplorerColumns
{
    /// <summary>Letters typed within this long of each other build one
    /// search, as in Explorer.</summary>
    internal static readonly TimeSpan TypeAheadWindow = TimeSpan.FromSeconds(1);

    private string _typed = "";
    private DateTime _lastTyped = DateTime.MinValue;

    partial void WireTypingCore() => _grid.PreviewTextInput += (_, e) =>
    {
        if (Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Alt) return;
        if (TypeAhead(e.Text)) e.Handled = true;
    };

    /// <summary>Jumps to the next row whose first visible column starts with
    /// what was typed. Returns false (and leaves the keystroke alone) while a
    /// cell is being edited, or for non-printing input.</summary>
    internal bool TypeAhead(string text)
    {
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0]) || IsEditing()) return false;

        var now = _clock();
        var continuing = now - _lastTyped <= TypeAheadWindow;
        _lastTyped = now;
        // Explorer: the same letter again moves on; a new letter refines.
        _typed = continuing && !(_typed.Length == 1 && _typed == text) ? _typed + text : text;

        var first = _grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
            .FirstOrDefault() as DataGridTextColumn;
        if (first?.Binding is not BindingBase binding) return true;

        var items = _grid.Items.Cast<object>().Where(i => i != CollectionView.NewItemPlaceholder).ToList();
        if (items.Count == 0) return true;
        var start = _grid.SelectedIndex < 0 ? 0 : _grid.SelectedIndex + (_typed.Length == 1 ? 1 : 0);
        for (var step = 0; step < items.Count; step++)
        {
            var item = items[(start + step) % items.Count];
            if (ValueOf(item, binding).StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase))
            {
                _grid.SelectedItem = item;
                _grid.CurrentCell = new DataGridCellInfo(item, first);
                _grid.ScrollIntoView(item);
                break;
            }
        }
        return true;
    }

    private bool IsEditing() =>
        Keyboard.FocusedElement is DependencyObject focused && focused is TextBoxBase
        && FindAncestor<DataGrid>(focused) == _grid;

    partial void ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ClearIfEmptySpace(source)) { /* selection cleared */ }
    }

    /// <summary>Clears the selection when <paramref name="source"/> is the
    /// grid's blank area (not a row, header or scrollbar), as a click below
    /// Explorer's last file does.</summary>
    internal bool ClearIfEmptySpace(DependencyObject source)
    {
        if (FindAncestor<DataGridRow>(source) is not null || FindAncestor<DataGridColumnHeader>(source) is not null
            || FindAncestor<ScrollBar>(source) is not null || FindAncestor<DataGrid>(source) != _grid)
            return false;
        _grid.UnselectAll();
        return true;
    }
}
```

Also declare the two partial hooks in `ExplorerColumns.Input.cs`, as written in Task 3: `partial void WireTypingCore();` and `partial void ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs e);`. This file implements them.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -c Release --filter "FullyQualifiedName~ExplorerColumnsTests|FullyQualifiedName~BulkRename"`
Expected: PASS.

- [ ] **Step 5: check.bat, then commit**

```bash
git add src/OrdoSort.Wpf/Views/ExplorerColumns.Typing.cs src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml tests/OrdoSort.Wpf.Tests/ExplorerColumnsTests.cs tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs
git commit -m "feat(tables): type to jump, F2 to edit, click empty space to clear (Explorer)"
```

---

### Task 7: Remove the old auto-fit; docs

**Files:**
- Delete:
  - `src/OrdoSort.Wpf/Views/DataGridColumnCap.cs`
  - `src/OrdoSort.Wpf/Views/ColumnShares.cs`
  - `tests/OrdoSort.Wpf.Tests/DataGridColumnCapTests.cs`
  - `tests/OrdoSort.Wpf.Tests/ColumnSharesTests.cs`
  - any auto-fit test file left from Task 4
- Modify:
  - `src/OrdoSort.Ui/Views/TrimmedTextTooltip.cs` (doc comments that name DataGridColumnCap)
  - `src/OrdoSort.Ui/Theme/Styles.xaml` (GridCellText comment naming the star wrap test)
  - `STATUS.md` and `README.md`

- [ ] **Step 1: Delete and build**

```bash
git rm src/OrdoSort.Wpf/Views/DataGridColumnCap.cs src/OrdoSort.Wpf/Views/ColumnShares.cs tests/OrdoSort.Wpf.Tests/DataGridColumnCapTests.cs tests/OrdoSort.Wpf.Tests/ColumnSharesTests.cs
dotnet build OrdoSort.sln -c Release
```

Expected: 0 errors. Any remaining reference is a leftover from Task 4; fix it there.

- [ ] **Step 2: Stale comments**

Run: `grep -rn "DataGridColumnCap\|ColumnShares\|percentile\|Rule 5\|star filler" src --include=*.cs --include=*.xaml`

Rewrite each hit in plain terms of table rules v2, or delete it. Doc comments must not name deleted types.

- [ ] **Step 3: STATUS.md and README.md**

- STATUS: a Now row saying "Tables mimic File Explorer's Details view (table rules v2) | ✅ Done", naming the spec path. Add a Decisions row saying the September table rules 3 and 5 were retired by the owner on 2026-09-25 in favour of Explorer's model.
- README: under the tools list, add one line: "Tables work like File Explorer's Details view: drag or double-click column dividers, right-click a header to show or hide columns, Ctrl + Plus fits all; layouts are remembered per PC."

- [ ] **Step 4: check.bat, then commit**

```bash
git add src/OrdoSort.Ui/Views/TrimmedTextTooltip.cs src/OrdoSort.Ui/Theme/Styles.xaml STATUS.md README.md
git commit -m "refactor(tables): remove the old auto-fit (DataGridColumnCap, ColumnShares); docs"
```

- [ ] **Step 5: Live check**

Run the Release build from a scratch copy, as done for Bulk rename on 2026-09-25, and in two windows (Bulk rename, History) check:
- drag a divider;
- double-click a divider;
- Ctrl + Plus;
- right-click a header, hide a column;
- drag a header to move a column;
- click a header twice (the chevron flips);
- close and reopen (the layout is back);
- type a letter (the matching row is selected);
- click below the rows (the selection clears).

Record the result in STATUS.
