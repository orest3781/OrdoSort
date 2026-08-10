using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Smoke.E2E;

/// <summary>The filing loop itself: three PDFs in an inbox, driven through the
/// REAL <see cref="MainWindow"/> under Edge's live PDF viewer — commit under
/// the viewer, set aside, undo, and the audit rows that fall out of it. It
/// proves the one thing no unit test can: that Edge releases the PDF file
/// handle, so the move actually succeeds.
///
/// <b>One routine, two callers.</b> <c>dotnet run --project
/// tools\OrdoSort.Smoke</c> with no arguments is the standalone smoke harness
/// this logic started life as, and <c>-- e2e routing</c> is the same loop
/// reported as a scenario. They differ in exactly four things, all of them
/// parameters here: where the fixture lives (<see cref="Prepare"/>), which
/// <see cref="IDialogService"/> answers the prompts
/// (<see cref="OpenWindow"/>), how a result is reported, and where progress is
/// traced (<see cref="Reporter"/>). Everything else — the config, the three
/// documents, every wait, every assertion — is this file, once. Two copies of
/// an assertion this expensive to run is two copies that drift.
///
/// <b>The reporting seam.</b> <see cref="Check"/> has
/// <c>ScenarioContext.Check</c>'s exact signature on purpose: the e2e scenario
/// passes <c>ctx.Check</c> straight through and gets every check, passing and
/// failing alike, into the evidence report; the standalone mode passes a
/// two-line adapter that keeps only the failures, which is all
/// <c>SmokeUi.RunSta</c>'s <c>List&lt;string&gt;</c> contract has ever carried.
/// Neither caller can see the other's shape.
///
/// <b>Waiting.</b> Every wait goes through <see cref="E2EPump.Until"/>, which
/// pumps a nested dispatcher frame. That is not a stylistic choice: WebView2
/// cannot initialize, load a document, or release its lock unless the message
/// loop keeps running, so a blocking wait on this thread is a wait that can
/// never come true. It is also why this file replaced the old harness's
/// <c>Dispatcher.Run()</c> + <c>Loaded</c>-handler shape — a nested frame
/// gives the standalone mode the same pumping the e2e runner already does,
/// which is what let the two collapse into one routine.</summary>
public static class RoutingLoop
{
    /// <summary>How the caller records what the loop found. Same signature as
    /// <c>ScenarioContext.Check</c>, deliberately — see the class doc.</summary>
    public delegate void Check(string what, bool ok, string? detail);

    /// <summary>Everything the loop needs from its caller that differs between
    /// the two modes: how to record a check, where the app's own warnings are
    /// collecting (neither RecordingDialogs nor ScriptedDialogs exposes them
    /// through IDialogService, and a filing failure's reason is the single
    /// most useful thing to print), and where — if anywhere — to trace
    /// progress.</summary>
    public sealed record Reporter(
        Check Check, Func<IReadOnlyList<string>> Warnings, Action<string>? Log = null);

    /// <summary>The folders, documents and config.json one run drives, all
    /// under the root it was prepared in.</summary>
    public sealed record Bed(string Inbox, string Dest, string Deferred, string CfgPath);

    // The three documents, in the order a filename_asc session presents them.
    private const string First = "20240101--1111111111.pdf";
    private const string Second = "20240102--2222222222.pdf";
    private const string Third = "20240103--3333333333.pdf";

    private const string Typed = "SMITH JOHN";

    /// <summary>What naming_mode=insert + uppercase_names + the route's
    /// _INVOICE suffix make of <see cref="First"/> when <see cref="Typed"/> is
    /// entered. Asserted by name rather than by "something appeared in the
    /// route folder": the loop is meant to prove the app's own naming, not
    /// merely that a file moved.</summary>
    private const string FiledName = "20240101-SMITH JOHN-1111111111_INVOICE.pdf";

    /// <summary>Per-step budget. WebView2's very first initialization on a
    /// cold machine is the slow one.</summary>
    private const int StepMs = 15_000;

    /// <summary>Backstop for the loop as a whole once it has gone
    /// asynchronous, so a wedged commit ends as a reported failure rather than
    /// a hung run.</summary>
    private const int LoopMs = 90_000;

    /// <summary>How long Edge is given to finish loading — and locking — the
    /// first document before the commit tries to move it out from under it.
    /// This is not a wait FOR anything: it is the loop deliberately making the
    /// move as hard as it can be, which is the entire point of the scenario.
    /// It pumps rather than sleeps, because the message loop is exactly what
    /// Edge needs in order to take that lock in the first place.</summary>
    private const int EdgeLockMs = 1_500;

    /// <summary>The same idea, shorter, before the set-aside.</summary>
    private const int SettleMs = 500;

    /// <summary>Lay the fixture down under <paramref name="root"/>: an inbox
    /// with three real PDFs, a destination route folder, a deferred folder,
    /// and a real config.json the app parses through its own Config.Load.
    ///
    /// The route carries append_suffix/suffix, which ConfigFixture's simpler
    /// route does not — <see cref="FiledName"/> depends on it — so this writes
    /// its own config rather than reusing that one.</summary>
    public static Bed Prepare(string root)
    {
        var inbox = Path.Combine(root, "inbox");
        var dest = Path.Combine(root, "invoices");
        var deferred = Path.Combine(root, "deferred");
        foreach (var d in new[] { inbox, dest, deferred }) Directory.CreateDirectory(d);

        MinimalPdf.Write(Path.Combine(inbox, First), "ALPHA ONE");
        MinimalPdf.Write(Path.Combine(inbox, Second), "BETA TWO");
        MinimalPdf.Write(Path.Combine(inbox, Third), "GAMMA THREE");

        var cfgPath = Path.Combine(root, "config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(new
        {
            inbox = inbox.Replace('\\', '/'),
            deferred = deferred.Replace('\\', '/'),
            history_db = "history.sqlite",
            naming_mode = "insert",
            sort = "filename_asc",
            uppercase_names = true,
            routes = new[]
            {
                new { label = "Invoices", path = dest.Replace('\\', '/'),
                      hotkey = "Ctrl+1", append_suffix = true, suffix = "_INVOICE" },
            },
        }));

        return new Bed(inbox, dest, deferred, cfgPath);
    }

    /// <summary>The real MainWindow over that bed, with the caller's dialog
    /// service in place of the modal one.
    ///
    /// The SynchronizationContext install is load-bearing, and it belongs here
    /// rather than in either caller because both need it for the same reason:
    /// MainWindow's constructor reads SynchronizationContext.Current for
    /// FolderWatchService AND for ShellViewModel's uiContext, and every await
    /// inside OnRouteAsync/OnSkipAsync/OnUndoAsync captures it as well. A bare
    /// STA thread has none — E2EHarnessTests.ABareHarnessThreadHasNo-
    /// SynchronizationContextUntilTheRunnerInstallsOne pins exactly that — so
    /// without this the shell's continuations would resume on thread-pool
    /// threads and touch bound ObservableCollections from off the UI thread.
    /// The e2e runner has already installed one by the time a scenario runs;
    /// installing an equivalent one over the same dispatcher again is a no-op
    /// in effect.</summary>
    public static MainWindow OpenWindow(Bed bed, IDialogService dialogs)
    {
        E2ERunner.InstallUiSynchronizationContext();
        var window = new MainWindow(Config.Load(bed.CfgPath), bed.CfgPath);
        window.Dialogs = dialogs;
        return window;
    }

    /// <summary>Drive the loop to completion, pumping the dispatcher the whole
    /// way. The window must already have been shown.</summary>
    public static void Run(MainWindow window, Bed bed, Reporter to)
    {
        // DriveAsync runs synchronously as far as its first genuine await
        // (the commit), pushing its own nested frames on the way; from there
        // on this outer frame is what pumps its continuations.
        var loop = DriveAsync(window, bed, to);
        if (!E2EPump.Until(() => loop.IsCompleted, LoopMs))
            to.Check("the routing loop ran to completion", false,
                $"still running after {LoopMs}ms");
        else if (loop.Exception is { } ex)
            to.Check("the routing loop ran to completion", false,
                ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
    }

    private static async Task DriveAsync(MainWindow window, Bed bed, Reporter to)
    {
        var shell = window.Shell;
        void Log(string m) => to.Log?.Invoke(m);

        // Every wait is recorded, not just the ones that time out: in the e2e
        // report a green "the first document reaches the viewer" line is the
        // evidence, and in the standalone mode the Check adapter drops it
        // again, which is exactly the old Wait's behaviour.
        bool Wait(Func<bool> cond, string what, Action? kickoff = null, int ms = StepMs)
        {
            Log("wait: " + what);
            var ok = E2EPump.Until(cond, ms, kickoff);
            Log((ok ? "  ok: " : "  TIMEOUT: ") + what);
            to.Check(what, ok, $"timed out after {ms}ms");
            return ok;
        }

        string Warned() =>
            to.Warnings() is { Count: > 0 } w ? " — the app warned: " + w[0] : " — no warning was raised";

        try
        {
            if (!Wait(() => window.Pdf.Ready || window.Pdf.InitError != null,
                    "the PDF viewer finishes starting up"))
                return;

            // The suite's ONE dependency on WebView2 being installed, named
            // rather than left to time out: a machine without the Evergreen
            // runtime must read the init error, not fifteen silent seconds
            // followed by a predicate nobody can interpret.
            if (window.Pdf.InitError is { } initError)
            {
                to.Check("Edge's PDF viewer started", false,
                    "WebView2 init: " + initError.Split('\n')[0]);
                return;
            }

            // Initialize() — the first scan — runs in the window's OWN Loaded
            // handler; wait for the Ready refresh it ends with, so a scan
            // landing late cannot undo StartProcessing.
            Wait(() => shell.CountLine.Length > 0, "the inbox is scanned");

            // StartProcessing goes through kickoff rather than being called
            // inline: it is fire-and-forget (`_ = StartProcessingAsync()`) and
            // its first await must capture a live dispatcher context — see
            // E2EPump.Until's own kickoff doc comment, and Screenshots.cs,
            // which drives this same window the same way.
            Wait(() => window.Pdf.CurrentUrl.Contains("1111111111", StringComparison.Ordinal),
                "the first document reaches the viewer", kickoff: shell.StartProcessing);

            Log($"beat: {EdgeLockMs}ms for Edge to take its lock on {First}");
            E2EPump.Until(() => false, EdgeLockMs);

            // ---------------------------------------------------------- commit
            shell.TypedName = Typed;
            await shell.OnRouteAsync(0);

            var filed = Path.Combine(bed.Dest, FiledName);
            to.Check("the document is filed under the name the app derived",
                File.Exists(filed), $"no {FiledName} in the route folder{Warned()}");
            to.Check("...and is gone from the inbox — Edge released the file handle",
                !File.Exists(Path.Combine(bed.Inbox, First)),
                $"{First} is still in the inbox{Warned()}");

            // ------------------------------------------------------- set aside
            Wait(() => window.Pdf.CurrentUrl.Contains("2222222222", StringComparison.Ordinal),
                "the second document reaches the viewer");
            E2EPump.Until(() => false, SettleMs);

            await shell.OnSkipAsync();
            to.Check("the set-aside document is in the deferred folder",
                File.Exists(Path.Combine(bed.Deferred, Second)),
                $"no {Second} in the deferred folder{Warned()}");

            // ------------------------------------------------------------ undo
            // OnUndo is fire-and-forget, so this one really does need a wait —
            // and it waits on the shell's OWN verdict (StatusLine, set by
            // OnUndoAsync right after the move) rather than on the file
            // reappearing. A File.Exists predicate here would be answered by
            // the disk without the window ever having learned anything, which
            // is the difference between driving the app and driving the disk.
            shell.OnUndo();
            Wait(() => shell.StatusLine.StartsWith("Undid ", StringComparison.Ordinal),
                "the window reports the undo");
            to.Check("undo put the document back in the inbox",
                File.Exists(Path.Combine(bed.Inbox, Second)),
                $"{Second} did not come back{Warned()}");

            // ----------------------------------------------------------- audit
            to.Check("both moves went into the audit log",
                shell.Session.RowIds.Count >= 2,
                $"{shell.Session.RowIds.Count} row(s) recorded, expected at least 2");

            to.Check("the app raised no warning anywhere in the loop",
                to.Warnings().Count == 0, string.Join(" | ", to.Warnings()));

            // The third document was never touched: a loop that quietly filed
            // or deferred more than it was asked to would otherwise pass.
            to.Check("the untouched third document is still in the inbox",
                File.Exists(Path.Combine(bed.Inbox, Third)), $"{Third} moved on its own");
        }
        catch (Exception ex)
        {
            to.Check("the routing loop ran without throwing", false,
                ex.GetType().Name + ": " + ex.Message);
        }
    }
}
