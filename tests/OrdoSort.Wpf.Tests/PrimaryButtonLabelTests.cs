using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>A PrimaryButton's fill is the accent colour, so its text must be
/// the accent text colour — which the button style alone can't give plain
/// Content="OK": the app's implicit TextBlock style pins Theme.Text, so the
/// label needs PrimaryButtonLabel. Box Labels' Settings shipped its OK button
/// that way (found in a render, 2026-09-28): dark on dark in the light theme,
/// light on light in the dark. This sweeps every registered window.</summary>
[Collection(HighlightContrastTests.Name)]
public class PrimaryButtonLabelTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public PrimaryButtonLabelTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    public static TheoryData<string> Windows()
    {
        var data = new TheoryData<string>();
        foreach (var name in WindowOverflowTests.Registry().Keys) data.Add(name);
        return data;
    }

    [Theory, MemberData(nameof(Windows))]
    public void EveryPrimaryButtonLabelUsesTheAccentTextColour(string windowName) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var probe = WindowOverflowTests.Registry()[windowName];
        var (window, cleanup) = probe.Build();
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            Settle(window);
            var primary = _fx.App.FindResource("PrimaryButton");
            var accentText = ((SolidColorBrush)_fx.App.FindResource("Theme.AccentText")).Color;

            var wrong = Descendants<Button>(window)
                .Where(b => ReferenceEquals(b.Style, primary) && b.IsVisible)
                .SelectMany(b => Descendants<TextBlock>(b)
                    .Where(t => t.Text.Length > 0 && t.Foreground is SolidColorBrush s && s.Color != accentText)
                    .Select(t => $"\"{t.Text}\""))
                .ToList();

            Assert.True(wrong.Count == 0,
                $"{windowName}: primary button text not in the accent text colour: {string.Join(", ", wrong)}");
        }
        finally
        {
            window.Close();
            cleanup?.Invoke();
        }
    });
}
