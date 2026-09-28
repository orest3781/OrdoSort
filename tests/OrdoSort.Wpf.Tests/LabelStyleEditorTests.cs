using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The label style editor both Settings windows use (Box Labels'
/// Label style tab, OrdoSort's Box labels tab).</summary>
public class LabelStyleEditorTests
{
    [Fact]
    public void TheEditorStartsFromTheStoredStyleAndIsUnchanged()
    {
        var vm = new LabelStyleEditorViewModel(new BoxLabels.LabelStyle(BoxLabels.LayoutBig, false));

        Assert.True(vm.LayoutBig);
        Assert.False(vm.LeadingZeros);
        Assert.True(vm.DatesBars);
        Assert.False(vm.IsChanged);
    }

    [Fact]
    public void PickingHugeAndPlainDatesChangesTheStyleAndSaysSo()
    {
        var vm = new LabelStyleEditorViewModel(BoxLabels.LabelStyle.Default);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.LayoutHuge = true;
        vm.DatesPlain = true;

        Assert.Equal(new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, true, BoxLabels.DateStylePlain), vm.Style);
        Assert.True(vm.IsChanged);
        Assert.Contains(nameof(vm.Style), raised);   // the live preview redraws
    }

    [Fact]
    public void PickingTheOriginalStyleAgainIsNotAChange()
    {
        var vm = new LabelStyleEditorViewModel(BoxLabels.LabelStyle.Default);
        vm.LayoutBig = true;
        vm.LayoutStandard = true;

        Assert.False(vm.IsChanged);
    }
}
