// UI smoke harness: runs the REAL WPF MainWindow, waits for WebView2 to render
// the first PDF, then drives commit / set-aside / undo through the same view
// model the buttons bind to. Proves the one thing unit tests can't: that
// Edge's PDF viewer releases the file handle so the move actually succeeds.
//
// The loop itself lives in E2E/RoutingLoop.cs, because `-- e2e routing` runs
// the identical loop as a reported scenario. This file is only the standalone
// mode's half of it: a temp fixture, a RecordingDialogs, and a reporter that
// turns the loop's checks into the List<string> of failures SmokeUi.RunSta
// prints. See RoutingLoop's class doc for the split.
//
// WPF + WebView2 require an STA thread; a console app's implicit Main is MTA,
// so the whole UI runs on an explicit STA thread here.
//
// Exit 0 + "SMOKE PASS" on success; nonzero otherwise.

using System.Windows.Threading;
using OrdoSort.Smoke.E2E;

if (args.Length > 0 && args[0] == "dialogs") return DialogCheck.Run();
if (args.Length > 0 && args[0] == "screenshots") return Screenshots.Run(args);
if (args.Length > 0 && args[0] == "reentrancy") return Reentrancy.Run();
if (args.Length > 0 && args[0] == "demo-full") return DemoWorkbench.Run(args);
if (args.Length > 0 && args[0] == "sounds") return SoundSet.Run(args);
if (args.Length > 0 && args[0] == "e2e") return OrdoSort.Smoke.E2E.E2ERunner.Run(args);

// hard watchdog: never hang CI
_ = Task.Run(async () =>
{
    await Task.Delay(75_000);
    Console.WriteLine("SMOKE FAIL: 75s watchdog fired (UI never completed)");
    Environment.Exit(2);
});

return SmokeUi.RunSta(Drive,
    "SMOKE PASS — commit under live Edge viewer, set-aside, undo, history all OK",
    "SMOKE FAIL:");

static List<string> Drive()
{
    var failures = new List<string>();

    // The standalone mode's half of RoutingLoop.Reporter: SmokeUi.RunSta's
    // contract is a list of failure lines, so a passing check is dropped and a
    // failing one is flattened into the string it prints. The e2e scenario
    // hands the same loop ctx.Check instead and keeps both.
    void Check(string what, bool ok, string? detail)
    {
        if (!ok) failures.Add(detail is null ? what : $"{what} — {detail}");
    }

    void Log(string m) { Console.WriteLine($"[{Environment.TickCount64 % 100000,6}] {m}"); Console.Out.Flush(); }

    var bed = RoutingLoop.Prepare(
        Path.Combine(Path.GetTempPath(), "ordo_smoke_" + Guid.NewGuid().ToString("N")));

    SmokeUi.Boot();
    var dialogs = new RecordingDialogs();
    var window = RoutingLoop.OpenWindow(bed, dialogs);

    window.Show();
    try
    {
        RoutingLoop.Run(window, bed,
            new RoutingLoop.Reporter(Check, () => dialogs.Warnings, Log));
    }
    finally
    {
        Log("closing");
        window.Close();
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }
    return failures;
}
