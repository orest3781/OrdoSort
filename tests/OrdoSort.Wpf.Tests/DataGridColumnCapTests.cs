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
/// infinity would leave a column at its old width forever.
///
/// FIX ROUND 1 (2026-08-29 review): the facts above this note all built a
/// grid with exactly ONE governed column beside the star — so "split the
/// remainder equally among governed columns" (the rule this task replaced)
/// and "split in proportion to content" (the rule it became) produce the
/// IDENTICAL number every time, and none of them could actually tell the
/// two rules apart. <see cref="BuildPair"/> and
/// <see cref="TwoGovernedColumnsSplitTheShortfallInProportionToTheirContent"/>
/// below are what close that gap. Separately, <see cref="BuildTwoStars"/>
/// and <see cref="TwoStarColumnsSplitTheLeftoverInProportionToTheirContentRatherThanEvenly"/>
/// prove a star column's WEIGHT (not just its rendered width) now carries
/// its computed share — needed the moment a grid has more than one star
/// column, which HistoryWindow alone does (Original, Filed as).</summary>
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

    private sealed class PairRow
    {
        public string Name { get; init; } = "";
        public string Note { get; init; } = "";
        public string Extra { get; init; } = "";
    }

    private sealed record BareGridPair(
        Window Window, DataGrid DataGrid, DataGridTextColumn Name, DataGridTextColumn Note, DataGridTextColumn Extra,
        ObservableCollection<PairRow> Rows);

    /// <summary>A grid with TWO governed columns beside the star, so a fact
    /// can tell a proportional split from an equal one — with one governed
    /// column the two rules produce the same number, which is why every
    /// fact above this one is blind to the rule it is meant to prove.</summary>
    private static BareGridPair BuildPair(double windowWidth, double nameFloor, params PairRow[] rows)
    {
        var items = new ObservableCollection<PairRow>(rows);
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
        var extra = new DataGridTextColumn
        {
            Header = "Extra", Binding = new Binding("Extra"), Width = DataGridLength.Auto,
            ElementStyle = Wrapping(),
        };
        grid.Columns.Add(name);
        grid.Columns.Add(note);
        grid.Columns.Add(extra);
        DataGridColumnCap.Track(grid, note, extra);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false,
        };
        return new BareGridPair(window, grid, name, note, extra, items);
    }

    private sealed class TwoStarRow
    {
        public string Wide { get; init; } = "";
        public string Narrow { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private sealed record BareGridTwoStars(
        Window Window, DataGrid DataGrid, DataGridTextColumn Wide, DataGridTextColumn Narrow, DataGridTextColumn Note,
        ObservableCollection<TwoStarRow> Rows);

    /// <summary>Two star columns beside one tracked Auto column — the shape
    /// HistoryWindow alone has (Original, Filed as). Every other builder
    /// above has exactly one star column, where WPF's default 1:1 leftover
    /// split to a SINGLE recipient is indistinguishable from a proportional
    /// one; this is the shape that can tell them apart.</summary>
    private static BareGridTwoStars BuildTwoStars(double windowWidth, params TwoStarRow[] rows)
    {
        var items = new ObservableCollection<TwoStarRow>(rows);
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        var wide = new DataGridTextColumn
        {
            Header = "Wide", Binding = new Binding("Wide"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 60,
            ElementStyle = Wrapping(),
        };
        var narrow = new DataGridTextColumn
        {
            Header = "Narrow", Binding = new Binding("Narrow"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 60,
            ElementStyle = Wrapping(),
        };
        var note = new DataGridTextColumn
        {
            Header = "Note", Binding = new Binding("Note"), Width = DataGridLength.Auto,
            ElementStyle = Wrapping(),
        };
        grid.Columns.Add(wide);
        grid.Columns.Add(narrow);
        grid.Columns.Add(note);
        DataGridColumnCap.Track(grid, note);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false,
        };
        return new BareGridTwoStars(window, grid, wide, narrow, note, items);
    }

    private sealed class LeadingRow
    {
        public string First { get; init; } = "";
        public string Name { get; init; } = "";
    }

    private sealed record BareGridLeading(
        Window Window, DataGrid DataGrid, DataGridTextColumn First, DataGridTextColumn Name,
        ObservableCollection<LeadingRow> Rows);

    /// <summary>A GOVERNED column first, the star filler second —
    /// BulkRenameWindow's own visual order (Current name, then New name),
    /// not the star-first shape every other builder above uses. Table-rules
    /// Rule 5's "the FIRST column, if empty, matches the one to its right"
    /// branch needs exactly this shape to exercise at all: every other
    /// builder here would only ever exercise the "matches its left
    /// neighbour" branch.</summary>
    private static BareGridLeading BuildLeading(double windowWidth, double nameFloor, params LeadingRow[] rows)
    {
        var items = new ObservableCollection<LeadingRow>(rows);
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        var first = new DataGridTextColumn
        {
            Header = "First", Binding = new Binding("First"), Width = DataGridLength.Auto,
            ElementStyle = Wrapping(),
        };
        var name = new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = nameFloor,
            ElementStyle = Wrapping(),
        };
        grid.Columns.Add(first);
        grid.Columns.Add(name);
        DataGridColumnCap.Track(grid, first);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false,
        };
        return new BareGridLeading(window, grid, first, name, items);
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
        // wider than a 500px window can show. FIX ROUND 1 (2026-08-29
        // review): this fact used to also recompute the expected cap as
        // available*content/Sigma-content and assert Note's MaxWidth against
        // it — a restatement of the production formula, and this Build()
        // shape (one governed column beside the star) can't even tell a
        // proportional split from an equal one (see the class doc above),
        // so that assertion never independently proved anything.
        // TwoGovernedColumnsSplitTheShortfallInProportionToTheirContent below
        // is what actually tells the two rules apart; this fact keeps the
        // behavioural assertions only.
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
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

    /// <summary>Not a guard on the cap — kept deliberately anyway, as the
    /// platform characterization the whole feature rests on. Breaking
    /// <see cref="DataGridColumnCap.AutofitCaps"/> entirely (comment it out,
    /// return null unconditionally, whatever) leaves this fact GREEN: WPF's
    /// own space-fitting wraps a cell that doesn't fit its column and grows
    /// that cell's row to hold it regardless of whether anything ever set a
    /// MaxWidth, because a narrowed cell wraps and grows its row instead of
    /// clipping, and a short row beside it stays one line, with or without
    /// this class in the picture. What DOES make this fact fail is a
    /// regression in the two things autofit-then-wrap actually depends on:
    /// the wrapping ElementStyle (delete TextWrapping="Wrap" and the cell
    /// clips a single line instead of growing) or the row's own height
    /// mechanics (a RowHeight setter that pins every row to one height would
    /// stop the tall row from growing at all). Both of those are worth a
    /// fact — this is that fact — even though neither is what this class
    /// computes.</summary>
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

    /// <summary>FIX ROUND 1 (2026-08-29 review): the original version of
    /// this fact asserted <c>double.IsPositiveInfinity(g.Name.MaxWidth)</c>,
    /// which cannot hold for ANY star column in WPF — confirmed with an
    /// isolated, zero-dependency probe: the moment a DataGridColumn's Width
    /// becomes a Star length, WPF itself coerces that column's own MaxWidth
    /// to 10000, independent of this class entirely (it never assigns a
    /// star column's MaxWidth, before or after this fix round). That made
    /// the OLD assertion fail against both the old and the new
    /// DataGridColumnCap for a reason unrelated to either.
    ///
    /// First attempt at a replacement called ColumnShares.Compute directly
    /// as an oracle (available/natural/floors reconstructed by hand) and
    /// compared it to Name's ActualWidth — measured 35px off, every time,
    /// not a flake: <see cref="ColumnShares.Compute"/>'s "share" for a lone
    /// star participant is an abstract accounting figure against an
    /// "available" pool that reserves a scrollbar allowance and a safety
    /// margin; neither is actually spent when nothing else claims them, so
    /// (per this class's own doc comment) they land back in the star column
    /// instead — exactly SafetyMargin(20) + SystemParameters.
    /// VerticalScrollBarWidth on this machine. Reproducing that reservation
    /// by hand in the test would be restating the production accounting,
    /// not independently checking it. This version asserts the STRUCTURAL
    /// promise instead, which needs none of that: the star and the governed
    /// column between them must account for the whole grid, with nothing
    /// wasted and nothing overflowing into a scrollbar — which is what "the
    /// star takes what the governed column leaves, not a cap of its own"
    /// actually means.</summary>
    [Fact]
    public void AStarColumnEndsUpAtTheShareTheGovernedColumnsLeaveIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
            Assert.True(Math.Abs((g.Name.ActualWidth + g.Note.ActualWidth) - g.DataGrid.ActualWidth) <= 3,
                $"Name ({g.Name.ActualWidth}px) + Note ({g.Note.ActualWidth}px) should account for the whole grid " +
                $"({g.DataGrid.ActualWidth}px) — the star should take exactly what the governed column leaves, " +
                "not sit at some cap of its own that leaves the rest unaccounted for");
            Assert.True(g.Name.ActualWidth > 150,
                $"the star should get a genuine share of the room, not be starved down near its 40px floor: {g.Name.ActualWidth}px");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    /// <summary>FIX ROUND 1 (2026-08-29 review), item A: every fact above
    /// this one tracks exactly ONE governed column, so "split the remainder
    /// EQUALLY among governed columns" (the rule this task replaced) and
    /// "split in PROPORTION to content" (the rule it became) produce the
    /// identical number — there is only one governed column to split
    /// anything among. Three of the six live callers track two governed
    /// columns each (BulkRename, History, MatchMerge) and three track one
    /// (MergePdfs, PageCounts, ZipTools) — History tracks two, not three,
    /// since When left the governed set — so this is the shape that
    /// actually exercises the rule this task changed, not just a shape the
    /// old and new rule happen to agree on.</summary>
    [Fact]
    public void TwoGovernedColumnsSplitTheShortfallInProportionToTheirContent() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        // Sixty M's against thirty: the caps must come out about 2:1. An
        // equal split — the rule this replaced — would make them equal, so
        // this is the fact that tells the two rules apart.
        var g = BuildPair(500, 40, new PairRow { Name = Ms(20), Note = Ms(60), Extra = Ms(30) });
        try
        {
            ShowAndSettle(g.Window);
            var noteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));
            var extraContent = ContentWidthOf(CellText(g.DataGrid, g.Extra, 0));
            var expectedRatio = noteContent / extraContent;
            var actualRatio = g.Note.MaxWidth / g.Extra.MaxWidth;
            Assert.True(Math.Abs(actualRatio - expectedRatio) < 0.15,
                $"Note:Extra caps are {g.Note.MaxWidth}:{g.Extra.MaxWidth} = {actualRatio:F2}, " +
                $"but their content is {noteContent}:{extraContent} = {expectedRatio:F2} — an equal split would be 1.00");
            Assert.True(g.Note.MaxWidth - g.Extra.MaxWidth > 40,
                "the two caps must differ substantially; equal caps mean the split is not proportional");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>ITEM 1 (2026-08-29 fix-wave review), the CRITICAL this class
    /// shipped with: every unheld participant in the non-fit branch gets
    /// <c>wanted * (available / sum-of-wanted)</c> — strictly LESS than its
    /// own wanted width, by construction, whenever the branch engages at
    /// all. For a column with EMPTY or short cells, "wanted" IS its header
    /// width (<see cref="ContentWidths.Of"/> seeds "widest" with the header
    /// measurement), and the old floor was only <c>Math.Max(MinimumCap,
    /// column.MinWidth)</c> — 20px on every governed column in this app,
    /// since none declares a MinWidth. So a column whose cells happen to be
    /// empty could be, and was, capped below its own header — and a
    /// DataGridColumnHeader neither wraps nor trims, so that clips it
    /// outright. Reproduced directly in the review, not hypothesized:
    /// MatchMerge after an ordinary successful batch leaves Note empty
    /// (header ~49px) beside a long File, and a measured run put File's cap
    /// at 597.78px, a ratio around 0.78, which lands Note near 38px.
    ///
    /// Extra here is empty — its wanted width is exactly its header, nothing
    /// else — beside Note fed enough content to dominate the pool and force
    /// the non-fit branch at BuildPair's 500px window (the same shape and
    /// width <see cref="TwoGovernedColumnsSplitTheShortfallInProportionToTheirContent"/>
    /// above already proves engages it). Without the header floor, Extra's
    /// proportional share collapses toward its bare 20px floor, well under
    /// its own header.
    ///
    /// Two assertions, not one: the first (MaxWidth against the independently
    /// measured header width) is the arithmetic; the second — the REALIZED
    /// header's own DesiredSize.Width fitting inside the column's
    /// ActualWidth — is the actual user-visible guarantee the arithmetic
    /// exists to protect, checked on the real header WPF renders rather than
    /// trusted from the formula alone. DesiredSize.Width, not ActualWidth,
    /// for the header side of that comparison: a realized header's
    /// ActualWidth is stretched to fill whatever the column resolves to —
    /// always equal to the column, never informative — where DesiredSize is
    /// what the header's own content asked for, the number that exceeds the
    /// column and clips if the floor is missing.</summary>
    [Fact]
    public void AColumnWithEmptyContentIsFlooredAtItsOwnHeaderWidthNotBelowIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = BuildPair(500, 40, new PairRow { Name = Ms(20), Note = Ms(90), Extra = "" });
        try
        {
            ShowAndSettle(g.Window);

            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Extra);
            var headerText = FindDescendant<TextBlock>(header)!;
            var expectedHeaderFloor = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;

            Assert.True(g.Extra.MaxWidth >= expectedHeaderFloor - 1,
                $"Extra's cap is {g.Extra.MaxWidth}px, under its own {expectedHeaderFloor}px header — " +
                "a header never wraps or trims, so a cap below it clips the header text");
            Assert.True(header.DesiredSize.Width <= g.Extra.ActualWidth + 1,
                $"Extra's realized header wants {header.DesiredSize.Width}px but the column is only " +
                $"{g.Extra.ActualWidth}px wide — the header would be clipped");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>FIX ROUND 1 (2026-08-29 review), item B: HistoryWindow tracks
    /// two governed columns (Name, Destination) beside TWO star columns
    /// (Original, Filed as) —
    /// every other builder in this file has exactly one star column, where
    /// WPF's default 1:1 leftover split to a SINGLE recipient is
    /// indistinguishable from a proportional one. Left at the flat weight
    /// every star column starts with, two star columns split 1:1 regardless
    /// of content — the class doc's promise of a proportional split would
    /// silently not extend to them. This is the shape that tells the
    /// difference.</summary>
    [Fact]
    public void TwoStarColumnsSplitTheLeftoverInProportionToTheirContentRatherThanEvenly() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = BuildTwoStars(900, new TwoStarRow { Wide = Ms(60), Narrow = Ms(15), Note = "x" });
        try
        {
            ShowAndSettle(g.Window);
            Assert.True(g.Wide.ActualWidth > 1.5 * g.Narrow.ActualWidth,
                $"the wide-content star should end up meaningfully wider than the narrow one, not an even split: " +
                $"Wide={g.Wide.ActualWidth}px, Narrow={g.Narrow.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    // ---- Table-rules Rule 3: 80th percentile, not the maximum ----------

    /// <summary>Table-rules, Rule 3 — the governing defect the owner
    /// reported: "i simply want to prevent 1 really long filename to make
    /// the column too wide." Four short rows and one very long outlier:
    /// nearest-rank on five sorted widths picks rank ceil(0.8×5)=4, the
    /// FOURTH-smallest — one of the four short rows, never the outlier at
    /// rank 5. A window wide enough that nothing is held at a floor isolates
    /// the percentile arithmetic itself from the proportional-split branch
    /// TwoGovernedColumnsSplitTheShortfallInProportionToTheirContent already
    /// covers.</summary>
    [Fact]
    public void ASingleOutlierAmongSeveralRowsDoesNotSetTheColumnsWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(2000, 40,
            new Row { Name = "a", Note = Ms(15) },
            new Row { Name = "b", Note = Ms(15) },
            new Row { Name = "c", Note = Ms(15) },
            new Row { Name = "d", Note = Ms(15) },
            new Row { Name = "e", Note = Ms(80) });   // the one very long outlier
        try
        {
            ShowAndSettle(g.Window);
            var shortContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));
            var longContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 4));
            Assert.True(longContent > 3 * shortContent,
                $"the outlier ({longContent}px) needs to be dramatically longer than the short rows " +
                $"({shortContent}px), or this fact cannot tell the percentile from the maximum");

            Assert.True(Math.Abs(g.Note.MaxWidth - shortContent) <= 2,
                $"Note's cap is {g.Note.MaxWidth}px; the 80th percentile of four {shortContent}px rows " +
                $"and one {longContent}px outlier should sit at the short rows' own width");
            Assert.True(g.Note.MaxWidth < longContent / 2,
                $"Note's cap ({g.Note.MaxWidth}px) should be nowhere near the outlier's content " +
                $"({longContent}px) — a single long value must not set the column's width");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Table-rules, Rule 3's own degenerate case: exactly one row.
    /// Nearest-rank's formula (ceil(0.8×1)=1) already resolves this to the
    /// row's own width without a special branch, but the brief asks for the
    /// boundary handled EXPLICITLY, so this fact pins it by name rather than
    /// leaving it to be inferred from a fact (like almost every other one in
    /// this file, all built on a single row) whose own point is something
    /// else.</summary>
    [Fact]
    public void APercentileOfExactlyOneRowIsThatRowsOwnWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 40, new Row { Name = "a.pdf", Note = "a solitary note" });
        try
        {
            ShowAndSettle(g.Window);
            var content = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));
            // MaxWidth (the cap PercentileOf computes), not ActualWidth: a
            // cap inflated well past what "a solitary note" actually needs
            // would still leave ActualWidth sitting at the content's own
            // natural size (WPF's Auto column renders at min(desired,
            // MaxWidth), and desired never NEEDS the inflated cap here) —
            // measured directly reverting this fact's own guard: an
            // inflated cap of 435px against ~88px of content left
            // ActualWidth at 87px, an innocent-looking pass that proved
            // nothing about the percentile itself. MaxWidth is the number
            // this fact actually needs to pin.
            Assert.True(Math.Abs(g.Note.MaxWidth - content) <= 2,
                $"with exactly one realized row, the 80th percentile must be that row's own width: " +
                $"Note's cap is {g.Note.MaxWidth}px for {content}px of content");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Table-rules, Rule 3's other degenerate case: no rows at all.
    /// The percentile of an empty list (0, this fact's whole point) has
    /// nothing to contribute, so the column's cap comes entirely from its
    /// own header floor — the SAME number
    /// AColumnWithEmptyContentIsFlooredAtItsOwnHeaderWidthNotBelowIt already
    /// measures for a different reason (a column whose rows are all blank,
    /// not a column with no rows to begin with); this fact is what proves
    /// the percentile function itself never throws or misbehaves on an
    /// empty sample, which that one does not exercise.</summary>
    [Fact]
    public void APercentileOfNoRowsAtAllFloorsAtTheHeaderWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 40);   // no rows
        try
        {
            ShowAndSettle(g.Window);
            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Note);
            var headerText = FindDescendant<TextBlock>(header)!;
            var expectedFloor = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;
            Assert.True(Math.Abs(g.Note.MaxWidth - expectedFloor) <= 2,
                $"with no rows at all the percentile is 0 and contributes nothing; Note's cap " +
                $"({g.Note.MaxWidth}px) should sit exactly at its own header floor ({expectedFloor}px)");
        }
        finally { g.Window.Close(); }
    });

    // ---- Table-rules Rule 5: an empty column matches its neighbour ------

    /// <summary>Table-rules, Rule 5: a column whose realized cells are ALL
    /// blank takes the width of the column immediately to its LEFT rather
    /// than collapsing to its own header. BuildPair's own visual order is
    /// Name (star) | Note (governed) | Extra (governed), so Extra's left
    /// neighbour is Note.
    ///
    /// Asserts ActualWidth, not MaxWidth (fix round 1, the CRITICAL this
    /// rule shipped inert with): MaxWidth is a ceiling, and an Auto column
    /// renders at min(desired, MaxWidth) — an empty column's own DESIRED
    /// width is its bare header, already under any cap this rule could ever
    /// raise, so a version of this fact that only checked the cap passed
    /// while nothing moved on screen. The exact trap this task's own
    /// revert-proof row 2b already caught once, for Rule 3's cap, before
    /// this same mistake was carried into Rule 5 by habit.</summary>
    [Fact]
    public void AnEmptyColumnMatchesTheWidthOfTheColumnToItsLeft() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = BuildPair(2000, 40, new PairRow { Name = "x", Note = Ms(50), Extra = "" });
        try
        {
            ShowAndSettle(g.Window);
            var noteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));

            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Extra);
            var headerText = FindDescendant<TextBlock>(header)!;
            var ownHeaderFloor = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;
            Assert.True(noteContent > ownHeaderFloor + 20,
                $"this fact needs Note ({noteContent}px) meaningfully wider than Extra's own header " +
                $"floor ({ownHeaderFloor}px) — otherwise matching the neighbour and merely flooring at " +
                "the header would look identical");

            // Compare the two RENDERED widths to each other, not to Note's
            // CONTENT width. The rule is "the empty column matches its
            // neighbour", and that holds in both of ColumnShares' branches:
            // when the wanted widths fit, both render at Note's content; when
            // they don't, both carry the same wanted width into the
            // proportional split and so take the same reduced share. Asserting
            // the content width instead silently required the fit branch — and
            // a CI runner whose screen is narrower than this 2000px window
            // clamps it, engaging the split and failing a correct
            // implementation (Extra rendered 468px beside a 629px content
            // measurement; it had borrowed correctly, both were just squeezed).
            Assert.True(g.Extra.ActualWidth > ownHeaderFloor + 20,
                $"Extra must not merely be sitting at its own {ownHeaderFloor}px header floor: " +
                $"{g.Extra.ActualWidth}px");
            Assert.True(Math.Abs(g.Extra.ActualWidth - g.Note.ActualWidth) <= 2,
                $"Extra is empty; its RENDERED width ({g.Extra.ActualWidth}px) should match its LEFT " +
                $"neighbour Note's own RENDERED width ({g.Note.ActualWidth}px), not collapse to its own " +
                $"{ownHeaderFloor}px header");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Table-rules, Rule 5's other branch: the FIRST column, if
    /// empty, matches the one to its RIGHT instead — BuildLeading's own
    /// shape (a governed column first, the star filler second) is what
    /// makes this branch reachable at all; every star-first builder above
    /// can only ever exercise the "matches its left neighbour" branch.
    /// ActualWidth, not MaxWidth — see the sibling fact above for why.</summary>
    [Fact]
    public void AnEmptyFirstColumnMatchesTheWidthOfTheColumnToItsRight() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        // 800px window and short content, deliberately: First's RIGHT
        // neighbour here is the STAR column, which expands to fill, so its
        // rendered width is NOT its content width and the two cannot be
        // compared to each other the way the two governed columns are in the
        // left-neighbour fact. That leaves the content-width assertion below,
        // which only holds in ColumnShares' fit branch — so the fixture has to
        // guarantee that branch on any screen. A 2000px window does not: a CI
        // runner narrower than the window clamps it, and First + the star's
        // own wanted width (2 x ~630px at Ms(50)) no longer fit, engaging the
        // proportional split and failing a correct implementation.
        var g = BuildLeading(800, 40, new LeadingRow { First = "", Name = Ms(15) });
        try
        {
            ShowAndSettle(g.Window);
            var nameContent = ContentWidthOf(CellText(g.DataGrid, g.Name, 0));

            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.First);
            var headerText = FindDescendant<TextBlock>(header)!;
            var ownHeaderFloor = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;
            Assert.True(nameContent > ownHeaderFloor + 20,
                $"this fact needs Name ({nameContent}px) meaningfully wider than First's own header " +
                $"floor ({ownHeaderFloor}px) — otherwise matching the neighbour and merely flooring at " +
                "the header would look identical");

            Assert.True(g.First.ActualWidth > ownHeaderFloor + 20,
                $"First must not merely be sitting at its own {ownHeaderFloor}px header floor: " +
                $"{g.First.ActualWidth}px");
            Assert.True(Math.Abs(g.First.ActualWidth - nameContent) <= 2,
                $"First is empty and is the FIRST column, so its RENDERED width should match its RIGHT " +
                $"neighbour Name's own content width ({nameContent}px), not its own {ownHeaderFloor}px " +
                $"header: got {g.First.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Table-rules, Rule 5's own trap (fix round 1): AutofitCaps'
    /// own <c>floors</c> reads each governed column's MinWidth, and this
    /// rule now WRITES a blank column's MinWidth to force its adopted width
    /// to actually render (see the sibling fact above). Reading that write
    /// back live on the next pass would ratchet the floor up forever —
    /// proved here by borrowing a WIDE width, then narrowing the SAME
    /// neighbour and confirming Extra tracks it back down rather than
    /// staying pinned at the width it once borrowed.</summary>
    [Fact]
    public void AnEmptyColumnDoesNotRatchetWhenItsBorrowedFromNeighbourShrinks() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = BuildPair(2000, 40, new PairRow { Name = "x", Note = Ms(50), Extra = "" });
        try
        {
            ShowAndSettle(g.Window);
            var wideNoteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));
            var wideExtra = g.Extra.ActualWidth;
            Assert.True(Math.Abs(wideExtra - g.Note.ActualWidth) <= 2,
                $"precondition: Extra should adopt Note's own rendered {g.Note.ActualWidth}px while " +
                $"Note is wide — got {wideExtra}px");

            // Replace the row (PairRow's properties are init-only) with a
            // MUCH shorter Note, Extra still blank.
            g.Rows.Clear();
            g.Rows.Add(new PairRow { Name = "x", Note = Ms(5), Extra = "" });
            Settle(g.Window);

            var narrowNoteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0));
            Assert.True(narrowNoteContent < wideNoteContent - 20,
                $"this fact needs the new Note ({narrowNoteContent}px) meaningfully narrower than the " +
                $"old one ({wideNoteContent}px), or a ratchet and a correct shrink would look identical");

            Assert.True(g.Extra.ActualWidth < wideExtra - 20,
                $"Extra should have SHRUNK from the {wideExtra}px it borrowed while Note was wide: got " +
                $"{g.Extra.ActualWidth}px — a ratchet, if MinWidth's own floor fed back into itself");
            Assert.True(Math.Abs(g.Extra.ActualWidth - g.Note.ActualWidth) <= 2,
                $"Extra should track Note's CURRENT (narrower) rendered width, {g.Note.ActualWidth}px: " +
                $"got {g.Extra.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Test gap 7 (churn audit): two ADJACENT blank columns.
    /// ApplyEmptyColumnNeighbourRule reads every neighbour width from a
    /// snapshot taken before any column's own substitution runs, specifically
    /// so two blank columns beside each other cannot chain — Extra's own
    /// left neighbour, Note, is ALSO blank here, so Extra must fall back to
    /// its own header floor rather than inheriting whatever Note borrowed
    /// from Name.</summary>
    [Fact]
    public void TwoAdjacentBlankColumnsDoNotChainThroughEachOther() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = BuildPair(2000, 40, new PairRow { Name = Ms(30), Note = "", Extra = "" });
        try
        {
            ShowAndSettle(g.Window);

            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Extra);
            var headerText = FindDescendant<TextBlock>(header)!;
            var extraOwnHeaderFloor = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;

            Assert.True(g.Note.ActualWidth > extraOwnHeaderFloor + 20,
                $"this fact needs Note's own adopted width ({g.Note.ActualWidth}px, borrowed from " +
                $"Name) meaningfully wider than Extra's own header floor ({extraOwnHeaderFloor}px), or " +
                "a wrongly-chained Extra would be indistinguishable from a correctly-floored one");

            Assert.True(Math.Abs(g.Extra.ActualWidth - extraOwnHeaderFloor) <= 2,
                $"Extra's own left neighbour, Note, is ALSO blank — Extra must fall back to its own " +
                $"{extraOwnHeaderFloor}px header floor, not chain through to Note's borrowed width " +
                $"({g.Note.ActualWidth}px): got {g.Extra.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    /// <summary>Table-rules, Rule 5's own boundary: a column with SOME
    /// blank cells and some filled ones is not empty, and is left to Rule
    /// 3's percentile exactly as if Rule 5 did not exist. Three rows, two
    /// blank and one filled: nearest-rank on three sorted widths ([0, 0,
    /// filled]) picks rank ceil(0.8×3)=3 — the filled row's OWN width, the
    /// maximum of the three — never Name's width, which is what Rule 5
    /// would have produced had it wrongly treated this column as empty.</summary>
    [Fact]
    public void AColumnWithSomeBlankAndSomeFilledCellsIsNotEmptyAndFollowsThePercentileNormally() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(2000, 40,
            new Row { Name = Ms(3), Note = "" },
            new Row { Name = Ms(3), Note = "" },
            new Row { Name = Ms(3), Note = Ms(50) });
        try
        {
            ShowAndSettle(g.Window);
            var filledContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 2));
            var nameContent = ContentWidthOf(CellText(g.DataGrid, g.Name, 0));
            Assert.True(Math.Abs(filledContent - nameContent) > 20,
                $"this fact needs Note's own filled content ({filledContent}px) and Name's content " +
                $"({nameContent}px) to differ meaningfully, or Rule 3's answer and Rule 5's would be " +
                "indistinguishable");

            Assert.True(Math.Abs(g.Note.ActualWidth - filledContent) <= 2,
                $"two of Note's three rows are blank but one is filled — not empty, so Rule 5 must " +
                $"not apply: Note's RENDERED width ({g.Note.ActualWidth}px) should be its OWN " +
                $"percentile ({filledContent}px, the maximum of three), not Name's width ({nameContent}px)");
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
