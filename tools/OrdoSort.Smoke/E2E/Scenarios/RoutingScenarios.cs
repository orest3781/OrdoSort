using System.Windows;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>The filing loop — the app's actual job, and the only surface in
/// the suite that needs WebView2.
///
/// <b>What this proves that nothing else can.</b> Every other scenario drives
/// a tool window over files nobody else has open. This one commits a document
/// while Edge's PDF viewer is displaying it, which is the situation the whole
/// <c>IPdfViewer.ReleaseAsync</c> seam exists for: if Edge does not let go of
/// the file handle, the move fails and the document stays in the inbox. No
/// unit test can reach that — there is no Edge in a unit test — so it is
/// asserted here or nowhere.
///
/// <b>Where the logic lives.</b> Not in this file. <see cref="RoutingLoop"/>
/// is the single copy, shared with the standalone smoke harness that
/// <c>dotnet run --project tools\OrdoSort.Smoke</c> (no arguments) still runs;
/// see its class doc for the four things the two callers differ in. This file
/// is the scenario's half: an isolated fixture, ScriptedDialogs, ctx.Check as
/// the reporter, and the two bits of window handling only an offscreen,
/// many-windows-per-process run needs (see <see cref="Park"/> and the Closing
/// override below).
///
/// <b>Marshalling.</b> Unlike every tool window in this suite, MainWindow does
/// not own a DebouncedProbe: ShellViewModel takes a uiContext but posts
/// through it only from FolderWatchService's poll callback
/// (ShellViewModel.cs:517,528). The filing loop's own state — CountLine,
/// CurrentFilename, StatusLine, Screen — is assigned inside
/// StartProcessingAsync / OnRouteAsync / OnSkipAsync / OnUndoAsync, i.e. in
/// each method's continuation after its `await _scheduler.Run(...)` hop onto
/// the thread pool (ShellViewModel.cs:1137, 1228, 1274, 1311). Those are real
/// TaskWorkScheduler hops — MainWindow constructs ShellViewModel with the
/// production scheduler and there is no seam here to swap it for an
/// InlineScheduler — so every wait below genuinely needs the dispatcher pumped
/// and genuinely can fail. The one wait that is NOT on a view-model property
/// is the WebView2 readiness pair (Pdf.Ready / Pdf.InitError), which is the
/// browser's own state and the only honest thing to wait on there.</summary>
public static class RoutingScenarios
{
    private const string Surface = "Routing loop";

    public static IReadOnlyList<Scenario> All() => new[]
    {
        new Scenario(Surface, "commit, set aside and undo under the live viewer", "clean", Drive),
    };

    private static void Drive(ScenarioContext ctx)
    {
        var bed = RoutingLoop.Prepare(ctx.Fx.Root);
        var window = RoutingLoop.OpenWindow(bed, ctx.Dialogs);

        // MainWindow moves itself: EnterCompact parks the dashboard in the
        // work area's top-right corner, and EnterNormal (MainWindow.cs:283-295)
        // re-positions it again the moment the session starts. E2EPump's
        // one-shot Left = -20000 does not survive that the way it does for the
        // tool windows, so re-park on every move — the run must not throw a
        // full-size window across the user's desktop halfway through.
        window.LocationChanged += (_, _) => Park(window);

        // The runner's cleanup closes every window once. MainWindow.Closing
        // turns a close during a live session into StopSession and cancels the
        // close instead (MainWindow.cs:139-148), which would leave this window
        // — and its WebView2, and its open handle on the fixture's
        // history.sqlite — alive for the rest of the process. Registered after
        // MainWindow's own handler, so this runs second and un-cancels it:
        // this is teardown, not a user pressing X, which is the same
        // distinction MainWindow's own _reallyExit flag draws for File > Exit.
        // It changes nothing the scenario asserts — every check below has
        // already been recorded by the time anything closes.
        window.Closing += (_, e) => e.Cancel = false;

        E2EPump.ShowOffscreen(window);
        Park(window);

        // Evidence is nominated before the loop runs, so a loop that throws
        // still leaves a screenshot behind rather than an empty report row.
        ctx.Capture(window);

        RoutingLoop.Run(window, bed,
            new RoutingLoop.Reporter(ctx.Check, () => ctx.Dialogs.Warnings));

        ctx.Check("the window is still up at the end of the loop", window.IsLoaded,
            "MainWindow went away mid-run");
    }

    private static void Park(Window win)
    {
        if (win.Left > -10_000) { win.Left = -20_000; win.Top = 0; }
    }
}
