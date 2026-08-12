using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 6 (zip tool). InlineWorkScheduler resolves every
/// _scheduler.Run call synchronously — same reasoning as
/// ZipMergeViewModelTests' own class doc — so CreateAsync/CreateWithDialogAsync
/// (the internal methods CreateCommand/CreateAsCommand wrap) can be awaited
/// directly and asserted immediately after, no polling needed.</summary>
public class ZipViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordozipvm_" + Guid.NewGuid());

    public ZipViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string TouchFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string TouchFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ZipViewModel MakeVm(FakeDialogs dialogs,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, zipper);

    [Fact]
    public async Task CreateCommandCallsTheZipperWithANullOutputPath()
    {
        var a = TouchFile("a.txt");
        string? seenOutput = "not called";
        var vm = MakeVm(new FakeDialogs(), (paths, output) =>
        {
            seenOutput = output;
            return new Zipper.ZipResult("ok", Path.Combine(_dir, "a.zip"));
        });
        await vm.AddPaths(new[] { a });

        await vm.CreateAsync(null);

        Assert.Null(seenOutput);
    }

    [Fact]
    public async Task CreateCommandAppliesTheOkStatusWordingWithTheItemCount()
    {
        var a = TouchFile("a.txt");
        var b = TouchFile("b.txt");
        var vm = MakeVm(new FakeDialogs(),
            (paths, output) => new Zipper.ZipResult("ok", Path.Combine(_dir, "made.zip")));
        await vm.AddPaths(new[] { a, b });

        await vm.CreateAsync(null);

        Assert.Contains("made.zip", vm.Status);
        Assert.Contains("2 items", vm.Status);
    }

    [Fact]
    public async Task CreateCommandAppliesTheErrorMessageVerbatimOnFailure()
    {
        var a = TouchFile("a.txt");
        var vm = MakeVm(new FakeDialogs(),
            (paths, output) => new Zipper.ZipResult("error", null, "nothing to zip"));
        await vm.AddPaths(new[] { a });

        await vm.CreateAsync(null);

        Assert.Equal("nothing to zip", vm.Status);
    }

    [Fact]
    public async Task CreateAsCommandPassesTheChosenPathToTheZipper()
    {
        var a = TouchFile("a.txt");
        var chosen = Path.Combine(_dir, "chosen.zip");
        string? seenOutput = null;
        var calls = 0;
        var vm = MakeVm(new FakeDialogs { NextSaveFile = chosen }, (paths, output) =>
        {
            calls++;
            seenOutput = output;
            return new Zipper.ZipResult("ok", chosen);
        });
        await vm.AddPaths(new[] { a });

        await vm.CreateWithDialogAsync();

        Assert.Equal(1, calls);
        Assert.Equal(chosen, seenOutput);
    }

    [Fact]
    public async Task CreateAsCommandSkipsTheZipperWhenTheDialogIsCancelled()
    {
        var a = TouchFile("a.txt");
        var calls = 0;
        var vm = MakeVm(new FakeDialogs { NextSaveFile = null }, (paths, output) =>
        {
            calls++;
            return new Zipper.ZipResult("ok", "irrelevant.zip");
        });
        await vm.AddPaths(new[] { a });

        await vm.CreateWithDialogAsync();

        Assert.Equal(0, calls);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task AddPathsDedupesDropsMissingPathsAndSetsAddNote()
    {
        var a = TouchFile("a.txt");
        var ghost = Path.Combine(_dir, "gone.txt");
        var vm = MakeVm(new FakeDialogs());

        await vm.AddPaths(new[] { a, ghost });
        Assert.Single(vm.Rows);
        Assert.Equal(a, vm.Rows[0].Path);
        Assert.NotEqual("", vm.AddNote);

        await vm.AddPaths(new[] { a });   // same file again
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>Windows resolves a path case-insensitively, so "a.txt" and
    /// "A.txt" are the same file on disk — File.Exists says yes to both.
    /// AddPaths' dedupe now runs through Intake.Add (Core), which
    /// canonicalizes each path before comparing — this pins that the second
    /// spelling is turned away as "already listed" instead of landing as a
    /// second row over the same bytes.</summary>
    [Fact]
    public async Task ACaseOnlyDuplicateIsNotAddedTwice()
    {
        var a = TouchFile("a.txt");
        var shouty = Path.Combine(_dir, "A.txt");   // same file, different spelling
        var vm = MakeVm(new FakeDialogs());

        await vm.AddPaths(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task AddPathsRecordsFileAndFolderKindSeparately()
    {
        var file = TouchFile("a.txt");
        var folder = TouchFolder("sub");
        var vm = MakeVm(new FakeDialogs());

        await vm.AddPaths(new[] { file, folder });

        var fileRow = Assert.Single(vm.Rows, r => r.Path == file);
        var folderRow = Assert.Single(vm.Rows, r => r.Path == folder);
        Assert.Equal("file", fileRow.Kind);
        Assert.Equal("folder", folderRow.Kind);
    }

    [Fact]
    public async Task ZipButtonTextReflectsRowCount()
    {
        var vm = MakeVm(new FakeDialogs());
        Assert.Equal("Zip", vm.ZipButtonText);

        var a = TouchFile("a.txt");
        await vm.AddPaths(new[] { a });
        Assert.Equal("Zip 1 item", vm.ZipButtonText);

        var b = TouchFile("b.txt");
        await vm.AddPaths(new[] { b });
        Assert.Equal("Zip 2 items", vm.ZipButtonText);
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNote()
    {
        var a = TouchFile("a.txt");
        var vm = MakeVm(new FakeDialogs(),
            (paths, output) => new Zipper.ZipResult("ok", Path.Combine(_dir, "a.zip")));
        await vm.AddPaths(new[] { a });
        await vm.CreateAsync(null);
        Assert.NotEqual("", vm.Status);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Zip", vm.ZipButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        var a = TouchFile("a.txt");
        var b = TouchFile("b.txt");
        var vm = MakeVm(new FakeDialogs());
        await vm.AddPaths(new[] { a, b });
        Assert.Equal(2, vm.Rows.Count);

        var toRemove = vm.Rows.Where(r => r.Path == a).ToList();
        vm.RemoveSelected(toRemove);

        var remaining = Assert.Single(vm.Rows);
        Assert.Equal(b, remaining.Path);
    }

    [Fact]
    public async Task RealZipperSmokeTestOnTwoTempFiles()
    {
        var a = TouchFile("a.txt");
        var b = TouchFile("b.txt");
        var vm = MakeVm(new FakeDialogs());   // default zipper: the real Zipper.CreateZip

        await vm.AddPaths(new[] { a, b });
        await vm.CreateAsync(null);

        Assert.Contains("Created", vm.Status);
        // default name for two loose files = the parent folder's own name
        // (_dir's own name) — see Zipper.DefaultName's own doc comment.
        var expected = Path.Combine(_dir, Path.GetFileName(_dir) + ".zip");
        Assert.True(File.Exists(expected));
        using var zip = ZipFile.OpenRead(expected);
        Assert.Equal(2, zip.Entries.Count);
    }
}
