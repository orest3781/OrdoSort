using OrdoSort.Core;
using OrdoSort.TestSupport;

namespace OrdoSort.Core.Tests;

/// <summary>The filing loop's local read-ahead (spec
/// 2026-09-29-filing-loop-design.md): a copy is handed out only while it
/// matches the inbox file, every failure falls back to the inbox file, and
/// nothing is left on the disk.</summary>
public class DocumentStageTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ADocumentAskedForIsCopiedUnderItsOwnName()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/20240115--111111.pdf", "page one");
        using var stage = new DocumentStage(tmp.Dir("stage"));

        var copy = await stage.CopyForAsync(doc, Plenty);

        Assert.NotNull(copy);
        Assert.StartsWith(stage.Folder, copy);
        Assert.Equal("20240115--111111.pdf", Path.GetFileName(copy));
        Assert.Equal("page one", File.ReadAllText(copy!));
        Assert.Equal("page one", File.ReadAllText(doc));   // the inbox file is untouched
    }

    [Fact]
    public async Task WantedDocumentsAreCopiedAheadOfBeingAskedFor()
    {
        using var tmp = new TempDir();
        var docs = new[] { tmp.File("inbox/a.pdf", "a"), tmp.File("inbox/b.pdf", "b") };
        var copied = new List<string>();
        using var stage = new DocumentStage(tmp.Dir("stage"), (s, d) =>
        {
            lock (copied) copied.Add(Path.GetFileName(s));
            File.Copy(s, d, true);
        });

        stage.Want(docs);

        Wait.WaitFor(() => { lock (copied) return copied.Count == 2; }, "both wanted documents are copied");
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, copied);
        // asking now costs no second copy
        Assert.NotNull(await stage.CopyForAsync(docs[1], TimeSpan.FromMilliseconds(500)));
        Assert.Equal(2, copied.Count);
    }

    [Fact]
    public async Task ACopyNotReadyInTimeMeansShowTheInboxFile()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/slow.pdf", "x");
        using var gate = new ManualResetEventSlim();
        using var stage = new DocumentStage(tmp.Dir("stage"), (s, d) =>
        {
            gate.Wait(Plenty);
            File.Copy(s, d, true);
        });

        Assert.Null(await stage.CopyForAsync(doc, TimeSpan.FromMilliseconds(50)));

        gate.Set();
        Assert.NotNull(await stage.CopyForAsync(doc, Plenty));   // the same copy, once it lands
    }

    [Fact]
    public async Task AFailedCopyMeansShowTheInboxFile()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/locked.pdf", "x");
        using var stage = new DocumentStage(tmp.Dir("stage"), (_, _) => throw new IOException("in use"));

        Assert.Null(await stage.CopyForAsync(doc, Plenty));
    }

    [Fact]
    public async Task AMissingDocumentMeansShowTheInboxFile()
    {
        using var tmp = new TempDir();
        using var stage = new DocumentStage(tmp.Dir("stage"));

        Assert.Null(await stage.CopyForAsync(Path.Combine(tmp.Path, "gone.pdf"), Plenty));
    }

    /// <summary>The one thing the stage must never do: show an old page for
    /// a document that has changed since its copy was made.</summary>
    [Fact]
    public async Task ADocumentChangedAfterItsCopyIsCopiedAgain()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "old");
        using var stage = new DocumentStage(tmp.Dir("stage"));
        Assert.NotNull(await stage.CopyForAsync(doc, Plenty));

        File.WriteAllText(doc, "new and longer");

        Assert.Null(await stage.CopyForAsync(doc, Plenty));   // the stale copy is refused
        var fresh = await stage.CopyForAsync(doc, Plenty);     // and a new one is made
        Assert.Equal("new and longer", File.ReadAllText(fresh!));
    }

    [Fact]
    public async Task ADocumentChangedWhileItCopiedIsNotHandedOut()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "first");
        using var stage = new DocumentStage(tmp.Dir("stage"), (s, d) =>
        {
            File.Copy(s, d, true);
            File.WriteAllText(s, "second, still being written");   // the scanner is not done
        });

        Assert.Null(await stage.CopyForAsync(doc, Plenty));
    }

    [Fact]
    public async Task AFileOverTheSizeCapIsNotCopied()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/huge.pdf", new string('x', 100));
        using var stage = new DocumentStage(tmp.Dir("stage"), maxFileBytes: 99);

        Assert.Null(await stage.CopyForAsync(doc, Plenty));
    }

    [Fact]
    public async Task TheSessionStopsCopyingAtItsTotalCap()
    {
        using var tmp = new TempDir();
        var a = tmp.File("inbox/a.pdf", new string('a', 60));
        var b = tmp.File("inbox/b.pdf", new string('b', 60));
        using var stage = new DocumentStage(tmp.Dir("stage"), maxTotalBytes: 100);

        Assert.NotNull(await stage.CopyForAsync(a, Plenty));
        Assert.Null(await stage.CopyForAsync(b, Plenty));
    }

    [Fact]
    public async Task ACopyNoLongerWantedIsDeleted()
    {
        using var tmp = new TempDir();
        var a = tmp.File("inbox/a.pdf", "a");
        var b = tmp.File("inbox/b.pdf", "b");
        using var stage = new DocumentStage(tmp.Dir("stage"));
        var copyOfA = await stage.CopyForAsync(a, Plenty);

        stage.Want(new[] { b });

        Assert.False(File.Exists(copyOfA));
    }

    [Fact]
    public async Task DisposingDeletesTheSessionsFolder()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "x");
        var stage = new DocumentStage(tmp.Dir("stage"));
        await stage.CopyForAsync(doc, Plenty);

        stage.Dispose();

        Assert.False(Directory.Exists(stage.Folder));
        Assert.Null(await stage.CopyForAsync(doc, Plenty));   // and hands nothing out after
    }

    /// <summary>A copy holds the inbox file open: the move must wait for it
    /// rather than fail against it.</summary>
    [Fact]
    public async Task LettingGoWaitsForACopyStillRunning()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "x");
        using var copying = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var stage = new DocumentStage(tmp.Dir("stage"), (s, d) =>
        {
            copying.Set();
            gate.Wait(Plenty);
            File.Copy(s, d, true);
        });
        stage.Want(new[] { doc });
        Assert.True(copying.Wait(Plenty));

        var letGo = stage.LetGoAsync(doc, Plenty);
        await Task.Delay(50);
        Assert.False(letGo.IsCompleted);
        gate.Set();
        await letGo;
    }

    [Fact]
    public async Task ADocumentLetGoIsNotCopiedAgain()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "x");
        var copies = 0;
        using var stage = new DocumentStage(tmp.Dir("stage"), (s, d) =>
        {
            Interlocked.Increment(ref copies);
            File.Copy(s, d, true);
        });

        await stage.LetGoAsync(doc, Plenty);
        stage.Want(new[] { doc });
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref copies));
    }

    [Fact]
    public async Task AChangeAfterTheCopyWasShownIsNoticed()
    {
        using var tmp = new TempDir();
        var doc = tmp.File("inbox/doc.pdf", "what the user saw");
        using var stage = new DocumentStage(tmp.Dir("stage"));
        Assert.NotNull(await stage.CopyForAsync(doc, Plenty));
        Assert.True(await stage.IsUnchangedAsync(doc));

        File.WriteAllText(doc, "something else entirely");

        Assert.False(await stage.IsUnchangedAsync(doc));
    }

    [Fact]
    public async Task ADocumentNeverCopiedCountsAsUnchanged()
    {
        using var tmp = new TempDir();
        using var stage = new DocumentStage(tmp.Dir("stage"));

        Assert.True(await stage.IsUnchangedAsync(Path.Combine(tmp.Path, "never.pdf")));
    }

    [Fact]
    public void TheSweepTakesAFolderLeftByACrashedSession()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("stage");
        var orphan = Path.Combine(root, "crashed");
        Directory.CreateDirectory(Path.Combine(orphan, "1"));
        File.WriteAllText(Path.Combine(orphan, "1", "doc.pdf"), "x");
        File.WriteAllText(Path.Combine(orphan, DocumentStage.LockName), "");   // nobody holds it

        DocumentStage.SweepOrphans(root);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void TheSweepLeavesARunningSessionAlone()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("stage");
        using var running = new DocumentStage(root);

        DocumentStage.SweepOrphans(root);

        Assert.True(Directory.Exists(running.Folder));
    }

    [Fact]
    public void TheSweepLeavesAJustCreatedFolderWithNoLockYet()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("stage");
        var starting = Directory.CreateDirectory(Path.Combine(root, "starting")).FullName;

        DocumentStage.SweepOrphans(root);

        Assert.True(Directory.Exists(starting));
    }
}
