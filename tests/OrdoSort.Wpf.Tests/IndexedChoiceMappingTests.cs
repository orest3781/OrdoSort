using System.Xml.Linq;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>DW-43: two combo boxes bind SelectedIndex to a number that C#
/// maps to a config value with a switch, while the XAML lists the items in
/// its own order. Nothing tied the two: reorder the XAML items (or the
/// switch) and "Silent" would save as "windows", with no build error. These
/// read each combo's items straight from the XAML and pin, label by label,
/// the value the view model saves for it.</summary>
public class IndexedChoiceMappingTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The ComboBoxItem labels, in order, of the combo whose
    /// SelectedIndex binds to <paramref name="binding"/>.</summary>
    private static List<string> ItemLabels(string xamlFile, string binding)
    {
        var xaml = XDocument.Load(Path.Combine(Repo.Root, "src", "OrdoSort.Wpf", xamlFile));
        var combo = xaml.Descendants(Wpf + "ComboBox")
            .Single(c => (string?)c.Attribute("SelectedIndex") == $"{{Binding {binding}}}");
        return combo.Elements(Wpf + "ComboBoxItem").Select(i => (string)i.Attribute("Content")!).ToList();
    }

    [Theory]
    [InlineData("OrdoSort", "")]
    [InlineData("Windows chime", "windows")]
    [InlineData("Custom .wav…", @"C:\sounds\ding.wav")]
    [InlineData("Silent", "none")]
    public void EachSoundChoiceSavesTheValueItsLabelSays(string label, string spec)
    {
        var index = ItemLabels(@"Windows\SettingsWindow.xaml", "Choice").IndexOf(label);
        Assert.True(index >= 0, $"no \"{label}\" item in the sound combo");
        var dialogs = new FakeDialogs { NextOpenFile = @"C:\sounds\ding.wav" };
        var choice = new SoundChoiceVm("New alert", SoundEvent.NewAlert, "none", dialogs, new RecordingSoundService());

        choice.Choice = index;

        Assert.Equal(spec, choice.Spec);
        Assert.Equal(index, new SoundChoiceVm("x", SoundEvent.NewAlert, spec, dialogs, new RecordingSoundService()).Choice);
    }

    [Theory]
    [InlineData("Active only", "active")]
    [InlineData("All", "all")]
    [InlineData("Hidden", "hidden")]
    public void EachTileChoiceSavesTheValueItsLabelSays(string label, string mode)
    {
        var index = ItemLabels("MainWindow.xaml", "TileVisibilityIndex").IndexOf(label);
        Assert.True(index >= 0, $"no \"{label}\" item in the tiles combo");
        using var fx = new ShellFixture(cfg => cfg.TileVisibility = mode == "active" ? "hidden" : "active");

        fx.Shell.TileVisibilityIndex = index;

        Assert.Equal(mode, fx.Shell.Cfg.TileVisibility);
        Assert.Equal(index, fx.Shell.TileVisibilityIndex);
    }
}
