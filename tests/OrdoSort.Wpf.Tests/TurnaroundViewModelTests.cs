using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 6 (Turn-around Time report window). Loading runs through the
/// same debounced, off-UI-thread DebouncedProbe&lt;SweptTable.Table&gt; shape
/// FilenameListViewModel uses for its own listing — see
/// FilenameListViewModelTests' own doc comment for why "eventually correct"
/// has to be polled for even with InlineWorkScheduler and probeDelayMs: 0
/// (the underlying System.Threading.Timer still fires on a threadpool
/// thread). Only the column-mapping/threshold setters — no probe involved —
/// are safe to assert immediately after driving them.</summary>
public class TurnaroundViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordotat_" + Guid.NewGuid());

    public TurnaroundViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Same shape as FilenameListViewModelTests.WaitFor.</summary>
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

    private static TurnaroundViewModel MakeVm(Config cfg, FakeDialogs dialogs, Action? saveCfg = null) =>
        new(cfg, dialogs, saveCfg, new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    private const string PecfHeaders = "SourceType,Controlid,FileName,Pagecount";

    [Fact]
    public void LoadingAFolderPopulatesHeadersDocumentsAndStatus()
    {
        Write("20250303-1144-PECF Report.csv",
            PecfHeaders + "\n" +
            "DRG,1,20250228-HELTON-EMILY-KYPT2024-11-63094.pdf,3\n" +
            "DRG,2,20250301-x.pdf,2\n");
        Write("20250304-0900-PECF Report.csv",
            PecfHeaders + "\n" +
            "COPR,3,20250302-y.pdf,1\n");
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Documents.Count == 3, "both fixtures' rows should load");
        Assert.Equal(new[] { "SourceType", "Controlid", "FileName", "Pagecount" }, vm.Headers);
        Assert.Equal("2 files · 3 rows · 0 without TAT", vm.Status);
    }

    [Fact]
    public void HeadersAutoGuessFilenameAndCategoryColumns()
    {
        Write("20250303-1144-PECF Report.csv", PecfHeaders + "\nDRG,1,20250228-a.pdf,1\n");
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.FilenameColumn == "FileName" && vm.CategoryColumn == "SourceType",
            "FilenameColumn/CategoryColumn should auto-guess from the headers");
    }

    [Fact]
    public void SavedMappingWinsOverAutoGuess()
    {
        Write("20250303-1144-PECF Report.csv", PecfHeaders + "\nDRG,1,20250228-a.pdf,1\n");
        var cfg = new Config();
        cfg.TatHeaders["filename"] = "Controlid";
        var vm = MakeVm(cfg, new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.FilenameColumn == "Controlid",
            "the saved mapping should win over the auto-guess");
    }

    [Fact]
    public void TatMathEndToEndAndThresholdChangeRecomputesIsOverThreshold()
    {
        Write("20250303-1144-PECF Report.csv",
            PecfHeaders + "\nDRG,1,20250228-HELTON-EMILY-KYPT2024-11-63094.pdf,3\n");
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Documents.Count == 1, "the one row should load");
        Assert.Equal("3", vm.Documents[0].TatDaysText);
        Assert.False(vm.Documents[0].IsOverThreshold);   // default threshold is 5, TAT is 3

        vm.ThresholdDays = 2;

        Assert.True(vm.Documents[0].IsOverThreshold);   // TAT 3 > threshold 2
    }

    [Fact]
    public void ChangingFilenameColumnPersistsToConfigAndRecomputes()
    {
        Write("20250303-1144-PECF Report.csv",
            PecfHeaders + "\nDRG,1,20250228-a.pdf,1\n");
        var cfg = new Config();
        var saveCfgCalls = 0;
        var vm = MakeVm(cfg, new FakeDialogs(), () => saveCfgCalls++);

        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Documents.Count == 1, "the row should load before changing the mapping");

        vm.FilenameColumn = "Controlid";

        Assert.Equal("Controlid", cfg.TatHeaders["filename"]);
        Assert.Equal(1, saveCfgCalls);
        // recompute happened: the Controlid cell ("1") isn't a document
        // date, so TatDaysText goes from "3" to the unparseable placeholder.
        Assert.Equal("—", vm.Documents[0].TatDaysText);
    }

    [Fact]
    public void ExportWritesTheDocRowsThroughAskSaveFile()
    {
        Write("20250303-1144-PECF Report.csv",
            PecfHeaders + "\nDRG,1,20250228-HELTON-EMILY-KYPT2024-11-63094.pdf,3\n");
        var savePath = Path.Combine(_dir, "out.csv");
        var dialogs = new FakeDialogs { NextSaveFile = savePath };
        var vm = MakeVm(new Config(), dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Documents.Count == 1, "the row should load before exporting");

        // InlineWorkScheduler runs the export synchronously, so no polling
        // is needed here — Execute's fire-and-forget ExportAsync() runs to
        // completion inline because nothing it awaits ever actually suspends.
        vm.ExportCommand.Execute(null);

        Assert.True(File.Exists(savePath));
        var lines = File.ReadAllLines(savePath);
        Assert.Equal("source_report,file_name,category,doc_date,upload_date,tat_days", lines[0]);
        Assert.Contains("20250228-HELTON-EMILY-KYPT2024-11-63094.pdf", lines[1]);
        Assert.Single(dialogs.Infos);
    }

    [Fact]
    public void DisposeIsSafeEvenWithAProbeInFlight()
    {
        var vm = MakeVm(new Config(), new FakeDialogs());
        vm.AddPaths(new[] { _dir });   // arms the table probe

        var ex = Record.Exception(() => vm.Dispose());

        Assert.Null(ex);
    }
}
