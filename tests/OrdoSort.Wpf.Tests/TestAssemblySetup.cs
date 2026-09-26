using System.Runtime.CompilerServices;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs once when the test assembly loads, before any test: every
/// window a test builds attaches ExplorerColumns, which saves its layout on
/// close, and that must never reach the real user's %LOCALAPPDATA%.</summary>
internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void RedirectTableLayouts() =>
        TableLayoutStore.DefaultPath = Path.Combine(Path.GetTempPath(),
            "ordo_test_layouts_" + Environment.ProcessId, "table-columns.json");
}
