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
/// 1688 bytes in, 1830 out — which is why a config.json written by OrdoSort's
/// own Config.Save is one of the cases below rather than a hand-written
/// stand-in.</summary>
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
    /// label_clients and date_style at the top.
    ///
    /// The config is written by Config.Save — the app's own writer — so it
    /// has exactly the shape a real station's file has, and the test is
    /// hermetic. It used to copy demo-full/config.json, which is gitignored,
    /// so it could only pass on a machine that had run scripts\demo-full.bat and
    /// failed every CI run.</summary>
    [Fact]
    public void OrdoSortsOwnConfigIsRefused()
    {
        var config = Path.Combine(_dir, "config.json");
        Config.Save(new Config
        {
            Inbox = Path.Combine(_dir, "inbox"),
            Deferred = Path.Combine(_dir, "set-aside"),
            Routes = { new Route { Label = "Invoices", Path = Path.Combine(_dir, "invoices"), Hotkey = "Ctrl+1" } },
            WatchFolders = { new WatchFolder { Label = "Failed", Path = Path.Combine(_dir, "failed") } },
            AlertTexts = { "URGENT" },
            SavedPasswords = { new SavedPassword { Label = "Payer A", Password = "letmein" } },
        }, config);

        Assert.Equal(LabelsFileKind.NotAStore, LabelsFileCheck.Classify(config));
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
