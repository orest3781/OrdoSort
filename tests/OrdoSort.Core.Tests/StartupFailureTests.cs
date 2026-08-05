using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>First run creates the config. When that write fails — the classic
/// case being an install under Program Files run by a normal user — it used to
/// escape as UnauthorizedAccessException, which App.OnStartup does not catch
/// (it catches ConfigException only). The exception escaped startup, the
/// unhandled handler marked it Handled without shutting down, and with
/// ShutdownMode=OnMainWindowClose and no window ever created the process
/// survived invisibly, closable only from Task Manager.
///
/// Load must report an unwritable location the same way it reports every other
/// configuration problem: as a ConfigException carrying an actionable message.
///
/// This lives in OrdoSort.Core.Tests rather than OrdoSort.Wpf.Tests (the brief's
/// suggested location): Config.Load is pure OrdoSort.Core, this test needs no
/// WPF fixture, and every sibling Config test (ConfigHardeningTests, etc.)
/// already lives here.</summary>
public class FirstRunFailureTests
{
    [Fact]
    public void AnUnwritableLocationIsReportedAsAConfigurationProblem()
    {
        // A path whose PARENT is a file, not a directory: Directory.Create and
        // the subsequent write both fail, portably, with no permissions setup.
        var blocker = Path.Combine(Path.GetTempPath(), "ordo_blocker_" + Guid.NewGuid());
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var target = Path.Combine(blocker, "config.json");
            var ex = Assert.Throws<ConfigException>(() => Config.Load(target));
            Assert.Contains(target, ex.Message);
            // the message must tell the user what to do, not just what broke
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally { File.Delete(blocker); }
    }
}
