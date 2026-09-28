using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>OrdoSort's Settings "Box labels" tab (2026-09-28): the label style
/// is kept in the shared box-labels.json, not config.json, so OK writes it
/// there on its own, after config.json has saved.</summary>
public class SettingsLabelStyleTests
{
    // xUnit's per-test synchronization context would post the shell's
    // continuations past the asserts; released work runs inline without it.
    public SettingsLabelStyleTests() => SynchronizationContext.SetSynchronizationContext(null);

    private static Config Copy(Config c) => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(c))!;

    [Fact]
    public void AnUnchangedStyleIsNotWritten()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: BoxLabels.LabelStyle.Default);

        Assert.Null(vm.LabelStyleResult);
    }

    [Fact]
    public void AChangedStyleIsTheResult()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: BoxLabels.LabelStyle.Default);
        vm.LabelStyle!.LayoutHuge = true;

        Assert.Equal(BoxLabels.LayoutHuge, vm.LabelStyleResult!.Layout);
    }

    [Fact]
    public void AStoreThatCouldNotBeReadLeavesTheTabWithAReasonAndNothingToSave()
    {
        var vm = new SettingsViewModel(new Config(), new FakeDialogs(), labelStyle: null,
            labelStyleProblem: "the box-labels file is locked");

        Assert.Null(vm.LabelStyle);
        Assert.Equal("the box-labels file is locked", vm.LabelStyleProblem);
        Assert.Null(vm.LabelStyleResult);
    }

    [Fact]
    public async Task ApplyingWritesTheStyleToTheSharedFile()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var style = new BoxLabels.LabelStyle(BoxLabels.LayoutBig, false);

        await fx.Shell.ApplySettingsAsync(Copy(fx.Cfg), style);

        Assert.Equal(style, BoxLabelStore.Read(fx.Shell.BoxLabelsPath).Style);
        Assert.Empty(fx.Dialogs.Warnings);
    }

    /// <summary>Review focus 5: the file and the style changed in one OK.</summary>
    [Fact]
    public async Task TheStyleGoesToTheNewFileWhenTheFileChangedToo()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var edited = Copy(fx.Cfg);
        edited.BoxLabelsFile = Path.Combine(fx.Dir, "other-labels.json");

        await fx.Shell.ApplySettingsAsync(edited, new BoxLabels.LabelStyle(BoxLabels.LayoutHuge));

        Assert.Equal(BoxLabels.LayoutHuge, BoxLabelStore.Read(edited.BoxLabelsFile).Style.Layout);
    }

    [Fact]
    public async Task AStyleThatCannotBeWrittenIsReportedAndTheOtherSettingsStay()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var edited = Copy(fx.Cfg);
        edited.UppercaseNames = !fx.Cfg.UppercaseNames;
        // a damaged labels file: config.json still saves (it never rewrites an
        // existing labels file), but the style write fails at once
        File.WriteAllText(fx.Shell.BoxLabelsPath, "{ not json");

        await fx.Shell.ApplySettingsAsync(edited, new BoxLabels.LabelStyle(BoxLabels.LayoutBig));

        Assert.Contains("Label style not saved", Assert.Single(fx.Dialogs.Warnings).Message);
        Assert.Equal(edited.UppercaseNames, fx.Shell.Cfg.UppercaseNames);
    }
}
