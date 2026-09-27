using System.Runtime.CompilerServices;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs once when the test assembly loads, before any test, and keeps
/// the run hermetic (docs/testing.md): everything the app would write to the
/// real user's profile goes under one per-run temp folder instead, deleted
/// when the run ends. A running OrdoSort and the tests then never share a
/// browser profile, a table layout or a crash log.</summary>
internal static class TestAssemblySetup
{
    /// <summary>The per-run folder that stands in for the user's profile.</summary>
    internal static readonly string RunRoot =
        Path.Combine(Path.GetTempPath(), "ordotest_run_" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void RedirectProfilePaths()
    {
        Directory.CreateDirectory(RunRoot);
        TableLayoutStore.DefaultPath = Path.Combine(RunRoot, "layouts", "table-columns.json");
        WebViewPdfViewer.UserDataFolder = Path.Combine(RunRoot, "WebView2");
        App._crashDir = Path.Combine(RunRoot, "crash");
        Directory.CreateDirectory(App._crashDir);
        // Windows built by tests don't remember layouts at all, or one
        // test's saved File list would open the next test's File list.
        // ExplorerColumnsTests hand in their own store to test remembering.
        OrdoSort.Wpf.Views.ExplorerColumns.RememberByDefault = false;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDir.DeleteWithRetry(RunRoot);
    }
}

/// <summary>The redirect above is what keeps the run off the real profile;
/// these fail if a path slips back to it.</summary>
public class TestRunIsHermeticTests
{
    private static readonly string RealProfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrdoSort");

    [Fact]
    public void NothingTheAppWritesPointsAtTheRealProfile()
    {
        foreach (var path in new[] { TableLayoutStore.DefaultPath, WebViewPdfViewer.UserDataFolder, App._crashDir })
        {
            Assert.False(Path.GetFullPath(path).StartsWith(RealProfile, StringComparison.OrdinalIgnoreCase),
                $"{path} is under the real profile {RealProfile}");
        }
    }
}
