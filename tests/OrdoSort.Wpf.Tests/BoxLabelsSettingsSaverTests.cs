using BoxLabelsApp.Services;
using OrdoSort.Core;
using OrdoSort.TestSupport;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>Box Labels' Settings OK (2026-09-28): the General tab is saved
/// per PC beside the exe, the label style (only if changed) to the shared
/// labels file. Each half is saved and reported on its own.</summary>
public class BoxLabelsSettingsSaverTests
{
    private static BoxLabelsSettingsViewModel Vm(string labelsFile) =>
        new(labelsFile, "auto", "", 0, BoxLabels.LabelStyle.Default, "");

    [Fact]
    public void OkSavesAppearanceBesideTheExeAndTheStyleToTheSharedFile()
    {
        using var dir = new TempDir();
        var settings = LabelsFileSettings.PathIn(dir.Path);
        var labels = Path.Combine(dir.Path, "box-labels.json");
        var vm = Vm(labels);
        vm.ThemeDark = true;
        vm.UiFontSizeText = "16";
        vm.LabelStyle!.LayoutHuge = true;

        var outcome = BoxLabelsSettingsSaver.Save(settings, vm);

        Assert.Empty(outcome.Failures);
        Assert.Equal("dark", LabelsFileSettings.ReadTheme(settings));
        Assert.Equal(("", 16), LabelsFileSettings.ReadFont(settings));
        Assert.Equal(BoxLabels.LayoutHuge, BoxLabelStore.Read(labels).Style.Layout);
    }

    /// <summary>Review focus 4: the shared file can't be written.</summary>
    [Fact]
    public void AStyleThatCannotBeSavedIsReportedAndTheAppearanceStillSaves()
    {
        using var dir = new TempDir();
        var settings = LabelsFileSettings.PathIn(dir.Path);
        var labels = dir.File("box-labels.json", "{ not json");   // damaged
        var vm = Vm(labels);
        vm.ThemeLight = true;
        vm.LabelStyle!.LayoutBig = true;

        var outcome = BoxLabelsSettingsSaver.Save(settings, vm);

        Assert.True(outcome.AppearanceSaved);
        Assert.False(outcome.StyleSaved);
        Assert.Contains(outcome.Failures, f => f.Contains("Label style not saved"));
        Assert.Equal("light", LabelsFileSettings.ReadTheme(settings));
    }

    [Fact]
    public void AnUnchangedStyleIsNotWritten()
    {
        using var dir = new TempDir();
        var labels = Path.Combine(dir.Path, "box-labels.json");

        BoxLabelsSettingsSaver.Save(LabelsFileSettings.PathIn(dir.Path), Vm(labels));

        Assert.False(File.Exists(labels));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("big")]
    public void ABadTextSizeIsAProblem(string size)
    {
        var vm = Vm("x.json");
        vm.UiFontSizeText = size;

        Assert.Contains(AppFonts.SizeProblem(size), vm.Problems());
    }

    [Fact]
    public void AFontThatIsNotOneOfTheChoicesIsAProblem()
    {
        var vm = Vm("x.json");
        vm.UiFontFamily = "Comic Sans MS";

        Assert.Contains(vm.Problems(), p => p.Contains("Comic Sans MS"));
    }
}
