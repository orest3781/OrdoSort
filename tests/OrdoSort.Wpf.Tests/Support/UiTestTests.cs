using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

[Collection(HighlightContrastTests.Name)]
public class UiTestTests : UiTest
{
    public UiTestTests(HighlightContrastFixture fx) : base(fx) { }

    /// <summary>Stands in for the next test's class being built.</summary>
    private sealed class NextTest : UiTest
    {
        public NextTest(HighlightContrastFixture fx) : base(fx) { }
    }

    [Fact]
    public void ANewTestStartsWithNothingTheLastOneLeftBehind()
    {
        const string leakedKey = "ordotest.leaked";
        var baselineFontSize = Fx.BaselineResources["AppFontSize"];
        Fx.Invoke(() =>
        {
            var box = new TextBox();
            var window = new Window { Content = box, Left = -20000, ShowActivated = false };
            window.Show();
            box.Focus();
            ThemeManager.Apply(Fx.App, dark: true);
            ThemeManager.IsHighContrast = () => !SystemParameters.HighContrast;
            Fx.App.Resources[leakedKey] = 1;
            Fx.App.Resources["AppFontSize"] = 30.0;
        });

        _ = new NextTest(Fx);

        Fx.Invoke(() =>
        {
            Assert.Empty(Fx.App.Windows.OfType<Window>().Where(w => w.IsVisible));
            Assert.Null(Keyboard.FocusedElement);
            Assert.False(ThemeManager.IsDark);
            Assert.Equal(SystemParameters.HighContrast, ThemeManager.IsHighContrast());
            Assert.False(Fx.App.Resources.Contains(leakedKey));
            Assert.Equal(baselineFontSize, Fx.App.Resources["AppFontSize"]);
        });
    }

    [Fact]
    public void EveryClassOnTheSharedUiThreadDerivesFromUiTest()
    {
        var missing = typeof(UiTest).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(CollectionAttribute)
                && a.ConstructorArguments.Count == 1
                && Equals(a.ConstructorArguments[0].Value, HighlightContrastTests.Name)))
            .Where(t => !typeof(UiTest).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();
        Assert.True(missing.Count == 0,
            "these share the UI thread without the per-test reset: " + string.Join(", ", missing));
    }
}
