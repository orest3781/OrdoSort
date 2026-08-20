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

    // ------------------------ five Tools-menu utilities (2026-08-09 merge)
    //
    // Filename list, Zip, Merge PDFs from zip, Unzip and PDF page counts —
    // the five windows this task's own audit named as having ZERO coverage
    // in this suite (and in DataGridNoteColourTests): DataGridSelectionContrastTests
    // ENUMERATES columns within windows it already has a builder for, so a
    // window nobody added a builder for is invisible to it — exactly what
    // happened here for a full day after these five landed. Three of them
    // (Zip, Unzip, Merge PDFs from zip) became ZipToolsWindow's two tabs on
    // 2026-08-18; one builder with a tab flag stands for what used to be
    // three. Each builder below seeds its ViewModel's Rows directly
    // (ZipItemRow/PageCountRow via their own internal Apply, the same
    // internal-member access ZipExtractViewModelTests/MergePdfsViewModelTests/
    // PageCountsViewModelTests already use) rather than driving a real
    // file-system batch through AddPaths — same shortcut
    // BuildMatchMergeWindow/BuildBulkRenameWindow above take with
    // MatchRow/RenameRow, and harmless here since these Windows/ViewModels
    // never re-derive Status from anything but the Apply call itself.
    //
    // See DataGridWindowCoverageTests for the OTHER half of this task's own
    // brief: a suite that discovers — via reflection over
    // OrdoSort.Wpf.Windows plus each window's own XAML source, not a
    // hand-maintained list of window NAMES — every Window with a
    // &lt;DataGrid&gt; and fails loudly, naming it, if it's missing from
    // this file's own CoveredWindows registry. That closes the DISCOVERY
    // gap (a future window can no longer vanish silently); it does not
    // itself write the builder a future window still needs — see that
    // file's own class doc for the honest scope of what "automatic" means
    // here.

    /// <summary>One builder for both of ZipToolsWindow's tabs (2026-08-18:
    /// Zip, Unzip and Merge PDFs from zip became this one window). A
    /// TabControl realizes ONLY the selected tab's content, so the tab is
    /// chosen after Show and layout flushed again — without that,
    /// FindDescendant returns the OTHER tab's grid and the assertion would
    /// pass while measuring the wrong columns.
    ///
    /// Each tab is seeded through the result shape its own tab actually
    /// produces: an extract failure on Zip &amp; unzip, a merge failure on
    /// Merge PDFs. Only the tab under test is seeded, so a failure message
    /// can only be about the grid that was measured.</summary>
    private static (ZipToolsWindow win, DataGrid grid) BuildZipToolsWindow(bool mergeTab)
    {
        var vm = new ZipToolsViewModel(new FakeDialogs());
        var row = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
        if (mergeTab)
        {
            row.Apply(new ZipMerge.MergeResult(row.Path, "error",
                Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
            vm.MergePdfs.Rows.Add(row);
        }
        else
        {
            row.Apply(new Zipper.UnzipResult(row.Path, "error", null,
                "not a valid zip archive — a long enough exception message to matter"));
            vm.ZipExtract.Rows.Add(row);
        }
        var win = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        win.Tabs.SelectedIndex = mergeTab ? 1 : 0;
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under ZipToolsWindow");
        // The tab really did switch: both grids bind the same row property
        // names, so a selection that silently didn't take would still find a
        // grid, still find every column, and still pass.
        Assert.Same(mergeTab ? vm.MergePdfs.Rows : vm.ZipExtract.Rows, grid.ItemsSource);
        return (win, grid);
    }

    private static (PageCountsWindow win, DataGrid grid) BuildPageCountsWindow()
    {
        var vm = new PageCountsViewModel(new FakeDialogs());
        var row = new PageCountRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf");
        row.Apply(new PageCounts.CountResult(row.Path, null,
            "password-protected or unreadable — couldn't count"));
        vm.Rows.Add(row);
        var win = new PageCountsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under PageCountsWindow");
        return (win, grid);
    }

    private static (FilenameListWindow win, DataGrid grid) BuildFilenameListWindow()
    {
        var vm = new FilenameListViewModel(new FakeDialogs())
        {
            // Every optional column on. This suite's whole contract (see the
            // class doc above) is to enumerate whatever DataGridTextColumns
            // are actually ON the grid — but FilenameListWindow's five
            // optional columns (Task 9) set Visibility=Collapsed from
            // code-behind when their Show* flag is off, and a Collapsed
            // DataGridColumn never gets a DataGridCell realized at all (not
            // merely hidden — verified empirically: AssertEvery*ColumnClears
            // Contrast reported "cell never realized" for all five before
            // this line was added). Leaving Columns at its None default
            // would silently narrow this suite back down to just the
            // always-visible Name column, exactly the single-hand-written-
            // case trap this suite exists to replace.
            Columns = FilenameList.Columns.Number | FilenameList.Columns.Size
                    | FilenameList.Columns.Modified | FilenameList.Columns.Folder
                    | FilenameList.Columns.FullPath,
        };
        vm.Rows.Add(new FilenameList.FileRow(
            "a-long-enough-filename-to-matter.pdf", null, null, "", ""));
        var win = new FilenameListWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        win.Show();
        win.UpdateLayout();
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant under FilenameListWindow");
        return (win, grid);
    }

    /// <summary>Both tabs' columns, selected and unselected, in one pass per
    /// tab: the two grids are separate visual trees that can only be measured
    /// one at a time, so building the window twice per scheme (once per tab)
    /// is what covering every column costs here. Selected and unselected run
    /// against the SAME built window rather than two, since neither assertion
    /// leaves state the other reads — each sets grid.SelectedIndex itself.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ZipToolsZipTabAllColumnsClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildZipToolsWindow(mergeTab: false);
        try
        {
            AssertEverySelectedColumnClearsContrast(grid, p, "ZipToolsWindow (Zip & unzip tab)");
            AssertEveryUnselectedColumnClearsContrast(grid, p, "ZipToolsWindow (Zip & unzip tab)");
        }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ZipToolsMergeTabAllColumnsClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildZipToolsWindow(mergeTab: true);
        try
        {
            AssertEverySelectedColumnClearsContrast(grid, p, "ZipToolsWindow (Merge PDFs tab)");
            AssertEveryUnselectedColumnClearsContrast(grid, p, "ZipToolsWindow (Merge PDFs tab)");
        }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void PageCountsAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildPageCountsWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "PageCountsWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void PageCountsAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildPageCountsWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "PageCountsWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void FilenameListAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildFilenameListWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "FilenameListWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void FilenameListAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildFilenameListWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "FilenameListWindow"); }
        finally { win.Close(); }
    });

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

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MatchMergeAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildMatchMergeWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "MatchMergeWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MatchMergeAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildMatchMergeWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "MatchMergeWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void BulkRenameAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildBulkRenameWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "BulkRenameWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void BulkRenameAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid) = BuildBulkRenameWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "BulkRenameWindow"); }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void HistoryAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid, history, dbPath) = BuildHistoryWindow();
        try { AssertEverySelectedColumnClearsContrast(grid, p, "HistoryWindow"); }
        finally { CleanupHistory(win, history, dbPath); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void HistoryAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (win, grid, history, dbPath) = BuildHistoryWindow();
        try { AssertEveryUnselectedColumnClearsContrast(grid, p, "HistoryWindow"); }
        finally { CleanupHistory(win, history, dbPath); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void TriageAllColumnsSelectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
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

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void TriageAllColumnsUnselectedClearContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var win = BuildTriageWindowWithWhy();
        try
        {
            Assert.Contains(win.Candidates.Columns.OfType<DataGridTextColumn>(),
                c => (c.Header as string) == "Why");
            AssertEveryUnselectedColumnClearsContrast(win.Candidates, p, "TriageWindow");
        }
        finally { win.Close(); }
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
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MatchMergeSelectedNoteStaysAccentTextEvenWhileRowIsHovered(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
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
                $"MatchMerge Note selected+hovered ({schemeKey}): {fg} on {cellBg} = {ratio:F2}");
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
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void BulkRenameNewNameUnchangedRowIsSubtleUnlessSelected(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

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
                $"BulkRename New name unchanged, unselected ({schemeKey}): {ToRgb(text.Foreground)} on {p.Surface} = {unselectedRatio:F2}");

            grid.SelectedIndex = 0;
            grid.UpdateLayout();
            Assert.Equal(p.AccentText, ToRgb(text.Foreground));
            var selectedRatio = ThemePalette.ContrastRatio(ToRgb(text.Foreground), p.Accent);
            Assert.True(selectedRatio >= 4.5,
                $"BulkRename New name unchanged, selected ({schemeKey}): {ToRgb(text.Foreground)} on {p.Accent} = {selectedRatio:F2}");
        }
        finally { win.Close(); }
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void BulkRenameNewNameManualEditStaysBoldSelectedOrNot(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

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
                $"BulkRename New name manual+bold, selected ({schemeKey}): {ToRgb(text.Foreground)} on {p.Accent} = {ratio:F2}");
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
