using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Regression coverage for the reported "browse a folder with
/// Include subfolders checked and the subfolder CSVs still don't load" —
/// drives the SAME IncludeSubfolders property the window's checkbox binds
/// to, in both orders a user can reach (tick then browse, browse then
/// tick), against a folder whose CSVs live only in subfolders.</summary>
public class TurnaroundSubfolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordotat_sub_" + Guid.NewGuid());

    public TurnaroundSubfolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
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

    private static TurnaroundViewModel MakeVm() =>
        new(new Config(), new FakeDialogs(), null, new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    private const string PecfHeaders = "SourceType,Controlid,FileName,Pagecount";

    [Fact]
    public void TickSubfoldersThenBrowseLoadsNestedCsvs()
    {
        Write(Path.Combine("2025-03", "20250303-1144-PECF Report.csv"),
            PecfHeaders + "\nDRG,1,20250228-a.pdf,3\n");
        Write(Path.Combine("2025-03", "week2", "20250310-0900-PECF Report.csv"),
            PecfHeaders + "\nCOPR,2,20250302-y.pdf,1\n");
        var vm = MakeVm();

        vm.IncludeSubfolders = true;
        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Documents.Count == 2,
            $"both nested CSVs should load; status was: '{vm.Status}'");
    }

    [Fact]
    public void BrowseThenTickSubfoldersLoadsNestedCsvs()
    {
        Write(Path.Combine("sub", "20250303-1144-PECF Report.csv"),
            PecfHeaders + "\nDRG,1,20250228-a.pdf,3\n");
        var vm = MakeVm();

        vm.AddPaths(new[] { _dir });   // nothing at the top level — loads 0
        vm.IncludeSubfolders = true;   // the checkbox tick, after the browse

        WaitFor(() => vm.Documents.Count == 1,
            $"ticking Include subfolders after browsing should pull the nested CSV in; status was: '{vm.Status}'");
    }

    [Fact]
    public void UncheckedSubfoldersLoadsNothingFromANestedOnlyFolder()
    {
        Write(Path.Combine("sub", "20250303-1144-PECF Report.csv"),
            PecfHeaders + "\nDRG,1,20250228-a.pdf,3\n");
        var vm = MakeVm();

        vm.AddPaths(new[] { _dir });

        // Deliberately give the probe time to run, then confirm nothing loaded.
        WaitFor(() => vm.Status.Length > 0, "the status line should settle after the probe");
        Assert.Empty(vm.Documents);
    }

    /// <summary>The subfolder-loading fix (Intake.Expand switching to
    /// EnumerationOptions) threads Intake.Expanded through the load so an
    /// empty result can explain itself — here every file found is the wrong
    /// extension (Intake.Ignored), never SweptTable.Load's own per-file
    /// FileErrors. Before this fix the status line was just "0 files · 0
    /// rows · 0 without TAT" with no hint why.</summary>
    [Fact]
    public void EmptyLoadExplainsExtensionSkippedFiles()
    {
        Write(Path.Combine("sub", "notes.txt"), "irrelevant");
        Write("readme.txt", "irrelevant");
        var vm = MakeVm();

        vm.IncludeSubfolders = true;
        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Status.Contains("skipped"),
            $"status should explain the empty load; was: '{vm.Status}'");
        Assert.Empty(vm.Documents);
        Assert.Equal("0 files · 0 rows · 0 without TAT · 2 skipped (not csv/xlsx)", vm.Status);
    }
}
