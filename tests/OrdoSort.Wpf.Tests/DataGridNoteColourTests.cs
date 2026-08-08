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
        foreach (var dark in new[] { false, true })
        foreach (var selected in new[] { false, true })
            yield return new object[] { dark, selected };
    }

    // ---------------------------------------------------------- Match & Merge

    /// <summary>Shared body for every MatchMergeWindow Note-column case.
    /// Builds a real window with one row of the given Status, reads the Note
    /// cell's actual TextBlock, and asserts against the vocabulary: amber for
    /// ambiguous/suggested/no_roster (needs a person), subtle for already/
    /// no_match/no_name (a fact, not a problem) — or, once selected,
    /// AccentText regardless of Status (selection wins).</summary>
    private void AssertMatchMergeNoteColour(bool dark, bool selected, string status,
        Func<ThemePalette, Rgb> expectedUnselected)
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
                    $"MatchMerge Note selected, {status} ({(dark ? "dark" : "light")}): {fg} on {p.Accent} = {ratio:F2}");
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
                    $"MatchMerge Note unselected, {status} ({(dark ? "dark" : "light")}): {fg} on {p.Surface} = {ratio:F2}");
            }
            _ = cellBg;
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeAmbiguousNoteIsAmberUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "ambiguous", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeSuggestedNoteIsAmberUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "suggested", p => p.StatusAmber));

    /// <summary>no_roster ("load a roster first") is a thing the user must
    /// fix, not a passive fact — the plan's mapping calls it amber
    /// explicitly, distinct from already/no_match/no_name below.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoRosterNoteIsAmberUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "no_roster", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeAlreadyNoteIsSubtleUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "already", p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoMatchNoteIsSubtleUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "no_match", p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void MatchMergeNoNameNoteIsSubtleUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertMatchMergeNoteColour(dark, selected, "no_name", p => p.SubtleText));

    // ---------------------------------------------------------- Bulk rename

    /// <summary>Shared body for BulkRenameWindow Note-column cases. Unlike
    /// MatchMerge's single Status field, BulkRename's row is built directly
    /// with the exact (Changed, Manual, NoteIsProblem) combination each case
    /// needs — see NoteIsProblem's own doc comment in
    /// BulkRenameViewModel.cs for why Manual/Changed/NeedsName alone can't
    /// tell "edited by hand" apart from "a problem that also got a manual
    /// edit".</summary>
    private void AssertBulkRenameNoteColour(bool dark, bool selected, RenameRow row,
        Func<ThemePalette, Rgb>? expectedUnselected)
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
                    $"BulkRename Note selected ({(dark ? "dark" : "light")}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                Assert.NotNull(expectedUnselected);
                var expected = expectedUnselected!(p);
                Assert.Equal(expected, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"BulkRename Note unselected ({(dark ? "dark" : "light")}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameEditedByHandNoteIsSubtleUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(dark, selected,
            new RenameRow(@"C:\inbox\a.pdf", "a.pdf", "NEW.pdf", "edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "NEW.pdf", noteIsProblem: false),
            p => p.SubtleText));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameNoChangeNoteIsSubtleUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(dark, selected,
            new RenameRow(@"C:\inbox\b.pdf", "b.pdf", "b.pdf", "(no change)",
                changed: false, manual: false, needsName: false, editSeed: "b.pdf", noteIsProblem: false),
            p => p.SubtleText));

    /// <summary>The "couldn't do it" family (doesn't match the layout, an
    /// illegal name, a would-be-empty name) — NeedsName true, NoteIsProblem
    /// true.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void BulkRenameSkippedNoteIsAmberUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(dark, selected,
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
    public void BulkRenameCollisionNoteIsAmberEvenWhenManualUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
        AssertBulkRenameNoteColour(dark, selected,
            new RenameRow(@"C:\inbox\d.pdf", "d.pdf", "TAKEN (2).pdf",
                "name was taken — using a counter — edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "TAKEN (2).pdf", noteIsProblem: true),
            p => p.StatusAmber));

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
    private void AssertTriageWhyColour(bool dark, bool selected)
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
                    $"Triage Why selected ({(dark ? "dark" : "light")}): {fg} on {p.Accent} = {ratio:F2}");
            }
            else
            {
                Assert.Equal(p.SubtleText, fg);
                var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
                Assert.True(ratio >= 4.5,
                    $"Triage Why unselected ({(dark ? "dark" : "light")}): {fg} on {p.Surface} = {ratio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void TriageWhyNoteIsSubtleUnlessSelected(bool dark, bool selected) =>
        _fx.Invoke(() => AssertTriageWhyColour(dark, selected));

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
