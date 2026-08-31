using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;
using ZipFile = System.IO.Compression.ZipFile;

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

    /// <summary>A type no MergeTypes group recognizes at all — the foreign-
    /// type-at-intake fact's fixture (Task 8).</summary>
    private string ExePath() => _dir.File($"installer{_fileNumber++}.exe");

    /// <summary>A REAL zip holding one REAL one-page PDF entry — needed only
    /// by facts that exercise the actual PdfMerge.MergeZip path (fix 1's
    /// fact below), where a dummy "x" file would never round-trip through a
    /// real ZipArchive/PdfReader.</summary>
    private string ZipWithOnePdf()
    {
        var zipPath = Path.Combine(_dir.Path, $"withpdf{_fileNumber++}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        ms.Position = 0;
        using var entryStream = zip.CreateEntry("a.pdf").Open();
        ms.CopyTo(entryStream);
        return zipPath;
    }

    private static MergePdfsViewModel NewViewModel(
        Config? config = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null,
        IDocumentConverter? converter = null) =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger, fileMerger,
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: pdfProbe ?? ((p, _) => new Unlock.ProbeResult("not_encrypted", p)),
            config: config, converter: converter);

    /// <summary>Claims exactly one extension, or none at all when
    /// <paramref name="handles"/> is null — models "nothing on this PC can
    /// convert this type", the brief's own StubConverter shorthand.</summary>
    private sealed class StubConverter : IDocumentConverter
    {
        private readonly string? _handles;
        public StubConverter(string? handles) => _handles = handles;
        public bool Handles(string extension) =>
            _handles is not null && extension.Equals(_handles, StringComparison.OrdinalIgnoreCase);
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask) =>
            new("unsupported", null, $"{displayName} isn't a type this stub converts");
    }

    /// <summary>A REAL zip holding one REAL one-page ".docx"-named entry
    /// (arbitrary bytes — nothing here ever reads them; a FakeConverter or
    /// <see cref="RecordingDocxConverter"/> stands in for real conversion),
    /// needed by <see cref="AToggleFlippedDuringOneUnitDoesNotChangeWhatALaterUnitInTheSameBatchSees"/>
    /// below, which exercises the REAL PdfMerge.MergeZip and so needs a
    /// REAL ZipArchive to read.</summary>
    private string ZipWithOneDocx(string entryName)
    {
        var zipPath = Path.Combine(_dir.Path, $"withdocx{_fileNumber++}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var entryStream = zip.CreateEntry(entryName).Open();
        var bytes = new byte[] { 1, 2, 3 };
        entryStream.Write(bytes, 0, bytes.Length);
        return zipPath;
    }

    /// <summary>Converts ".docx" to a real one-page PDF (so the merge path
    /// is genuinely exercised, the same reasoning PdfMergeTests' own
    /// FakeConverter documents), records every name it converted in call
    /// order, and fires <paramref name="onFirstConvert"/> exactly once, on
    /// its first call — the hook
    /// <see cref="AToggleFlippedDuringOneUnitDoesNotChangeWhatALaterUnitInTheSameBatchSees"/>
    /// uses to flip a toggle WHILE the first zip's own unit is still being
    /// processed, simulating what a mid-run checkbox click would do if the
    /// UI did not already disable it.</summary>
    private sealed class RecordingDocxConverter : IDocumentConverter
    {
        private readonly Action _onFirstConvert;
        private bool _fired;
        public RecordingDocxConverter(Action onFirstConvert) => _onFirstConvert = onFirstConvert;
        public List<string> ConvertedNames { get; } = new();
        public bool Handles(string extension) => extension.Equals("docx", StringComparison.OrdinalIgnoreCase);
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
        {
            ConvertedNames.Add(displayName);
            if (!_fired) { _fired = true; _onFirstConvert(); }
            using var doc = new PdfDocument();
            doc.AddPage();
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            return new("ok", ms.ToArray());
        }
    }

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

    // ---- 2026-08-30 review fix round: three Important findings ----------

    /// <summary>Review finding 1: the toggles governed the list but not an
    /// archive's OWN contents — the default zipMerger/fileMerger never
    /// passed includeTypes to PdfMerge, so it stayed null ("every type is
    /// on") no matter what was switched off. Concrete effect this closes:
    /// switch PDF off, leave Zip on, and a zip containing only a PDF used to
    /// merge it anyway. Exercises the REAL PdfMerge.MergeZip (no fake
    /// zipMerger) — a fake could not tell the fix from the bug, since the
    /// filtering happens INSIDE PdfMerge, not in this view model.</summary>
    [Fact]
    public async Task ASwitchedOffTypeInsideAnIncludedZipDoesNotMerge()
    {
        var vm = NewViewModel();   // real zipMerger: PdfMerge.MergeZip
        var zip = ZipWithOnePdf();
        await vm.AddPaths([zip]);
        vm.SetTypeEnabled(MergeTypes.Pdf, false);   // Zip itself stays ON

        await vm.MergeAsync(null);

        var row = vm.Rows.Single();
        Assert.Equal(ZipItemRowStatus.NoPdfs, row.StatusKind);
        Assert.Equal("nothing to merge inside", row.Note);
    }

    /// <summary>2026-08-30 review, Important 3: the enabled-type set the
    /// merger lambdas read is snapshotted ONCE, at the top of MergeAsync
    /// (MergePdfsViewModel._activeIncludeTypes), not read live off
    /// _enabledTypes on every PdfMerge call. The toggle row is disabled in
    /// the UI for the whole run for exactly this reason
    /// (MergePdfsWindow.xaml's IsEnabled="{Binding IsIdle}"), but the
    /// snapshot is what makes the guarantee hold regardless of how a toggle
    /// got flipped — belt-and-braces, not merely a UI nicety, since
    /// _enabledTypes is a plain HashSet a worker thread reads with no lock.
    ///
    /// Two zips, one docx entry each, both routed through the REAL
    /// PdfMerge.MergeZip (no fake zipMerger — the includeTypes threading
    /// under test happens INSIDE PdfMerge, so a fake merger could not tell
    /// the fix from the bug it replaces). The fake CONVERTER's first call
    /// (converting zip1's docx, mid-way through zip1's own unit) flips Word
    /// off — simulating a toggle click while a batch is running. Zip2's own
    /// unit runs strictly AFTER zip1's in RunBatchAsync's sequential loop,
    /// so under the OLD live-field design zip2's own PdfMerge.MergeZip call
    /// would already see Word disabled and report "nothing to merge inside"
    /// for its docx entry; under the snapshot, it still sees the enabled
    /// set as it stood when MergeAsync started, so both zips succeed
    /// identically.</summary>
    [Fact]
    public async Task AToggleFlippedDuringOneUnitDoesNotChangeWhatALaterUnitInTheSameBatchSees()
    {
        MergePdfsViewModel? vm = null;
        var converter = new RecordingDocxConverter(onFirstConvert: () => vm!.SetTypeEnabled(MergeTypes.Word, false));
        vm = NewViewModel(converter: converter);

        var zip1 = ZipWithOneDocx("a.docx");
        var zip2 = ZipWithOneDocx("b.docx");
        await vm.AddPaths([zip1, zip2]);

        await vm.MergeAsync(null);

        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single(r => r.Path == zip1).StatusKind);
        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single(r => r.Path == zip2).StatusKind);
        Assert.Equal(new[] { "a.docx", "b.docx" }, converter.ConvertedNames);
        // The toggle flip itself is real and takes effect for the NEXT run
        // — the snapshot is per-run, not permanent.
        Assert.False(vm.IsTypeEnabled(MergeTypes.Word));
    }

    /// <summary>Review finding 2: ZipItemRow.IsIncluded's old swap-on-
    /// transition design captured "the note before exclusion" exactly ONCE,
    /// at the moment IsIncluded flipped to false — so a LATER direct write
    /// to Note (Mark/Apply, called by a probe or a run, neither of which
    /// knows or cares about IsIncluded) permanently overwrote the exclusion
    /// message with nothing to restore it. Reachable through the feature's
    /// own headline use case: a type switched off in an earlier, PERSISTED
    /// session — SetTypeEnabled runs before AddPaths here, not after — so
    /// the add-time probe lands on a row that is already excluded. The fix
    /// (Note as a MASKED computed getter, never overwritten) is proven both
    /// ways: the exclusion note survives the probe, and the probe's own
    /// verdict — never actually lost, only hidden — reappears unchanged the
    /// instant the type is switched back on.</summary>
    [Fact]
    public async Task AProbeLandingAfterExclusionDoesNotEraseTheExclusionNoteOrThePriorRealNote()
    {
        var vm = NewViewModel(pdfProbe: (p, _) => new Unlock.ProbeResult("ready", p, MatchedIndex: 0));
        vm.SetTypeEnabled(MergeTypes.Pdf, false);
        await vm.AddPaths([PdfPath()]);
        var row = vm.Rows.Single();

        Assert.False(row.IsIncluded);
        Assert.Contains("not included", row.Note);   // the probe's verdict must not have erased this

        vm.SetTypeEnabled(MergeTypes.Pdf, true);
        Assert.True(row.IsIncluded);
        Assert.Equal("a saved password opens this", row.Note);   // ...and must not have been lost either
    }

    /// <summary>Reviewer's Minor 2, folded in: the one scenario that is
    /// fully functional today without leaning on Task 8's still-pending
    /// IsPdf sweep (contrast <see cref="AnExcludedRowIsNotSelectedIntoTheRun"/>
    /// above, which is honest but weak for exactly that reason) — PDF
    /// switched off, then a REAL MergeAsync run through the real
    /// PdfMerge.MergeFiles, not a fake fileMerger. The most realistic proof
    /// available at this point in the plan, and previously only covered
    /// indirectly.</summary>
    [Fact]
    public async Task ARealMergeRunLeavesASwitchedOffLoosePdfRowUntouched()
    {
        var pdf = PdfPath();
        var vm = NewViewModel();   // real fileMerger: PdfMerge.MergeFiles
        await vm.AddPaths([pdf]);
        vm.SetTypeEnabled(MergeTypes.Pdf, false);

        await vm.MergeAsync(null);

        var row = vm.Rows.Single();
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);   // never selected into a unit
        Assert.False(row.IsIncluded);
        Assert.Equal("Merge", vm.MergeButtonText);
        Assert.Equal(new[] { pdf }, Directory.GetFiles(_dir.Path));   // nothing was ever written
    }

    // ---- Task 8: probe-on-add and foreign-type refusal, the brief's own facts

    /// <summary>The probe that already runs on add is what tells you at DROP
    /// time, not after a long run: a .docx dropped where nothing on this PC
    /// can convert it is marked immediately, before Merge is ever clicked.</summary>
    [Fact]
    public async Task ADocumentNothingCanConvertIsMarkedWhenItIsDropped()
    {
        var vm = NewViewModel(converter: new StubConverter(handles: null));
        await vm.AddPaths(new[] { DocxPath() });
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Contains("Word", row.Note);
    }

    /// <summary>A type switched OFF is still listed (masked, excluded) —
    /// this is the different case: a type no MergeTypes group recognizes AT
    /// ALL is refused outright at intake, the same as it always was, and
    /// never becomes a row for the probe to even look at.</summary>
    [Fact]
    public async Task AForeignTypeIsStillRefusedAtIntake()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { ExePath() });
        Assert.Empty(vm.Rows);
    }

    /// <summary>The other half of the probe-on-add fact: while the Word
    /// group is switched OFF, the very same undroppable-anyway .docx is left
    /// alone rather than marked Error — there is nothing useful to tell
    /// someone about a row that will not join a run either way until they
    /// switch the type back on, and Note is masked regardless
    /// (ZipItemRow.IsIncluded) so a verdict written now would not even be
    /// seen.
    ///
    /// 2026-08-30 review, Minor 1: this fact used to stop here, which PINNED
    /// the limitation ("never probed") rather than closing it — switching
    /// the type back on left the row exactly Pending, the merge button
    /// counted it, and a run failed on click, which is precisely the
    /// failure the add-time probe exists to prevent. The scenario picked is
    /// the feature's own headline case (a PC without Word: switch Word off,
    /// drop a .docx, switch Word back on), and the second half below is what
    /// now proves SetTypeEnabled re-probes a row it never got the chance to
    /// the first time.</summary>
    [Fact]
    public async Task ADocumentOfASwitchedOffTypeIsNotProbedUntilItsTypeIsSwitchedBackOn()
    {
        var vm = NewViewModel(converter: new StubConverter(handles: null));
        vm.SetTypeEnabled(MergeTypes.Word, false);
        await vm.AddPaths(new[] { DocxPath() });
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);
        Assert.Contains("not included", row.Note);

        vm.SetTypeEnabled(MergeTypes.Word, true);

        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Contains("Word", row.Note);
    }
}
