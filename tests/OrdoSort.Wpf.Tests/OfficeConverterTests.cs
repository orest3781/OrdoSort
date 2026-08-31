using System.Diagnostics;
using System.Runtime.InteropServices;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Wpf.Tests;

/// <summary>OfficeConverter drives real Word/Excel/PowerPoint over COM, so
/// every fact here except the disposal-related always-running ones needs
/// Office actually installed to mean anything. SkippableFact is not
/// referenced anywhere in this repo and no new packages are allowed, so the
/// skip route taken is an early <c>return</c> guarded by a registry lookup
/// (<see cref="WordInstalled"/> / <see cref="ExcelInstalled"/> /
/// <see cref="PowerPointInstalled"/>, computed once, never touching COM) --
/// with a comment at each site saying why.
///
/// Every Office-touching fact is wrapped in <see cref="WithTimeout"/>: the
/// failure this whole feature exists to prevent is a HANG (a modal dialog on
/// a hidden window), and a hanging fact wedges the entire test run rather
/// than failing it -- exactly the case a hard per-fact timeout exists to
/// turn into a loud, readable failure instead.
///
/// Fixtures (<see cref="OfficeFixtures"/>) are built by Office itself, once
/// for the whole class via <c>IClassFixture</c> (constructed once, disposed
/// once after every fact has run -- never per-fact, since even one cold
/// start costs ~0.5-0.8s per Task 1's own measurements), under a fresh GUID
/// temp folder deleted in Dispose.
///
/// Review round 1 found two real defects in this file itself, not just in
/// the production class.
///
/// The first: the fact then named
/// <c>ABorrowedWordInstanceSurvivesAfterTheConverterFinishes</c> declared
/// the converter as <c>using var</c> INSIDE its own try block, with the
/// survival assertion as the last statement in that scope -- so the
/// implicit <c>Dispose()</c> ran AFTER the assertion, meaning the fact
/// checked the user's process before Dispose() had done anything to it at
/// all. Deleting the production guard it was meant to prove did not fail
/// it. Every fact below that checks a process's state after disposal now
/// closes the converter's scope explicitly, first.
///
/// The second, found while fixing the first: that same fact's whole premise
/// -- "Word is single-instance COM, so a second CreateInstance call is
/// forced to borrow the first" -- turned out to be true for PowerPoint
/// (proven by Task 1's own spike) but NOT verified for Word, and a direct
/// probe here showed it is actually FALSE for Word on this machine: two
/// successive <c>Activator.CreateInstance</c> calls for
/// <c>"Word.Application"</c> produced two SEPARATE processes, not one
/// shared instance (same for Excel). That made the Word-based "borrowed
/// instance survives" fact pass VACUOUSLY -- the converter's own,
/// separately-started Word instance never touched the "pretend user's" one
/// at all, regardless of whether the borrow-vs-start logic was even
/// correct -- exactly the "already-true-predicate test trap" this repo's
/// own history warns about. Every fact that needs to force a genuinely
/// BORROWED session end-to-end now uses PowerPoint instead, the one app
/// this technique can reliably provoke that state in.</summary>
public sealed class OfficeConverterTests : IClassFixture<OfficeConverterTests.OfficeFixtures>
{
    internal static readonly bool WordInstalled = Type.GetTypeFromProgID("Word.Application") is not null;
    internal static readonly bool ExcelInstalled = Type.GetTypeFromProgID("Excel.Application") is not null;
    internal static readonly bool PowerPointInstalled = Type.GetTypeFromProgID("PowerPoint.Application") is not null;

    private readonly OfficeFixtures _fx;
    public OfficeConverterTests(OfficeFixtures fx) => _fx = fx;

    internal static void WithTimeout(TimeSpan limit, Action body)
    {
        var task = Task.Run(body);
        Assert.True(task.Wait(limit),
            $"timed out after {limit.TotalSeconds}s -- a modal Office dialog is the likely cause");
        if (task.IsFaulted) throw task.Exception!.InnerException!;
    }

    private static int PageCountOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>New PIDs of <paramref name="processName"/> since
    /// <paramref name="before"/>, with every <see cref="Process"/> handle
    /// enumerated along the way disposed -- the same "Process objects were
    /// never disposed, at every site including the fixtures" finding review
    /// round 1 raised, fixed here too, not just in the fixture builder and
    /// production code.</summary>
    private static HashSet<int> NewPidsSince(HashSet<int> before, string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var newIds = processes.Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        foreach (var p in processes) p.Dispose();
        return newIds;
    }

    /// <summary>Hard-kills a "pretend user" Office process this TEST itself
    /// created, by exact PID -- not the app's own Quit(), which review's own
    /// debugging found unreliable here: PowerPoint in particular can take
    /// 20-30s to actually exit after Quit() (Task 1's own measurement), so a
    /// fact whose cleanup only called Quit() could leave the process alive
    /// long enough for the NEXT PowerPoint-touching fact to silently attach
    /// to it instead of starting fresh -- corrupting that fact's own
    /// started/borrowed signal (found by exactly this happening, twice,
    /// while writing these facts: two facts failed in a row, each because
    /// the previous one's "cleanup" had not actually finished). A
    /// deliberate exception to "kill by PID only through the shared
    /// production helper": that rule protects a REAL user's session from
    /// OfficeConverter's own kill path; this kills a process the TEST
    /// created for itself, on purpose, so whatever runs next gets a
    /// genuinely clean slate.
    ///
    /// Takes <paramref name="app"/> as <c>object</c>, not <c>dynamic</c> --
    /// measured directly (debugging a revert-proof run) that passing an
    /// ALREADY-SEPARATED COM reference as a dynamic-typed argument throws
    /// InvalidComObjectException during the DLR's own call-site binding,
    /// before this method's try/catch ever gets a chance to run: C# treats
    /// a call as dynamically bound whenever ANY argument's compile-time
    /// type is dynamic, regardless of the callee's own parameter type, so
    /// the caller must convert away from dynamic before calling this (a
    /// plain cast is enough -- dynamic IS object at runtime, so the
    /// conversion itself touches nothing COM-related and cannot throw).</summary>
    private static void KillPretendUserProcess(object app, int? pid)
    {
        try { Marshal.FinalReleaseComObject(app); } catch { /* best effort */ }
        if (pid is not int p) return;
        try
        {
            using var process = Process.GetProcessById(p);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch { /* best effort -- already gone is fine too */ }
    }

    [Fact]
    public void IsAvailableReportsABoolWithoutTouchingOffice()
    {
        // The ONE fact in this file that is NOT behind an Office-installed
        // guard: Type.GetTypeFromProgID is a registry lookup, never a COM
        // activation, so this can never hang and never needs Office present.
        // Without this, a machine with no Office at all would run zero tests
        // from this class, which is exactly the "empty class" a skip guard
        // must never produce.
        using var converter = new OfficeConverter();
        Assert.IsType<bool>(converter.IsAvailable(MergeTypes.Word));
        Assert.IsType<bool>(converter.IsAvailable(MergeTypes.Excel));
        Assert.IsType<bool>(converter.IsAvailable(MergeTypes.PowerPoint));
        Assert.False(converter.IsAvailable("not-a-real-group"));
    }

    [Fact]
    public void ToPdfAfterDisposeThrowsObjectDisposedException()
    {
        // Also Office-independent: a converter that never called ToPdf
        // before disposing never started any session (_word/_excel/
        // _powerPoint are all still null), so Dispose() never touches COM
        // at all here. Reusing a disposed converter is a caller-contract
        // violation, not a document-conversion failure -- it must throw
        // rather than silently start a fresh, never-tracked instance.
        var converter = new OfficeConverter();
        converter.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            converter.ToPdf([1, 2, 3], "whatever.docx", Array.Empty<string>(), null));
    }

    [Fact]
    public void ExactlyOneNewPidIsTreatedAsStarted()
    {
        // Pure decision function, no Office needed -- see the two-PID fact
        // below for the actually load-bearing case this pairs with.
        var (started, pid) = OfficeConverter.DecideStartedOrBorrowed([111]);
        Assert.True(started);
        Assert.Equal(111, pid);
    }

    [Fact]
    public void ZeroNewPidsIsTreatedAsBorrowed()
    {
        var (started, pid) = OfficeConverter.DecideStartedOrBorrowed([]);
        Assert.False(started);
        Assert.Null(pid);
    }

    [Fact]
    public void TwoNewPidsInTheDiffWindowIsTreatedAsBorrowedNotStarted()
    {
        // CRITICAL 2's fix, isolated: a genuine race -- another WINWORD
        // process starting in the exact same before/after window as this
        // class's own CreateInstance call (a user double-clicking a .docx
        // mid-merge, say) -- must never be resolved by guessing which PID
        // is "ours". Guessing wrong risks Quit()-ing and force-killing a
        // third party's process; refusing to guess at all just leaks an
        // orphan of our own, which is the strictly cheaper failure.
        var (started, pid) = OfficeConverter.DecideStartedOrBorrowed([111, 222]);
        Assert.False(started);
        Assert.Null(pid);
    }

    [Fact]
    public void HandlesRecognizesTheOfficeDocumentExtensions()
    {
        if (!(WordInstalled && ExcelInstalled && PowerPointInstalled)) return; // needs all three registered to prove anything
        using var converter = new OfficeConverter();
        Assert.True(converter.Handles("docx"));
        Assert.True(converter.Handles("doc"));
        Assert.True(converter.Handles("xlsx"));
        Assert.True(converter.Handles("xls"));
        Assert.True(converter.Handles("pptx"));
        Assert.False(converter.Handles("pdf"));
        Assert.False(converter.Handles("png"));
    }

    [Fact]
    public void HandlesExcludesLegacyPptBecauseNoSafePasswordPathExistsForIt()
    {
        if (!PowerPointInstalled) return; // Office not installed on this machine
        using var converter = new OfficeConverter();
        Assert.False(converter.Handles("ppt"),
            "legacy .ppt is deliberately excluded -- no password parameter exists to open one safely, and its OLE2 container gives no byte-level signal the way pptx's ZIP-vs-CFBF split does");
    }

    [Fact]
    public void HandlesExcludesCsvAndTsvBecauseTableToPdfAlreadyHandlesThemWithoutOffice()
    {
        if (!ExcelInstalled) return; // Office not installed on this machine
        using var converter = new OfficeConverter();
        Assert.False(converter.Handles("csv"),
            "TableToPdf already converts csv deterministically without Office, and Task 8's chain puts this converter first");
        Assert.False(converter.Handles("tsv"),
            "TableToPdf already converts tsv deterministically without Office, and Task 8's chain puts this converter first");
    }

    [Fact]
    public void ARealDocxConvertsToPages()
    {
        if (!WordInstalled) return; // Office not installed on this machine -- SkippableFact isn't available (see class doc), early return instead
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.PlainDocxPath), "plain.docx", Array.Empty<string>(), null);
            Assert.Equal("ok", result.Status);
            Assert.True(PageCountOf(result.Pdf!) >= 1);
        });
    }

    [Fact]
    public void ARealPptxConverts()
    {
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null);
            Assert.Equal("ok", result.Status);
            Assert.True(PageCountOf(result.Pdf!) >= 2, "the fixture deck has two slides");
        });
    }

    [Fact]
    public void AProtectedWordDocumentNeedsAPasswordRatherThanHanging()
    {
        if (!WordInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.LockedDocxPath), "locked.docx", Array.Empty<string>(), null);
            Assert.Equal("needs_password", result.Status);
        });
    }

    [Fact]
    public void TheRightPasswordOpensTheProtectedWordDocument()
    {
        if (!WordInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.LockedDocxPath), "locked.docx",
                [OfficeFixtures.Password], null);
            Assert.Equal("ok", result.Status);
        });
    }

    [Fact]
    public void AProtectedWorkbookNeedsAPasswordRatherThanHanging()
    {
        // Word's wrong-password HRESULT (0x800A1520) and Excel's
        // (0x800A03EC) are different numbers -- this fact exercises Excel's
        // OWN catch clause, which the Word-only fact above cannot. This
        // workbook is genuinely CFBF-encrypted (built via SaveAs with a
        // real Password), so it still lands as WrongPassword under the
        // narrowed catch filter -- see ACorruptNonEncryptedWorkbook... below
        // for the case that must NOT.
        if (!ExcelInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.LockedXlsxPath), "locked.xlsx", Array.Empty<string>(), null);
            Assert.Equal("needs_password", result.Status);
        });
    }

    [Fact]
    public void TheRightPasswordOpensTheProtectedWorkbook()
    {
        if (!ExcelInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.LockedXlsxPath), "locked.xlsx",
                [OfficeFixtures.Password], null);
            Assert.Equal("ok", result.Status);
        });
    }

    [Fact]
    public void ACorruptNonEncryptedWorkbookIsUnreadableNotAWrongPasswordPrompt()
    {
        // Important-correctness fix: Excel's 0x800A03EC is a catch-all
        // runtime error, not a dedicated wrong-password code (the original
        // brief mandated that mapping unconditionally, and it was wrong).
        // Plain ZIP/OOXML signature (so IsCfbfEncrypted correctly reports
        // "not encrypted"), but not a real workbook at all -- must come back
        // unreadable and must never reach the prompt, which PasswordTry's
        // own contract forbids for exactly this reason ("asking again would
        // be a lie").
        if (!ExcelInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var asked = false;
            byte[] corrupt = "PK\x03\x04not a real workbook at all -- just garbage bytes after a plausible-looking zip header"u8.ToArray();
            var result = converter.ToPdf(corrupt, "corrupt.xlsx", ["some-saved-password"],
                _ => { asked = true; return "typed-password"; });
            Assert.Equal("error", result.Status);
            Assert.False(asked, "a corrupt (non-encrypted) workbook must not prompt for a password that could never help");
        });
    }

    [Fact]
    public void EveryWorksheetOfATwoSheetWorkbookIsIncluded()
    {
        if (!ExcelInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            using var converter = new OfficeConverter();
            var result = converter.ToPdf(File.ReadAllBytes(_fx.BookXlsxPath), "book.xlsx", Array.Empty<string>(), null);
            Assert.Equal("ok", result.Status);
            // Proxy for "both sheets survived", the same technique Task 1's
            // spike fell back to: opening the merged PDF back up in Word to
            // read its own page count HUNG in that spike, so a real,
            // dependency-free PDF reader's own page count is the safe
            // substitute -- a single surviving sheet could not produce two
            // pages of fitted, single-column content.
            Assert.True(PageCountOf(result.Pdf!) >= 2,
                "both worksheets should have produced at least one page each");
        });
    }

    [Fact]
    public void ALockedPptxIsRefusedSafelyWithoutEverCallingOffice()
    {
        // This class's own fourth-hazard mitigation, found beyond the brief:
        // PowerPoint's Presentations.Open has no password parameter at all,
        // so a protected pptx is refused by a byte-level pre-check BEFORE any
        // COM call -- never by trying and catching, because there is nothing
        // to catch a hang with. A synthetic OLE2/CFBF header proves the check
        // engages without needing a real encrypted deck, and runs near-
        // instantly since it never touches COM at all. Still gated on
        // PowerPoint being installed: Handles() itself requires that before
        // a .pptx ever reaches this path.
        //
        // Status is "error", not "needs_password": no password this class
        // could ever be given would let PowerPoint open it, so a status
        // that invites a retry would be dishonest -- the message has to
        // name the real limitation instead.
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(10), () =>
        {
            using var converter = new OfficeConverter();
            byte[] fakeEncryptedPptx = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
            var result = converter.ToPdf(fakeEncryptedPptx, "fake.pptx", Array.Empty<string>(), null);
            Assert.Equal("error", result.Status);
            Assert.Contains("password", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PowerPoint", result.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void NoOfficeProcessSurvivesAfterDisposal()
    {
        if (!(WordInstalled || ExcelInstalled || PowerPointInstalled)) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            var beforeWord = OfficeConverter.SnapshotPids("WINWORD");
            var beforeExcel = OfficeConverter.SnapshotPids("EXCEL");
            var beforePowerPoint = OfficeConverter.SnapshotPids("POWERPNT");

            using (var converter = new OfficeConverter())
            {
                if (WordInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.PlainDocxPath), "plain.docx", Array.Empty<string>(), null).Status);
                if (ExcelInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.BookXlsxPath), "book.xlsx", Array.Empty<string>(), null).Status);
                if (PowerPointInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null).Status);
            } // Dispose runs here -- this closing brace is what the fact actually proves

            Assert.Empty(NewPidsSince(beforeWord, "WINWORD"));
            Assert.Empty(NewPidsSince(beforeExcel, "EXCEL"));
            Assert.Empty(NewPidsSince(beforePowerPoint, "POWERPNT"));
        });
    }

    [Fact]
    public void ABorrowedPowerPointInstanceSurvivesAfterTheConverterFinishes()
    {
        // Hazard 2, tested end-to-end rather than via the fallback decision-
        // function route the brief allows -- PowerPoint specifically, not
        // Word. Measured directly while fixing this fact: Word's own
        // Activator.CreateInstance does NOT reuse an existing instance on
        // this machine -- a second call spawns its own, separate process
        // (confirmed with a throwaway PowerShell probe: two successive
        // `New-Object -ComObject Word.Application` calls produced two
        // distinct WINWORD PIDs). A Word-based version of this fact would
        // therefore pass VACUOUSLY: the converter's own separate instance
        // would never touch the "user's" one regardless of whether the
        // borrow logic is even correct -- exactly the shape of bug this
        // repo's own history calls "the already-true-predicate test trap".
        // PowerPoint IS confirmed single-instance, both by Task 1's own
        // spike and by the same direct probe, which is exactly what makes
        // this simulation trustworthy: the converter-under-test's own
        // CreateInstance call has nowhere else to attach.
        //
        // A second, subtler correction made while debugging THIS fact's own
        // revert-proof: an earlier version checked whether userPowerPoint's
        // own COM reference was still usable after Dispose(), reasoning
        // that FinalReleaseComObject on a borrowed session would invalidate
        // a shared RCW. Measured directly (a throwaway PowerShell probe:
        // two independent references to the SAME running PowerPoint, Quit()
        // called via one) that this reasoning was WRONG on two counts --
        // two independent Activator.CreateInstance calls do NOT share one
        // RCW even for a single-instance server (each gets its own proxy to
        // the same remote object), and more importantly, Quit() called via
        // ONE reference does not actually terminate the process, or even
        // affect the OTHER reference's usability, while ANY other client
        // (this test's own userPowerPoint included) still holds a live
        // connection -- the probe showed the process staying alive, and the
        // other reference's properties staying perfectly readable, for as
        // long as that second reference existed. The process only exits
        // once EVERY reference is gone. So the fact below deliberately
        // releases its OWN reference to userPowerPoint before checking
        // anything: that is what makes "did the converter call Quit() on a
        // session it does not own" and "did it correctly never touch it"
        // actually distinguishable, rather than both reading as "still
        // alive" regardless.
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            var before = OfficeConverter.SnapshotPids("POWERPNT");
            dynamic userPowerPoint = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            int? userPid = null;
            try
            {
                // The sanity check lives INSIDE this try, not before it --
                // review's own bug hunt found the earlier shape (assert
                // between creation and the try) orphaned userPowerPoint
                // whenever the assert itself failed, since the try/finally
                // that would have cleaned it up was never entered. That
                // orphan then corrupted the NEXT PowerPoint-touching fact's
                // own "before" baseline (single-instance COM means it
                // would attach to the leftover instead of starting fresh),
                // cascading one missed cleanup into two failing facts.
                var afterUserStarts = NewPidsSince(before, "POWERPNT");
                Assert.Single(afterUserStarts); // exactly one new POWERPNT from our own "pretend user" activation
                userPid = afterUserStarts.Single();

                var converter = new OfficeConverter();
                try
                {
                    var result = converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null);
                    Assert.Equal("ok", result.Status);
                }
                finally
                {
                    // Explicit, and BEFORE the assertions below -- not a
                    // `using var` whose implicit Dispose would run only at
                    // the end of this try block, AFTER an assertion had
                    // already read a state Dispose() had not yet touched.
                    // That exact ordering bug (found in review, on this
                    // fact's original Word-based version) let it pass even
                    // with hazard 2's own guard deleted.
                    converter.Dispose();
                }

                // Hazard 2's whole point, checked only now that Dispose()
                // has run: release THIS test's own reference first (see the
                // fact's own doc comment above for why that is load-bearing
                // for the check itself), then look for the process. A
                // session the converter correctly never touched sits there
                // indefinitely with zero active clients -- nothing ever
                // asked it to close. A session the converter erroneously
                // Quit() closes promptly once the last reference (this
                // test's own, just released) is gone.
                try { Marshal.FinalReleaseComObject(userPowerPoint); } catch { /* best effort */ }
                // A GC pass is what actually tears down the underlying RCP
                // proxy and notifies the server side -- without this,
                // FinalReleaseComObject alone was measured to leave the
                // process appearing to survive well past 5s even in the
                // erroneously-Quit() case, matching the direct PowerShell
                // probe that informed this fact's design (which also
                // needed an explicit Collect + WaitForPendingFinalizers
                // before the process actually exited).
                GC.Collect();
                GC.WaitForPendingFinalizers();

                using var userProcess = Process.GetProcessById(userPid.Value);
                Assert.False(userProcess.WaitForExit(5000),
                    "the converter must never Quit() an instance it borrowed rather than started");

                // And it must not have quietly STARTED a wholly separate
                // instance instead of truly borrowing this one.
                Assert.Equal(new HashSet<int> { userPid.Value }, NewPidsSince(before, "POWERPNT"));
            }
            finally
            {
                // Tearing down the pretend "user" session is explicitly NOT
                // the converter's job -- that is exactly what this fact
                // proves by doing it here instead. Hard-killed by PID --
                // see KillPretendUserProcess's own doc comment for why
                // relying on Quit() alone left a still-alive PowerPoint for
                // the NEXT fact to silently (mis)attach to. The COM
                // reference itself was already released above as part of
                // the fact's own check; this is the process-level safety
                // net regardless of outcome.
                KillPretendUserProcess((object)userPowerPoint, userPid);
            }
        });
    }

    [Fact]
    public void BorrowedPowerPointAlertsAreRestoredAfterTheConverterFinishes()
    {
        // PowerPoint, not Word -- same single-instance reasoning as the
        // fact above. PowerPoint only has ONE flag this class ever changes
        // (DisplayAlerts; Visible is deliberately left alone -- measured:
        // refused outright), so this proves a narrower slice of
        // RestoreFlagsIfBorrowed's logic than Word/Excel's three-flag
        // AppFlags struct would, but it exercises the identical shared
        // restoration mechanism (the same "session.Started" gate, the same
        // write-the-property-back-then-verify logic) via the one app this
        // technique can reliably force into a genuinely borrowed state.
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            var before = OfficeConverter.SnapshotPids("POWERPNT");
            dynamic userPowerPoint = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            int? userPid = null;
            try
            {
                userPid = NewPidsSince(before, "POWERPNT").Single();

                // Deliberately set AWAY from what the converter itself sets
                // (DisplayAlerts=1/ppAlertsNone) -- setting it to the
                // OPPOSITE of the converter's own target value first is
                // what makes a restoration failure actually observable.
                // PpAlertLevel only accepts its two named members (measured
                // directly: 0 and -1 both throw "not a valid enumeration
                // value"), so 2 (ppAlertsAll) is the only "opposite" there is.
                userPowerPoint.DisplayAlerts = 2; // ppAlertsAll

                var converter = new OfficeConverter();
                try
                {
                    var result = converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null);
                    Assert.Equal("ok", result.Status);
                }
                finally
                {
                    converter.Dispose();
                }

                Assert.Equal(2, (int)userPowerPoint.DisplayAlerts);
                Assert.Empty(converter.RestorationWarnings);
            }
            finally
            {
                // Hard-killed by PID, not Quit() -- see
                // KillPretendUserProcess's own doc comment for why Quit()
                // alone left a still-alive PowerPoint for the NEXT
                // PowerPoint-touching fact to silently (mis)attach to.
                KillPretendUserProcess((object)userPowerPoint, userPid);
            }
        });
    }

    [Fact]
    public void ARestorationFailureIsRecordedNotSwallowed()
    {
        // "Three empty catches" was review round 1's finding: if restoring
        // a borrowed session's flags fails, the user's own Office
        // application is left hidden/muted with no signal anywhere.
        // PowerPoint, not Word, for the same single-instance reasoning as
        // the two facts above -- simulating the user's session vanishing
        // WHILE this class still held it borrowed is a real failure mode
        // (a crash, a forced logoff), not hypothetical, and is exactly
        // what proves a failure now surfaces via RestorationWarnings
        // instead of vanishing silently.
        //
        // Killing the underlying process directly, not Quit(): measured
        // directly (on this fact's original Word-based version) that
        // calling Quit() on the pretend "user" instance does not reliably
        // fail a SUBSEQUENT property set against the same RCW soon enough
        // to provoke this deterministically -- Quit() does not sever the
        // COM connection instantly, so a set issued immediately afterward
        // can still succeed. Killing the process does sever it
        // unambiguously, and unlike waiting for a graceful Quit(), Kill()
        // is immediate regardless of how slowly the app would otherwise
        // exit on its own. This is a deliberate exception to "kill by PID
        // only through the shared production helper" -- that rule
        // protects a real user's session from OfficeConverter's own kill
        // path; here the test is killing a process IT created ITSELF, on
        // purpose, to simulate the crash this fact needs to prove against.
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            var before = OfficeConverter.SnapshotPids("POWERPNT");
            dynamic userPowerPoint = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            var converter = new OfficeConverter();
            try
            {
                // userPid's lookup lives INSIDE this try, not before it --
                // the same "orphans the pretend-user instance if the setup
                // step itself fails" bug the other PowerPoint-borrow facts
                // had (see their own comments) applied here too.
                var userPid = NewPidsSince(before, "POWERPNT").Single();
                var result = converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null);
                Assert.Equal("ok", result.Status);

                using var userProcess = Process.GetProcessById(userPid);
                userProcess.Kill();
                userProcess.WaitForExit(5000);
            }
            finally
            {
                converter.Dispose();
                try { Marshal.FinalReleaseComObject(userPowerPoint); } catch { /* best effort -- the process is already gone */ }
            }

            Assert.NotEmpty(converter.RestorationWarnings);
        });
    }

    /// <summary>Builds every fixture ONCE for the whole class, via Office
    /// itself, under its own GUID temp folder deleted in Dispose. Each
    /// Build* method claims its own Office process via
    /// <see cref="ClaimOwnProcess"/>, which mirrors OfficeConverter's own
    /// production session-start logic exactly (the same shared
    /// <see cref="OfficeConverter.DecideStartedOrBorrowed"/> decision and
    /// the same held-<see cref="Process"/>-object discipline CRITICAL 2
    /// fixed there), and the SAME shared
    /// <see cref="OfficeConverter.ForceKillAfterGracePeriod"/> when it
    /// started its own instance -- not a second hand-rolled copy -- so that
    /// by the time this constructor returns, no fixture-building process is
    /// still mid-teardown to confuse the FIRST real fact's own diff (Word is
    /// single-instance COM; a lingering not-yet-exited fixture-builder
    /// instance could otherwise get silently "borrowed" by the next
    /// OfficeConverter created, corrupting that fact's started/borrowed
    /// signal). A borrowed fixture-building instance restores the one or two
    /// flags it changed instead, the same discipline the production class
    /// itself follows.</summary>
    public sealed class OfficeFixtures : IDisposable
    {
        public const string Password = "secret";
        private const int GraceMs = 4000;

        public string PlainDocxPath { get; }
        public string LockedDocxPath { get; }
        public string DeckPptxPath { get; }
        public string BookXlsxPath { get; }
        public string LockedXlsxPath { get; }

        private readonly string _root;

        public OfficeFixtures()
        {
            _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            PlainDocxPath = Path.Combine(_root, "plain.docx");
            LockedDocxPath = Path.Combine(_root, "locked.docx");
            DeckPptxPath = Path.Combine(_root, "deck.pptx");
            BookXlsxPath = Path.Combine(_root, "book.xlsx");
            LockedXlsxPath = Path.Combine(_root, "locked.xlsx");

            if (WordInstalled) WithTimeout(TimeSpan.FromSeconds(60), BuildWordFixtures);
            if (PowerPointInstalled) WithTimeout(TimeSpan.FromSeconds(60), BuildPowerPointFixture);
            if (ExcelInstalled) WithTimeout(TimeSpan.FromSeconds(60), BuildExcelFixtures);
        }

        private static (bool Started, Process? OwnProcess) ClaimOwnProcess(HashSet<int> before, string processName)
        {
            var after = Process.GetProcessesByName(processName);
            var newIds = after.Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToList();
            var (started, pid) = OfficeConverter.DecideStartedOrBorrowed(newIds);
            Process? ownProcess = null;
            foreach (var p in after)
            {
                if (started && p.Id == pid) ownProcess = p;
                else p.Dispose();
            }
            return (started, ownProcess);
        }

        private void BuildWordFixtures()
        {
            var before = OfficeConverter.SnapshotPids("WINWORD");
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            var (started, ownProcess) = ClaimOwnProcess(before, "WINWORD");
            var savedVisible = app.Visible;
            var savedAlerts = app.DisplayAlerts;
            app.Visible = false;
            app.DisplayAlerts = 0;
            try
            {
                dynamic plain = app.Documents.Add();
                plain.Content.Text = "Plain unprotected document for OfficeConverter tests.";
                plain.SaveAs2(FileName: PlainDocxPath, FileFormat: 16);
                plain.Close(SaveChanges: false);
                try { Marshal.FinalReleaseComObject(plain); } catch { /* best effort */ }

                dynamic locked = app.Documents.Add();
                locked.Content.Text = "Locked document for OfficeConverter tests.";
                locked.SaveAs2(FileName: LockedDocxPath, FileFormat: 16, Password: Password);
                locked.Close(SaveChanges: false);
                try { Marshal.FinalReleaseComObject(locked); } catch { /* best effort */ }
            }
            finally
            {
                if (started)
                {
                    try { app.Quit(); } catch { /* best effort */ }
                    try { Marshal.FinalReleaseComObject(app); } catch { /* best effort */ }
                    if (ownProcess is not null) OfficeConverter.ForceKillAfterGracePeriod(ownProcess, GraceMs);
                }
                else
                {
                    try { app.Visible = savedVisible; } catch { /* best effort */ }
                    try { app.DisplayAlerts = savedAlerts; } catch { /* best effort */ }
                    ownProcess?.Dispose();
                }
            }
        }

        private void BuildExcelFixtures()
        {
            var before = OfficeConverter.SnapshotPids("EXCEL");
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
            var (started, ownProcess) = ClaimOwnProcess(before, "EXCEL");
            var savedVisible = app.Visible;
            var savedAlerts = app.DisplayAlerts;
            app.Visible = false;
            app.DisplayAlerts = false;
            try
            {
                dynamic book = app.Workbooks.Add();
                while (book.Worksheets.Count < 2) book.Worksheets.Add();
                book.Worksheets[1].Range["A1"].Value = "Sheet 1 content for OfficeConverter tests";
                book.Worksheets[2].Range["A1"].Value = "Sheet 2 content for OfficeConverter tests";
                book.SaveAs(Filename: BookXlsxPath, FileFormat: 51);
                book.Close(SaveChanges: false);
                try { Marshal.FinalReleaseComObject(book); } catch { /* best effort */ }

                dynamic locked = app.Workbooks.Add();
                locked.Worksheets[1].Range["A1"].Value = "Locked workbook content";
                locked.SaveAs(Filename: LockedXlsxPath, FileFormat: 51, Password: Password);
                locked.Close(SaveChanges: false);
                try { Marshal.FinalReleaseComObject(locked); } catch { /* best effort */ }
            }
            finally
            {
                if (started)
                {
                    try { app.Quit(); } catch { /* best effort */ }
                    try { Marshal.FinalReleaseComObject(app); } catch { /* best effort */ }
                    if (ownProcess is not null) OfficeConverter.ForceKillAfterGracePeriod(ownProcess, GraceMs);
                }
                else
                {
                    try { app.Visible = savedVisible; } catch { /* best effort */ }
                    try { app.DisplayAlerts = savedAlerts; } catch { /* best effort */ }
                    ownProcess?.Dispose();
                }
            }
        }

        private void BuildPowerPointFixture()
        {
            var before = OfficeConverter.SnapshotPids("POWERPNT");
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            var (started, ownProcess) = ClaimOwnProcess(before, "POWERPNT");
            try
            {
                dynamic presentation = app.Presentations.Add(WithWindow: false);
                presentation.Slides.Add(1, 2); // ppLayoutText
                presentation.Slides.Add(2, 2);
                presentation.SaveAs(DeckPptxPath, 24); // ppSaveAsOpenXMLPresentation
                presentation.Close();
                try { Marshal.FinalReleaseComObject(presentation); } catch { /* best effort */ }
            }
            finally
            {
                // PowerPoint's Visible is never touched here (matches the
                // production class's own rule -- Task 1 measured it
                // refused); DisplayAlerts IS touched by production code,
                // but this fixture builder never sets it at all, so there
                // is nothing of its own to restore on a borrowed instance.
                if (started)
                {
                    try { app.Quit(); } catch { /* best effort */ }
                    try { Marshal.FinalReleaseComObject(app); } catch { /* best effort */ }
                    if (ownProcess is not null) OfficeConverter.ForceKillAfterGracePeriod(ownProcess, GraceMs);
                }
                else
                {
                    ownProcess?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
