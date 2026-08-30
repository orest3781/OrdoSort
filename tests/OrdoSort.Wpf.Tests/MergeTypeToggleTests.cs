using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The per-type toggle row (2026-08-30 spec, Task 7): the owner's
/// own ask — "i want there to be a toggle for all file types that should
/// merge". Exclusion is a LIVE computed property (ZipItemRow.IsIncluded),
/// not a status a run produces, which is what lets a row already in the
/// list join back in the instant its type is switched back on, with no
/// re-add — the whole point of the design, and what
/// <see cref="SwitchingTheTypeBackOnIncludesTheRowsAlreadyInTheListWithoutReAdding"/>
/// exists to prove. The choice is remembered through Config.MergeTypes
/// (Task 2's MergeTypes.Save/Load round trip), not the window itself, so a
/// bare in-memory Config passed to two view models in a row is enough to
/// prove persistence without touching disk.</summary>
public class MergeTypeToggleTests : IDisposable
{
    private readonly TempDir _dir = new();
    private int _fileNumber;

    public void Dispose() => _dir.Dispose();

    // DocxPath/PdfPath/ZipPath: a fresh dummy file per call, extension only
    // — nothing here ever actually converts or reads the bytes (every
    // merger below is fake), so "x" is as good a fixture as a real
    // document.
    private string DocxPath() => _dir.File($"doc{_fileNumber++}.docx");
    private string PdfPath() => _dir.File($"file{_fileNumber++}.pdf");
    private string ZipPath() => _dir.File($"archive{_fileNumber++}.zip");

    private static MergePdfsViewModel NewViewModel(
        Config? config = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null) =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger, fileMerger,
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p),
            config: config);

    /// <summary>Records every path a fake fileMerger was handed — the
    /// brief's own "RecordingMerger" shorthand, spelled out as a real,
    /// non-static helper so nothing leaks state between facts.</summary>
    private sealed class RecordingMerger
    {
        public List<string> PathsSeen { get; } = new();

        public PdfMerge.MergeResult Merge(IReadOnlyList<string> paths, string? outputPath,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
        {
            PathsSeen.AddRange(paths);
            return new PdfMerge.MergeResult(paths.Count > 0 ? paths[0] : "", "ok",
                Output: outputPath ?? "Job.pdf", PdfCount: paths.Count);
        }
    }

    [Fact]
    public async Task ARowOfASwitchedOffTypeIsListedButNotIncluded()
    {
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath(), PdfPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        var word = vm.Rows.Single(r => r.Kind == "word");
        Assert.False(word.IsIncluded);
        Assert.False(word.IsRunnable);
        Assert.Contains("not included", word.Note);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);   // the PDF only
    }

    [Fact]
    public async Task SwitchingTheTypeBackOnIncludesTheRowsAlreadyInTheListWithoutReAdding()
    {
        // The whole reason exclusion is a live property rather than a status.
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        Assert.False(vm.Rows.Single().IsIncluded);
        vm.SetTypeEnabled(MergeTypes.Word, true);
        Assert.True(vm.Rows.Single().IsIncluded);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);
    }

    [Fact]
    public async Task TogglingRaisesPropertyChangedOnTheRowsSoTheGridRepaints()
    {
        var vm = NewViewModel();
        await vm.AddPaths([DocxPath()]);
        var row = vm.Rows.Single();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        Assert.Contains(nameof(ZipItemRow.IsIncluded), raised);
    }

    [Fact]
    public void TheChoiceIsSavedAndComesBack()
    {
        var config = new Config();
        var vm = NewViewModel(config);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        vm.SetTypeEnabled(MergeTypes.Images, false);
        var reopened = NewViewModel(config);
        Assert.False(reopened.IsTypeEnabled(MergeTypes.Word));
        Assert.False(reopened.IsTypeEnabled(MergeTypes.Images));
        Assert.True(reopened.IsTypeEnabled(MergeTypes.Pdf));
    }

    [Fact]
    public void UntickingEverythingStaysUntickedAfterAReopen()
    {
        // The "everything off" sentinel from Task 2 - without it an empty
        // stored value reads as "never set" and everything comes back on.
        var config = new Config();
        var vm = NewViewModel(config);
        foreach (var group in MergeTypes.AllGroups) vm.SetTypeEnabled(group, false);
        Assert.All(MergeTypes.AllGroups, g => Assert.False(NewViewModel(config).IsTypeEnabled(g)));
    }

    /// <summary>The brief's own fact, verbatim. As written this is honest
    /// but weak while Task 7 stands alone: MergeAsync's loose-unit selection
    /// still filters on IsPdf (Task 8 widens that to every included non-zip
    /// row), so a .docx never reaches _fileMerger regardless of IsIncluded
    /// today. What it DOES prove now — and what actually matters for this
    /// task — is that IsRunnable folds in IsIncluded, which is the general
    /// mechanism ANY future selection built on IsRunnable inherits for
    /// free, including the one Task 8 is about to write. See
    /// <see cref="AnExcludedZipIsNotSelectedIntoTheRun"/> below for the
    /// equivalent guarantee exercised through a selection that already
    /// exists today.</summary>
    [Fact]
    public async Task AnExcludedRowIsNotSelectedIntoTheRun()
    {
        var recorder = new RecordingMerger();
        var vm = NewViewModel(
            zipMerger: (path, _, _) => new PdfMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1),
            fileMerger: recorder.Merge);
        await vm.AddPaths([DocxPath(), PdfPath()]);
        vm.SetTypeEnabled(MergeTypes.Word, false);
        await vm.MergeAsync(null);
        Assert.DoesNotContain(recorder.PathsSeen, p => p.EndsWith(".docx"));
    }

    /// <summary>Supplementary to the brief's own fact above: zips ARE
    /// already selected by IsRunnable alone (MergeAsync's zip-unit filter
    /// is `r.IsZip && r.IsRunnable`, unchanged by this task), so switching
    /// Zip off is a genuine, discriminating proof today that an excluded
    /// row never reaches a merger — not just a proof that stands ready for
    /// Task 8.</summary>
    [Fact]
    public async Task AnExcludedZipIsNotSelectedIntoTheRun()
    {
        var zipCalls = new List<string>();
        var vm = NewViewModel(zipMerger: (path, _, _) =>
        {
            zipCalls.Add(path);
            return new PdfMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1);
        });
        var zip = ZipPath();
        await vm.AddPaths([zip]);
        vm.SetTypeEnabled(MergeTypes.Zip, false);
        await vm.MergeAsync(null);
        Assert.Empty(zipCalls);
        Assert.Equal("Merge", vm.MergeButtonText);   // nothing else was added
    }

    /// <summary>The XAML binds CheckBoxes to this collection — pins the
    /// shape that binding depends on (one toggle per group, in
    /// MergeTypes.AllGroups' own order, a non-empty Label) and that flipping
    /// a toggle through SetTypeEnabled — not through the checkbox itself —
    /// still reaches the SAME toggle object's IsEnabled, the way a test (or
    /// another part of the view model) doing so must.</summary>
    [Fact]
    public void TypeTogglesCoverEveryGroupInOrderAndReflectDirectChanges()
    {
        var vm = NewViewModel();
        Assert.Equal(MergeTypes.AllGroups, vm.TypeToggles.Select(t => t.Group));
        Assert.All(vm.TypeToggles, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
        Assert.All(vm.TypeToggles, t => Assert.True(t.IsEnabled));   // nothing configured yet

        vm.SetTypeEnabled(MergeTypes.Excel, false);
        Assert.False(vm.TypeToggles.Single(t => t.Group == MergeTypes.Excel).IsEnabled);

        // The reverse direction: setting IsEnabled on the toggle itself
        // (what the checkbox's own two-way binding does) round-trips back
        // through IsTypeEnabled.
        vm.TypeToggles.Single(t => t.Group == MergeTypes.Excel).IsEnabled = true;
        Assert.True(vm.IsTypeEnabled(MergeTypes.Excel));
    }
}
