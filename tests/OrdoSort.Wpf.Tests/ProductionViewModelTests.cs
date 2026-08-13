using System.Diagnostics;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 7 (Production report window). Loading runs through the same
/// debounced, off-UI-thread DebouncedProbe&lt;SweptTable.Table&gt; shape
/// TurnaroundViewModel uses for its own load — see
/// TurnaroundViewModelTests' own doc comment for why "eventually correct"
/// has to be polled for even with InlineWorkScheduler and probeDelayMs: 0
/// (the underlying System.Threading.Timer still fires on a threadpool
/// thread). Only tick-driven setters — no probe involved — are safe to
/// assert immediately after driving them.</summary>
public class ProductionViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoprod_" + Guid.NewGuid());

    public ProductionViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
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

    private static ProductionViewModel MakeVm(Config cfg, FakeDialogs dialogs, Action? saveCfg = null) =>
        new(cfg, dialogs, saveCfg, new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    /// <summary>Final whole-branch review finding 3: vm.Rows is keyed by each
    /// column's INDEX in vm.ColumnNames, not by its NAME (see
    /// ProductionViewModel.Rows' own doc comment for why — a literal
    /// "Records" header, or the same header ticked in both Group and Sum,
    /// would otherwise collide on a name key). Every test below that used to
    /// read <c>vm.Rows[n]["SOME-HEADER"]</c> directly now goes through this
    /// helper, which resolves "SOME-HEADER" to its live position in
    /// ColumnNames first — so these tests keep reading as "the SOURCE-FOLDER
    /// cell", not "cell 0", while still exercising the real index-based
    /// storage.</summary>
    private static string Cell(ProductionViewModel vm, Dictionary<string, string> row, string column)
    {
        var index = vm.ColumnNames.ToList().IndexOf(column);
        Assert.True(index >= 0, $"'{column}' not found in ColumnNames ({string.Join(",", vm.ColumnNames)})");
        return row[index.ToString(CultureInfo.InvariantCulture)];
    }

    private const string SweepHeaders = "DATE-TIME,FILE-OWNER,FILE-NAME,ACTION,PDF-PAGE-COUNT,SOURCE-FOLDER";

    // Two owners, two source-folders — INVOICES carries BOTH owners (so
    // unchecking Employee actually merges two rows into one, not just drops
    // a column that happened to already be 1:1 with its folder), CLAIMS
    // carries only user2.
    private const string FixtureRows =
        "4/1/2025 7:55,ACME\\user1,a.pdf,filed,3,INVOICES\n" +
        "4/1/2025 9:10,ACME\\user1,b.pdf,filed,2,INVOICES\n" +
        "4/1/2025 11:20,ACME\\user2,e.pdf,filed,4,INVOICES\n" +
        "4/1/2025 14:30,ACME\\user2,c.pdf,filed,5,CLAIMS\n" +
        "4/2/2025 8:00,ACME\\user2,d.pdf,filed,1,CLAIMS\n";

    [Fact]
    public void LoadingAFolderPopulatesPickListsWithDerivedColumnsAndDefaults()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.GroupPicks.Count > 0, "pick lists should populate after load");
        Assert.Equal(
            new[] { "DATE-TIME", "FILE-OWNER", "FILE-NAME", "ACTION", "PDF-PAGE-COUNT", "SOURCE-FOLDER",
                "Employee", "Date", "Hour" },
            vm.GroupPicks.Select(p => p.Name));
        Assert.Equal(
            new[] { "DATE-TIME", "FILE-OWNER", "FILE-NAME", "ACTION", "PDF-PAGE-COUNT", "SOURCE-FOLDER",
                "Employee", "Date", "Hour" },
            vm.SumPicks.Select(p => p.Name));

        // defaults: group = SOURCE-FOLDER + Employee, sum = PDF-PAGE-COUNT
        Assert.True(vm.GroupPicks.Single(p => p.Name == "SOURCE-FOLDER").IsChosen);
        Assert.True(vm.GroupPicks.Single(p => p.Name == "Employee").IsChosen);
        Assert.False(vm.GroupPicks.Single(p => p.Name == "Date").IsChosen);
        Assert.False(vm.GroupPicks.Single(p => p.Name == "Hour").IsChosen);
        Assert.True(vm.SumPicks.Single(p => p.Name == "PDF-PAGE-COUNT").IsChosen);
        Assert.False(vm.SumPicks.Single(p => p.Name == "ACTION").IsChosen);
    }

    [Fact]
    public void GroupedMathEndToEndMatchesHandComputedTotals()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        // default group = [SOURCE-FOLDER, Employee], sorted ordinally:
        // CLAIMS/user2, INVOICES/user1, INVOICES/user2
        WaitFor(() => vm.Rows.Count == 3, "all three (SOURCE-FOLDER, Employee) groups should appear");

        Assert.Equal("CLAIMS", Cell(vm, vm.Rows[0], "SOURCE-FOLDER"));
        Assert.Equal("user2", Cell(vm, vm.Rows[0], "Employee"));
        Assert.Equal("2", Cell(vm, vm.Rows[0], "Records"));
        Assert.Equal("6", Cell(vm, vm.Rows[0], "PDF-PAGE-COUNT"));

        Assert.Equal("INVOICES", Cell(vm, vm.Rows[1], "SOURCE-FOLDER"));
        // ACME\user1 -> user1: domain prefix stripped
        Assert.Equal("user1", Cell(vm, vm.Rows[1], "Employee"));
        Assert.Equal("2", Cell(vm, vm.Rows[1], "Records"));
        Assert.Equal("5", Cell(vm, vm.Rows[1], "PDF-PAGE-COUNT"));

        Assert.Equal("INVOICES", Cell(vm, vm.Rows[2], "SOURCE-FOLDER"));
        Assert.Equal("user2", Cell(vm, vm.Rows[2], "Employee"));
        Assert.Equal("1", Cell(vm, vm.Rows[2], "Records"));
        Assert.Equal("4", Cell(vm, vm.Rows[2], "PDF-PAGE-COUNT"));
    }

    /// <summary>Windows resolves a path case-insensitively, so the same
    /// folder root added twice under two different spellings names one
    /// location on disk. The dedupe Intake.Add now owns for this tool must
    /// keep the second spelling from sweeping the fixture a second time and
    /// doubling every row.</summary>
    [Fact]
    public void ACaseOnlyDuplicateIsNotAddedTwice()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var vm = MakeVm(new Config(), new FakeDialogs());
        var shouty = Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir).ToUpperInvariant());

        vm.AddPaths(new[] { _dir, shouty });

        WaitFor(() => vm.Rows.Count == 3, "all three (SOURCE-FOLDER, Employee) groups should appear exactly once, not doubled");
        // CLAIMS/user2 — would be "4" if the fixture had swept twice
        Assert.Equal("2", Cell(vm, vm.Rows[0], "Records"));
    }

    /// <summary>The spelling difference that case-insensitivity alone never
    /// caught: "…\dir" and "…\dir\" compare unequal as raw strings, so before
    /// canonicalisation both could sit in _sources and the folder was swept
    /// twice — silently doubling every count in a report someone reads as
    /// fact. This is the defect that made the three source-root tools worth
    /// migrating, not just the seven that hold files.</summary>
    [Fact]
    public void ATrailingSeparatorDoesNotSweepTheSameFolderTwice()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir, _dir + Path.DirectorySeparatorChar });

        WaitFor(() => vm.Rows.Count == 3, "the fixture should sweep exactly once, not twice");
        Assert.Equal("2", Cell(vm, vm.Rows[0], "Records"));
        Assert.Contains("already listed", vm.AddNote);
    }

    [Fact]
    public void UncheckingEmployeeRegroupsAndPersistsToConfig()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var cfg = new Config();
        var saveCfgCalls = 0;
        var vm = MakeVm(cfg, new FakeDialogs(), () => saveCfgCalls++);

        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 3, "three groups should appear before unchecking Employee");

        vm.GroupPicks.Single(p => p.Name == "Employee").IsChosen = false;

        Assert.Equal(new[] { "SOURCE-FOLDER" }, cfg.ProductionGroupColumns);
        Assert.True(saveCfgCalls > 0);
        Assert.DoesNotContain("Employee", vm.ColumnNames);
        // fewer key columns really does mean fewer rows here: INVOICES'
        // user1 and user2 rows collapse into one folder-level total
        Assert.Equal(2, vm.Rows.Count);
        var sourceFolderIndex = vm.ColumnNames.ToList().IndexOf("SOURCE-FOLDER").ToString(CultureInfo.InvariantCulture);
        var invoices = vm.Rows.Single(r => r[sourceFolderIndex] == "INVOICES");
        Assert.Equal("3", Cell(vm, invoices, "Records"));
        Assert.Equal("9", Cell(vm, invoices, "PDF-PAGE-COUNT"));
    }

    [Fact]
    public void SavedConfigRestoresOnlyThoseColumnsChecked()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var cfg = new Config
        {
            ProductionGroupColumns = new List<string> { "SOURCE-FOLDER" },
            ProductionSumColumns = new List<string> { "PDF-PAGE-COUNT" },
        };
        var vm = MakeVm(cfg, new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.GroupPicks.Count > 0, "pick lists should populate after load");
        Assert.True(vm.GroupPicks.Single(p => p.Name == "SOURCE-FOLDER").IsChosen);
        Assert.False(vm.GroupPicks.Single(p => p.Name == "Employee").IsChosen);
        Assert.True(vm.SumPicks.Single(p => p.Name == "PDF-PAGE-COUNT").IsChosen);
        Assert.False(vm.SumPicks.Single(p => p.Name == "ACTION").IsChosen);
    }

    [Fact]
    public void DatetimeAutoGuessDerivesDateAndHourColumns()
    {
        Write("20250303-1144-swept.csv",
            SweepHeaders + "\n4/1/2025 7:55,ACME\\user1,a.pdf,filed,3,INVOICES\n");
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.GroupPicks.Count > 0, "pick lists should populate after load");

        Assert.Equal("DATE-TIME", vm.DatetimeColumn);

        // isolate grouping to Date/Hour only, so the row's derived values
        // are easy to read straight off Rows
        foreach (var p in vm.GroupPicks) p.IsChosen = p.Name is "Date" or "Hour";
        foreach (var p in vm.SumPicks) p.IsChosen = false;

        Assert.Single(vm.Rows);
        Assert.Equal("2025-04-01", Cell(vm, vm.Rows[0], "Date"));
        Assert.Equal("07", Cell(vm, vm.Rows[0], "Hour"));
        Assert.Equal("1", Cell(vm, vm.Rows[0], "Records"));
    }

    [Fact]
    public void ExportWritesGroupedRowsWithExpectedHeaderLine()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var savePath = Path.Combine(_dir, "out.csv");
        var dialogs = new FakeDialogs { NextSaveFile = savePath };
        var vm = MakeVm(new Config(), dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 3, "rows should load before exporting");

        // InlineWorkScheduler runs the export synchronously, so no polling
        // is needed here — same reasoning as TurnaroundViewModelTests'
        // ExportWritesTheDocRowsThroughAskSaveFile.
        vm.ExportCommand.Execute(null);

        Assert.True(File.Exists(savePath));
        var lines = File.ReadAllLines(savePath);
        Assert.Equal("SOURCE-FOLDER,Employee,record_count,PDF-PAGE-COUNT", lines[0]);
        Assert.Equal(4, lines.Length);   // header + 3 groups
        Assert.Single(dialogs.Infos);
        // Finding 4 (final whole-branch review): count is results.Count —
        // GROUPS, not source rows — the completion dialog's own wording used
        // to say "rows" regardless.
        Assert.Equal($"Exported 3 groups to {savePath}", dialogs.Infos[0].Message);
    }

    /// <summary>Finding 3 (final whole-branch review, feature/reports): before
    /// this fix, vm.Rows' display Dictionary was keyed by column NAME, which
    /// collides whenever two entries in ColumnNames share a name — here, the
    /// same header (SOURCE-FOLDER) ticked as BOTH a Group and a Sum column.
    /// ProductionReport.Group's own doc comment: a non-numeric sum cell
    /// contributes 0, so the SUM value ("0") and the GROUP value (the real
    /// folder text) are unmistakably different — pre-fix, whichever
    /// RecomputeResults wrote last (the sum loop runs after the group loop)
    /// would have silently overwritten the group's own folder text with "0"
    /// under the shared "SOURCE-FOLDER" key. Keyed by INDEX instead, both
    /// values now live at their own distinct position.</summary>
    [Fact]
    public void SameColumnTickedAsBothGroupAndSumDoesNotClobberEitherDisplayedValue()
    {
        Write("20250303-1144-swept.csv", SweepHeaders + "\n" + FixtureRows);
        var cfg = new Config
        {
            ProductionGroupColumns = new List<string> { "SOURCE-FOLDER" },
            ProductionSumColumns = new List<string> { "SOURCE-FOLDER" },
        };
        var vm = MakeVm(cfg, new FakeDialogs());

        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "two SOURCE-FOLDER groups should appear");

        // SOURCE-FOLDER appears TWICE in ColumnNames: once as the ticked
        // GROUP column, once as the ticked SUM column.
        var indexes = vm.ColumnNames
            .Select((name, i) => (name, i)).Where(x => x.name == "SOURCE-FOLDER").Select(x => x.i).ToList();
        Assert.Equal(2, indexes.Count);
        var groupIndex = indexes[0].ToString(CultureInfo.InvariantCulture);
        var sumIndex = indexes[1].ToString(CultureInfo.InvariantCulture);

        var claims = vm.Rows.Single(r => r[groupIndex] == "CLAIMS");
        Assert.Equal("CLAIMS", claims[groupIndex]);   // the group's own real value
        Assert.Equal("0", claims[sumIndex]);          // the non-numeric sum, unclobbered
    }

    /// <summary>Mirrors TurnaroundSubfolderTests.SubfoldersAreIncludedByDefault
    /// — IncludeSubfolders now defaults to true (reports live in dated
    /// subfolder trees; a fresh window should sweep the whole tree without
    /// the user knowing the checkbox exists), so AddPaths alone, no checkbox
    /// touch at all, must already pull nested CSVs in.</summary>
    [Fact]
    public void SubfoldersAreIncludedByDefault()
    {
        Write(Path.Combine("sub", "20250303-1144-swept.csv"), SweepHeaders + "\n" + FixtureRows);
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Rows.Count == 3,
            $"nested CSVs should load with no checkbox touch at all; status was: '{vm.Status}'");
    }

    /// <summary>Mirrors TurnaroundSubfolderTests.EmptyLoadExplainsExtensionSkippedFiles
    /// — same Intake.Expanded plumbing, same skipped-note wording, applied to
    /// this view model's own status line shape ("... groups" instead of "...
    /// without TAT").</summary>
    [Fact]
    public void EmptyLoadExplainsExtensionSkippedFiles()
    {
        Write(Path.Combine("sub", "notes.txt"), "irrelevant");
        Write("readme.txt", "irrelevant");
        var vm = MakeVm(new Config(), new FakeDialogs());

        vm.IncludeSubfolders = true;
        vm.AddPaths(new[] { _dir });

        WaitFor(() => vm.Status.Contains("skipped"),
            $"status should explain the empty load; was: '{vm.Status}'");
        Assert.Empty(vm.Rows);
        Assert.Equal("0 files · 0 rows · 0 groups · 2 skipped (not csv/xlsx)", vm.Status);
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
