using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>DW-89: the Reformat list window greys out the controls that do
/// nothing for the chosen shape, through IsEnabled bindings to
/// <see cref="ListReformatViewModel.SpaceAfterApplies"/> and
/// <see cref="ListReformatViewModel.IsCustomDelimiter"/>. The view model tests
/// cover those properties, but a typo in a binding path fails silently and
/// leaves the control always enabled; only the real window shows that.</summary>
[Collection(HighlightContrastTests.Name)]
public class ListReformatWindowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public ListReformatWindowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    [Theory]
    [InlineData(ListReformat.OutputShape.CommaLine, true, false)]
    [InlineData(ListReformat.OutputShape.OnePerLine, false, false)]
    [InlineData(ListReformat.OutputShape.CustomDelimiter, true, true)]
    public void OnlyTheControlsThatApplyToTheShapeAreEnabled(
        ListReformat.OutputShape shape, bool spaceAfterEnabled, bool delimiterEnabled) => _fx.Invoke(() =>
    {
        var vm = new ListReformatViewModel();
        var win = new ListReformatWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        try
        {
            win.Show();
            // Set after Show so the bindings must follow a change, not just
            // read the starting value.
            vm.Shape = shape;
            win.UpdateLayout();
            PumpRender();

            var spaceAfter = FindAllDescendants<CheckBox>(win)
                .Single(c => (string)c.Content == "Space after separator");
            var delimiter = FindAllDescendants<TextBox>(win)
                .Single(t => AutomationProperties.GetName(t) == "Text between items");

            Assert.Equal(spaceAfterEnabled, spaceAfter.IsEnabled);
            Assert.Equal(delimiterEnabled, delimiter.IsEnabled);
        }
        finally { win.Close(); }
    });
}
