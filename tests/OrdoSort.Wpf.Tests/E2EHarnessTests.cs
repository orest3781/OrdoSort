using System.Windows.Threading;
using OrdoSort.Smoke.E2E;

namespace OrdoSort.Wpf.Tests;

/// <summary>Unit coverage for the E2E harness's own moving parts — the pump,
/// the fixture builder, the scripted dialogs, and the evidence writer. The
/// scenarios themselves can only run under the STA harness; these are the
/// pieces that must be right before any scenario can be trusted.</summary>
public class E2EHarnessTests
{
    /// <summary>A condition already true must not arm a frame at all — the
    /// common case, and the one where an unnecessary DispatcherFrame would
    /// hang a caller that has no message loop running.</summary>
    [Fact]
    public void UntilReturnsImmediatelyWhenConditionAlreadyTrue()
    {
        Assert.True(E2EPump.Until(() => true, timeoutMs: 50));
    }

    /// <summary>A condition that never comes true reports false rather than
    /// throwing: one stuck scenario must not abort the run.</summary>
    [Fact]
    public void UntilReturnsFalseOnTimeoutWithoutThrowing()
    {
        Assert.False(E2EPump.Until(() => false, timeoutMs: 150));
    }

    /// <summary>The condition flips from a dispatcher callback, which is the
    /// real shape: DebouncedProbe marshals its result back through uiContext,
    /// so Until only observes it if it is genuinely pumping the queue.</summary>
    [Fact]
    public void UntilObservesAConditionSetFromADispatcherCallback()
    {
        var flipped = false;
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        t.Tick += (_, _) => { flipped = true; t.Stop(); };
        t.Start();

        Assert.True(E2EPump.Until(() => flipped, timeoutMs: 3000));
    }

    /// <summary>Regression coverage for the bug hunted down while verifying
    /// Task 1's extraction: <paramref name="kickoff"/> must be queued
    /// through the dispatcher (BeginInvoke), not invoked inline before the
    /// pump starts. PushFrame installs a DispatcherSynchronizationContext
    /// only for the frame it's running; invoke kickoff before that frame is
    /// live and an `await` inside it captures whatever context is ambient
    /// AT THAT CALL (typically null on a bare thread), so its continuation
    /// resumes off a bare thread-pool thread instead — exactly what crashed
    /// the real StartProcessing/OnRouteAsync kickoffs in Screenshots.cs the
    /// moment they touched a bound ObservableCollection.
    ///
    /// A plain "did the continuation eventually run" flag does not catch
    /// this: Task.Yield's continuation completes either way, just via a
    /// different thread, so the flag flips true regardless. Asserting the
    /// *type* of SynchronizationContext.Current the continuation resumed on
    /// is what actually discriminates: null under the inline-invoke bug, a
    /// live DispatcherSynchronizationContext once queued correctly.</summary>
    [Fact]
    public void UntilQueuesKickoffSoItsAwaitResumesOnTheLiveDispatcherContext()
    {
        SynchronizationContext? resumedOn = null;
        var ready = false;

        E2EPump.Until(() => ready, timeoutMs: 3000, kickoff: async () =>
        {
            await Task.Yield();
            resumedOn = SynchronizationContext.Current;
            ready = true;
        });

        Assert.IsType<DispatcherSynchronizationContext>(resumedOn);
    }

    /// <summary>The whole isolation guarantee in one assertion: everything a
    /// fixture makes lives under its own root, and the root is gone after
    /// disposal. A scenario that writes outside this is a bug in the
    /// scenario.</summary>
    [Fact]
    public void FixtureCreatesUnderItsOwnRootAndCleansUp()
    {
        string root;
        using (var fx = Fixture.Create("iso-check"))
        {
            root = fx.Root;
            var pdf = fx.Pdf("inbox/one.pdf", "ALPHA");
            Assert.StartsWith(root, pdf, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(pdf));
        }
        Assert.False(Directory.Exists(root));
    }

    /// <summary>An encrypted fixture must actually be encrypted — otherwise
    /// every Unlock scenario silently proves nothing. Reading it back
    /// without the password must fail.</summary>
    [Fact]
    public void EncryptedPdfIsActuallyEncrypted()
    {
        using var fx = Fixture.Create("enc-check");
        var path = fx.EncryptedPdf("locked.pdf", "right-one");

        var probe = OrdoSort.Core.Unlock.ProbeReadiness(path, Array.Empty<string>());
        Assert.Equal("needs_password", probe.Status);
    }

    /// <summary>RawZip must write entry names verbatim, including a
    /// traversal name — if it sanitises them, the zip-slip scenario tests
    /// nothing.</summary>
    [Fact]
    public void RawZipPreservesATraversalEntryNameVerbatim()
    {
        using var fx = Fixture.Create("slip-check");
        var zip = fx.RawZip("evil.zip", (@"..\..\escaped.txt", new byte[] { 1, 2, 3 }));

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName.Contains("..", StringComparison.Ordinal));
    }

    /// <summary>Queued answers come back in order — a scenario that queues
    /// two save paths is describing two saves, and getting them swapped
    /// would file evidence under the wrong name.</summary>
    [Fact]
    public void ScriptedDialogsReturnQueuedAnswersInOrder()
    {
        var d = new ScriptedDialogs().QueueSaveFile("first.zip", "second.zip");

        Assert.Equal("first.zip", d.AskSaveFile("*.zip", "x"));
        Assert.Equal("second.zip", d.AskSaveFile("*.zip", "x"));
    }

    /// <summary>An empty queue answers null — "the user cancelled" — rather
    /// than throwing, because cancellation is a real path several scenarios
    /// exercise deliberately.</summary>
    [Fact]
    public void ScriptedDialogsAnswerNullWhenTheQueueIsEmpty()
    {
        Assert.Null(new ScriptedDialogs().AskSaveFile("*.zip", "x"));
    }

    /// <summary>A leftover answer means the scenario never took the path it
    /// claimed to — that is a broken scenario, and the runner must be able
    /// to see it.</summary>
    [Fact]
    public void ScriptedDialogsReportUnconsumedAnswers()
    {
        var d = new ScriptedDialogs().QueueSaveFile("never-used.zip");

        Assert.Contains("AskSaveFile (1)", d.Unconsumed);
    }

    /// <summary>A recorded failure must not throw — the runner needs every
    /// assertion in a scenario, not just the ones before the first break,
    /// and a partial report is exactly what you want when something breaks.</summary>
    [Fact]
    public void ContextRecordsFailuresWithoutThrowing()
    {
        using var fx = Fixture.Create("ctx-check");
        var ctx = new ScenarioContext(fx, new ScriptedDialogs());

        ctx.Check("this one holds", true);
        ctx.Check("this one does not", false);
        ctx.Check("and this one still runs", true);

        Assert.Equal(3, ctx.Assertions.Count);
        Assert.Single(ctx.Assertions.Where(a => !a.Passed));
    }

    /// <summary>BytesUnchanged is the never-overwrite guarantee's assertion —
    /// it must actually compare content, not just existence.</summary>
    [Fact]
    public void BytesUnchangedDetectsAModifiedFile()
    {
        using var fx = Fixture.Create("bytes-check");
        var path = fx.Text("original.txt", "before");
        var before = File.ReadAllBytes(path);
        File.WriteAllText(path, "after");

        var ctx = new ScenarioContext(fx, new ScriptedDialogs());
        ctx.BytesUnchanged(path, before, "original survives");

        Assert.False(ctx.Assertions.Single().Passed);
    }

    /// <summary>NothingNewOutside must not be fooled by a sibling whose name
    /// merely shares allowedDir's name as a string prefix — a stray
    /// "...\evil-extra.txt" is not under "...\evil". A bare
    /// StartsWith(allowedDir) would treat it as "inside" and hide exactly
    /// the zip-slip escape this assertion exists to catch, which is the
    /// worst direction for this check to be wrong in. This is a committed
    /// regression test for that fix (Scenario.cs's private IsUnder
    /// helper).</summary>
    [Fact]
    public void NothingNewOutsideCatchesASiblingSharingTheAllowedDirNamePrefix()
    {
        using var fx = Fixture.Create("prefix-check");
        var ctx = new ScenarioContext(fx, new ScriptedDialogs());
        var allowedDir = fx.Dir("evil");

        var before = ctx.Snapshot();

        // A sibling of allowedDir, not a descendant of it — its name just
        // happens to start with "evil" too.
        File.WriteAllText(Path.Combine(fx.Root, "evil-extra.txt"), "escaped");

        ctx.NothingNewOutside(allowedDir, before, "nothing escaped outside allowedDir");

        Assert.False(ctx.Assertions.Single().Passed);
    }

    private static ScenarioResult Result(string surface, string name, string kind, bool passed) =>
        new(surface, name, kind, passed,
            new[] { new Assertion("a check", passed, passed ? null : "it did not hold") },
            passed ? null : "boom", ScreenshotFile: null, ElapsedMs: 12);

    /// <summary>The report must be self-contained and must show failures —
    /// a report that renders a red run as green is worse than no report.</summary>
    [Fact]
    public void ReportHtmlIsSelfContainedAndShowsBothOutcomes()
    {
        using var fx = Fixture.Create("evidence-check");
        var outDir = fx.Dir("out");

        Evidence.Write(outDir, new[]
        {
            Result("Zip", "files and folders", "clean", passed: true),
            Result("Unzip", "zip slip", "awkward", passed: false),
        }, TimeSpan.FromSeconds(3));

        var html = File.ReadAllText(Path.Combine(outDir, "report.html"));

        Assert.Contains("files and folders", html);
        Assert.Contains("zip slip", html);
        Assert.Contains("it did not hold", html);
        // Self-contained: no external fetches of any kind.
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src", html);
    }

    /// <summary>Regression coverage for the attribute-injection defect found
    /// while implementing Evidence.Html: the img alt="" attribute is a
    /// double-quoted HTML attribute context, not a text node, so escaping
    /// only &amp;/&lt;/&gt; (as the original Esc did) is not enough — an
    /// unescaped `"` in a scenario Name closes the attribute early and lets
    /// anything after it land on the &lt;img&gt; tag as a live attribute of
    /// its own. This is the only committed test that reaches that branch:
    /// both ReportHtmlIsSelfContainedAndShowsBothOutcomes and
    /// ReportMarkdownCarriesEveryScenario go through Result(), which always
    /// passes ScreenshotFile: null, so neither exercises the &lt;img&gt;
    /// line at all.</summary>
    [Fact]
    public void ReportHtmlEscapesQuotesInScreenshotAltAttribute()
    {
        using var fx = Fixture.Create("evidence-quote-check");
        var outDir = fx.Dir("out");

        // A minimal real PNG (1x1, decoded from base64) so Evidence.Write's
        // File.Exists(png) guard lets the <img> branch run.
        File.WriteAllBytes(Path.Combine(outDir, "shot.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        var evilName = "evidence\" onmouseover=\"alert(1)";
        Evidence.Write(outDir, new[]
        {
            new ScenarioResult("Zip", evilName, "clean", true,
                new[] { new Assertion("a check", true) }, null, "shot.png", ElapsedMs: 12),
        }, TimeSpan.FromSeconds(1));

        var html = File.ReadAllText(Path.Combine(outDir, "report.html"));

        // The alt attribute must stay well-formed: the embedded quote comes
        // through as the &quot; entity, not a literal " that closes the
        // attribute early.
        Assert.Contains("<img alt=\"evidence&quot; onmouseover=&quot;alert(1)\"", html);
        // And onmouseover must never appear as a live, executable attribute
        // on the tag.
        Assert.DoesNotContain("onmouseover=\"alert(1)\"", html);
    }

    /// <summary>report.md carries the same verdicts, for pasting into a PR.</summary>
    [Fact]
    public void ReportMarkdownCarriesEveryScenario()
    {
        using var fx = Fixture.Create("evidence-md-check");
        var outDir = fx.Dir("out");

        Evidence.Write(outDir, new[]
        {
            Result("Zip", "files and folders", "clean", passed: true),
            Result("Unzip", "zip slip", "awkward", passed: false),
        }, TimeSpan.FromSeconds(3));

        var md = File.ReadAllText(Path.Combine(outDir, "report.md"));

        Assert.Contains("Zip", md);
        Assert.Contains("Unzip", md);
        Assert.Contains("1 failed", md);
    }

    /// <summary>Regression coverage for a defect in Task 6's own runner: it
    /// booted the STA thread and ran scenarios without ever installing a
    /// SynchronizationContext, so every scenario handed its view model
    /// <c>SynchronizationContext.Current</c> — which was null.
    ///
    /// That matters because the uiContext seam is exactly what
    /// ZipListViewModel.ApplyOnUi (and the rest) uses to get an off-thread
    /// result back onto the UI thread: "a raw thread-pool continuation has no
    /// synchronization context of its own to inherit". A null uiContext makes
    /// ApplyOnUi take its other branch and apply
    /// inline, so the whole marshalling hop the seam exists for goes
    /// unexercised — silently, with every scenario still green. That is the
    /// mock-theatre failure this suite is supposed to prevent.
    ///
    /// The first assertion is the load-bearing one, and the reason the install
    /// is needed at all: a bare STA thread does NOT get a context for free.
    /// WPF only installs one inside Application.Run/Dispatcher.Run, neither of
    /// which the runner calls. The third asserts the install has to be made for
    /// the THREAD rather than relied on from a pump: Dispatcher.PushFrame
    /// installs its own context only for the frame it is running and restores
    /// the previous one on the way out, so a context that came from a pump is
    /// gone again by the time the next scenario constructs a view model.</summary>
    [Fact]
    public void ABareHarnessThreadHasNoSynchronizationContextUntilTheRunnerInstallsOne()
    {
        SynchronizationContext? beforeInstall = null;
        SynchronizationContext? afterInstall = null;
        SynchronizationContext? afterAPumpHasComeAndGone = null;

        var t = new Thread(() =>
        {
            beforeInstall = SynchronizationContext.Current;
            E2ERunner.InstallUiSynchronizationContext();
            afterInstall = SynchronizationContext.Current;
            E2EPump.Until(() => false, timeoutMs: 50);
            afterAPumpHasComeAndGone = SynchronizationContext.Current;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.Null(beforeInstall);
        Assert.IsType<DispatcherSynchronizationContext>(afterInstall);
        Assert.IsType<DispatcherSynchronizationContext>(afterAPumpHasComeAndGone);
    }

    /// <summary>Regression coverage for a defect found in Task 13, in the
    /// PRE-EXISTING standalone smoke harness rather than in any new code.
    ///
    /// The old Program.cs built its MainWindow on a bare STA thread, before
    /// <c>Dispatcher.Run()</c>. By the test above, such a thread has no
    /// SynchronizationContext — so <c>MainWindow</c>'s constructor read
    /// <c>SynchronizationContext.Current</c> as <b>null</b> and handed that
    /// null on to both <c>FolderWatchService</c> and
    /// <c>ShellViewModel</c>'s uiContext (MainWindow.cs:54-57). Measured
    /// directly by printing it from the unmodified harness before the fix:
    /// <c>SynchronizationContext.Current at MainWindow construction =
    /// &lt;null&gt;</c>.
    ///
    /// ShellViewModel branches on that field once per timer callback
    /// (ShellViewModel.cs:80-99): <c>FlashTick</c>, <c>HideLastAction</c>,
    /// <c>HideToast</c> and <c>ExpireStatusNote</c> all read
    /// "if (_uiContext is null) DoItHere(); else _uiContext.Post(...)", and
    /// they are driven by timers, not by the dispatcher. With a null context
    /// the smoke harness ran every one of them straight off a timer thread,
    /// mutating state the window's bindings were reading — and the
    /// auto-hide behind HideLastAction is armed by the very commit and
    /// set-aside the harness exists to exercise, as ExpireStatusNote's is by
    /// the undo beside them. It never crashed only because the timers outlast
    /// the 25-second run.
    ///
    /// The fix is that RoutingLoop.OpenWindow installs the context BEFORE it
    /// constructs anything, so both callers — the standalone smoke mode and
    /// the e2e scenario — get the production shape.
    ///
    /// This pins the ORDERING, not merely the end state, and it does so
    /// without needing App.xaml's resources on the test's own thread. A
    /// MainWindow built where no Application has been booted throws out of
    /// InitializeComponent ("Cannot find resource named 'BoolToVis'"), which
    /// is exactly what makes the assertion discriminating: the context has to
    /// be installed on the way IN, because an install written after the
    /// `new MainWindow(...)` line — or deleted — never runs at all, and this
    /// thread's context is still null when OpenWindow gives up. The window
    /// itself is beside the point here; what is being pinned is what the
    /// constructor would have read.</summary>
    [Fact]
    public void RoutingLoopInstallsTheUiContextBeforeItBuildsTheWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "ordo_rl_" + Guid.NewGuid().ToString("N"));
        SynchronizationContext? beforeOpen = null;
        SynchronizationContext? afterOpen = null;

        var t = new Thread(() =>
        {
            beforeOpen = SynchronizationContext.Current;
            var bed = RoutingLoop.Prepare(root);
            // Whether the window itself comes up depends on an Application
            // having been booted on this thread, which is not this test's
            // subject — the install has to have happened either way.
            try { RoutingLoop.OpenWindow(bed, new ScriptedDialogs()); }
            catch { /* see the doc comment */ }
            afterOpen = SynchronizationContext.Current;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        try
        {
            // The premise: without the install, this thread has nothing to
            // give MainWindow's constructor.
            Assert.Null(beforeOpen);
            // THE ASSERTION: the reverted build leaves this null.
            Assert.IsType<DispatcherSynchronizationContext>(afterOpen);
        }
        finally
        {
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(root, true); break; } catch { Thread.Sleep(50); }
            }
        }
    }
}
