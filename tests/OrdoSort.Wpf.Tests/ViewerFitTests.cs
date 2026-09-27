using System.Windows;
using OrdoSort.Wpf.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Sizing the viewer pane to the document when a session starts.
///
/// The arithmetic lives in <see cref="FitMath"/> and is tested here directly;
/// the Processing window only measures itself, calls it, and assigns the result,
/// which is the part no unit test can reach. The view model's half — that the
/// measurement happens once, at Start, and stays quiet when there is nothing
/// to measure — is tested through the real shell.</summary>
public class ViewerFitTests
{
    // ------------------------------------------------------------- the math

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
public class MonitorWorkAreaTests : UiTest
{
    private readonly HighlightContrastFixture _fx;

    public MonitorWorkAreaTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

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
                PumpRender();

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
                PumpRender();

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
