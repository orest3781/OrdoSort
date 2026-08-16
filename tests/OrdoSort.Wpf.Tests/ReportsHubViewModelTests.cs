using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 4 (Reports hub view models — the review's Finding 1: no
/// tests existed for these three). Two ways a snapshot reaches a view model
/// here, matching what each class actually needs to prove:
///
/// <see cref="ReportsHubCoordinatorTests"/> exercises the coordinator's own job —
/// the folder walk, the debounced/off-thread reload, SetIgnored's
/// cheap-recompute path, and navigation — so it drives real, synthetic PECF
/// .xlsx fixtures through <see cref="ReportsViewModel.Reload"/> exactly the
/// way <c>TurnaroundViewModelTests</c>/<c>ProductionViewModelTests</c> drive
/// their own loads: InlineWorkScheduler + probeDelayMs: 0 + WaitFor polling
/// (the underlying System.Threading.Timer still fires on a threadpool
/// thread even at 0ms — see those classes' own doc comments).
///
/// <see cref="ReportsHubTurnaroundPageTests"/> and
/// <see cref="ReportsHubSourcesPageTests"/> are pure display-shape tests: a
/// <see cref="ReportsViewModel.Snapshot"/> is a plain, disk-free value (a
/// computed Summary + a LoadReport), so most of what those two page view
/// models do is exercised by building one directly (<see cref="Fx.Snapshot"/>,
/// the same computed-not-hand-assembled discipline
/// <c>TurnaroundExportTests</c> uses for its own Summary fixture) and
/// calling the page view model's own internal Apply — no probe, no disk, no
/// polling. <c>InternalsVisibleTo</c> (OrdoSort.Wpf.csproj) is what makes
/// <c>ReportsViewModel.Snapshot</c> and both Apply methods reachable here.
/// The one SourcesPageViewModel behavior that genuinely needs a live
/// coordinator (toggling IsIncluded must round-trip through
/// ReportsViewModel.SetIgnored's cached-table recompute, which needs
/// ReportsViewModel.Current already populated — only a real Reload sets
/// that, since ReportsViewModel.Apply itself is private) goes through the
/// same real-fixture path as ReportsHubCoordinatorTests.</summary>
internal static class Fx
{
    internal static SweptTable.Row Row(string fileName, string sourceType, string sourceFile,
        string pagecount = "10", string destination = "MIX") =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FileName"] = fileName, ["SourceType"] = sourceType,
            ["Pagecount"] = pagecount, ["Destination"] = destination,
        }, sourceFile);

    /// <summary>Builds a snapshot the same way ReportsViewModel.Build does —
    /// through the real Compute/Discover engine, never hand-assembled — so a
    /// test fixture can never drift from what the engine would actually
    /// produce.</summary>
    internal static ReportsViewModel.Snapshot Snapshot(
        IReadOnlyList<SweptTable.Row> rows, IReadOnlyList<string>? ignoredValues = null,
        int filesFound = 1, IReadOnlyList<string>? skipped = null,
        DateOnly? firstUpload = null, DateOnly? lastUpload = null)
    {
        var table = new SweptTable.Table(
            new[] { "FileName", "SourceType", "Pagecount", "Destination" },
            rows, filesFound, Array.Empty<string>());
        var ignore = new IgnoreList(ignoredValues ?? Array.Empty<string>());
        var summary = TurnaroundSummary.Compute(table, ignore);
        var discovered = ignore.Discover(rows.Select(r => r.Cells["SourceType"]));
        var report = new UploadReportFeed.LoadReport(filesFound, skipped ?? Array.Empty<string>(),
            firstUpload, lastUpload, rows.Count);
        var feed = new UploadReportFeed.Result(table, report);
        return new ReportsViewModel.Snapshot(feed, summary, discovered);
    }

    /// <summary>A one-sheet workbook of inline strings — the same technique
    /// UploadReportFeedTests (Core.Tests) uses, copied here because it lives
    /// in a different assembly. Cell text must not contain &amp;, &lt; or
    /// &gt; — every fixture below is synthetic (spec: PHI stance).</summary>
    internal static string WriteXlsx(string dir, string relativePath, string[][] rows)
    {
        var path = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder(
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
                sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{rows[r][c]}</t></is></c>");
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), Encoding.UTF8);
        w.Write(sb.ToString());
        return path;
    }
}

// ============================================================================
// ReportsViewModel — the coordinator's own job: folder walk, reload,
// SetIgnored, navigation.
// ============================================================================
public class ReportsHubCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoreports_" + Guid.NewGuid());

    public ReportsHubCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Same shape as TurnaroundViewModelTests.WaitFor.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    private static readonly string[] PecfHeader = { "FileName", "SourceType" };

    private static string[][] Rows(params (string FileName, string SourceType)[] rows) =>
        new[] { PecfHeader }.Concat(rows.Select(r => new[] { r.FileName, r.SourceType })).ToArray();

    [Fact]
    public void EmptyFolderFastPathAppliesAnEmptySnapshotSynchronously()
    {
        // Default Config().ReportsUploadFolder is "" — the constructor's own
        // Reload(immediate: true) must resolve this on the calling thread,
        // no probe/timer/scheduler involved, so the assertion right after
        // construction — no WaitFor — is itself part of what's being proven.
        var vm = new ReportsViewModel(new Config(), new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

        Assert.Equal("0 files · 0 rows", vm.FooterText);
        Assert.False(vm.Turnaround.HasData);
        Assert.Equal("No folder chosen — browse to your upload reports", vm.Sources.StatusText);
    }

    /// <summary>The fast path's whole point (ReportsViewModel.Reload's own
    /// doc comment: "cancelling any in-flight probe ... so a slow stale load
    /// can't repopulate the hub afterwards"). A real load is left in flight
    /// on a scheduler the test fully controls (ControlledWorkScheduler,
    /// dispatched-but-not-executed — the same double DebouncedProbeTests
    /// uses for exactly this shape of race), the folder is then cleared, and
    /// only afterward is the stale load allowed to finish. It must never
    /// overwrite the empty snapshot the clear already applied.</summary>
    [Fact]
    public void ClearingTheFolderCancelsAnInFlightLoadSoItCannotOverwriteTheEmptySnapshot()
    {
        Fx.WriteXlsx(_dir, "20260706-0900-PECF Report.xlsx", Rows(("20260706-A.pdf", "Email")));
        var cfg = new Config { ReportsUploadFolder = _dir };
        var scheduler = new ControlledWorkScheduler();
        var vm = new ReportsViewModel(cfg, new FakeDialogs(), null, scheduler, uiContext: null, probeDelayMs: 0);

        WaitFor(() => scheduler.Queued == 1, "the constructor's own load should reach the scheduler, not run yet");

        vm.Folder = "";   // user clears the folder while that load is still in flight

        Assert.Equal("0 files · 0 rows", vm.FooterText);
        Assert.False(vm.Turnaround.HasData);

        scheduler.ReleaseAll();   // the stale load finally "finishes"

        Assert.Equal("0 files · 0 rows", vm.FooterText);
        Assert.False(vm.Turnaround.HasData);
    }

    [Fact]
    public void ReloadOverARealFolderOfFixturesPopulatesTheSnapshot()
    {
        Fx.WriteXlsx(_dir, "20260706-0900-PECF Report.xlsx", Rows(
            ("20260706-A.pdf", "Email"),
            ("20260701-B.pdf", "FAX")));
        var cfg = new Config { ReportsUploadFolder = _dir };
        var vm = new ReportsViewModel(cfg, new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

        WaitFor(() => vm.Turnaround.HasData, "the real fixture should load and populate the snapshot");

        Assert.Equal("1 files · 2 rows", vm.FooterText);
        Assert.StartsWith("1 files · 2 rows · 2026-07-06 to 2026-07-06", vm.Sources.StatusText);
        Assert.Single(vm.Turnaround.MonthRows);
        Assert.Equal("Jul", vm.Turnaround.MonthRows[0].Month);
    }

    [Fact]
    public void SetIgnoredPersistsRecomputesAndExcludesTheValueWhileItsCountSurvivesInIgnored()
    {
        Fx.WriteXlsx(_dir, "20260706-0900-PECF Report.xlsx", Rows(
            ("20260706-A.pdf", "Email"),    // measurable, same day
            ("20260706-B.pdf", "ECAA")));   // same day too, until ignored
        var cfg = new Config { ReportsUploadFolder = _dir };
        var vm = new ReportsViewModel(cfg, new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);
        WaitFor(() => vm.Current?.Summary.Overall.Total == 2, "both rows should be measurable before ignoring");

        vm.SetIgnored("ECAA", true);

        WaitFor(() => vm.Current?.Summary.Overall.Total == 1, "the recompute should exclude the ignored row");
        Assert.Contains("ECAA", cfg.TatIgnoredSources);
        var ignored = Assert.Single(vm.Current!.Summary.Ignored);
        Assert.Equal("ECAA", ignored.Value);
        Assert.Equal(1, ignored.Count);   // the count survives even though the row is excluded
        Assert.Equal(1, vm.Current!.Summary.Overall.SameDay);   // only "Email" remains measurable
    }

    [Fact]
    public void ShowPageAndSelectedPageIndexSwitchCurrentPageBetweenTurnaroundAndSources()
    {
        var vm = new ReportsViewModel(new Config(), new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);
        Assert.Same(vm.Turnaround, vm.CurrentPage);   // default: Turn-around
        Assert.Equal(0, vm.SelectedPageIndex);

        vm.ShowPage(1);

        Assert.Same(vm.Sources, vm.CurrentPage);
        Assert.Equal(1, vm.SelectedPageIndex);

        vm.SelectedPageIndex = 0;

        Assert.Same(vm.Turnaround, vm.CurrentPage);
    }

    [Fact]
    public void DisposeIsSafeEvenWithAProbeInFlight()
    {
        Fx.WriteXlsx(_dir, "20260706-0900-PECF Report.xlsx", Rows(("20260706-A.pdf", "Email")));
        var cfg = new Config { ReportsUploadFolder = _dir };
        var vm = new ReportsViewModel(cfg, new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 300);   // real delay: probe still armed

        var ex = Record.Exception(() => vm.Dispose());

        Assert.Null(ex);
    }
}

// ============================================================================
// TurnaroundPageViewModel — pure display shape over a coordinator's Apply.
// ============================================================================
public class ReportsHubTurnaroundPageTests
{
    private const string R1 = "20260706-0900-PECF Report.xlsx";   // upload 2026-07-06, a Monday

    private static ReportsViewModel MakeVm(FakeDialogs? dialogs = null) =>
        new(new Config(), dialogs ?? new FakeDialogs(), null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    /// <summary>Same fixture TurnaroundExportTests pins: SameDay, 1-day,
    /// 2-day, 3+-day, one ignored (ECAA), one future-dated — 0-1% == 50.0,
    /// one month, one ISO week.</summary>
    private static ReportsViewModel.Snapshot FixtureA() => Fx.Snapshot(new[]
    {
        Fx.Row("20260706-A.pdf", "Email", R1),   // Same day
        Fx.Row("20260703-B.pdf", "Email", R1),   // Fri -> Mon = 1
        Fx.Row("20260702-C.pdf", "FAX", R1),     // Thu -> Mon = 2
        Fx.Row("20260701-D.pdf", "Paper", R1),   // Wed -> Mon = 3+
        Fx.Row("07022026 E.pdf", "ECAA", R1),    // ignored
        Fx.Row("20260707-F.pdf", "Email", R1),   // future-dated
    }, ignoredValues: new[] { "ECAA" },
       firstUpload: new DateOnly(2026, 7, 6), lastUpload: new DateOnly(2026, 7, 6));

    [Fact]
    public void HeroBucketTilesAndContextTextRenderInvariantlyFromAKnownSnapshot()
    {
        var vm = MakeVm();

        vm.Turnaround.Apply(FixtureA());

        Assert.True(vm.Turnaround.HasData);
        Assert.Equal("50.0%", vm.Turnaround.HeroPercentText);
        Assert.Equal("1", vm.Turnaround.SameDayText);
        Assert.Equal("1", vm.Turnaround.OneDayText);
        Assert.Equal("1", vm.Turnaround.TwoDaysText);
        Assert.Equal("1", vm.Turnaround.ThreePlusText);
        Assert.Equal("Upload reports · Jul 6 – Jul 6, 2026 · 1 files · 6 rows", vm.Turnaround.ContextText);
    }

    [Fact]
    public void EmptySnapshotShowsDashHeroButZeroCountsAndNoDataContext()
    {
        var vm = MakeVm();

        vm.Turnaround.Apply(Fx.Snapshot(Array.Empty<SweptTable.Row>()));

        Assert.False(vm.Turnaround.HasData);
        Assert.Equal("—", vm.Turnaround.HeroPercentText);         // no data: dash, not "0.0%"
        Assert.Equal("0", vm.Turnaround.SameDayText);              // but the raw counts are plain zeros
        Assert.Equal("Upload reports · no data loaded — set the folder on the Sources page",
            vm.Turnaround.ContextText);
    }

    [Fact]
    public void DeltaChipIsAbsentWithFewerThanTwoMonths()
    {
        var vm = MakeVm();

        vm.Turnaround.Apply(FixtureA());   // one month only

        Assert.False(vm.Turnaround.HasDelta);
        Assert.Equal("", vm.Turnaround.DeltaChipText);
    }

    [Fact]
    public void DeltaChipShowsAnUpArrowAndPlusSignWhenTheZeroToOneShareImproves()
    {
        // June: 1 same-day + 1 three-plus -> 50%. July: 1 same-day + 1 one-day -> 100%.
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260601-A.pdf", "Email", "20260601-0900-PECF Report.xlsx"),   // same day
            Fx.Row("20260518-B.pdf", "Email", "20260601-0900-PECF Report.xlsx"),   // 10 business days -> 3+
            Fx.Row("20260706-C.pdf", "Email", R1),                                  // same day
            Fx.Row("20260703-D.pdf", "Email", R1),                                  // Fri -> Mon = 1
        });
        var vm = MakeVm();

        vm.Turnaround.Apply(snapshot);

        Assert.True(vm.Turnaround.HasDelta);
        Assert.StartsWith("▲ +50.0 pt vs Jun", vm.Turnaround.DeltaChipText);
        Assert.Equal(2, vm.Turnaround.MonthRows.Count);
        Assert.Equal("Jun", vm.Turnaround.MonthRows[0].Month);
        Assert.Equal("50.0%", vm.Turnaround.MonthRows[0].ZeroToOne);
        Assert.Equal("Jul", vm.Turnaround.MonthRows[1].Month);
        Assert.Equal("100.0%", vm.Turnaround.MonthRows[1].ZeroToOne);
    }

    [Fact]
    public void DeltaChipShowsADownArrowAndMinusSignWhenTheZeroToOneShareWorsens()
    {
        // June: both same-day -> 100%. July: 1 same-day + 1 three-plus -> 50%.
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260601-A.pdf", "Email", "20260601-0900-PECF Report.xlsx"),   // same day
            Fx.Row("20260601-B.pdf2", "Email", "20260601-0900-PECF Report.xlsx"),  // same day
            Fx.Row("20260706-C.pdf", "Email", R1),                                  // same day
            Fx.Row("20260622-D.pdf", "Email", R1),                                  // 10 business days -> 3+
        });
        var vm = MakeVm();

        vm.Turnaround.Apply(snapshot);

        Assert.True(vm.Turnaround.HasDelta);
        Assert.StartsWith("▼ −50.0 pt vs Jun", vm.Turnaround.DeltaChipText);
    }

    [Fact]
    public void SparkBarsScaleTheWorstWeekToPoint2AndTheBestToPoint1()
    {
        // ISO week 1 (upload 2026-06-29): 1 same-day + 1 three-plus -> 50%.
        // ISO week 2 (upload 2026-07-06): 2 same-day -> 100%.
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260629-A.pdf", "Email", "20260629-0900-PECF Report.xlsx"),   // same day
            Fx.Row("20260615-B.pdf", "Email", "20260629-0900-PECF Report.xlsx"),   // 10 business days -> 3+
            Fx.Row("20260706-C.pdf", "Email", R1),                                  // same day
            Fx.Row("20260706-D2.pdf", "Email", R1),                                 // same day
        });
        var vm = MakeVm();

        vm.Turnaround.Apply(snapshot);

        Assert.Equal(2, vm.Turnaround.SparkBars.Count);
        Assert.Equal(0.2, vm.Turnaround.SparkBars[0].HeightFraction, 3);
        Assert.Equal(1.0, vm.Turnaround.SparkBars[1].HeightFraction, 3);
    }

    [Fact]
    public void SparkBarsAreFullHeightInTheDegenerateFlatSeriesCase()
    {
        var vm = MakeVm();

        vm.Turnaround.Apply(FixtureA());   // exactly one ISO week

        var bar = Assert.Single(vm.Turnaround.SparkBars);
        Assert.Equal(1.0, bar.HeightFraction, 3);
    }

    [Fact]
    public void SetAsideChipsCarryTheRightCounts()
    {
        var vm = MakeVm();

        vm.Turnaround.Apply(FixtureA());

        Assert.Equal(4, vm.Turnaround.SetAsideChips.Count);
        Assert.Equal("0", vm.Turnaround.SetAsideChips[0].CountText);   // Duplicates
        Assert.Equal("1", vm.Turnaround.SetAsideChips[1].CountText);   // Future-dated
        Assert.Equal("0", vm.Turnaround.SetAsideChips[2].CountText);   // No date
        Assert.Equal("ECAA ignored", vm.Turnaround.SetAsideChips[3].Label);
        Assert.Equal("1", vm.Turnaround.SetAsideChips[3].CountText);
    }

    [Fact]
    public void SetAsideChipsLabelABlankIgnoredSourceTypeAsBlank()
    {
        var snapshot = Fx.Snapshot(new[] { Fx.Row("20260706-A.pdf", "", R1) },
            ignoredValues: new[] { "" });
        var vm = MakeVm();

        vm.Turnaround.Apply(snapshot);

        var chip = vm.Turnaround.SetAsideChips.Single(c => c.Key == TurnaroundPageViewModel.SourceIgnored);
        Assert.Equal("(blank) ignored", chip.Label);
        Assert.Equal("1", chip.CountText);
    }

    [Fact]
    public void InspectSetAsideJumpsToDetailTabFilteredToTheGivenSource()
    {
        var vm = MakeVm();
        vm.Turnaround.Apply(FixtureA());
        Assert.Equal(0, vm.Turnaround.SelectedTabIndex);

        vm.Turnaround.InspectSetAside(TurnaroundPageViewModel.SourceFutureDated);

        Assert.Equal(1, vm.Turnaround.SelectedTabIndex);
        Assert.Equal(TurnaroundPageViewModel.SourceFutureDated, vm.Turnaround.SelectedDetailSource);
        var row = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260707-F.pdf", row.FileName);
    }

    [Fact]
    public void SelectedDetailSourceSwitchesBetweenMeasurableDuplicatesAndIgnoredRowSets()
    {
        // A dedupes against its own re-listed duplicate; C is ignored.
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260706-A.pdf", "Email", R1),
            Fx.Row("20260706-A.pdf", "Email", R1),   // same FileName -> duplicate
            Fx.Row("20260706-C.pdf", "ECAA", R1),    // ignored
        }, ignoredValues: new[] { "ECAA" });
        var vm = MakeVm();
        vm.Turnaround.Apply(snapshot);

        Assert.Equal(TurnaroundPageViewModel.SourceMeasurable, vm.Turnaround.SelectedDetailSource);
        var measurable = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260706-A.pdf", measurable.FileName);
        Assert.Equal("2026-07-06", measurable.DocDate);
        Assert.Equal("1 rows · measurable documents", vm.Turnaround.DetailCountText);

        vm.Turnaround.SelectedDetailSource = TurnaroundPageViewModel.SourceDuplicates;
        var duplicate = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260706-A.pdf", duplicate.FileName);
        Assert.Equal("—", duplicate.DocDate);   // raw-row detail: no computed date, per FromRawRow

        vm.Turnaround.SelectedDetailSource = TurnaroundPageViewModel.SourceIgnored;
        var ignored = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260706-C.pdf", ignored.FileName);
    }

    [Fact]
    public void DetailFilterNarrowsRowsCaseInsensitivelyByFileNameOrSourceType()
    {
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260706-ALPHA.pdf", "Email", R1),
            Fx.Row("20260706-BETA.pdf", "FAX", R1),
        });
        var vm = MakeVm();
        vm.Turnaround.Apply(snapshot);
        Assert.Equal(2, vm.Turnaround.DetailRows.Count);

        vm.Turnaround.DetailFilter = "alpha";   // lowercase — matches the filename case-insensitively

        var byName = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260706-ALPHA.pdf", byName.FileName);

        vm.Turnaround.DetailFilter = "fax";   // matches SourceType case-insensitively

        var bySource = Assert.Single(vm.Turnaround.DetailRows);
        Assert.Equal("20260706-BETA.pdf", bySource.FileName);

        vm.Turnaround.DetailFilter = "";

        Assert.Equal(2, vm.Turnaround.DetailRows.Count);
    }

    [Fact]
    public void CopySummaryCommandRaisesCopyTextRequestedWithExactlyBuildCopyTextsOutput()
    {
        var vm = MakeVm();
        var snapshot = FixtureA();
        vm.Turnaround.Apply(snapshot);
        string? captured = null;
        vm.Turnaround.CopyTextRequested += t => captured = t;

        vm.Turnaround.CopySummaryCommand.Execute(null);

        var expected = TurnaroundExport.BuildCopyText(snapshot.Summary, snapshot.Feed.Report);
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void NoteCopiedAndNoteClipboardBusyRouteThroughTheDialogDouble()
    {
        var dialogs = new FakeDialogs();
        var vm = MakeVm(dialogs);

        vm.Turnaround.NoteCopied();
        var info = Assert.Single(dialogs.Infos);
        Assert.Contains("copied", info.Message, StringComparison.OrdinalIgnoreCase);

        vm.Turnaround.NoteClipboardBusy();
        var warn = Assert.Single(dialogs.Warnings);
        Assert.Contains("busy", warn.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// SourcesPageViewModel — the upload-feed card.
// ============================================================================
public class ReportsHubSourcesPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordosources_" + Guid.NewGuid());

    public ReportsHubSourcesPageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    private static ReportsViewModel MakeVm(Config? cfg = null, FakeDialogs? dialogs = null,
        Action? saveCfg = null, IWorkScheduler? scheduler = null) =>
        new(cfg ?? new Config(), dialogs ?? new FakeDialogs(), saveCfg,
            scheduler ?? new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    [Fact]
    public void StatusTextIsTheNoFolderMessageBeforeAnyFolderIsChosen()
    {
        var vm = MakeVm();

        Assert.Equal("No folder chosen — browse to your upload reports", vm.Sources.StatusText);
    }

    [Fact]
    public void StatusTextReportsFilesRowsAndSpanOnceAFolderIsSet()
    {
        // A folder is set but never actually walked — a ControlledWorkScheduler
        // never releases the real load — so Apply is driven directly with a
        // known snapshot, purely to exercise the "folder is set" branch of
        // StatusText without depending on disk state.
        var cfg = new Config { ReportsUploadFolder = @"S:\reports" };
        var vm = MakeVm(cfg, scheduler: new ControlledWorkScheduler());
        var snapshot = Fx.Snapshot(new[] { Fx.Row("20260706-A.pdf", "Email", "20260706-0900-PECF Report.xlsx") },
            firstUpload: new DateOnly(2026, 7, 6), lastUpload: new DateOnly(2026, 7, 6));

        vm.Sources.Apply(snapshot);

        Assert.Equal("1 files · 1 rows · 2026-07-06 to 2026-07-06", vm.Sources.StatusText);
    }

    [Fact]
    public void HasSkippedIsFalseWithNoSkippedFiles()
    {
        var vm = MakeVm();

        Assert.False(vm.Sources.HasSkipped);
        Assert.Equal("", vm.Sources.SkippedText);
    }

    [Fact]
    public void HasSkippedAndSkippedTextReflectAFeedWithACorruptFile()
    {
        var cfg = new Config { ReportsUploadFolder = @"S:\reports" };
        var vm = MakeVm(cfg, scheduler: new ControlledWorkScheduler());
        var snapshot = Fx.Snapshot(Array.Empty<SweptTable.Row>(),
            skipped: new[] { "20260701-0900-PECF Report.xlsx: corrupt zip" });

        vm.Sources.Apply(snapshot);

        Assert.True(vm.Sources.HasSkipped);
        Assert.Equal("1 skipped — 20260701-0900-PECF Report.xlsx: corrupt zip", vm.Sources.SkippedText);
    }

    [Fact]
    public void IgnoreEntriesReflectDiscoverCountsWithIsIncludedMirroringTheIgnoreState()
    {
        var cfg = new Config { ReportsUploadFolder = @"S:\reports" };
        var vm = MakeVm(cfg, scheduler: new ControlledWorkScheduler());
        const string sourceFile = "20260706-0900-PECF Report.xlsx";
        var snapshot = Fx.Snapshot(new[]
        {
            Fx.Row("20260706-A.pdf", "Email", sourceFile),
            Fx.Row("20260706-B.pdf", "Email", sourceFile),
            Fx.Row("20260706-C.pdf", "ECAA", sourceFile),
        }, ignoredValues: new[] { "ECAA" });

        vm.Sources.Apply(snapshot);

        Assert.Equal(2, vm.Sources.IgnoreEntries.Count);
        var email = vm.Sources.IgnoreEntries.Single(e => e.Value == "Email");
        Assert.Equal(2, email.Count);
        Assert.True(email.IsIncluded);
        var ecaa = vm.Sources.IgnoreEntries.Single(e => e.Value == "ECAA");
        Assert.Equal(1, ecaa.Count);
        Assert.False(ecaa.IsIncluded);
    }

    [Fact]
    public void TogglingIsIncludedRoundTripsThroughSetIgnoredAndPersistsToConfig()
    {
        var path = Path.Combine(_dir, "20260706-0900-PECF Report.xlsx");
        Fx.WriteXlsx(_dir, "20260706-0900-PECF Report.xlsx", new[]
        {
            new[] { "FileName", "SourceType" },
            new[] { "20260706-A.pdf", "Email" },
            new[] { "20260706-B.pdf", "ECAA" },
        });
        Assert.True(File.Exists(path));
        var cfg = new Config { ReportsUploadFolder = _dir };
        var saveCalls = 0;
        var vm = MakeVm(cfg, saveCfg: () => saveCalls++);
        WaitFor(() => vm.Sources.IgnoreEntries.Count == 2, "the real load should discover both SourceType values");

        var ecaa = vm.Sources.IgnoreEntries.Single(e => e.Value == "ECAA");
        Assert.True(ecaa.IsIncluded);   // nothing ignored yet

        ecaa.IsIncluded = false;        // unchecking the box = ignore it

        Assert.Contains("ECAA", cfg.TatIgnoredSources);
        Assert.True(saveCalls > 0);
        WaitFor(() => vm.Sources.IgnoreEntries.Single(e => e.Value == "ECAA").IsIncluded == false,
            "the recomputed snapshot should re-render the checklist with the box still unchecked");
    }
}
