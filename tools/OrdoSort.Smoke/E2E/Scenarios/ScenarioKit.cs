using System.IO.Compression;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The waits and readers every surface shares.
///
/// These live here rather than as private helpers on one surface file for a
/// specific reason: <see cref="Settle"/> is not a convenience, it is the
/// mechanism that keeps the suite honest, and a rule each of nine files
/// re-invents is a rule that will drift.
///
/// The rule: a scenario must wait on the SURFACE's own verdict, never on the
/// filesystem. Every view model in this app hands its result back to the UI
/// thread through the uiContext seam, and the run only traverses that hop if
/// something pumps the dispatcher while the result is in flight. A filesystem
/// predicate — <c>File.Exists(target)</c>, <c>Archives(ctx).Length &gt; 0</c> —
/// is typically already true the instant the command returns, so
/// <see cref="E2EPump.Until"/> answers on its first evaluation without ever
/// arming a frame. The scenario then goes green having proved the archive was
/// written but never that the window learned about it, which is the difference
/// between driving the app and driving the disk.</summary>
public static class ScenarioKit
{
    /// <summary>Pump until the surface publishes its verdict, and record that it
    /// did. <paramref name="status"/> must read a UI-facing string — the
    /// property the result line binds to — not a filesystem probe; see the class
    /// summary for why that distinction is load-bearing. A warning counts as a
    /// verdict too: a surface that refuses the work has still reported.</summary>
    public static void Settle(ScenarioContext ctx, Func<string> status, int timeoutMs = 15000)
    {
        var settled = E2EPump.Until(
            () => status().Length > 0 || ctx.Dialogs.Warnings.Count > 0, timeoutMs);
        ctx.Check("the window reported a result", settled,
            $"no status line and no warning within {timeoutMs}ms");
    }

    /// <summary>Wait for an intake call (AddPaths/AddFiles/…) and record that it
    /// finished. Callers otherwise discard the Task, so intake that never
    /// completes surfaces as a puzzling timeout several assertions downstream
    /// instead of naming itself.</summary>
    public static void Added(ScenarioContext ctx, Task intake, int timeoutMs = 8000)
    {
        ctx.Check("the sources finished loading",
            E2EPump.Until(() => intake.IsCompleted, timeoutMs),
            $"intake had not completed within {timeoutMs}ms");
    }

    /// <summary>Entry names from an archive, sorted, and never throwing: an
    /// archive that cannot be opened has to surface as a failed assertion
    /// carrying the reason, not as an exception that ends the scenario — the
    /// same discipline as ScenarioContext's own I/O helpers. Shared because
    /// Unzip and Zip merge read archives back for the same reason Zip does.</summary>
    public static IReadOnlyList<string> EntriesOf(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Select(e => e.FullName)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            return new[] { $"<unreadable: {ex.GetType().Name}: {ex.Message}>" };
        }
    }
}
