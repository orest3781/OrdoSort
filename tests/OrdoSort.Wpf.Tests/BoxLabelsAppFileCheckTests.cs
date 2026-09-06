using BoxLabelsApp.Services;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Before BoxLabels.exe opens a file it has to know the file really
/// is the shared box-labels store.
///
/// The store is forgiving in a way that is dangerous here: BoxLabelsDoc has a
/// [JsonExtensionData] bag, so some other JSON file parses fine, shows an
/// empty client list, and gets label_clients and date_style written into it on
/// the first save. That was measured against a copy of demo-full/config.json —
/// 1688 bytes in, 1830 out — which is why that exact file is one of the cases
/// below rather than a hand-written stand-in.</summary>
public sealed class BoxLabelsAppFileCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "boxlabels_check_" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string contents)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, contents);
        return p;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("OrdoSort.sln not found");
    }

    [Fact]
    public void AFileThatIsNotThereIsMissing()
    {
        Assert.Equal(LabelsFileKind.Missing,
            LabelsFileCheck.Classify(Path.Combine(_dir, "never-created.json")));
    }

    /// <summary>The real thing: a store the app itself wrote.</summary>
    [Fact]
    public void AStoreWrittenByTheAppIsRecognised()
    {
        var store = Path.Combine(_dir, "box-labels.json");
        BoxLabelStore.Mutate(store, d =>
        {
            d.LabelClients.Add(new LabelClient { Id = "ACME", DestroyDays = 30, NextNumber = 1 });
            return 0;
        });

        Assert.Equal(LabelsFileKind.Store, LabelsFileCheck.Classify(store));
    }

    /// <summary>An empty client list is still a store — a brand-new shared file
    /// with no clients added yet must not be refused.</summary>
    [Fact]
    public void AStoreWithNoClientsYetIsStillAStore()
    {
        Assert.Equal(LabelsFileKind.Store,
            LabelsFileCheck.Classify(Write("empty-roster.json", "{ \"label_clients\": [] }")));
    }

    [Fact]
    public void AHandCreatedEmptyObjectIsUsable()
    {
        Assert.Equal(LabelsFileKind.EmptyObject, LabelsFileCheck.Classify(Write("new.json", "{}")));
    }

    /// <summary>The case this check exists for. Pointing the app at OrdoSort's
    /// own config.json used to be accepted silently, and writing to it injected
    /// label_clients and date_style at the top.</summary>
    [Fact]
    public void OrdoSortsOwnConfigIsRefused()
    {
        var source = Path.Combine(RepoRoot(), "demo-full", "config.json");
        Assert.True(File.Exists(source),
            $"this test needs the demo workbench config at {source} — run demo-full.bat");
        var copy = Path.Combine(_dir, "config.json");
        File.Copy(source, copy);

        Assert.Equal(LabelsFileKind.NotAStore, LabelsFileCheck.Classify(copy));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"label_clients\": [ ")]       // truncated mid-write
    [InlineData("[1, 2, 3]")]                      // an array, not an object
    [InlineData("\"just a string\"")]
    [InlineData("")]                               // zero bytes
    [InlineData("   \n  ")]                        // whitespace only
    public void DamagedOrForeignContentIsUnreadable(string contents)
    {
        Assert.Equal(LabelsFileKind.Unreadable,
            LabelsFileCheck.Classify(Write("damaged.json", contents)));
    }

    /// <summary>A zero-byte file is refused at the picker rather than accepted
    /// and failed later: BoxLabelStore treats a pre-existing empty store as a
    /// save that was interrupted and refuses to issue numbers from it, so
    /// accepting one here would only defer the same error to the first
    /// print.</summary>
    [Fact]
    public void AnEmptyFileIsRefusedRatherThanDeferredToTheFirstPrint()
    {
        var empty = Write("interrupted.json", "");

        Assert.Equal(LabelsFileKind.Unreadable, LabelsFileCheck.Classify(empty));
        Assert.Throws<ConfigException>(() => BoxLabelStore.Mutate(empty, _ => 0));
    }
}
