using OrdoSort.Core;
using OrdoSort.TestSupport;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-01 (2026-09-28): the list showed one order and the merge used
/// another (by name). Loose files now merge in list order, and Move up / Move
/// down change it, so a cover page can go first.</summary>
public class MergePdfsOrderTests
{
    private static MergePdfsViewModel MakeVm(List<string> merged) =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger: null,
            fileMerger: (paths, _, _, _) =>
            {
                merged.AddRange(paths.Select(Path.GetFileName)!);
                return new PdfMerge.MergeResult(paths[0], "ok", Output: paths[0] + ".out.pdf", PdfCount: paths.Count);
            },
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));

    private static IList<string> Names(MergePdfsViewModel vm) =>
        vm.Rows.Select(r => Path.GetFileName(r.Path)).ToList();

    [Fact]
    public async Task LooseFilesMergeInTheOrderTheListShows()
    {
        using var dir = new TempDir();
        var merged = new List<string>();
        var vm = MakeVm(merged);
        await vm.AddPaths(new[] { dir.File("01-invoice.pdf") });
        await vm.AddPaths(new[] { dir.File("cover.pdf") });

        await vm.MergeAsync(null);

        Assert.Equal(new[] { "01-invoice.pdf", "cover.pdf" }, merged);
    }

    [Fact]
    public async Task MovingAFileUpPutsItEarlierInTheMerge()
    {
        using var dir = new TempDir();
        var merged = new List<string>();
        var vm = MakeVm(merged);
        await vm.AddPaths(new[] { dir.File("01-invoice.pdf"), dir.File("02-receipt.pdf") });
        await vm.AddPaths(new[] { dir.File("cover.pdf") });
        var cover = vm.Rows.Single(r => r.Path.EndsWith("cover.pdf"));

        vm.MoveSelected(new[] { cover }, up: true);
        vm.MoveSelected(new[] { cover }, up: true);
        await vm.MergeAsync(null);

        Assert.Equal(new[] { "cover.pdf", "01-invoice.pdf", "02-receipt.pdf" }, Names(vm));
        Assert.Equal(new[] { "cover.pdf", "01-invoice.pdf", "02-receipt.pdf" }, merged);
    }

    [Fact]
    public async Task ABlockMovesTogetherAndStopsAtTheEnd()
    {
        using var dir = new TempDir();
        var vm = MakeVm(new List<string>());
        await vm.AddPaths(new[] { dir.File("a.pdf"), dir.File("b.pdf"), dir.File("c.pdf") });
        var ab = vm.Rows.Take(2).ToList();

        vm.MoveSelected(ab, up: false);
        Assert.Equal(new[] { "c.pdf", "a.pdf", "b.pdf" }, Names(vm));

        vm.MoveSelected(ab, up: false);   // already at the bottom
        Assert.Equal(new[] { "c.pdf", "a.pdf", "b.pdf" }, Names(vm));
    }
}
