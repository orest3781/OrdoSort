using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The systemic test the status-colour-vocabulary work (2026-08-08)
/// should have shipped with the first time, per its own retro: every prior
/// fix in this family (Match &amp; Merge's dead row triggers, History's dead
/// reverted-row colour, the grids' missing hover feedback, and now the
/// File/Becomes/Current name/New name selection-contrast bug this suite
/// exists to close — the FOURTH instance of the exact same
/// "Style-Setter-on-the-TextBlock-outranks-inheritance" trap found in this
/// repo in one day) was pinned by a hand-written test naming that one
/// column. A hand-written test only ever proves the columns someone
/// remembered to write a case for; it says nothing about the next one.
///
/// So instead of a fifth per-column case: this ENUMERATES every
/// <see cref="DataGridTextColumn"/> actually present on each of the four
/// grid windows' <see cref="DataGrid"/> — MatchMergeWindow, BulkRenameWindow,
/// HistoryWindow, TriageWindow (the "why" column included, via a suggested
/// item) — renders that window off-screen, selects row 0, and asserts the
/// RESOLVED Foreground of every column's ACTUAL realized TextBlock clears
/// 4.5:1 against the resolved selected-row background, in both palettes.
/// A column added to any of these grids next month is covered by this suite
/// the next time it runs, without a new test case. Reverting the
/// GridCellText-based fix on any one column (Theme/Styles.xaml, or that
/// column's own BasedOn) makes the corresponding test below fail, and name
/// that column in its assertion message — verified directly by doing exactly
/// that during this task and pasting the failing output (see the task's own
/// report), not assumed.
///
/// Deliberately asserts resolved, rendered brushes only — never XAML
/// presence of a Setter or Trigger, which is exactly what let the row-level
/// triggers this suite's sibling tests (DataGridNoteColourTests,
/// HighlightContrastTests' DataGridRow hover coverage) already found dead
/// ship unnoticed.</summary>
[Collection(HighlightContrastTests.Name)]
public class DataGridSelectionContrastTests
{
    private readonly HighlightContrastFixture _fx;
    public DataGridSelectionContrastTests(HighlightContrastFixture fx) => _fx = fx;

    public static IEnumerable<object[]> Palettes()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

    // ------------------------------------------------------- window builders

    private static (MatchMergeWindow win, DataGrid grid) BuildMatchMergeWindow()
    {
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        vm.Rows.Add(new MatchRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf",
            "a-long-enough-filename-to-matter.pdf", "SMITH, JOHN — 1234567890.pdf",
            "3 candidates — decide in Review matches", "ambiguous"));
        var win = new MatchMergeWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under MatchMergeWindow");
        return (win, grid);
    }

    private static (BulkRenameWindow win, DataGrid grid) BuildBulkRenameWindow()
    {
        var vm = new BulkRenameViewModel();
        vm.Preview.Add(new RenameRow(@"C:\inbox\old-name-before-review.pdf", "old-name-before-review.pdf",
            "20240101-SMITH-JOHN.pdf", "edited by hand",
            changed: true, manual: true, needsName: false, editSeed: "20240101-SMITH-JOHN.pdf",
            noteIsProblem: false));
        var win = new BulkRenameWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under BulkRenameWindow");
        return (win, grid);
    }

    private static (HistoryWindow win, DataGrid grid, History history, string dbPath) BuildHistoryWindow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        history.LogCommit(@"c:\in\a.pdf", "a.pdf", "A.pdf", "A",
            "insert", "", "Invoices", @"c:\out", tagged: false, "");
        var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
        var win = new HistoryWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under HistoryWindow");
        return (win, grid, history, dbPath);
    }

    /// <summary>TurnaroundViewModel has no direct settable Rows/Preview
    /// collection like MatchMergeViewModel/BulkRenameViewModel — its
    /// Documents grid is populated only by actually loading a fixture file
    /// through AddPaths (SweptTable.Load + TurnaroundTime.ComputeAll), the
    /// same off-thread DebouncedProbe&lt;SweptTable.Table&gt; shape
    /// TurnaroundViewModelTests exercises with InlineWorkScheduler/
    /// probeDelayMs: 0/uiContext: null. That combination still runs the
    /// probe's completion on a threadpool thread rather than synchronously
    /// inline (see that class's own doc comment), so — unlike every other
    /// builder in this file — this one has to poll for the row before
    /// Show()ing the window, not just Add() it directly. threshold controls
    /// whether the resulting single row is flagged IsOverThreshold: the
    /// fixture's fixed TAT (61 days: report upload 2025-03-03, document date
    /// 2025-01-01) is compared against cfg.TatThresholdDays (1000 = always
    /// under; 0 = always over) rather than varying the dates, so both
    /// callers share the exact same rows/columns/text lengths.</summary>
    private static (TurnaroundWindow win, DataGrid grid, TurnaroundViewModel vm, string dir)
        BuildTurnaroundWindow(bool overThreshold)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ordo_test_tat_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "20250303-1144-PECF Report.csv"),
            "SourceType,FileName\nDRG,20250101-a-long-enough-filename-to-matter.pdf\n");
        var cfg = new Config { TatThresholdDays = overThreshold ? 0 : 1000 };
        var vm = new TurnaroundViewModel(cfg, new FakeDialogs(), saveCfg: null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

        vm.AddPaths(new[] { dir });
        WaitForTurnaroundLoad(() => vm.Documents.Count == 1, "the TAT fixture row should load");

        var win = new TurnaroundWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under TurnaroundWindow");
        return (win, grid, vm, dir);
    }

    /// <summary>Same shape as TurnaroundViewModelTests.WaitFor — the load
    /// this builder drives is genuinely off-thread even with
    /// InlineWorkScheduler/probeDelayMs: 0 (DebouncedProbe's own Timer still
    /// fires on a threadpool thread), so the row has to be polled for rather
    /// than asserted the instant AddPaths returns. Bounded, not indefinite:
    /// a stuck load fails the test instead of hanging the run.</summary>
    private static void WaitForTurnaroundLoad(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    private static void CleanupTurnaround(Window win, TurnaroundViewModel vm, string dir)
    {
        try { win.Close(); } catch { /* best effort */ }
        vm.Dispose();
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>Built with a "suggested" current item so the code-built "Why"
    /// column (TriageWindow.xaml.cs, ShowCurrentAsync) is present — the one
    /// column on this grid that ISN'T built in the constructor, so a test
    /// that only exercises the constructor's roster columns would silently
    /// skip it. Same construction technique as DataGridNoteColourTests'
    /// AssertTriageWhyColour / HighlightContrastTests'
    /// TriageGridRowHoverRendersViaTheImplicitStyle: ShowCurrentAsync()
    /// called directly rather than Show()ing the window, since Show() would
    /// start a real WebView2 init this sandbox can't complete.</summary>
    private static TriageWindow BuildTriageWindowWithWhy()
    {
        var item = new MatchMerge.MatchResult(@"C:\inbox\doc.pdf", "suggested", "SMITH", "JOHN",
            Suggestions: new List<MatchMerge.Suggestion>
            {
                new(new MatchMerge.Candidate("1", new Dictionary<string, string> { ["A"] = "x" }),
                    "token match on last name"),
            });
        var win = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" })
        {
            Dialogs = new FakeDialogs(),
        };
#pragma warning disable xUnit1031
        win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        win.Candidates.ApplyTemplate();
        win.Candidates.Measure(new Size(440, 500));
        win.Candidates.Arrange(new Rect(0, 0, 440, 500));
        win.Candidates.UpdateLayout();
        return win;
    }

    // -------------------------------------------------------- the enumerator

    /// <summary>The core deliverable. Selects row 0, then for EVERY
    /// <see cref="DataGridTextColumn"/> currently on <paramref name="grid"/>
    /// (whatever they are — this file never names one), reads that column's
    /// realized row-0 cell TextBlock and asserts its Foreground clears
    /// 4.5:1 against <paramref name="p"/>.Accent, the selected cell's own
    /// background (Styles.xaml's DataGridCell style). Every failing column is
    /// collected and named in ONE combined failure rather than stopping at
    /// the first, so a regression touching two columns at once is reported
    /// as two.</summary>
    private static void AssertEverySelectedColumnClearsContrast(DataGrid grid, ThemePalette p, string windowName)
    {
        grid.SelectedIndex = 0;
        grid.UpdateLayout();
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new InvalidOperationException($"{windowName}: row 0 never realized a container");
        row.ApplyTemplate();
        row.UpdateLayout();

        var columns = grid.Columns.OfType<DataGridTextColumn>().ToList();
        Assert.True(columns.Count > 0, $"{windowName}: no DataGridTextColumn found — enumeration is vacuous");

        var failures = new List<string>();
        foreach (var column in columns)
        {
            var header = column.Header?.ToString() ?? "(no header)";
            var cell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == column);
            if (cell is null) { failures.Add($"'{header}': cell never realized"); continue; }
            cell.ApplyTemplate();
            cell.UpdateLayout();
            var text = FindDescendant<TextBlock>(cell);
            if (text is null) { failures.Add($"'{header}': cell TextBlock never realized"); continue; }

            var fg = ToRgb(text.Foreground);
            var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
            if (ratio < 4.5)
                failures.Add($"'{header}': {fg} on {p.Accent} = {ratio:F2}");
        }

        Assert.True(failures.Count == 0,
            $"{windowName} selected-row columns failing 4.5:1 against Theme.Accent: {string.Join("; ", failures)}");
    }

    /// <summary>Same enumeration, unselected: every column's cell must clear
    /// 4.5:1 against Theme.Surface — the flat paint every one of these four
    /// grids' rows actually sits on at rest (Styles.xaml's DataGrid style;
    /// MatchMerge/BulkRename/History's own RowStyle sets Background
    /// "Transparent" specifically so their status-vocabulary columns sit on
    /// this same flat reference rather than alternating row shading — see
    /// DataGridNoteColourTests' own comment on this).</summary>
    private static void AssertEveryUnselectedColumnClearsContrast(DataGrid grid, ThemePalette p, string windowName)
    {
        grid.SelectedIndex = -1;
        grid.UpdateLayout();
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new InvalidOperationException($"{windowName}: row 0 never realized a container");
        row.ApplyTemplate();
        row.UpdateLayout();

        var columns = grid.Columns.OfType<DataGridTextColumn>().ToList();
        Assert.True(columns.Count > 0, $"{windowName}: no DataGridTextColumn found — enumeration is vacuous");

        var failures = new List<string>();
        foreach (var column in columns)
        {
            var header = column.Header?.ToString() ?? "(no header)";
            var cell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == column);
            if (cell is null) { failures.Add($"'{header}': cell never realized"); continue; }
            cell.ApplyTemplate();
            cell.UpdateLayout();
            var text = FindDescendant<TextBlock>(cell);
            if (text is null) { failures.Add($"'{header}': cell TextBlock never realized"); continue; }

            var fg = ToRgb(text.Foreground);
            var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
            if (ratio < 4.5)
                failures.Add($"'{header}': {fg} on {p.Surface} = {ratio:F2}");
        }

        Assert.True(failures.Count == 0,
            $"{windowName} unselected-row columns failing 4.5:1 against Theme.Surface: {string.Join("; ", failures)}");
    }

    // ---------------------------------------------------------------- Tests

    [Theory, MemberData(nameof(Palettes))]
    public void MatchMergeAllColumnsSelectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid) = BuildMatchMergeWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "MatchMergeWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void MatchMergeAllColumnsUnselectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid) = BuildMatchMergeWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "MatchMergeWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void BulkRenameAllColumnsSelectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid) = BuildBulkRenameWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "BulkRenameWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void BulkRenameAllColumnsUnselectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid) = BuildBulkRenameWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "BulkRenameWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void HistoryAllColumnsSelectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, history, dbPath) = BuildHistoryWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "HistoryWindow"); }
        finally { CleanupHistory(win, history, dbPath); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void HistoryAllColumnsUnselectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, history, dbPath) = BuildHistoryWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "HistoryWindow"); }
        finally { CleanupHistory(win, history, dbPath); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void TriageAllColumnsSelectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var win = BuildTriageWindowWithWhy();
        try
        {
            // sanity: the "Why" column really is on this grid, or this test
            // would silently only cover the roster columns
            Assert.Contains(win.Candidates.Columns.OfType<DataGridTextColumn>(),
                c => (c.Header as string) == "Why");
            AssertEverySelectedColumnClearsContrast(win.Candidates, p, "TriageWindow");
        }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void TriageAllColumnsUnselectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var win = BuildTriageWindowWithWhy();
        try
        {
            Assert.Contains(win.Candidates.Columns.OfType<DataGridTextColumn>(),
                c => (c.Header as string) == "Why");
            AssertEveryUnselectedColumnClearsContrast(win.Candidates, p, "TriageWindow");
        }
        finally { win.Close(); }
    });

    /// <summary>The Documents grid — built via <see cref="BuildTurnaroundWindow"/>
    /// with a row that is NOT over threshold, so the generic assertions'
    /// background assumptions (Theme.Accent selected / Theme.Surface
    /// unselected — see AssertEverySelectedColumnClearsContrast's and
    /// AssertEveryUnselectedColumnClearsContrast's own doc comments) hold
    /// exactly as they do for the other four windows. The IsOverThreshold
    /// DataTrigger's OWN combination — Theme.Warning row background,
    /// Theme.WarningText foreground, and what selection does to both — is a
    /// different composite than "Surface" and gets its own dedicated tests
    /// below (TurnaroundOverThresholdRow*), the same split MatchMerge's Note
    /// and BulkRename's New name use between "every column, generically" and
    /// "this column's own extra trigger, specifically".</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void TurnaroundAllColumnsSelectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, vm, dir) = BuildTurnaroundWindow(overThreshold: false);
        try { AssertEverySelectedColumnClearsContrast(grid, p, "TurnaroundWindow"); }
        finally { CleanupTurnaround(win, vm, dir); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void TurnaroundAllColumnsUnselectedClearContrast(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, vm, dir) = BuildTurnaroundWindow(overThreshold: false);
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "TurnaroundWindow"); }
        finally { CleanupTurnaround(win, vm, dir); }
    });

    // --------------------------------------- Turnaround's own extra trigger

    /// <summary>IsOverThreshold's own composite, unselected: the ROW's
    /// Background goes to Theme.Warning (DataGrid.RowStyle) and the File
    /// name column's own DataTrigger flips its TextBlock's Foreground to
    /// Theme.WarningText alongside it — TurnaroundWindow.xaml's own comment
    /// on why every text column needs this pairing, not just Theme.Text on
    /// Theme.Warning (which was never validated to clear 4.5:1). Then
    /// selecting the row proves the "let selection win" trailing DataTrigger
    /// still overrides it, the same contract every other status-bearing
    /// column in the app holds to (BulkRenameNewNameUnchangedRowIsSubtleUnlessSelected
    /// is the closest sibling: one unselected assertion, one selected
    /// assertion, same test).</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void TurnaroundOverThresholdRowIsWarningColoredUnlessSelected(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, vm, dir) = BuildTurnaroundWindow(overThreshold: true);
        try
        {
            // sanity: the fixture really did land an over-threshold row, or
            // the rest of this test would silently pass against a row that
            // never exercised the trigger at all
            Assert.True(vm.Documents[0].IsOverThreshold);

            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();

            var rowBg = ToRgb((SolidColorBrush)row.Background);
            Assert.Equal(p.Warning, rowBg);

            var (text, _) = ResolveCellOn(row, grid, "File name");
            Assert.Equal(p.WarningText, ToRgb(text.Foreground));
            var unselectedRatio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), rowBg);
            Assert.True(unselectedRatio >= 4.5,
                $"Turnaround File name over-threshold, unselected ({(dark ? "dark" : "light")}): {ToRgb(text.Foreground)} on {rowBg} = {unselectedRatio:F2}");

            grid.SelectedIndex = 0;
            grid.UpdateLayout();
            Assert.Equal(p.AccentText, ToRgb(text.Foreground));
            var selectedRatio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), p.Accent);
            Assert.True(selectedRatio >= 4.5,
                $"Turnaround File name over-threshold, selected ({(dark ? "dark" : "light")}): {ToRgb(text.Foreground)} on {p.Accent} = {selectedRatio:F2}");
        }
        finally { CleanupTurnaround(win, vm, dir); }
    });

    /// <summary>Same "selected cell background is opaque and wins over the
    /// row's own tint" proof as MatchMergeSelectedNoteStaysAccentTextEvenWhileRowIsHovered
    /// — here for Theme.Warning instead of Theme.RowHover, since a warning
    /// row is exactly the case someone is most likely to have their mouse
    /// over (it's the row the whole tab exists to call attention to) at the
    /// moment they click to select it.</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void TurnaroundOverThresholdRowStaysAccentTextEvenWhileHovered(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid, vm, dir) = BuildTurnaroundWindow(overThreshold: true);
        try
        {
            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();
            ForceMouseOver(row, true);
            grid.SelectedIndex = 0;
            row.UpdateLayout();
            grid.UpdateLayout();

            var (text, cell) = ResolveCellOn(row, grid, "File name");

            // the selected CELL's own opaque background, not the hovered
            // ROW's Warning tint underneath — this is the composite the
            // text actually sits on (a DataGridCell is a visual child of
            // its row, rendered after it)
            var cellBg = ToRgb((SolidColorBrush)cell.Background);
            Assert.Equal(p.Accent, cellBg);

            var fg = ToRgb(text.Foreground);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, cellBg);
            Assert.True(ratio >= 4.5,
                $"Turnaround File name over-threshold selected+hovered ({(dark ? "dark" : "light")}): {fg} on {cellBg} = {ratio:F2}");
        }
        finally { CleanupTurnaround(win, vm, dir); }
    });

    // --------------------------------------------- hovered-and-selected too

    /// <summary>The task's own instruction: "hovered-and-selected must be
    /// readable too", since Theme.RowHover is now a warm tint rather than a
    /// fainter grey. Forces IsMouseOver on the row (Theme.RowHover paints
    /// the ROW's own Background) AND selects it (Theme.Accent paints the
    /// CELL's own Background, opaque, on top of whatever the row painted
    /// underneath — a DataGridCell is a visual child of its row, rendered
    /// after it) — proving the selected cell's own opaque background, not a
    /// blend with the hover tint, is what a selected+hovered row's text
    /// actually sits on. Uses MatchMerge's colour-bearing Note column
    /// specifically: it is the column with the MOST triggers competing for
    /// Foreground (two status triggers plus the selection override), so it
    /// is the shape most likely to regress if trigger precedence ever
    /// shifts under a future edit.</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void MatchMergeSelectedNoteStaysAccentTextEvenWhileRowIsHovered(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);
        var (win, grid) = BuildMatchMergeWindow();
        try
        {
            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();
            ForceMouseOver(row, true);
            grid.SelectedIndex = 0;
            row.UpdateLayout();
            grid.UpdateLayout();

            var noteColumn = grid.Columns.FirstOrDefault(c => (c.Header as string) == "Note")
                ?? throw new InvalidOperationException("no 'Note' column found");
            var cell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == noteColumn)
                ?? throw new InvalidOperationException("'Note' cell never realized");
            cell.ApplyTemplate();
            cell.UpdateLayout();
            var text = FindDescendant<TextBlock>(cell)
                ?? throw new InvalidOperationException("'Note' cell TextBlock never realized");

            // the selected CELL's own background, not the hovered ROW's —
            // this is the composite the text actually sits on
            var cellBg = ToRgb((SolidColorBrush)cell.Background);
            Assert.Equal(p.Accent, cellBg);

            var fg = ToRgb(text.Foreground);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, cellBg);
            Assert.True(ratio >= 4.5,
                $"MatchMerge Note selected+hovered ({(dark ? "dark" : "light")}): {fg} on {cellBg} = {ratio:F2}");
        }
        finally { win.Close(); }
    });

    // ------------------------------------ New name: the second literal fix

    /// <summary>New name (BulkRenameWindow.xaml) had NO "let selection win"
    /// override at all before this task — unlike Note, which already carried
    /// one — so a selected, unchanged row's New name cell showed
    /// Theme.SubtleText on Theme.Accent (measured ~1.85:1 light) instead of
    /// AccentText. Covers both halves the fix has to preserve: the
    /// SubtleText/Bold vocabulary must still render UNSELECTED (Changed/
    /// Manual DataTriggers, untouched by this task), and selection must WIN
    /// once a row is picked, the same contract every other status-bearing
    /// column in the app already holds to. FontWeight is asserted
    /// unconditionally (both selected and unselected) since the new trigger
    /// only touches Foreground — Bold surviving selection is exactly the
    /// "must survive" case the task named explicitly.</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void BulkRenameNewNameUnchangedRowIsSubtleUnlessSelected(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var vm = new BulkRenameViewModel();
        vm.Preview.Add(new RenameRow(@"C:\inbox\b.pdf", "b.pdf", "b.pdf", "(no change)",
            changed: false, manual: false, needsName: false, editSeed: "b.pdf", noteIsProblem: false));
        var win = new BulkRenameWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            win.Show();
            win.UpdateLayout();
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("no DataGrid descendant under BulkRenameWindow");
            var (text, _) = ResolveCell(grid, "New name  ·  click or press F2 to edit");

            Assert.Equal(p.SubtleText, ToRgb(text.Foreground));
            var unselectedRatio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), p.Surface);
            Assert.True(unselectedRatio >= 4.5,
                $"BulkRename New name unchanged, unselected ({(dark ? "dark" : "light")}): {ToRgb(text.Foreground)} on {p.Surface} = {unselectedRatio:F2}");

            grid.SelectedIndex = 0;
            grid.UpdateLayout();
            Assert.Equal(p.AccentText, ToRgb(text.Foreground));
            var selectedRatio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), p.Accent);
            Assert.True(selectedRatio >= 4.5,
                $"BulkRename New name unchanged, selected ({(dark ? "dark" : "light")}): {ToRgb(text.Foreground)} on {p.Accent} = {selectedRatio:F2}");
        }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(Palettes))]
    public void BulkRenameNewNameManualEditStaysBoldSelectedOrNot(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var vm = new BulkRenameViewModel();
        vm.Preview.Add(new RenameRow(@"C:\inbox\d.pdf", "d.pdf", "TYPED-BY-HAND.pdf", "edited by hand",
            changed: true, manual: true, needsName: false, editSeed: "TYPED-BY-HAND.pdf", noteIsProblem: false));
        var win = new BulkRenameWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            win.Show();
            win.UpdateLayout();
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("no DataGrid descendant under BulkRenameWindow");
            var (text, _) = ResolveCell(grid, "New name  ·  click or press F2 to edit");

            Assert.Equal(FontWeights.Bold, text.FontWeight);
            Assert.Equal(p.Text, ToRgb(text.Foreground));   // Changed=true: not SubtleText

            grid.SelectedIndex = 0;
            grid.UpdateLayout();
            Assert.Equal(FontWeights.Bold, text.FontWeight);   // Bold must survive selection
            Assert.Equal(p.AccentText, ToRgb(text.Foreground));
            var ratio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), p.Accent);
            Assert.True(ratio >= 4.5,
                $"BulkRename New name manual+bold, selected ({(dark ? "dark" : "light")}): {ToRgb(text.Foreground)} on {p.Accent} = {ratio:F2}");
        }
        finally { win.Close(); }
    });

    // -------------------------------------------------------------- plumbing

    private static void CleanupHistory(Window win, History history, string dbPath)
    {
        try { win.Close(); } catch { /* best effort */ }
        history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    private static (TextBlock text, DataGridCell cell) ResolveCell(DataGrid grid, string columnHeader)
    {
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new InvalidOperationException($"{columnHeader}: row 0 never realized a container");
        row.ApplyTemplate();
        row.UpdateLayout();
        return ResolveCellOn(row, grid, columnHeader);
    }

    /// <summary>Same resolution as <see cref="ResolveCell"/>, but against an
    /// ALREADY-realized row a caller needs to keep its own hold on — the
    /// over-threshold hover tests force IsMouseOver on a specific
    /// DataGridRow instance before selecting it, and re-deriving "row 0"
    /// from the grid afterward risks resolving a different container than
    /// the one that was actually hovered.</summary>
    private static (TextBlock text, DataGridCell cell) ResolveCellOn(DataGridRow row, DataGrid grid, string columnHeader)
    {
        var column = grid.Columns.FirstOrDefault(c => (c.Header as string) == columnHeader)
            ?? throw new InvalidOperationException($"no '{columnHeader}' column found");
        var cell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == column)
            ?? throw new InvalidOperationException($"{columnHeader}: cell never realized");
        cell.ApplyTemplate();
        cell.UpdateLayout();
        var text = FindDescendant<TextBlock>(cell)
            ?? throw new InvalidOperationException($"{columnHeader}: cell TextBlock never realized");
        return (text, cell);
    }

    /// <summary>Same reflection trick as HighlightContrastTests' own
    /// ForceMouseOver — UIElement.IsMouseOver is read-only, normally flipped
    /// only by real mouse input.</summary>
    private static void ForceMouseOver(UIElement el, bool value)
    {
        var field = typeof(UIElement).GetField("IsMouseOverPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "UIElement has no private static IsMouseOverPropertyKey field");
        var key = (DependencyPropertyKey)field.GetValue(null)!;
        el.SetValue(key, value);
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

    private static Rgb ToRgb(Brush? brush) => brush switch
    {
        SolidColorBrush s => new Rgb(s.Color.R, s.Color.G, s.Color.B),
        _ => throw new InvalidOperationException(
            $"expected a resolved SolidColorBrush, got {brush?.GetType().Name ?? "null"}"),
    };
}
