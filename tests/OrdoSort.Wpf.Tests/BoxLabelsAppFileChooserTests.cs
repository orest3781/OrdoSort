using BoxLabelsApp.Services;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Every path BoxLabels.exe opens is checked first — not only the
/// ones picked in the file dialog. A "--file" path and a remembered path used
/// to be opened unchecked, so <c>--file ...\config.json</c> wrote box-label
/// settings into OrdoSort's config, and a remembered store that had been
/// renamed silently started a new box-number list at 1.
///
/// The picker is scripted and "remember" is recorded, so these run without a
/// window.</summary>
public sealed class BoxLabelsAppFileChooserTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "boxlabels_chooser_" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeDialogs _dialogs = new();
    private readonly Queue<string?> _picks = new();
    private readonly List<string> _remembered = new();
    private int _pickerShown;

    private const string Title = "Test — box labels";
    private static readonly Func<string, bool> Reachable = _ => true;

    private string SettingsPath => LabelsFileSettings.PathIn(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LabelsFileChooser Chooser() => new(_dialogs, Title, () =>
    {
        _pickerShown++;
        return _picks.Count > 0 ? _picks.Dequeue() : null;
    });

    private string? Resolve(params string[] args) =>
        Chooser().Resolve(args, SettingsPath, Reachable, _remembered.Add);

    private string AStore()
    {
        var store = Path.Combine(_dir, "box-labels.json");
        BoxLabelStore.Mutate(store, d =>
        {
            d.LabelClients.Add(new LabelClient { Id = "ACME", NextNumber = 40 });
            return 0;
        });
        return store;
    }

    private string OrdoSortConfig()
    {
        var config = Path.Combine(_dir, "config.json");
        Config.Save(new Config { Inbox = Path.Combine(_dir, "inbox") }, config);
        return config;
    }

    [Fact]
    public void ARememberedGoodStoreOpensWithoutAskingAnything()
    {
        var store = AStore();
        LabelsFileSettings.Write(SettingsPath, store);

        Assert.Equal(store, Resolve());
        Assert.Equal(0, _pickerShown);
        Assert.Empty(_dialogs.Warnings);
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public void ACommandLineFileThatIsNotAStoreIsRefusedAndLeftUntouched()
    {
        var config = OrdoSortConfig();
        var before = File.ReadAllBytes(config);
        var store = AStore();
        _picks.Enqueue(store);

        var opened = Resolve("--file", config);

        Assert.Equal(store, opened);
        Assert.Contains("does not look like a box labels file", Assert.Single(_dialogs.Warnings).Message);
        Assert.Equal(1, _pickerShown);
        Assert.Empty(_remembered);                          // a --file run stays a one-off
        Assert.Equal(before, File.ReadAllBytes(config));
    }

    [Fact]
    public void ARememberedFileThatIsNotAStoreIsRefusedAndTheReplacementIsKept()
    {
        var config = OrdoSortConfig();
        LabelsFileSettings.Write(SettingsPath, config);
        var store = AStore();
        _picks.Enqueue(store);

        var opened = Resolve();

        Assert.Equal(store, opened);
        Assert.Single(_dialogs.Warnings);
        Assert.Equal(store, Assert.Single(_remembered));   // the user told us where it really is
    }

    [Fact]
    public void ARememberedStoreThatHasGoneAsksBeforeStartingANewList()
    {
        var gone = Path.Combine(_dir, "renamed-away.json");
        LabelsFileSettings.Write(SettingsPath, gone);
        _dialogs.ConfirmAnswer = false;   // "Choose another file"
        // then cancels the picker

        var opened = Resolve();

        Assert.Null(opened);
        Assert.Contains("NEW, empty box-number list", Assert.Single(_dialogs.Confirms).Message);
        Assert.Equal(1, _pickerShown);
        Assert.False(File.Exists(gone));
    }

    [Fact]
    public void AMissingCommandLineFileCanStillStartANewListOnceConfirmed()
    {
        var fresh = Path.Combine(_dir, "first-station.json");
        _dialogs.ConfirmAnswer = true;    // "Start a new list"

        Assert.Equal(fresh, Resolve("--file", fresh));
        Assert.Single(_dialogs.Confirms);
        Assert.Equal(0, _pickerShown);
    }

    [Fact]
    public void AnUnreadableCommandLineFileIsRefused()
    {
        var damaged = Path.Combine(_dir, "damaged.json");
        File.WriteAllText(damaged, "{ \"label_clients\": [ ");

        Assert.Null(Resolve("--file", damaged));   // picker cancelled after the warning
        Assert.Contains("cannot be read", Assert.Single(_dialogs.Warnings).Message);
        Assert.Equal(1, _pickerShown);
    }

    [Fact]
    public void ABadPickIsRefusedAndThePickerShownAgain()
    {
        var config = OrdoSortConfig();
        var store = AStore();
        _picks.Enqueue(config);
        _picks.Enqueue(store);

        Assert.Equal(store, Chooser().PickAndCheck());
        Assert.Single(_dialogs.Warnings);
        Assert.Equal(2, _pickerShown);
    }
}
