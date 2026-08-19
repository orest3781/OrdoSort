using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using OrdoSort.Smoke.E2E.Scenarios;

namespace OrdoSort.Smoke.E2E;

/// <summary>The e2e mode: every surface, driven as real windows against real
/// files, with an evidence report and a CI exit code.
///
///   dotnet run --project tools\OrdoSort.Smoke -- e2e [surface] [--keep]
///
/// A surface argument (case-insensitive prefix) runs one surface — useful on
/// a machine without WebView2, where `e2e zip` still works. --keep leaves the
/// fixtures on disk for inspection.</summary>
public static class E2ERunner
{
    private static IReadOnlyList<Scenario> AllScenarios() =>
        ZipScenarios.All()
            .Concat(UnzipScenarios.All())
            .Concat(ZipMergeScenarios.All())
            .Concat(UnlockScenarios.All())
            .Concat(BulkRenameScenarios.All())
            .Concat(MatchMergeScenarios.All())
            .Concat(SmallToolScenarios.All())
            .Concat(HistoryScenarios.All())
            .Concat(RoutingScenarios.All())
            .ToList();

    public static int Run(string[] args)
    {
        var filter = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var keep = args.Contains("--keep");

        var scenarios = AllScenarios();
        if (filter is not null)
        {
            // Exact wins over prefix, so "zip" selects Zip alone rather than
            // dragging in "Zip merge" — otherwise the narrow filter that
            // exists to skip a broken surface would silently include it.
            var exact = scenarios.Where(s => Same(s.Surface, filter)).ToList();
            scenarios = exact.Count > 0
                ? exact
                : scenarios.Where(s => Matches(s.Surface, filter)).ToList();
        }

        if (scenarios.Count == 0)
        {
            Console.WriteLine($"E2E FAIL:\n  * no surface matches \"{filter}\"");
            return 1;
        }

        List<ScenarioResult> results = new();
        var outDir = Evidence.NewRunDirectory();
        var sw = Stopwatch.StartNew();

        var ui = new Thread(() => results = DriveAll(scenarios, outDir, keep));
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        sw.Stop();

        // Unreachable today — DriveAll always returns one ScenarioResult per
        // scenario, and Run already rejected an empty scenario list above —
        // but a green E2E job that ran zero scenarios is the single outcome
        // this suite must never produce silently, so it gets its own guard
        // rather than relying on that invariant holding forever.
        if (results.Count == 0)
        { Console.WriteLine("E2E FAIL:\n  * the run produced no results"); return 1; }

        Evidence.Write(outDir, results, sw.Elapsed);

        var failed = results.Where(r => !r.Passed).ToList();
        var surfaces = results.Select(r => r.Surface).Distinct().Count();
        var reportPath = Path.Combine(outDir, "report.html");

        if (failed.Count == 0)
        {
            Console.WriteLine($"E2E PASS — {results.Count} scenarios, {surfaces} surfaces");
            Console.WriteLine($"  evidence: {reportPath}");
            return 0;
        }

        Console.WriteLine("E2E FAIL:");
        foreach (var r in failed)
        {
            var why = r.Error
                ?? string.Join("; ", r.Assertions.Where(a => !a.Passed).Select(a => a.Description));
            Console.WriteLine($"  * [{r.Surface}] {r.Name} — {why}");
        }
        Console.WriteLine($"  evidence: {reportPath}");
        return 1;
    }

    private static bool Same(string surface, string filter) =>
        surface.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || surface.Replace(" ", "").Equals(filter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string surface, string filter) =>
        surface.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
        || surface.Replace(" ", "").StartsWith(filter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static List<ScenarioResult> DriveAll(
        IReadOnlyList<Scenario> scenarios, string outDir, bool keep)
    {
        var results = new List<ScenarioResult>();
        InstallUiSynchronizationContext();
        var app = SmokeUi.Boot();   // real App.xaml resources + theme, as in production

        // App.xaml declares ShutdownMode="OnMainWindowClose", and
        // InitializeComponent() (inside Boot()) re-applies that XAML value over
        // whatever Boot()'s own object initializer set — so it has to be set
        // again HERE, after Boot returns. WPF auto-assigns
        // Application.MainWindow to the first window Show()n, which is the
        // FIRST scenario's window; closing it at the end of that scenario
        // queues a whole-Application shutdown that fires the moment anything
        // pumps (the first Until whose predicate isn't already true) and takes
        // Application.Current out from under every scenario after it. Same trap,
        // same fix, as Screenshots.cs — a run that shows and closes many windows
        // in one process needs shutdown to stay opt-in.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Application's base constructor queues a ONE-TIME dispatcher operation
        // that runs the REAL App.OnStartup the first time this thread's
        // dispatcher pumps. Boot() itself never pumps — but E2EPump.Until does,
        // so left alone it fires partway through whichever scenario pumps first:
        // it parses (nonexistent) --config args, writes a default config.json
        // and history.sqlite beside the built exe, calls ThemeManager.Start(this,
        // "auto") — re-reading the real OS dark-mode preference over the theme
        // Boot just forced, changing what every later screenshot looks like —
        // and shows its own phantom MainWindow into Application.Current.Windows.
        // Flushing it once here means it has already fired, and cannot fire
        // again, before the first scenario is measured.
        FlushPendingAppStartup(app);

        foreach (var s in scenarios)
        {
            Console.WriteLine($"  {s.Surface}: {s.Name}");
            Console.Out.Flush();
            results.Add(DriveOne(s, outDir, keep));
        }

        Dispatcher.CurrentDispatcher.InvokeShutdown();
        return results;
    }

    /// <summary>Give this thread the SynchronizationContext a WPF UI thread
    /// carries in production.
    ///
    /// Every view model in this app takes a uiContext precisely so a result
    /// computed off-thread can Post itself back onto the UI thread
    /// (ZipListViewModel.ApplyOnUi: "a raw thread-pool continuation has no
    /// synchronization context of its own to inherit"). In the real app that
    /// context exists because Application.Run installs one. This runner never
    /// calls Application.Run or Dispatcher.Run — it drives the dispatcher with
    /// nested frames — and Dispatcher.PushFrame installs a
    /// DispatcherSynchronizationContext only for the duration of the frame it
    /// is running, restoring the previous one on the way out. Between two
    /// E2EPump.Until calls, which is exactly where a scenario constructs its
    /// view model, SynchronizationContext.Current is therefore null. Left
    /// alone, every scenario would hand its view model a null uiContext and
    /// quietly skip the very hop the seam exists for. Installing one here, once
    /// for the whole STA thread, gives the scenarios the production shape.
    ///
    /// Pinned by E2EHarnessTests.ABareHarnessThreadHasNoSynchronizationContext-
    /// UntilTheRunnerInstallsOne.</summary>
    public static void InstallUiSynchronizationContext() =>
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

    /// <summary>Let the queued one-time App.OnStartup run (and discard whatever
    /// window it puts up) while nothing is being measured. Duplicated from
    /// Screenshots.cs rather than shared: this task may not modify the existing
    /// smoke files, and the whole thing is four lines.</summary>
    private static void FlushPendingAppStartup(Application app)
    {
        E2EPump.Until(() => false, 1000);
        foreach (var w in app.Windows.OfType<Window>().ToList())
            try { w.Close(); } catch { /* best effort — it only exists to be discarded */ }
    }

    private static ScenarioResult DriveOne(Scenario s, string outDir, bool keep)
    {
        var stem = Slug(s.Surface) + "-" + Slug(s.Name);
        var sw = Stopwatch.StartNew();
        var fx = Fixture.Create(stem);
        var ctx = new ScenarioContext(fx, new ScriptedDialogs());
        string? error = null;
        string? shot = null;
        string? captureNote = null;

        // Recorded here, once, so all nine surface files inherit it rather than
        // remembering to assert it themselves. Scenarios pass
        // SynchronizationContext.Current as their view model's uiContext, and
        // ZipListViewModel.ApplyOnUi reads "if (UiContext is null) apply(…);
        // else UiContext.Post(…)". If this thread ever loses its context, every
        // scenario silently takes the inline branch: the result is applied
        // synchronously, the Settle wait finds Status already set and returns
        // without arming a frame, and the whole run goes green having never
        // exercised the marshalling hop — byte-identical output to a correct
        // run. Asserting it is the only thing that makes that visible.
        ctx.Check("the scenario thread carries a UI synchronization context",
            SynchronizationContext.Current is not null,
            "null — every view model gets a null uiContext and skips the marshalling hop");

        // What the scenario itself added starts from here, so the runner's own
        // bookkeeping assertions (above and below) cannot mask a scenario that
        // asserted nothing of its own.
        var beforeRun = ctx.Assertions.Count;

        try
        {
            s.Run(ctx);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            // Capture while the window is still open, then close everything so
            // the next scenario starts from a clean desktop.
            if (ctx.Captured is not null)
            {
                shot = Evidence.Capture(ctx.Captured, outDir, stem);
                // A window WAS nominated but rasterizing it still came back
                // empty (zero-size window, or the catch in Evidence.Capture)
                // — that is worth a visible note on the report row. It is
                // deliberately not a failed ctx.Check: a machine without a
                // desktop session shouldn't turn the whole run red on
                // rasterization alone, but a silently missing screenshot is
                // still worth less than one that says why it's missing.
                if (shot is null)
                    captureNote = "the scenario nominated a window, but no screenshot " +
                        "was captured (zero-size window, or Evidence.Capture threw)";
            }
            // Null-conditional on Current deliberately: if an Application
            // shutdown ever does slip through, this cleanup must degrade into a
            // reported scenario failure, not a NullReferenceException thrown
            // from a finally block that takes the whole run (and its evidence)
            // down with it.
            foreach (var w in Application.Current?.Windows.OfType<Window>().ToList() ?? new List<Window>())
                try { w.Close(); } catch { /* best effort */ }

            if (!keep) fx.Dispose();
        }

        // A scenario that asserted nothing passed vacuously; that is a defect
        // in the scenario, and silently green is exactly the failure mode this
        // whole suite exists to avoid. Decided BEFORE the runner adds any
        // further checks of its own, and against beforeRun rather than zero, so
        // it keeps judging the scenario.
        var assertedNothing = ctx.Assertions.Count == beforeRun;

        // A leftover dialog answer means the scenario never reached the prompt
        // it was written for — a broken scenario, reported as one.
        if (ctx.Dialogs.Unconsumed.Count > 0)
            ctx.Check("every queued dialog answer was used", false,
                "unused: " + string.Join(", ", ctx.Dialogs.Unconsumed));

        // Symmetric with the guard above, and for the same reason: a scenario
        // that never nominated a window yields a report row with no screenshot,
        // and an evidence report with silently missing evidence is worth less
        // than one that says so.
        if (ctx.Captured is null)
            ctx.Check("the scenario nominated a window as evidence", false,
                "no ctx.Capture(win) call — this row has no screenshot");

        if (assertedNothing && error is null) error = "scenario asserted nothing";

        sw.Stop();
        var passed = error is null && ctx.Assertions.Count > 0 && ctx.Assertions.All(a => a.Passed);

        return new ScenarioResult(s.Surface, s.Name, s.Kind, passed,
            ctx.Assertions.ToList(), error, shot, sw.ElapsedMilliseconds, captureNote);
    }

    private static string Slug(string s)
    {
        var raw = string.Concat(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (raw.Contains("--", StringComparison.Ordinal)) raw = raw.Replace("--", "-");
        return raw.Trim('-');
    }
}
