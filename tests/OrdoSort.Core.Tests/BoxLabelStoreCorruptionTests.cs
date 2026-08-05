using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The box counters are the one piece of state with physical-world
/// consequences: a reissued number means two boxes in a warehouse wearing the
/// same label. Mutate used to treat a 0-byte file (a crash mid-write) as "no
/// clients yet" and rewrite it empty, wiping every counter — while Read threw
/// on the identical input. These pin both halves: the wipe is refused, and a
/// 0-byte state can no longer be produced in the first place.</summary>
public class BoxLabelStoreCorruptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ordo_boxlabels_" + Guid.NewGuid());

    public BoxLabelStoreCorruptionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>A truncated file is corruption, not emptiness. Mutate must
    /// refuse it exactly as Read does — never silently reset the counters.</summary>
    [Fact]
    public void MutateRefusesAZeroByteFileInsteadOfWipingTheCounters()
    {
        var path = Path.Combine(_dir, "box-labels.json");
        File.WriteAllText(path, "");

        var ex = Assert.Throws<ConfigException>(
            () => BoxLabelStore.Mutate(path, doc => doc.LabelClients.Count));

        // the file must be left exactly as found — refusing is not repairing
        Assert.Equal("", File.ReadAllText(path));
        Assert.Contains("box-labels", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A genuinely absent file still means "no clients yet" — that
    /// is first run, not corruption, and must keep working.</summary>
    [Fact]
    public void AMissingFileIsStillTreatedAsFirstRun()
    {
        var path = Path.Combine(_dir, "box-labels.json");
        var count = BoxLabelStore.Mutate(path, doc => doc.LabelClients.Count);
        Assert.Equal(0, count);
        Assert.True(File.Exists(path));
    }

    /// <summary>A shrinking write must never pass through a 0-byte state:
    /// write first, truncate after. Asserted on the file's own bytes after a
    /// large-to-small rewrite, and on the fact that the result parses.</summary>
    [Fact]
    public void AShrinkingWriteLeavesAValidFileNotAnEmptyOne()
    {
        var path = Path.Combine(_dir, "box-labels.json");
        BoxLabelStore.Mutate(path, doc =>
        {
            for (var i = 0; i < 200; i++)
                doc.LabelClients.Add(new LabelClient { Id = "client-" + i });
            return 0;
        });
        var big = new FileInfo(path).Length;

        BoxLabelStore.Mutate(path, doc => { doc.LabelClients.Clear(); return 0; });

        var small = new FileInfo(path).Length;
        Assert.True(small < big, "the shrink should actually have shrunk the file");
        Assert.True(small > 0, "the file must never end up empty");
        Assert.Empty(BoxLabelStore.Read(path).LabelClients);
    }
}
