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
/// between driving the app and driving the disk.
///
/// <see cref="Added"/> used to be the same bug in a different disguise: it
/// waited on a Task's own IsCompleted instead of on the filesystem, but for
/// ZipListViewModel.AddPaths — the single intake method both zip tabs now
/// share, where there used to be one per tool — the only await in the method
/// body is `await Scheduler.Run(...)`, and every scenario constructs those view
/// models with InlineScheduler, whose Run() hands back an already-completed
/// Task.FromResult. The intake Task was therefore IsCompleted before Added
/// was ever called, <see cref="E2EPump.Until"/>'s own pre-pump fast path
/// (`if (kickoff is null && ready()) return true`) answered on the spot, and
/// the assertion built on that could not fail no matter what AddPaths
/// actually did — a passing check that proved nothing, at 7 of its 10 call
/// sites. That form is gone: those 7 sites now assert the row count the
/// intake was supposed to produce, right where the result is needed anyway,
/// which is a check that genuinely fails if the rows never land. <see
/// cref="Added"/> itself survives only for the 3 call sites in
/// UnlockScenarios.cs, where UnlockViewModel's own lack of a scheduler seam
/// makes intake.IsCompleted a real, failable condition — see that method's
/// doc comment.</summary>
public static class ScenarioKit
{
    /// <summary>Pump until the surface publishes its verdict, and record that it
    /// did. <paramref name="status"/> must read a UI-facing string — the
    /// property the result line binds to — not a filesystem probe; see the class
    /// summary for why that distinction is load-bearing. A warning counts as a
    /// verdict too: a surface that refuses the work has still reported.
    ///
    /// Only call this where <paramref name="status"/> is actually assigned
    /// from INSIDE the uiContext.Post marshalling hop, not where the calling
    /// thread assigns it directly and returns. It used to be called at 7
    /// sites where it could never fail (5 in BulkRenameScenarios.cs, 2 in
    /// MatchMergeScenarios.cs): BulkRenameViewModel.ApplyAsync/UndoBatchAsync
    /// (whose scheduler hops collapse to nothing under the InlineScheduler
    /// those scenarios inject — see BulkRenameScenarios' class doc) and
    /// MatchMergeViewModel's DoMerge/UndoBatch all set Status on the calling
    /// thread, with no Post anywhere in the method — so
    /// `status()` already satisfied the predicate before E2EPump.Until ever
    /// ran, and its pre-pump fast path (`if (kickoff is null && ready())
    /// return true`) answered on the spot. The recorded assertion could not
    /// fail no matter what Apply/UndoBatch/DoMerge actually did — the same
    /// false-green-line defect <see cref="Added"/>'s doc comment describes,
    /// just aimed at a view-model property instead of a Task. Those 7 sites
    /// now assert the outcome string Apply/UndoBatch/DoMerge actually
    /// produced (e.g. `vm.Status.StartsWith("Renamed", …)`), which is a check
    /// that genuinely fails if the operation didn't do what it claims. Do not
    /// reintroduce Settle at a site whose verdict property is assigned
    /// outside of a uiContext.Post callback: check the resulting outcome
    /// string directly instead, the way those 7 sites do now.</summary>
    public static void Settle(ScenarioContext ctx, Func<string> status, int timeoutMs = 15000)
    {
        var settled = E2EPump.Until(
            () => status().Length > 0 || ctx.Dialogs.Warnings.Count > 0, timeoutMs);
        ctx.Check("the window reported a result", settled,
            $"no status line and no warning within {timeoutMs}ms");
    }

    /// <summary>Wait for an intake call (AddFilesAsync) to cross a genuine
    /// thread-pool hop, and record that it did.
    ///
    /// Only call this where the intake can really still be running at the
    /// moment it's checked. Today that is the 3 call sites in
    /// UnlockScenarios.cs — the one view model in this suite with no
    /// IWorkScheduler seam at all: UnlockViewModel.AddFilesAsync awaits a raw
    /// Task.Run, so there is no InlineScheduler to collapse it to synchronous
    /// completion, and intake.IsCompleted genuinely can read false on the
    /// first check.
    ///
    /// It used to be called at 7 other sites too (5 in ZipScenarios.cs, 1
    /// each in UnzipScenarios.cs and ZipMergeScenarios.cs) where it could
    /// never fail — see the class doc comment above for why, and for what
    /// replaced it there. Do not reintroduce it at a site whose view model
    /// takes an IWorkScheduler and is constructed with InlineScheduler: check
    /// the resulting row count directly instead, the way those 7 sites do
    /// now.</summary>
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
