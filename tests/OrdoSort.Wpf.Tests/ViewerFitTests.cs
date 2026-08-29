using System.Windows;
using OrdoSort.Wpf.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Sizing the viewer pane to the document when a session starts.
///
/// The arithmetic lives in <see cref="FitMath"/> and is tested here directly;
/// MainWindow only measures its own pane, calls it, and assigns the result,
/// which is the part no unit test can reach. The view model's half — that the
/// measurement happens once, at Start, and stays quiet when there is nothing
/// to measure — is tested through the real shell.</summary>
public class ViewerFitTests
{
    // ------------------------------------------------------------- the math

    /// <summary>The pane's document area is its rectangle minus Edge's own
    /// furniture, so a 900x800 pane showing a page of aspect a is fitted when
    /// 900 - ScrollbarDip == (800 - ToolbarDip) * a.</summary>
    private static double PaneWidthFitting(double paneHeight, double aspect) =>
        (paneHeight - PanMath.ToolbarDip) * aspect + PanMath.ScrollbarDip;

    [Fact]
    public void APaneThatAlreadyFitsIsLeftExactlyWhereItIs()
    {
        const double paneHeight = 800, aspect = 612d / 792d;
        var paneWidth = PaneWidthFitting(paneHeight, aspect);

        var width = FitMath.WindowWidthFor(1280, paneWidth, paneHeight, aspect, 900, 2560);

        Assert.Equal(1280, width, 6);
    }

    [Fact]
    public void ALandscapePageWidensTheWindowByWhatThePaneIsShort()
    {
        const double paneHeight = 800, aspect = 792d / 612d;
        var wanted = PaneWidthFitting(paneHeight, aspect);

        var width = FitMath.WindowWidthFor(1280, 700, paneHeight, aspect, 900, 2560);

        Assert.Equal(1280 + (wanted - 700), width, 6);
        Assert.True(width > 1280, "a landscape page in a too-narrow pane has to grow the window");
    }

    [Fact]
    public void APortraitPageNarrowsAPaneThatWasTooWide()
    {
        var width = FitMath.WindowWidthFor(1900, 1400, 800, 612d / 792d, 900, 2560);

        Assert.True(width < 1900, "a portrait page in a very wide pane has to shrink the window");
        Assert.True(width >= 900, "and never below the window's own minimum");
    }

    [Fact]
    public void TheWindowNeverShrinksBelowItsMinimum() =>
        Assert.Equal(900, FitMath.WindowWidthFor(1000, 600, 800, 0.05, 900, 2560), 6);

    [Fact]
    public void TheWindowNeverGrowsPastTheWorkArea() =>
        Assert.Equal(1600, FitMath.WindowWidthFor(1280, 700, 800, 20, 900, 1600), 6);

    /// <summary>A screen narrower than the window's own minimum: the bounds
    /// cross, which is what Math.Clamp throws on. The minimum wins.</summary>
    [Fact]
    public void AScreenNarrowerThanTheMinimumDoesNotThrow() =>
        Assert.Equal(900, FitMath.WindowWidthFor(900, 500, 800, 1.3, 900, 800), 6);

    [Theory]
    [InlineData(0)]              // no aspect
    [InlineData(-1.5)]           // nonsense aspect
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableAspectLeavesTheWindowAlone(double aspect) =>
        Assert.Equal(1280, FitMath.WindowWidthFor(1280, 700, 800, aspect, 900, 2560), 6);

    [Fact]
    public void AnUnmeasuredPaneLeavesTheWindowAlone()
    {
        Assert.Equal(1280, FitMath.WindowWidthFor(1280, 0, 800, 1.3, 900, 2560), 6);
        Assert.Equal(1280, FitMath.WindowWidthFor(1280, 700, 0, 1.3, 900, 2560), 6);
    }

    /// <summary>A pane no taller than Edge's toolbar has no document area at
    /// all; dividing that up would produce a negative width.</summary>
    [Fact]
    public void APaneShorterThanTheToolbarLeavesTheWindowAlone() =>
        Assert.Equal(1280, FitMath.WindowWidthFor(1280, 700, PanMath.ToolbarDip, 1.3, 900, 2560), 6);

    // ------------------------------------------------------ staying on screen

    [Fact]
    public void AWindowThatStillFitsIsNotMoved() =>
        Assert.Equal(100, FitMath.LeftFor(100, 1200, new Rect(0, 0, 1920, 1080)), 6);

    [Fact]
    public void AWindowThatWouldHangOffTheRightEdgeIsPulledBack() =>
        Assert.Equal(720, FitMath.LeftFor(1000, 1200, new Rect(0, 0, 1920, 1080)), 6);

    [Fact]
    public void AWindowWiderThanTheScreenIsPinnedToTheLeftEdge() =>
        Assert.Equal(0, FitMath.LeftFor(400, 2400, new Rect(0, 0, 1920, 1080)), 6);

    /// <summary>A second monitor: the work area starts at 1920 and ends at
    /// 3840, so "back inside" means 2640, not 0.</summary>
    [Fact]
    public void TheWorkAreaMayNotStartAtZero() =>
        Assert.Equal(2640, FitMath.LeftFor(3000, 1200, new Rect(1920, 0, 1920, 1080)), 6);

    // ------------------------------------------------------- the view model

    private static string WritePdf(string path, double widthPt, double heightPt)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);
        doc.Save(path);
        return path;
    }

    [Fact]
    public void StartingASessionReportsTheFirstDocumentsShape()
    {
        using var fx = new ShellFixture();
        WritePdf(Path.Combine(fx.Inbox, "20240115--111111.pdf"), 792, 612);   // landscape
        var reported = new List<double>();
        fx.Shell.FitViewerToPage += reported.Add;

        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.Single(reported);
        Assert.Equal(792d / 612d, reported[0], 4);
    }

    /// <summary>Once per session, not once per document — the window must not
    /// move under someone's hands while they are filing.</summary>
    [Fact]
    public async Task FilingTheNextDocumentReportsNothing()
    {
        using var fx = new ShellFixture();
        WritePdf(Path.Combine(fx.Inbox, "20240115--111111.pdf"), 612, 792);
        WritePdf(Path.Combine(fx.Inbox, "20240116--222222.pdf"), 792, 612);   // a different shape
        var reported = new List<double>();
        fx.Shell.FitViewerToPage += reported.Add;

        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.Single(reported);

        await fx.Shell.OnRouteAsync(0);
        Assert.Single(reported);
    }

    [Fact]
    public void AnUnreadableDocumentReportsNothing()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");   // the fixture writes "pdf" as text
        var reported = new List<double>();
        fx.Shell.FitViewerToPage += reported.Add;

        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.Empty(reported);
    }

    [Fact]
    public void AnEmptyInboxReportsNothing()
    {
        using var fx = new ShellFixture();
        var reported = new List<double>();
        fx.Shell.FitViewerToPage += reported.Add;

        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.Empty(reported);
    }
}

/// <summary>Which screen the fit measures itself against.
/// <see cref="FitMath.LeftFor"/> above is told a work area and does the right
/// thing with whichever one it is handed; this pins the half that decides
/// WHICH — the trap being that <see cref="SystemParameters.WorkArea"/> is
/// always the primary monitor's, so a window on a secondary one got dragged
/// back to the primary at every session start.
///
/// Needs a real HWND (MonitorFromWindow takes one), so it runs on the shared
/// STA fixture and shows its windows off-screen, the same shape the other
/// window suites use.</summary>
[Collection(HighlightContrastTests.Name)]
public class MonitorWorkAreaTests
{
    private readonly HighlightContrastFixture _fx;

    public MonitorWorkAreaTests(HighlightContrastFixture fx) => _fx = fx;

    private static Window OffScreenWindow(double left, double top) => new()
    {
        Left = left,
        Top = top,
        Width = 400,
        Height = 300,
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    /// <summary>Far off every screen: MONITOR_DEFAULTTONEAREST still resolves
    /// a monitor, so the answer is a usable rectangle rather than an empty
    /// one a caller would divide by.</summary>
    [Fact]
    public void AWindowPlacedOffEveryScreenStillGetsAUsableWorkArea() =>
        _fx.Invoke(() =>
        {
            var window = OffScreenWindow(-20000, 0);
            try
            {
                window.Show();
                OverflowProbe.PumpRender();

                var work = MonitorWorkArea.For(window);

                Assert.True(work.Width > 0, $"width was {work.Width}");
                Assert.True(work.Height > 0, $"height was {work.Height}");
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The primary monitor is the case where the old code was right,
    /// and it has to stay right: a window sitting on the primary must resolve
    /// to exactly what SystemParameters.WorkArea reports, DPI conversion and
    /// all.</summary>
    [Fact]
    public void AWindowOnThePrimaryMonitorGetsThePrimaryWorkArea() =>
        _fx.Invoke(() =>
        {
            var primary = SystemParameters.WorkArea;
            var window = OffScreenWindow(primary.Left + 10, primary.Top + 10);
            try
            {
                window.Show();
                OverflowProbe.PumpRender();

                Assert.Equal(primary, MonitorWorkArea.For(window));
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>No HWND yet — MonitorFromWindow has nothing to answer about,
    /// so the caller gets the behaviour it had before this class existed
    /// rather than an empty rectangle or a throw.</summary>
    [Fact]
    public void AWindowThatWasNeverShownFallsBackToThePrimaryWorkArea() =>
        _fx.Invoke(() => Assert.Equal(SystemParameters.WorkArea, MonitorWorkArea.For(new Window())));

    // ---- the arithmetic ------------------------------------------------
    //
    // The three facts above cannot tell "this window's monitor" apart from
    // "the primary" on a one-monitor machine — every one of them still
    // passes if For() is gutted back to SystemParameters.WorkArea. These do
    // not: they are the multi-monitor and DPI cases stated as numbers, so
    // they hold on any machine and fail the moment the conversion drifts.

    /// <summary>The whole point of the fix: a monitor to the RIGHT of the
    /// primary keeps its own origin. Folding this onto Left 0 is exactly the
    /// defect the review found — a window at Left 2200 measured against a
    /// work area that stops at 1920 gets dragged onto the primary.</summary>
    [Fact]
    public void AMonitorRightOfThePrimaryKeepsItsOwnOrigin() =>
        Assert.Equal(new Rect(1920, 0, 1920, 1040),
            MonitorWorkArea.ToDips(1920, 0, 3840, 1040, 1, 1));

    /// <summary>And one to the LEFT, whose device coordinates are negative.</summary>
    [Fact]
    public void AMonitorLeftOfThePrimaryKeepsItsNegativeOrigin() =>
        Assert.Equal(new Rect(-1920, 0, 1920, 1040),
            MonitorWorkArea.ToDips(-1920, 0, 0, 1040, 1, 1));

    /// <summary>Device pixels are not DIPs anywhere but 100%: at 150% a
    /// 3840x2100 work area starting at device x=1920 is 2560x1400 DIPs
    /// starting at 1280.</summary>
    [Fact]
    public void AScaledMonitorIsConvertedToDips() =>
        Assert.Equal(new Rect(1280, 0, 2560, 1400),
            MonitorWorkArea.ToDips(1920, 0, 5760, 2100, 1.5, 1.5));

    /// <summary>Numbers no rectangle can be built from say so, rather than
    /// throwing out of Rect's constructor — that is what lets
    /// <see cref="MonitorWorkArea.For"/> promise a usable answer.</summary>
    [Theory]
    [InlineData(0, 0, 1920, 1040, 0, 1)]      // no DPI scale
    [InlineData(0, 0, 1920, 1040, 1, -1)]     // nonsense DPI scale
    [InlineData(0, 0, 0, 1040, 1, 1)]         // empty width
    [InlineData(0, 0, 1920, 0, 1, 1)]         // empty height
    [InlineData(1920, 0, 0, 1040, 1, 1)]      // inverted
    public void NumbersThatCannotMakeARectangleSaySo(
        int left, int top, int right, int bottom, double scaleX, double scaleY) =>
        Assert.Null(MonitorWorkArea.ToDips(left, top, right, bottom, scaleX, scaleY));
}
