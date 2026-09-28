using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-44: AsyncRelayCommand catches whatever its work throws and
/// hands it to OnError; with nothing wired there, the fault simply vanished.
/// Zip and unzip (Zip, Zip to…, Extract), Merge (Merge, Merge to…) and
/// Unlock never wired it, so a throw left a stale "Zipping 3 items…" or an
/// empty summary and no word to the user. Each now says the run stopped, the
/// way Bulk Rename, Match &amp; Merge and Standardise names already did.
/// The fault comes in through each view model's injectable worker.</summary>
public class CommandFaultReportingTests
{
    private static readonly InvalidOperationException Boom = new("the disk went away");

    private static ZipExtractViewModel ZipVm(FakeDialogs dialogs,
        Func<IReadOnlyList<string>, string?, CancellationToken, Zipper.ZipResult>? zipper = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, Zipper.UnzipResult>? extractor = null) =>
        new(dialogs, Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipper, extractor, (path, _) => new Zipper.ZipProbeResult(path, "not_encrypted"));

    private static MergePdfsViewModel MergeVm(FakeDialogs dialogs,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> fileMerger) =>
        new(dialogs, Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger: null, fileMerger: fileMerger,
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p));

    [Fact]
    public async Task AZipThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var vm = ZipVm(new FakeDialogs(), zipper: (_, _, _) => throw Boom);
        await vm.AddPaths(new[] { dir.File("a.txt") });

        vm.ZipCommand.Execute(null);
        await vm.ZipCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Status);
        Assert.Contains("the disk went away", vm.Status);
    }

    [Fact]
    public async Task AZipToThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs { NextSaveFile = Path.Combine(dir.Path, "out.zip") };
        var vm = ZipVm(dialogs, zipper: (_, _, _) => throw Boom);
        await vm.AddPaths(new[] { dir.File("a.txt") });

        vm.ZipAsCommand.Execute(null);
        await vm.ZipAsCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Status);
    }

    [Fact]
    public async Task AnExtractThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var vm = ZipVm(new FakeDialogs(), extractor: (_, _, _) => throw Boom);
        await vm.AddPaths(new[] { dir.File("a.zip") });

        vm.ExtractCommand.Execute(null);
        await vm.ExtractCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Status);
    }

    [Fact]
    public async Task AMergeThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var vm = MergeVm(new FakeDialogs(), (_, _, _, _) => throw Boom);
        await vm.AddPaths(new[] { dir.File("a.pdf") });

        vm.MergeCommand.Execute(null);
        await vm.MergeCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Status);
    }

    [Fact]
    public async Task AMergeToThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs { NextSaveFile = Path.Combine(dir.Path, "out.pdf") };
        var vm = MergeVm(dialogs, (_, _, _, _) => throw Boom);
        await vm.AddPaths(new[] { dir.File("a.pdf") });

        vm.MergeToCommand.Execute(null);
        await vm.MergeToCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Status);
    }

    [Fact]
    public async Task AnUnlockThatThrowsSaysItStopped()
    {
        using var dir = new TempDir();
        var vm = new UnlockViewModel(new Config(), () => true,
            unlocker: (_, _) => throw Boom,
            probe: (path, _) => new Unlock.ProbeResult("needs_password", path, Message: "x"),
            scheduler: new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { dir.File("a.pdf") });
        vm.Password = "secret";

        vm.UnlockCommand.Execute(null);
        await vm.UnlockCommand.Completion;

        Assert.Contains("stopped unexpectedly", vm.Summary);
        Assert.Contains("the disk went away", vm.Summary);
    }

    /// <summary>The sweep, kept: a view model that makes an AsyncRelayCommand
    /// wires an OnError for it in the same file. Counts, so a new command
    /// added without one fails here, naming its file.</summary>
    [Fact]
    public void EveryAsyncCommandHasAnErrorHandlerBesideIt()
    {
        var files = Directory.GetFiles(Path.Combine(Repo.Root, "src"), "*.cs", SearchOption.AllDirectories);
        var missing = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var made = System.Text.RegularExpressions.Regex.Matches(text, @"new AsyncRelayCommand\b").Count;
            var wired = System.Text.RegularExpressions.Regex.Matches(text, @"\.OnError \+=").Count;
            if (made > wired) missing.Add($"{Path.GetFileName(file)}: {made} commands, {wired} OnError");
        }
        Assert.True(missing.Count == 0, "AsyncRelayCommand without OnError:\n" + string.Join("\n", missing));
    }
}
