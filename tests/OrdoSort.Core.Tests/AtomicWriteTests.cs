using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Config writes must never leave a valid file destroyed. The old
/// File.WriteAllText truncated in place: a crash or a full disk between the
/// truncate and the write left 0 bytes, Config.Load threw, and App shut down
/// — on a shared config that bricked every station until someone fixed it by
/// hand.
///
/// The guarantee asserted here is the one that matters and the one a
/// temp-file-then-replace actually provides: an observer of the destination
/// path sees either the complete old content or the complete new content,
/// never a partial or empty file.</summary>
[Collection(AtomicPlaceTests.Name)]
public class AtomicWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ordo_atomic_" + Guid.NewGuid());

    public AtomicWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>The destination is never observed empty or partial. A writer
    /// that truncates in place fails this: the reader catches it at 0 bytes.
    /// The reader opens with full sharing (including FileShare.Delete) so the
    /// writer can proceed and actually truncate, making the failure observable.</summary>
    [Fact]
    public async Task TheDestinationIsNeverObservedEmptyOrPartial()
    {
        var path = Path.Combine(_dir, "config.json");
        var oldContent = "{\"old\":\"" + new string('o', 200_000) + "\"}";
        var newContent = "{\"new\":\"" + new string('n', 200_000) + "\"}";
        File.WriteAllText(path, oldContent);

        var stop = false;
        var bad = new List<string>();
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    // Open with full sharing to let the writer proceed and actually
                    // truncate, so truncation errors are observable.
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Write | FileShare.Delete, bufferSize: 4096, useAsync: false);
                    using var sr = new StreamReader(fs);
                    string seen = sr.ReadToEnd();
                    if (seen != oldContent && seen != newContent)
                        bad.Add(seen.Length == 0 ? "<empty>" : $"<partial {seen.Length}>");
                }
                catch (FileNotFoundException) { }  // file was replaced, that's ok
                catch (IOException) { }            // any transient I/O error is ok
            }
        });

        for (var i = 0; i < 40; i++)
        {
            Config.WriteAtomic(path, newContent);
            Config.WriteAtomic(path, oldContent);
        }
        Volatile.Write(ref stop, true);
        await reader;

        Assert.Empty(bad);
    }

    /// <summary>No temp file survives a successful write.</summary>
    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        var path = Path.Combine(_dir, "config.json");
        Config.WriteAtomic(path, "{}");
        Config.WriteAtomic(path, "{\"a\":1}");
        Assert.Equal(new[] { "config.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    /// <summary>The content actually lands, including over a missing file.</summary>
    [Fact]
    public void WritesLandWhetherOrNotTheTargetExists()
    {
        var path = Path.Combine(_dir, "fresh.json");
        Config.WriteAtomic(path, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Config.WriteAtomic(path, "{\"b\":2}");
        Assert.Equal("{\"b\":2}", File.ReadAllText(path));
    }

    /// <summary>When the destination is held open beyond the retry budget
    /// (a few seconds — see AtomicPlace.Attempts), the write fails loudly
    /// and leaves no .tmp sibling.</summary>
    [Fact]
    public void WriteFailsAndCleansUpWhenRetryBudgetExhausted()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{\"initial\":\"content\"}");

        // Hold the destination open for the whole test: longer than any
        // retry budget AtomicPlace could reasonably use.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: false))
        {
            // Attempt to write while the file is exclusively held.
            var ex = Assert.Throws<IOException>(() => Config.WriteAtomic(path, "{\"new\":\"content\"}"));
            Assert.NotNull(ex);  // Must throw loudly, not silently fail.
        }

        // No .tmp sibling should be left behind after failure.
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "config.json" }, files);
    }

    /// <summary>The retry loop is d52208c's entire behavioral claim: a
    /// concurrent reader holding the destination open (Config.Load uses
    /// File.ReadAllText, no FileShare.Delete) must not make a save fail where
    /// it would have succeeded once the reader let go. The other three tests
    /// in this file stay green with the loop deleted outright — the
    /// exhaustion test above just throws on attempt 1 instead of attempt 50,
    /// and the atomicity test's reader opens with FileShare.Delete so Replace
    /// never contends. This is the one test that actually needs the loop
    /// (2026-08 audit finding 2): hold the destination open for less than the
    /// retry budget, release it, and require the write to have succeeded —
    /// not thrown — with the new content landed.
    ///
    /// The release is gated on AtomicPlace.BeforeAttempt, fired synchronously
    /// from inside the retry loop, rather than on a Task.Run'd Thread.Sleep
    /// racing the loop's real 500ms budget on an independent clock (2026-08
    /// CI audit: that version flaked on GitHub Actions' windows-latest runner
    /// because the releaser's own Task.Run dispatch has no guaranteed upper
    /// bound under shared-runner thread-pool contention). Releasing on
    /// attempt 2 (not attempt 0) keeps the retry loop's own claim honest:
    /// the first two attempts genuinely fail while the reader still holds
    /// the file, so the eventual success is the loop actually working, not
    /// a lucky first try.
    ///
    /// The seam moved from Config.OnRetryForTests to AtomicPlace when the
    /// retry loop did. Two differences, neither of which changes what this
    /// test proves: it now fires BEFORE each attempt rather than after a
    /// failed one, so releasing on attempt 2 means attempt 2 is the one that
    /// lands rather than attempt 3; and it carries the destination path,
    /// because the hook is process-wide and every other test class's saves
    /// run through it too — hence the path guard below, which the old
    /// per-Config seam did not need.</summary>
    [Fact]
    public void WriteSucceedsOnceAReaderReleasesWithinTheRetryBudget()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{\"initial\":\"content\"}");

        var hold = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: false);
        var releasedAtAttempt = -1;
        AtomicPlace.BeforeAttempt = (destination, attempt) =>
        {
            if (destination != path) return;   // not our write
            if (attempt == 2)
            {
                releasedAtAttempt = attempt;
                hold.Dispose();
            }
        };
        try
        {
            Config.WriteAtomic(path, "{\"new\":\"content\"}");
        }
        finally
        {
            AtomicPlace.BeforeAttempt = null;
            hold.Dispose();   // no-op if the callback above already ran
        }

        Assert.Equal(2, releasedAtAttempt);   // the loop really did retry, not succeed on try #1
        Assert.Equal("{\"new\":\"content\"}", File.ReadAllText(path));
    }
}
