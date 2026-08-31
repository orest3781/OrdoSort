using System.Diagnostics;
using System.Runtime.InteropServices;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Services;

/// <summary>Word, Excel and PowerPoint, driven over LATE-BOUND COM -- no
/// interop package, nothing needed at build time, no coupling to an Office
/// generation.
///
/// Three hazards this class exists to contain, each measured before it was
/// written (the plan's Task 1 spike):
///
/// 1. THE HANG. Documents.Open, Workbooks.Open and Presentations.Open each
///    take a password. Pass none for a protected file and Office raises a
///    MODAL DIALOG on a hidden window: the call never returns and the run is
///    wedged. So a password is ALWAYS passed -- a deliberate sentinel when
///    we have no candidate -- and Office throws instead, which is what
///    becomes "needs_password". Word's wrong-password HRESULT
///    (<c>0x800A1520</c>) is a dedicated code and trusted outright. Excel's
///    (<c>0x800A03EC</c>) is NOT dedicated -- it is Excel's catch-all
///    runtime-error HRESULT, reused for corruption and other unrelated
///    failures -- so it is only trusted as "wrong password" when the file's
///    own bytes are independently confirmed encrypted (see
///    <see cref="IsCfbfEncrypted"/>); otherwise it is Unreadable, never a
///    reason to keep prompting for a password that cannot fix it.
/// 2. ORPHANED PROCESSES *and* SOMEONE ELSE'S SESSION. Every Application is
///    quit and released in a finally, with a kill-by-PID net -- one
///    instance per CONVERTER, not per file, since cold start dominates the
///    cost. But Activator.CreateInstance on these ProgIDs can silently
///    ATTACH to a session the user already has open -- Word, Excel and
///    PowerPoint are single-instance COM servers, and Task 1 proved this by
///    experiment, not inference: a simulated user session with unsaved work
///    was reused, and a name-based kill then destroyed it. So every session
///    is diffed -- <see cref="OfficeSession"/> compares the process list
///    immediately before and immediately after CreateInstance -- to know
///    whether THIS call started it or borrowed one already running.
///    EXACTLY ONE new PID is trusted as "started"; zero OR MORE THAN ONE
///    is treated as borrowed -- more than one means a genuine race this
///    class cannot resolve (another WINWORD process starting in the same
///    window, a user double-clicking a .docx mid-merge), and guessing wrong
///    there risks force-killing a third party's process, which is strictly
///    worse than leaking an orphan of our own.
///    Borrowed: never Quit(), never force-kill, never release the
///    Application object's own COM reference (see <see cref="OfficeSession"/>).
///    <c>Visible</c> is never written at all, on EITHER kind of session
///    (review Critical fix, 2026-08-30): <c>Documents.Open</c> already takes
///    its own <c>Visible: false</c> per document, <c>Workbooks.Open</c> gets
///    the equivalent via <c>workbook.Windows(1).Visible = false</c> right
///    after opening (see <see cref="ConvertExcel"/> -- Excel has no
///    per-document parameter the way Word does), and an automation-started
///    Application is invisible by default regardless (measured) -- so an
///    application-level write here was never doing anything for a STARTED
///    session and was doing exactly one thing for a BORROWED one: hiding a
///    window that belongs to the user, for as long as this class held the
///    session. <c>DisplayAlerts</c> and <c>AutomationSecurity</c> are
///    suppressed and restored PER CONVERSION, not per session -- set
///    immediately before <c>Documents.Open</c>/<c>Workbooks.Open</c>/
///    <c>Presentations.Open</c> and restored in the same <c>finally</c> that
///    closes the document (see <see cref="ConvertWord"/>/
///    <see cref="ConvertExcel"/>/<see cref="ConvertPowerPoint"/>), so the
///    window either flag is suppressed shrinks from "as long as the Merge
///    dialog stays open" to the few hundred milliseconds this class is
///    actually driving Office for that one document -- unconditionally, on
///    a session this class started as well as one it borrowed, which is
///    what makes a restoration failure on EITHER kind get recorded rather
///    than only a borrowed one's. A restoration that fails (the user closed
///    the app while this class held it borrowed, say, or a COM error on a
///    session this class started) is recorded in
///    <see cref="RestorationWarnings"/> rather than silently swallowed --
///    the alternative is the user's own Office left muted with nothing
///    anywhere to say why, and because this now happens per conversion
///    rather than at <see cref="Dispose"/>, a failure is recorded WHILE the
///    Merge window is still open, not only after it closes (see
///    MergePdfsViewModel.DrainConverterWarnings). Only the document this
///    class opened is closed. Started: Quit, release, then force-kill that
///    exact PID after a short grace period, because neither app exits
///    naturally within any practical window (measured: PowerPoint ~20-30 s,
///    Excel over two minutes -- waiting longer does not help a merge tool
///    that cannot sit idle for minutes per file). Kill-by-NAME is forbidden
///    outright, in every code path -- a diffed, HELD process handle (not a
///    bare PID number, which is not a stable identity once the grace period
///    lets the OS potentially recycle it) is the only thing this class ever
///    considered provably its own.
/// 3. TEMP FILES. Office can only open a real file. Names are generated
///    here, never taken from a zip entry (that is what keeps PdfMerge's
///    ZipSlip rule true), and deleted in a finally -- these are clients'
///    documents and this repo has a PHI history; residue is expensive.
///
/// A fourth gap, found while building this class rather than measured by
/// Task 1 (which never tried a protected .pptx): <c>Presentations.Open</c>
/// has NO password parameter at all, in any generation of the PowerPoint
/// object model -- unlike Word and Excel, there is no argument this class
/// could ever pass that would make a protected deck fail fast the way
/// hazard 1 relies on. Calling Open on one anyway risks the exact modal-
/// dialog hang hazard 1 exists to prevent, and no catch block can save it --
/// the hang happens before the call ever returns control to managed code.
/// See <see cref="ConvertPowerPoint"/> for the mitigation (a pre-flight byte
/// check, never touching COM at all for a file that fails it -- and
/// reporting "error", not "needs_password", since no password this class
/// could ever be given would change the outcome) and <see cref="Handles"/>
/// for why legacy ".ppt" is excluded even though MergeTypes recognizes it
/// under the PowerPoint group, and why ".csv"/".tsv" are excluded even
/// though MergeTypes recognizes them under the Excel group.
///
/// Not thread-safe: <see cref="EnsureWord"/>/<see cref="EnsureExcel"/>/
/// <see cref="EnsurePowerPoint"/> read-then-write the session fields with no
/// lock. Left as a doc line rather than a lock deliberately -- every caller
/// today converts one file at a time, sequentially, inside a single
/// background-worker Task (see PdfMerge's own sequential foreach over a
/// merge's documents), so there is nothing concurrent to protect against
/// yet, and a lock would be complexity guarding a scenario that does not
/// exist in this codebase.</summary>
public sealed class OfficeConverter : IDocumentConverter, IDisposable, IReportsRestorationWarnings
{
    private const string WordProgId = "Word.Application";
    private const string WordProcessName = "WINWORD";
    private const string ExcelProgId = "Excel.Application";
    private const string ExcelProcessName = "EXCEL";
    private const string PowerPointProgId = "PowerPoint.Application";
    private const string PowerPointProcessName = "POWERPNT";

    // Word: HRESULT 0x800A1520, "The password is incorrect. Word cannot open
    // the document." -- a DEDICATED wrong-password code, trusted outright.
    // Excel: HRESULT 0x800A03EC, "The password you supplied is not
    // correct." -- NOT dedicated; it is Excel's generic runtime-error
    // HRESULT, so it is only trusted as wrong-password when IsCfbfEncrypted
    // independently confirms the file really is encrypted (see
    // ConvertExcel). Both measured directly by Task 1's spike.
    private const int WordWrongPasswordHResult = unchecked((int)0x800A1520);
    private const int ExcelWrongPasswordHResult = unchecked((int)0x800A03EC);

    /// <summary>Wrapped in U+0001 (a control character no real password
    /// contains) on both sides specifically so it can never collide with a
    /// document's genuine password. Tried first, always -- see
    /// <see cref="WithSentinelFirst"/> -- so an unprotected file (measured:
    /// ignored, opens normally, both Word and Excel) never needs a saved
    /// candidate or a prompt at all, and a protected one fails fast and
    /// falls through to the caller's real candidates.</summary>
    internal const string NoPasswordSentinel = "ordosort-no-password";

    // Neither app exits on its own inside any window a merge tool can afford
    // to wait for (measured: PowerPoint ~20-30s, Excel over two minutes), so
    // this grace period is not an attempt to let it exit naturally -- it is
    // the ceiling ForceKillAfterGracePeriod waits before the unconditional,
    // PID-scoped kill that actually reclaims the process (returning early
    // the moment the process exits on its own, if it does).
    private const int ForceKillGraceMs = 4000;

    private OfficeSession? _word;
    private OfficeSession? _excel;
    private OfficeSession? _powerPoint;
    private AppFlags? _wordFlagsBeforeThisClassTouchedThem;
    private AppFlags? _excelFlagsBeforeThisClassTouchedThem;
    private object? _powerPointDisplayAlertsBeforeThisClassTouchedThem;
    private readonly List<string> _restorationWarnings = [];

    /// <summary>Every temp folder <see cref="ToPdf"/> has generated across
    /// this converter's lifetime, regardless of whether that call's own
    /// finally later deleted it -- so <see cref="Dispose"/> can sweep
    /// (Minor 5) whichever ones a failed document.Close() left behind
    /// (Word/Excel/PowerPoint still holding the file open makes
    /// Directory.Delete fail into an empty catch there) once the session
    /// that was holding them has been quit and force-killed.</summary>
    private readonly List<string> _generatedTempDirs = [];
    private bool _disposed;

    /// <summary>Filled whenever restoring DisplayAlerts or AutomationSecurity
    /// after a conversion fails -- empty in the overwhelmingly common case
    /// (the restore succeeds). Not gated to borrowed sessions only (review
    /// Critical fix): restoration now runs per conversion, unconditionally,
    /// so a failure on a session this class itself started is recorded here
    /// too, not only a borrowed one's. Exists because a silent failure here
    /// is not cosmetic the way a leftover temp file is: on a BORROWED
    /// session it leaves the user's OWN, already-open Office application
    /// sitting muted with no other signal anywhere that anything went
    /// wrong -- the empty try/catch this replaced would have swallowed
    /// exactly that. This class has no UI surface of its own to show it on,
    /// so a caller that cares (MergePdfsViewModel.DrainConverterWarnings) is
    /// expected to check this -- now while the window is still open, since
    /// the failure is recorded at the end of the very conversion that
    /// caused it, not only after Dispose().</summary>
    public IReadOnlyList<string> RestorationWarnings => _restorationWarnings;

    /// <summary>DisplayAlerts and AutomationSecurity as they stood the
    /// moment this class first touched this Application -- captured once, at
    /// session start (see <see cref="EnsureWord"/>/<see cref="EnsureExcel"/>),
    /// and used as the restore target after EVERY conversion that session
    /// runs (see <see cref="ConvertWord"/>/<see cref="ConvertExcel"/>), not
    /// re-read before each one: re-reading would risk capturing an
    /// already-suppressed value if a PRIOR restore in the same session had
    /// itself failed, silently making every later restore target the wrong
    /// thing. No <c>Visible</c> field: this class never writes that flag at
    /// all any more (see this class's own doc comment). AutomationSecurity is
    /// nullable: unlike DisplayAlerts, it was never measured by Task 1, so
    /// reading or writing it is wrapped defensively and a null here means
    /// "couldn't read it, so don't try to restore it either".</summary>
    private readonly record struct AppFlags(object DisplayAlerts, object? AutomationSecurity);

    /// <summary>Per app, not all-or-nothing -- Word may be present without
    /// PowerPoint, or vice versa. <paramref name="group"/> is one of
    /// <see cref="MergeTypes.Word"/>/<see cref="MergeTypes.Excel"/>/
    /// <see cref="MergeTypes.PowerPoint"/>; anything else is simply not one
    /// of the three apps this class drives, so it is never "available" here.
    /// A plain registry lookup (Type.GetTypeFromProgID resolves a ProgID's
    /// CLSID without instantiating anything) -- cheap, and safe to call on a
    /// machine with no Office installed at all: it can never hang, because
    /// it never starts a process.</summary>
    public bool IsAvailable(string group) => group switch
    {
        MergeTypes.Word => Type.GetTypeFromProgID(WordProgId) is not null,
        MergeTypes.Excel => Type.GetTypeFromProgID(ExcelProgId) is not null,
        MergeTypes.PowerPoint => Type.GetTypeFromProgID(PowerPointProgId) is not null,
        _ => false,
    };

    /// <summary>Maps the extension to its <see cref="MergeTypes"/> group and
    /// returns whether that app is registered on this machine -- narrower
    /// than MergeTypes.ExtensionsOf(...) in two deliberate ways.
    ///
    /// ".csv"/".tsv" are excluded even though MergeTypes lists them under
    /// the Excel group: TableToPdf already converts both deterministically,
    /// without Office, and is already tested. Claiming them here would make
    /// every CSV/TSV pay an Office cold start and a multi-second disposal
    /// for strictly worse output than TableToPdf already gives for free,
    /// since Task 8's chain puts this converter first.
    ///
    /// ".ppt" is excluded even though MergeTypes lists it under the
    /// PowerPoint group. Word's Documents.Open and Excel's Workbooks.Open
    /// both accept a password argument regardless of which generation of
    /// file format they are opening, so the sentinel technique in
    /// <see cref="ConvertWord"/>/<see cref="ConvertExcel"/> is equally safe
    /// for every OTHER extension MergeTypes lists under either group (docx,
    /// doc, xlsx, xls, ...). PowerPoint has no such parameter at all (see
    /// this class's own doc comment), so ".pptx" is instead guarded by a
    /// byte-level pre-check that only works because an ENCRYPTED pptx is
    /// wrapped in an OLE2/CFBF container while an ordinary one is a plain
    /// ZIP -- see <see cref="ConvertPowerPoint"/>. Legacy ".ppt" gives that
    /// check nothing to work with: every binary .ppt is itself an OLE2
    /// compound file, protected or not, so the identical byte check cannot
    /// tell them apart, and there is no password parameter to fall back on
    /// either. Attempting one anyway risks exactly the modal-dialog hang
    /// this class exists to prevent, with no way to detect it in advance and
    /// no way to answer it if it appears -- so it is refused here, not
    /// attempted.</summary>
    public bool Handles(string extension)
    {
        var group = MergeTypes.GroupOf(extension);
        if (group is not (MergeTypes.Word or MergeTypes.Excel or MergeTypes.PowerPoint)) return false;
        if (!IsAvailable(group)) return false;
        if (extension.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals("tsv", StringComparison.OrdinalIgnoreCase))
            return false;
        return group != MergeTypes.PowerPoint || !extension.Equals("ppt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes <paramref name="source"/> to a name generated here
    /// (never derived from <paramref name="displayName"/> -- hazard 3),
    /// converts, reads the PDF back, and deletes the whole temp folder in a
    /// finally regardless of outcome. Never throws FOR A CONVERSION FAILURE:
    /// every failure of that kind, including one this class did not
    /// anticipate, comes back as an "error" <see cref="ConversionResult"/>,
    /// the same discipline every sibling converter in this feature follows.
    /// It DOES throw <see cref="ObjectDisposedException"/> after
    /// <see cref="Dispose"/> -- that is a caller-contract violation (reusing
    /// a converter whose Office sessions have already been quit/released/
    /// killed), not a document-conversion failure, and silently starting a
    /// fresh, never-tracked, never-disposed instance to paper over the
    /// misuse would be strictly worse.</summary>
    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't a Word, Excel or PowerPoint document this PC can open");

        var group = MergeTypes.GroupOf(extension)!;
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        // Tracked before the try below can fail in any way, so Dispose's own
        // sweep (Minor 5) knows to check this path even if THIS call's own
        // cleanup below never gets the chance to run at all.
        _generatedTempDirs.Add(tempDir);
        try
        {
            Directory.CreateDirectory(tempDir);
            var inputPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "." + extension);
            var outputPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(inputPath, source);

            return group switch
            {
                MergeTypes.Word => ConvertWord(inputPath, outputPath, displayName, candidates, ask),
                MergeTypes.Excel => ConvertExcel(inputPath, outputPath, displayName, candidates, ask),
                MergeTypes.PowerPoint => ConvertPowerPoint(inputPath, outputPath, displayName),
                _ => new("unsupported", null, $"{displayName} isn't a Word, Excel or PowerPoint document"),
            };
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't convert it: {ex.Message}", displayName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort -- these are clients' documents; still try, but cleanup itself must never throw */ }
        }
    }

    private ConversionResult ConvertWord(string inputPath, string outputPath, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var session = EnsureWord();
        dynamic app = session.App;

        // DisplayAlerts/AutomationSecurity are suppressed for exactly this
        // one document's open-through-close cycle (review Critical fix),
        // not the whole dialog session -- set here, immediately before the
        // Open attempts below, and restored in the SAME finally that closes
        // the document, so the window either flag is suppressed shrinks to
        // the time this method is actually driving Word. Unconditional --
        // not gated on session.Started -- both because a session this class
        // started pays only a negligible extra COM round trip for it and
        // because that is what makes a restoration failure on EITHER kind
        // of session get recorded, not only a borrowed one's. There is no
        // hang risk in the gap between conversions: nothing calls into
        // Office there.
        app.DisplayAlerts = 0;
        TrySetAutomationSecurity(app, 3);

        dynamic? document = null;
        try
        {
            var unreadableMessage = "";
            var resolution = Passwords.Resolve(WithSentinelFirst(candidates), ask, displayName, inside: null, password =>
            {
                try
                {
                    document = app.Documents.Open(
                        FileName: inputPath,
                        ConfirmConversions: false,
                        ReadOnly: true,
                        AddToRecentFiles: false,
                        PasswordDocument: password,
                        Visible: false);
                    return PasswordTry.Opened;
                }
                catch (COMException ex) when (ex.HResult == WordWrongPasswordHResult)
                {
                    return PasswordTry.WrongPassword;
                }
                catch (Exception ex)
                {
                    unreadableMessage = ex.Message;
                    return PasswordTry.Unreadable;
                }
            });

            if (resolution.Status == "needs_password")
                return new("needs_password", null, "needs a password", displayName);
            if (resolution.Status == "unreadable")
                return new("error", null, $"couldn't read it: {unreadableMessage}", displayName);

            document!.ExportAsFixedFormat(outputPath, 17); // wdExportFormatPDF
            return new("ok", File.ReadAllBytes(outputPath));
        }
        finally
        {
            if (document is not null)
            {
                try { document.Close(SaveChanges: false); } catch { /* best effort */ }
                try { Marshal.FinalReleaseComObject(document); }
                catch { /* best effort -- this Document is ours alone regardless of whether the Application was borrowed */ }
            }
            if (_wordFlagsBeforeThisClassTouchedThem is { } saved) RestoreAppFlags(app, "Word", saved);
        }
    }

    private ConversionResult ConvertExcel(string inputPath, string outputPath, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var session = EnsureExcel();
        dynamic app = session.App;
        // Computed once, outside the per-candidate lambda: Excel's
        // 0x800A03EC is NOT a dedicated wrong-password code (unlike Word's
        // 0x800A1520) -- it is Excel's catch-all runtime error, reused for
        // corruption and other unrelated failures. Trusting it as "wrong
        // password" unconditionally would walk a corrupt-but-unencrypted
        // workbook through the whole candidate list and then prompt for a
        // password that could never fix it -- precisely what PasswordTry's
        // own contract forbids ("asking again would be a lie"). Gating on
        // the file's own bytes is what makes the distinction real rather
        // than assumed.
        var isEncrypted = IsCfbfEncrypted(inputPath);

        // Same per-conversion scope as ConvertWord -- see that method's own
        // comment for why unconditional, and this class's own doc comment
        // for why Visible is never touched at the Application level here at
        // all any more.
        app.DisplayAlerts = 0;
        TrySetAutomationSecurity(app, 3);

        dynamic? workbook = null;
        try
        {
            var unreadableMessage = "";
            var resolution = Passwords.Resolve(WithSentinelFirst(candidates), ask, displayName, inside: null, password =>
            {
                try
                {
                    workbook = app.Workbooks.Open(
                        Filename: inputPath,
                        UpdateLinks: 0,
                        ReadOnly: true,
                        Password: password,
                        IgnoreReadOnlyRecommended: true,
                        AddToMru: false);
                    // Workbooks.Open has no per-document Visible parameter,
                    // unlike Documents.Open -- this is the Excel analogue,
                    // hiding only the window THIS call opened rather than
                    // the whole Application (review Critical fix). Wrapped
                    // defensively: a workbook that somehow opens without a
                    // window, or a COM hiccup here, must not fail a
                    // conversion that otherwise succeeded.
                    try { workbook.Windows(1).Visible = false; } catch { /* best effort */ }
                    return PasswordTry.Opened;
                }
                catch (COMException ex) when (ex.HResult == ExcelWrongPasswordHResult && isEncrypted)
                {
                    return PasswordTry.WrongPassword;
                }
                catch (Exception ex)
                {
                    unreadableMessage = ex.Message;
                    return PasswordTry.Unreadable;
                }
            });

            if (resolution.Status == "needs_password")
                return new("needs_password", null, "needs a password", displayName);
            if (resolution.Status == "unreadable")
                return new("error", null, $"couldn't read it: {unreadableMessage}", displayName);

            // Unlike TableToPdf's no-Excel fallback (first worksheet only),
            // every worksheet rides along here -- Task 1 confirmed the
            // export's own /Pages /Count reflected both sheets of a
            // two-sheet workbook, so there is nothing to warn the caller
            // about the way TableToPdf's Message does.
            foreach (var worksheet in workbook!.Worksheets)
            {
                worksheet.PageSetup.Zoom = false;
                worksheet.PageSetup.FitToPagesWide = 1;
                worksheet.PageSetup.FitToPagesTall = false;
            }
            workbook.ExportAsFixedFormat(0, outputPath); // xlTypePDF
            return new("ok", File.ReadAllBytes(outputPath));
        }
        finally
        {
            if (workbook is not null)
            {
                try { workbook.Close(SaveChanges: false); } catch { /* best effort */ }
                try { Marshal.FinalReleaseComObject(workbook); }
                catch { /* best effort -- ours alone, same reasoning as ConvertWord */ }
            }
            if (_excelFlagsBeforeThisClassTouchedThem is { } saved) RestoreAppFlags(app, "Excel", saved);
        }
    }

    /// <summary>See this class's own doc comment on the fourth, PowerPoint-
    /// specific gap: Presentations.Open has no password parameter to give it
    /// at all, in any object-model generation, so there is no way to make a
    /// protected deck fail fast the way Word and Excel do. The mitigation is
    /// to never call it in the first place when the file is protected --
    /// checked by <see cref="IsCfbfEncrypted"/> reading the file's own first
    /// bytes, exactly the same OLE2/CFBF-vs-ZIP signature check Task 1 used
    /// to independently confirm locked.docx and locked.xlsx really were
    /// encrypted (MS-OFFCRYPTO wraps a protected OOXML package in an OLE2
    /// compound file the same way regardless of which app produced it), so
    /// extending it to pptx rests on the same measured mechanism, not a new
    /// one.
    ///
    /// Reports "error", not "needs_password": no candidate this class was
    /// ever given, and no password a caller could ever type, would let this
    /// succeed, because there is no parameter to hand it to Office in the
    /// first place. "needs_password" promises a retry can work; here none
    /// ever could, so saying so plainly -- naming the real limitation --
    /// is the honest status, not one that invites the user to keep trying
    /// something that cannot help.</summary>
    private ConversionResult ConvertPowerPoint(string inputPath, string outputPath, string displayName)
    {
        if (IsCfbfEncrypted(inputPath))
            return new("error", null,
                "this PowerPoint file is password-protected, and PowerPoint has no way to accept a password automatically -- open it in PowerPoint and save an unprotected copy first",
                displayName);

        var session = EnsurePowerPoint();
        dynamic app = session.App;
        // Per-conversion scope, same reasoning as ConvertWord/ConvertExcel:
        // set immediately before Presentations.Open, restored in the SAME
        // finally that closes the presentation, unconditionally.
        TrySetDisplayAlerts(app, 1); // ppAlertsNone -- see EnsurePowerPoint's own comment for why 1, not 0

        dynamic? presentation = null;
        try
        {
            presentation = app.Presentations.Open(inputPath, ReadOnly: true, Untitled: false, WithWindow: false);
            presentation.SaveAs(outputPath, 32); // ppSaveAsPDF -- ExportAsFixedFormat does not work here (Task 1: six failing conventions)
            return new("ok", File.ReadAllBytes(outputPath));
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't convert it: {ex.Message}", displayName);
        }
        finally
        {
            if (presentation is not null)
            {
                try { presentation.Close(); } catch { /* best effort */ }
                try { Marshal.FinalReleaseComObject(presentation); } catch { /* best effort -- ours alone */ }
            }
            RestorePowerPointDisplayAlerts(app);
        }
    }

    /// <summary>An encrypted OOXML package (docx/xlsx/pptx) is wrapped whole
    /// in an OLE2/CFBF compound file; an ordinary one is a plain ZIP. Task 1
    /// confirmed this directly for locked.docx and locked.xlsx by reading
    /// their first bytes; the same MS-OFFCRYPTO mechanism applies to any
    /// OOXML package regardless of which app produced it, which is what
    /// lets <see cref="ConvertPowerPoint"/> reuse it for pptx and
    /// <see cref="ConvertExcel"/> corroborate Excel's own non-dedicated
    /// HRESULT. Uses <see cref="Stream.ReadExactly(Span{byte})"/>, not a
    /// single bare <see cref="Stream.Read(Span{byte})"/> call -- Read is not
    /// guaranteed to fill the buffer in one call even when more bytes are
    /// available, and a short read that happened to return fewer than 8
    /// bytes would make a genuinely protected file look unencrypted here,
    /// which for <see cref="ConvertPowerPoint"/> is the SOLE guard against
    /// the uncatchable PowerPoint hang -- getting this wrong there has no
    /// second line of defense. Deliberately NOT applied to legacy binary
    /// formats (.doc/.xls/.ppt) -- those are ALWAYS OLE2 compound files,
    /// protected or not, so this exact check would misreport every ordinary
    /// legacy file as protected; see <see cref="Handles"/> for how that gap
    /// is actually closed (Word/Excel don't need this check at all -- their
    /// sentinel already covers every format generation -- and legacy .ppt is
    /// excluded rather than misdiagnosed).</summary>
    private static bool IsCfbfEncrypted(string path)
    {
        ReadOnlySpan<byte> cfbfSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            return false; // shorter than any valid CFBF header -- categorically not encrypted, whatever else it might be
        }
        return header.SequenceEqual(cfbfSignature);
    }

    /// <summary>The sentinel goes first, always -- trying it costs one fast
    /// COM call (measured: well under 100ms either way) and, when it works,
    /// means an unprotected file never needs a real candidate or a prompt at
    /// all. <see cref="Passwords.Resolve"/> stops at the first success, so
    /// this changes nothing about how a genuinely protected file's real
    /// candidates (or the prompt) are tried afterward -- it only adds one
    /// attempt in front of them, which is exactly what hazard 1 requires:
    /// a password is ALWAYS passed to Office, never omitted.</summary>
    private static IReadOnlyList<string> WithSentinelFirst(IReadOnlyList<string> candidates)
    {
        var withSentinel = new List<string>(candidates.Count + 1) { NoPasswordSentinel };
        withSentinel.AddRange(candidates);
        return withSentinel;
    }

    private OfficeSession EnsureWord()
    {
        if (_word is not null) return _word;
        _word = OfficeSession.Start(WordProgId, WordProcessName);
        dynamic app = _word.App;
        // DisplayAlerts/AutomationSecurity are captured here, once, but SET
        // and RESTORED per conversion (see ConvertWord) rather than for the
        // whole session. Visible is never written at all -- see this
        // class's own doc comment (review Critical fix): Documents.Open
        // already gets its own per-document Visible:false, and an
        // automation-started Application is invisible by default anyway, so
        // the only thing writing it here ever did was hide a BORROWED
        // session's window.
        _wordFlagsBeforeThisClassTouchedThem = new AppFlags(app.DisplayAlerts, TryGetAutomationSecurity(app));
        return _word;
    }

    private OfficeSession EnsureExcel()
    {
        if (_excel is not null) return _excel;
        _excel = OfficeSession.Start(ExcelProgId, ExcelProcessName);
        dynamic app = _excel.App;
        // Same shape as EnsureWord -- see its own comment. Excel's
        // per-document hiding is workbook.Windows(1).Visible = false, set
        // in ConvertExcel right after Workbooks.Open (Excel has no
        // Documents.Open-style Visible parameter to pass there directly).
        _excelFlagsBeforeThisClassTouchedThem = new AppFlags(app.DisplayAlerts, TryGetAutomationSecurity(app));
        return _excel;
    }

    private OfficeSession EnsurePowerPoint()
    {
        if (_powerPoint is not null) return _powerPoint;
        _powerPoint = OfficeSession.Start(PowerPointProgId, PowerPointProcessName);
        dynamic app = _powerPoint.App;
        // Visible is deliberately left alone (measured: refused outright,
        // every run). DisplayAlerts is captured here, once, but SET and
        // RESTORED per conversion (see ConvertPowerPoint), the same move as
        // Word/Excel above -- it is exactly the kind of prompt-suppression
        // that keeps an otherwise-uncatchable modal (a format-compatibility
        // prompt, a repair-this-file prompt) from ever appearing --
        // PowerPoint is the one app with no catchable password failure at
        // all, so anything it CAN raise is a hang with no net under it.
        // PpAlertLevel is NOT Word's WdAlertLevel: 0 is not a valid member
        // (measured directly -- late-bound dispatch rejects it with "Cannot
        // convert value 0 ... enumeration values that are not valid"), so
        // ConvertPowerPoint uses 1 (ppAlertsNone), not 0. Wrapped
        // defensively regardless: unmeasured by Task 1, so a build that
        // behaves differently here degrades to a no-op rather than failing
        // setup.
        try { _powerPointDisplayAlertsBeforeThisClassTouchedThem = app.DisplayAlerts; } catch { /* best effort */ }
        return _powerPoint;
    }

    // AutomationSecurity is unmeasured by Task 1 (unlike DisplayAlerts,
    // exercised directly) -- wrapped defensively so a build that doesn't
    // expose it degrades to a no-op rather than failing setup.
    private static object? TryGetAutomationSecurity(dynamic app)
    {
        try { return app.AutomationSecurity; } catch { return null; }
    }

    private static void TrySetAutomationSecurity(dynamic app, int value)
    {
        try { app.AutomationSecurity = value; } catch { /* best-effort hardening only */ }
    }

    private static void TrySetDisplayAlerts(dynamic app, object value)
    {
        try { app.DisplayAlerts = value; } catch { /* best effort -- unmeasured on PowerPoint by Task 1 */ }
    }

    /// <summary>Restores DisplayAlerts and (when captured) AutomationSecurity
    /// on <paramref name="app"/> to <paramref name="saved"/>, appending to
    /// <see cref="_restorationWarnings"/> rather than swallowing a failure --
    /// see that field's own doc comment for why silence here is worse than a
    /// leftover temp file. Called from the SAME finally that closes the
    /// document in <see cref="ConvertWord"/>/<see cref="ConvertExcel"/>, and
    /// unconditionally -- not gated on whether the session was started or
    /// borrowed, so a failure on either kind is recorded rather than only a
    /// borrowed one's.</summary>
    private void RestoreAppFlags(dynamic app, string appName, AppFlags saved)
    {
        try { app.DisplayAlerts = saved.DisplayAlerts; }
        catch (Exception ex) { _restorationWarnings.Add($"{appName}: couldn't restore DisplayAlerts ({ex.Message})"); }
        if (saved.AutomationSecurity is not null)
        {
            try { app.AutomationSecurity = saved.AutomationSecurity; }
            catch (Exception ex) { _restorationWarnings.Add($"{appName}: couldn't restore AutomationSecurity ({ex.Message})"); }
        }
    }

    /// <summary>PowerPoint's own narrower version of <see cref="RestoreAppFlags"/>
    /// -- DisplayAlerts is the only flag this class ever changes on
    /// PowerPoint. A null <see cref="_powerPointDisplayAlertsBeforeThisClassTouchedThem"/>
    /// means EnsurePowerPoint's own capture failed (unmeasured by Task 1, so
    /// wrapped defensively there too), in which case there is nothing known
    /// to restore TO, so this is skipped rather than writing a guess.</summary>
    private void RestorePowerPointDisplayAlerts(dynamic app)
    {
        if (_powerPointDisplayAlertsBeforeThisClassTouchedThem is not { } saved) return;
        try { app.DisplayAlerts = saved; }
        catch (Exception ex) { _restorationWarnings.Add($"PowerPoint: couldn't restore DisplayAlerts ({ex.Message})"); }
    }

    /// <summary>Disposes all three sessions -- which quits and force-kills
    /// only the ones this class STARTED (see <see cref="OfficeSession"/>);
    /// a BORROWED one is left running, untouched, exactly as
    /// <see cref="OfficeSession.Dispose"/> already documents. Restoring
    /// DisplayAlerts/AutomationSecurity no longer happens here at all
    /// (review Critical fix): that now runs per conversion, in
    /// <see cref="ConvertWord"/>/<see cref="ConvertExcel"/>/
    /// <see cref="ConvertPowerPoint"/>'s own finally blocks, so by the time
    /// Dispose runs there is nothing this class left mid-conversion to put
    /// back. What IS still this method's job: sweeping any generated temp
    /// folder <see cref="ToPdf"/> was unable to delete itself (Minor 5) --
    /// typically because the inner document.Close() threw and Office was
    /// still holding the file open at that moment. The Quit()/force-kill
    /// calls just above release that hold for a STARTED session, so a
    /// second attempt here, after them, has a real chance the first one at
    /// conversion time did not.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _word?.Dispose(); _word = null;
        _excel?.Dispose(); _excel = null;
        _powerPoint?.Dispose(); _powerPoint = null;

        SweepTempDirs(_generatedTempDirs);
    }

    /// <summary>Best-effort delete of every directory in
    /// <paramref name="dirs"/> that still exists -- internal so a fact can
    /// prove the sweep mechanism itself (a real locked file inside a real
    /// directory, one delete attempt that fails, a second one after the lock
    /// releases that succeeds) without needing to provoke the specific COM
    /// failure (<see cref="ToPdf"/>'s own document.Close() throwing) that is
    /// the only way this path is reached in production.</summary>
    internal static void SweepTempDirs(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort -- still locked (a borrowed session's own Word can keep a handle open even now), or already gone */ }
        }
    }

    /// <summary>The decision hazard 2 rests on, isolated as a pure function
    /// (plain PIDs in, a plain verdict out) so it can be tested directly
    /// against a synthetic before/after pair without needing to provoke the
    /// exact process-count race for real. Exactly one new PID is "started";
    /// zero is an ordinary borrow (a pre-existing instance was reused);
    /// MORE than one is an unresolvable race between this call's own
    /// CreateInstance and someone else's process starting in the same
    /// window, and is treated as borrowed for the same reason zero is -- a
    /// leaked orphan Office process is a bounded, recoverable cost, and
    /// guessing which of two new PIDs is "ours" risks force-killing a third
    /// party's process instead, which is not.</summary>
    internal static (bool Started, int? Pid) DecideStartedOrBorrowed(IReadOnlyList<int> newPids) =>
        newPids.Count == 1 ? (true, newPids[0]) : (false, null);

    /// <summary>A process-list snapshot as bare PIDs, with every
    /// <see cref="Process"/> handle opened along the way disposed before
    /// returning. internal, not private: <c>OfficeConverterTests</c>'
    /// fixture builder snapshots the same three process names the same way,
    /// for the same reason <see cref="ForceKillAfterGracePeriod"/> is
    /// shared rather than duplicated -- one implementation of the safety-
    /// relevant bookkeeping, not two that can drift.</summary>
    internal static HashSet<int> SnapshotPids(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var ids = processes.Select(p => p.Id).ToHashSet();
        foreach (var p in processes) p.Dispose();
        return ids;
    }

    /// <summary>The kill-by-PID net, pulled out of <see cref="OfficeSession"/>
    /// so it is written ONCE: this is the one place in the whole feature that
    /// is allowed to call <see cref="Process.Kill()"/>, and it does so only
    /// against a <see cref="Process"/> object the caller has held open,
    /// continuously, since the moment a before/after diff proved it was an
    /// instance THAT caller itself started -- never a name-based match, and
    /// never a bare PID number re-resolved at kill time (a PID is not a
    /// stable identity: the original process could have already exited and
    /// Windows could have handed that same number to something else
    /// entirely by the time a delayed kill runs). No `ownProcess.ProcessName
    /// == expected` re-check precedes the Kill() below, deliberately: on
    /// Windows, holding a Process handle open keeps the underlying kernel
    /// process object -- and therefore its PID -- from being reused by
    /// ANYTHING ELSE for as long as that handle stays open, which is exactly
    /// what this method has done continuously since the original diff. A
    /// name re-check would only ever compare the SAME still-open handle
    /// against itself; it cannot detect a case that guarantee already rules
    /// out. <see cref="Process.WaitForExit(int)"/> is used
    /// instead of an unconditional sleep-then-check: it returns the instant
    /// the process exits, so a Word that closes cleanly on its own well
    /// inside the grace period does not make every disposal pay the full
    /// window regardless. internal, not private: the test fixtures that
    /// author this class's own test documents create their OWN short-lived
    /// Office instances the exact same way <see cref="OfficeSession.Start"/>
    /// does, and reusing this instead of a second hand-written copy is what
    /// keeps the safety logic itself from drifting between production and
    /// test code. Disposes <paramref name="ownProcess"/> before returning,
    /// win or lose.</summary>
    internal static void ForceKillAfterGracePeriod(Process ownProcess, int graceMs)
    {
        using (ownProcess)
        {
            try
            {
                if (!ownProcess.WaitForExit(graceMs))
                {
                    ownProcess.Kill();
                    ownProcess.WaitForExit(5000);
                }
            }
            catch { /* best effort -- the process may have exited between the check and the call either way, which is the goal */ }
        }
    }

    /// <summary>One Application COM object plus the safety bookkeeping
    /// hazard 2 demands: <see cref="Started"/> is true only when the
    /// before/after process-list diff (<see cref="DecideStartedOrBorrowed"/>)
    /// found EXACTLY one brand new process ID -- the only reliable
    /// PID-capture technique available here (Task 1's spike found
    /// <c>app.Hwnd</c> does not resolve through late-bound dynamic dispatch
    /// on this machine's Office build). The matching <see cref="Process"/>
    /// object is held open from the moment of that diff onward, never just
    /// its number -- see <see cref="ForceKillAfterGracePeriod"/> for why a
    /// bare PID is not enough on its own.</summary>
    private sealed class OfficeSession : IDisposable
    {
        public dynamic App { get; }
        public bool Started { get; }
        private readonly Process? _ownProcess;
        private bool _disposed;

        private OfficeSession(dynamic app, bool started, Process? ownProcess)
        {
            App = app;
            Started = started;
            _ownProcess = ownProcess;
        }

        public static OfficeSession Start(string progId, string processName)
        {
            var before = SnapshotPids(processName);
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"{progId} isn't registered on this machine");
            dynamic app = Activator.CreateInstance(type)!;

            // ONE enumeration serves both the started/borrowed decision and
            // the process-object bookkeeping below, rather than diffing ids
            // and then separately re-querying for the matching object --
            // that gap would itself be a (narrower, but real) window for
            // the exact PID-identity problem this whole design exists to
            // avoid.
            var after = Process.GetProcessesByName(processName);
            var newIds = after.Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToList();
            var (started, pid) = DecideStartedOrBorrowed(newIds);

            Process? ownProcess = null;
            foreach (var p in after)
            {
                if (started && p.Id == pid) ownProcess = p; // ours: keep the handle open, held until Dispose
                else p.Dispose(); // not ours -- a pre-existing process, or (the ambiguous multi-match case) a new one this call declined to claim
            }
            return new OfficeSession(app, started, ownProcess);
        }

        /// <summary>BORROWED (<see cref="Started"/> false): never Quit(),
        /// never force-kill -- and just as important, never
        /// Marshal.FinalReleaseComObject the Application object either,
        /// because .NET caches one RCW per COM identity within an apartment;
        /// forcing THIS class's reference to zero would invalidate the exact
        /// same wrapper the user's own code still holds, not just a copy of
        /// it. Doing nothing to this reference is the only action guaranteed
        /// never to touch a session this class does not own -- ordinary GC
        /// reclaims it in time, harmlessly.
        ///
        /// STARTED (<see cref="Started"/> true): Quit(), then
        /// FinalReleaseComObject -- safe here, because nothing else in the
        /// process could hold a reference to an Application object this call
        /// itself created -- then force-kill the held process after a short
        /// grace period. Neither app exits naturally inside any window a
        /// merge tool can afford to wait for (measured: PowerPoint ~20-30s,
        /// Excel over two minutes), so the kill is load-bearing, not
        /// belt-and-braces.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!Started) return;

            try { App.Quit(); } catch { /* best effort -- the kill below is the real safety net */ }
            try { Marshal.FinalReleaseComObject(App); } catch { /* best effort */ }

            if (_ownProcess is not null) ForceKillAfterGracePeriod(_ownProcess, ForceKillGraceMs);
        }
    }
}
