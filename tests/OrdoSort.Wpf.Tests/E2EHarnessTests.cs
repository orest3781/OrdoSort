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

    /// <summary>The hand-written xlsx must be readable by the app's OWN
    /// reader (SweptTable.Load → XlsxTable.Read). Without this, a malformed
    /// fixture would surface later as a Reports scenario finding zero rows —
    /// a product bug that isn't one.</summary>
    [Fact]
    public void XlsxFixtureRoundTripsThroughSweptTable()
    {
        using var fx = Fixture.Create("xlsx-check");
        var path = fx.Xlsx("report.xlsx",
            new[] { "Document", "Category" },
            new[] { new[] { "20240101--1111.pdf", "INVOICE" } });

        var table = OrdoSort.Core.SweptTable.Load(new[] { path });

        Assert.Contains("Document", table.Headers);
        Assert.Contains("Category", table.Headers);
        Assert.Single(table.Rows);
        Assert.Equal("INVOICE", table.Rows[0].Cells["Category"]);
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
}
