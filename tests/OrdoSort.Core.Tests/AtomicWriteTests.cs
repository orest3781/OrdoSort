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
public class AtomicWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ordo_atomic_" + Guid.NewGuid());

    public AtomicWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>The destination is never observed empty or partial. A writer
    /// that truncates in place fails this: the reader catches it at 0 bytes.</summary>
    [Fact]
    public void TheDestinationIsNeverObservedEmptyOrPartial()
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
                string seen;
                try { seen = File.ReadAllText(path); }
                catch (IOException) { continue; }   // sharing violation is fine
                if (seen != oldContent && seen != newContent)
                    bad.Add(seen.Length == 0 ? "<empty>" : $"<partial {seen.Length}>");
            }
        });

        for (var i = 0; i < 40; i++)
        {
            Config.WriteAtomic(path, newContent);
            Config.WriteAtomic(path, oldContent);
        }
        Volatile.Write(ref stop, true);
        reader.Wait();

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
}
