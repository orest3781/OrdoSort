using System.Windows;
using System.Xml.Linq;

namespace OrdoSort.Wpf.Tests;

/// <summary>BoxLabels.exe merges exactly two dictionaries from OrdoSort.Ui,
/// and nothing else. If one of the shared windows reaches for a resource key
/// that only OrdoSort's App.xaml declares, nothing fails at build time — the
/// standalone throws at the moment that window is shown, on the machine of
/// the person it was given to.
///
/// So this walks every StaticResource key the three shared windows actually
/// use and proves the pair of dictionaries supplies all of them.</summary>
[Collection(HighlightContrastTests.Name)]
public class SharedUiResourcesResolveTests
{
    private readonly HighlightContrastFixture _fx;
    public SharedUiResourcesResolveTests(HighlightContrastFixture fx) => _fx = fx;

    private static string SharedWindowsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent!;
        var root = dir?.FullName ?? throw new InvalidOperationException("OrdoSort.sln not found");
        return Path.Combine(root, "src", "OrdoSort.Ui", "Windows");
    }

    /// <summary>Keys named in {StaticResource X} across the shared windows.
    /// Anything containing a brace is a nested/implicit reference such as
    /// {StaticResource {x:Type Window}}, which is resolved by type rather
    /// than by key and is not something a dictionary "contains".</summary>
    private static HashSet<string> KeysUsedBySharedWindows(out int windowsScanned)
    {
        var files = Directory.EnumerateFiles(SharedWindowsDir(), "*.xaml").ToList();
        windowsScanned = files.Count;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var raw in System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), @"\{StaticResource\s+([^}]+)\}")
                     .Select(m => m.Groups[1].Value.Trim()))
                if (!raw.Contains('{') && raw.Length > 0)
                    keys.Add(raw);
        return keys;
    }

    [Fact]
    public void EverySharedWindowResourceComesFromTheTwoSharedDictionaries() => _fx.Invoke(() =>
    {
        var keys = KeysUsedBySharedWindows(out var windowsScanned);

        // a scan that found nothing would pass no matter what was broken
        Assert.True(windowsScanned >= 3, $"only found {windowsScanned} shared windows");
        Assert.True(keys.Count >= 8, $"only found {keys.Count} resource keys");

        var styles = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/OrdoSort.Ui;component/Theme/Styles.xaml"),
        };
        var illustrations = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/OrdoSort.Ui;component/Theme/Illustrations.xaml"),
        };

        var missing = keys
            .Where(k => !styles.Contains(k) && !illustrations.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "BoxLabels.exe merges only Styles.xaml and Illustrations.xaml, so these keys "
            + "would be unresolved there and the window would throw when shown:\n"
            + string.Join("\n", missing));
    });
}
