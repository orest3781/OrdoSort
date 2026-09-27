using System.Windows;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>Base class for every test on the shared UI thread
/// (<see cref="HighlightContrastTests.Name"/>). Those tests share one
/// <see cref="Application"/>, so a window, focus, theme or app resource one
/// test leaves behind used to change what the next test saw: the classic
/// "passes alone, fails in the full run" (docs/testing.md). The constructor
/// puts the shared thread back to a known state before each test:
/// no windows open, nothing focused or captured, the app resources as they
/// were when the fixture started, and the light theme with the real
/// High Contrast check.</summary>
public abstract class UiTest
{
    protected UiTest(HighlightContrastFixture fx)
    {
        Fx = fx;
        fx.Invoke(() => ResetUiState(fx));
    }

    /// <summary>The shared UI thread and its <see cref="Application"/>.</summary>
    protected HighlightContrastFixture Fx { get; }

    private static void ResetUiState(HighlightContrastFixture fx)
    {
        // A window a test forgot to close could hold focus or be the active
        // window. MainWindow cancels a close mid-session, so hide what stays.
        foreach (var window in fx.App.Windows.OfType<Window>().ToList())
        {
            window.Close();
            if (window.IsVisible) window.Hide();
        }
        Keyboard.ClearFocus();
        Mouse.Capture(null);

        var resources = fx.App.Resources;
        foreach (var key in resources.Keys.Cast<object>().ToList())
            if (!fx.BaselineResources.ContainsKey(key)) resources.Remove(key);
        foreach (var (key, value) in fx.BaselineResources)
            if (!resources.Contains(key) || !ReferenceEquals(resources[key], value)) resources[key] = value;

        ThemeManager.IsHighContrast = () => SystemParameters.HighContrast;
        ThemeManager.Apply(fx.App, dark: false);
    }
}
