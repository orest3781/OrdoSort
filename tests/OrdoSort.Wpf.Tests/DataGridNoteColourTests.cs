using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Closes the gap named in the status-colour-vocabulary plan's own
/// brief (2026-08-08, Task 2 Step 3): nothing asserted the RENDERED colour of
/// any DataGrid Note/Why cell before this, and that gap is exactly what let
/// MatchMergeWindow's Note colouring ship dead — a <c>DataTrigger</c>
/// declared on <c>DataGridRow</c>, which Styles.xaml's <c>DataGridCell</c>
/// style (its own unconditional Foreground Setter) always outranks, so it
/// never painted anything from the day it shipped. Measured directly (Task 2
/// Step 1, off-screen, one row per Status): every one of ambiguous/
/// suggested/already/no_match/no_name resolved to plain Theme.Text. A test
/// that finds the trigger in XAML would have passed throughout — proving
/// nothing, the exact trap this suite exists to close. Every assertion below
/// instead reads the actual TextBlock's resolved Foreground brush, the same
/// technique <see cref="HighlightContrastTests"/> already uses.
///
/// The fix moved to a per-COLUMN <c>ElementStyle</c> targeting the column's
/// own TextBlock (MatchMergeWindow.xaml's NoteColumn, BulkRenameWindow.xaml's
/// NoteColumn, TriageWindow.xaml.cs's code-built "Why" column) — the
/// technique BulkRenameWindow's "New name" column already used successfully
/// for its own Changed/Manual triggers. That in turn reproduced Task 1's
/// OTHER trap on a new surface: a Style Setter on the TextBlock itself always
/// outranks whatever it would otherwise INHERIT from its ancestor
/// DataGridCell — including the Accent/AccentText pair the cell's own
/// IsSelected trigger paints. Measured directly (off-screen, selected
/// ambiguous row, before a fix): Theme.StatusAmber (light) on Theme.Accent
/// (light) = 2.26:1, StatusAmber's own selected-cell contrast measured 1.85:1
/// for SubtleText — both well under the 4.5:1 floor. Each ElementStyle below
/// therefore ends with its own "let selection win" DataTrigger (bound to the
/// ancestor DataGridCell's IsSelected via RelativeSource, declared last so it
/// overrides every status/note trigger above it), reverting to
/// Theme.AccentText once selected — same resolution as Task 1's Unlock file
/// list trap, on a different control.</summary>
[Collection(HighlightContrastTests.Name)]
public class DataGridNoteColourTests
{
    private readonly HighlightContrastFixture _fx;
    public DataGridNoteColourTests(HighlightContrastFixture fx) => _fx = fx;

    public static IEnumerable<object[]> PalettesAndSelection()
    {
        foreach (var s in ThemePalette.Schemes)
        foreach (var selected in new[] { false, true })
            yield return new object[] { s.Key, selected };
    }

    // ---------------------------------------------------------- Match & Merge

    /// <summary>Shared body for every MatchMergeWindow Note-column case.
    /// Builds a real window with one row of the given Status, reads the Note
    /// cell's actual TextBlock, and asserts against the vocabulary: amber for
    /// ambiguous/suggested/no_roster (needs a person), subtle for already/
    /// no_match/no_name (a fact, not a problem) — or, once selected,
    /// AccentText regardless of Status (selection wins).</summary>
    private void AssertMatchMergeNoteColour(string schemeKey, bool selected, string status,
        Func<ThemePalette, Rgb> expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        vm.Rows.Add(new MatchRow(@"C:\inbox\a.pdf", "a.pdf", "", "some note text here", status));
        var window = new MatchMergeWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under MatchMergeWindow");
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, cellBg) = ResolveNoteCellForeground(grid, "Note");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"MatchMerge Note selected, {status} ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                var expected = expectedUnselected(p);
                Assert.Equal(expected, fg);
                // The row's own RowStyle sets Background="Transparent" (both
                // MatchMergeWindow.xaml and BulkRenameWindow.xaml), so the
                // real paint behind an unselected cell is the DataGrid's own
                // Background — Theme.Surface, the same reference
                // AssertUnlockFileListNoteContrast uses for its ListBox.
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"MatchMerge Note unselected, {status} ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
            _ = cellBg;
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeAmbiguousNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "ambiguous", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeSuggestedNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "suggested", p => p.StatusAmber));

    /// <summary>no_roster ("load a roster first") is a thing the user must
    /// fix, not a passive fact — the plan's mapping calls it amber
    /// explicitly, distinct from already/no_match/no_name below.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoRosterNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "no_roster", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeAlreadyNoteIsSubtleUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "already", p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoMatchNoteIsSubtleUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "no_match", p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoNameNoteIsSubtleUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(schemeKey, selected, "no_name", p => p.SubtleText));

    // ---------------------------------------------------------- Bulk rename

    /// <summary>Shared body for BulkRenameWindow Note-column cases. Unlike
    /// MatchMerge's single Status field, BulkRename's row is built directly
    /// with the exact (Changed, Manual, NoteIsProblem) combination each case
    /// needs — see NoteIsProblem's own doc comment in
    /// BulkRenameViewModel.cs for why Manual/Changed/NeedsName alone can't
    /// tell "edited by hand" apart from "a problem that also got a manual
    /// edit".</summary>
    private void AssertBulkRenameNoteColour(string schemeKey, bool selected, RenameRow row,
        Func<ThemePalette, Rgb>? expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new BulkRenameViewModel();
        vm.Preview.Add(row);
        var window = new BulkRenameWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under BulkRenameWindow");
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Note");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"BulkRename Note selected ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                Assert.NotNull(expectedUnselected);
                var expected = expectedUnselected!(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"BulkRename Note unselected ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameEditedByHandNoteIsSubtleUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(schemeKey, selected,
            new RenameRow(@"C:\inbox\a.pdf", "a.pdf", "NEW.pdf", "edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "NEW.pdf", noteIsProblem: false),
            p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameNoChangeNoteIsSubtleUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(schemeKey, selected,
            new RenameRow(@"C:\inbox\b.pdf", "b.pdf", "b.pdf", "(no change)",
                changed: false, manual: false, needsName: false, editSeed: "b.pdf", noteIsProblem: false),
            p => p.SubtleText));

    /// <summary>The "couldn't do it" family (doesn't match the layout, an
    /// illegal name, a would-be-empty name) — NeedsName true, NoteIsProblem
    /// true.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameSkippedNoteIsAmberUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(schemeKey, selected,
            new RenameRow(@"C:\inbox\c.pdf", "c.pdf", "c.pdf",
                "doesn't match the review-file layout — skipped",
                changed: false, manual: false, needsName: true, editSeed: "c.pdf", noteIsProblem: true),
            p => p.StatusAmber));

    /// <summary>The edge case NoteIsProblem exists for: Plan() reported a
    /// problem (a name collision, auto-resolved with a counter) on a row that
    /// ALSO changed successfully AND carries a manual edit — Changed=true and
    /// Manual=true alike hold for both this row and the plain "edited by
    /// hand" case above, so only NoteIsProblem tells them apart. Vocabulary:
    /// still "reporting a problem" (the actual target differs from what was
    /// typed), so still amber — not "edited by hand"'s subtle.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameCollisionNoteIsAmberEvenWhenManualUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(schemeKey, selected,
            new RenameRow(@"C:\inbox\d.pdf", "d.pdf", "TAKEN (2).pdf",
                "name was taken — using a counter — edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "TAKEN (2).pdf", noteIsProblem: true),
            p => p.StatusAmber));

    // ------------------------------------------- Zip and unzip / Merge PDFs
    //
    // 2026-08-09 Tools-menu utilities audit finding 1: ZipMergeWindow's own
    // Result column rendered a genuine merge failure (Error) in the SAME
    // Theme.StatusAmber as NoPdfs — a zip with zero PDFs inside, which
    // ZipItemRow's own doc comment still calls out explicitly as "not a
    // failure the way an unreadable one is." The vocabulary
    // (status-colour-vocabulary plan, 2026-08-08): amber means "needs
    // attention," never "a merely informational fact" and never a stand-in
    // for a real error — Error gets Theme.StatusRed instead, the same colour
    // UnlockWindow.xaml's Unreadable DataTrigger already uses for its own
    // genuine failure. NoPdfs stays amber; it's still the correct "needs
    // attention" case.
    //
    // Both of those windows became ZipToolsWindow's two tabs on 2026-08-18,
    // and the two tabs' Result columns are SEPARATE XAML declarations with
    // separate trigger sets — the Zip & unzip one deliberately omits the
    // amber NoPdfs trigger, since only a merge can produce that status. So
    // the Error case runs against BOTH tabs (a red trigger dropped from
    // either one is a real regression), and the NoPdfs case against the
    // Merge tab alone.

    public static IEnumerable<object[]> PalettesSelectionAndTab()
    {
        foreach (var s in ThemePalette.Schemes)
        foreach (var selected in new[] { false, true })
        foreach (var mergeTab in new[] { false, true })
            yield return new object[] { s.Key, selected, mergeTab };
    }

    /// <summary>Shared body for both tabs' Result-column cases. Builds a real
    /// window with one ZipItemRow driven through its own internal Apply (same
    /// internal-member access ZipExtractViewModelTests/MergePdfsViewModelTests
    /// already use — InternalsVisibleTo covers this test assembly), reads the
    /// Result cell's actual TextBlock, and asserts against the vocabulary —
    /// or, once selected, AccentText regardless of status (selection wins).
    ///
    /// A TabControl realizes ONLY the selected tab's content, so the tab is
    /// chosen after Show and layout flushed again; without that,
    /// FindDescendant resolves the other tab's grid, which also has a
    /// "Result" column and would answer with the wrong colours.</summary>
    private void AssertZipToolsResultColour(string schemeKey, bool selected, bool mergeTab,
        string status, Func<ThemePalette, Rgb> expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new ZipToolsViewModel(new FakeDialogs());
        var row = new ZipItemRow(@"C:\inbox\a.zip", "zip");
        if (mergeTab)
        {
            row.Apply(new PdfMerge.MergeResult(row.Path, status, Message: "some result text here"));
            vm.MergePdfs.Rows.Add(row);
        }
        else
        {
            row.Apply(new Zipper.UnzipResult(row.Path, status, null, "some result text here"));
            vm.ZipExtract.Rows.Add(row);
        }

        var tab = mergeTab ? "Merge PDFs" : "Zip & unzip";
        var window = new ZipToolsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            window.Tabs.SelectedIndex = mergeTab ? 1 : 0;
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under ZipToolsWindow");
            Assert.Same(mergeTab ? vm.MergePdfs.Rows : vm.ZipExtract.Rows, grid.ItemsSource);
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Result");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"{tab} Result selected, {status} ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                var expected = expectedUnselected(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"{tab} Result unselected, {status} ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The literal fix the 2026-08-09 Finding 1 made, now carried on
    /// both tabs: a genuine failure is Theme.StatusRed, not Theme.StatusAmber.
    /// Break it on either tab (revert that column's trigger Setter to
    /// StatusAmber, or drop the trigger while copy-pasting one grid from the
    /// other) and this fails for a value reason — wrong brush, not a render
    /// error.</summary>
    [Theory, MemberData(nameof(PalettesSelectionAndTab))]
    public void ZipToolsErrorResultIsRedUnlessSelected(string schemeKey, bool selected, bool mergeTab) =>
        _fx.Invoke(() => AssertZipToolsResultColour(
            schemeKey, selected, mergeTab, "error", p => p.StatusRed));

    /// <summary>NoPdfs is deliberately left amber — a zip with nothing to
    /// merge is "needs attention," not an error and not merely informational
    /// either (someone likely wants to know). Merge tab only: nothing the Zip
    /// &amp; unzip tab runs can produce that status, which is why that tab's
    /// Result column carries no amber trigger at all. This case exists so a
    /// future edit that flattens Error and NoPdfs back to the same colour (in
    /// EITHER direction) is caught here, not just by the Error case
    /// above.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void ZipToolsNoPdfsResultIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertZipToolsResultColour(
            schemeKey, selected, mergeTab: true, "no_pdfs", p => p.StatusAmber));

    // -------------------------------------------------------- PDF page counts
    //
    // Not named in this task's Finding 1 (PageCountsWindow's Note column was
    // already correct — PageCounts.Count never puts anything but a genuine
    // failure message there, so unconditional StatusAmber was never a
    // vocabulary violation the way ZipMerge/Unzip's Error-as-amber was), but
    // added here as part of closing this suite's OWN coverage gap for the
    // five Tools windows — see DataGridWindowCoverageTests.

    private void AssertPageCountsNoteColour(string schemeKey, bool selected, string note,
        Func<ThemePalette, Rgb> expectedUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new PageCountsViewModel(new FakeDialogs());
        var row = new PageCountRow(@"C:\inbox\a.pdf");
        row.Apply(new PageCounts.CountResult(row.Path, note.Length == 0 ? 3 : null, note));
        vm.Rows.Add(row);
        var window = new PageCountsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under PageCountsWindow");
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Note");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"PageCounts Note selected ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                var expected = expectedUnselected(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"PageCounts Note unselected ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void PageCountsErrorNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertPageCountsNoteColour(schemeKey, selected,
            "password-protected or unreadable — couldn't count", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void PageCountsCleanCountNoteIsTextUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertPageCountsNoteColour(schemeKey, selected, "", p => p.Text));

    // ---------------------------------------------------------------- Triage

    /// <summary>TriageWindow's "Why" column only exists for a "suggested"
    /// item (TriageWindow.xaml.cs, ShowCurrentAsync) — a test that never
    /// inserts a suggested item can't test it at all, per the plan's own
    /// note. Deliberately never calls Show() on the window: that would fire
    /// the constructor's Loaded handler, which starts a REAL WebView2 init
    /// (environment-sensitive in this sandbox — see
    /// WebViewPdfViewerGuardBehaviourTests). Calling ShowCurrentAsync()
    /// directly (same technique TriageWindowDecisionRaceTests already uses)
    /// populates Candidates.ItemsSource and inserts the Why column
    /// synchronously and safely, since WebViewPdfViewer._ready is false
    /// before Show() — its ShowAsync is a genuine no-op. Realizing the grid's
    /// row/cell containers then only needs Candidates' own
    /// ApplyTemplate+Measure+Arrange (confirmed empirically: DataGrid, unlike
    /// Calendar — see HighlightContrastTests' CalendarDayNumbersMeetWcagAa —
    /// resolves its style and generates containers with no live
    /// PresentationSource at all), never the window itself.</summary>
    private void AssertTriageWhyColour(string schemeKey, bool selected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var item = new MatchMerge.MatchResult(@"C:\inbox\doc.pdf", "suggested", "SMITH", "JOHN",
            Suggestions: new List<MatchMerge.Suggestion>
            {
                new(new MatchMerge.Candidate("1", new Dictionary<string, string> { ["A"] = "x" }),
                    "token match on last name"),
            });
        var window = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" })
        {
            Dialogs = new FakeDialogs(),
        };
        try
        {
#pragma warning disable xUnit1031
            window.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

            var grid = window.Candidates;
            grid.ApplyTemplate();
            grid.Measure(new Size(440, 500));
            grid.Arrange(new Rect(0, 0, 440, 500));
            grid.UpdateLayout();
            if (selected) { grid.SelectedIndex = 0; grid.UpdateLayout(); }

            var (fg, _) = ResolveNoteCellForeground(grid, "Why");

            if (selected)
            {
                Assert.Equal(p.AccentText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Accent);
                Assert.True(ratio >= 4.5,
                    $"Triage Why selected ({schemeKey}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                Assert.Equal(p.SubtleText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"Triage Why unselected ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void TriageWhyNoteIsSubtleUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertTriageWhyColour(schemeKey, selected));

    // -------------------------------------------------------------- plumbing

    /// <summary>Walk to a named column's row-0 cell and return its
    /// TextBlock's resolved Foreground, plus the cell's own (pre-composite)
    /// Background — the latter is always Transparent by design (see the
    /// class doc) and returned only so a caller can assert that fact if it
    /// ever wants to, not used for contrast math here.</summary>
    private static (Rgb fg, Rgb? cellBg) ResolveNoteCellForeground(DataGrid grid, string columnHeader)
    {
        var column = grid.Columns.FirstOrDefault(c => (c.Header as string) == columnHeader)
            ?? throw new InvalidOperationException($"no '{columnHeader}' column found");
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new InvalidOperationException($"{columnHeader}: row 0 never realized a container");
        row.ApplyTemplate();
        row.UpdateLayout();
        var cell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == column)
            ?? throw new InvalidOperationException($"{columnHeader}: cell never realized");
        cell.ApplyTemplate();
        cell.UpdateLayout();
        var text = FindDescendant<TextBlock>(cell)
            ?? throw new InvalidOperationException($"{columnHeader}: cell TextBlock never realized");
        var cellBg = cell.Background is SolidColorBrush cb ? ToRgb(cb) : (Rgb?)null;
        return (ToRgb(text.Foreground), cellBg);
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
