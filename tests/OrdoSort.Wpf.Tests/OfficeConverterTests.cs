using System.Diagnostics;
using System.Runtime.InteropServices;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Wpf.Tests;

/// <summary>OfficeConverter drives real Word/Excel/PowerPoint over COM, so
/// every fact here except the first needs Office actually installed to mean
/// anything. SkippableFact is not referenced anywhere in this repo and no
/// new packages are allowed, so the skip route taken is an early
/// <c>return</c> guarded by a registry lookup (<see cref="WordInstalled"/> /
/// <see cref="ExcelInstalled"/> / <see cref="PowerPointInstalled"/>,
/// computed once, never touching COM) -- with a comment at each site saying
/// why. <see cref="IsAvailableReportsABoolWithoutTouchingOffice"/> is the one
/// fact that always runs, so a machine with no Office at all still runs
/// something from this class rather than reporting zero tests.
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
/// temp folder deleted in Dispose.</summary>
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
    public void HandlesRecognizesTheOfficeExtensionsButExcludesLegacyPpt()
    {
        if (!(WordInstalled && ExcelInstalled && PowerPointInstalled)) return; // needs all three registered to prove the ppt exclusion means something (not just "PowerPoint is absent anyway")
        using var converter = new OfficeConverter();
        Assert.True(converter.Handles("docx"));
        Assert.True(converter.Handles("doc"));
        Assert.True(converter.Handles("xlsx"));
        Assert.True(converter.Handles("xls"));
        Assert.True(converter.Handles("pptx"));
        Assert.False(converter.Handles("ppt"),
            "legacy .ppt is deliberately excluded -- see the class doc's fourth hazard: no password parameter exists to open one safely, and its OLE2 container gives no byte-level signal the way pptx's ZIP-vs-CFBF split does");
        Assert.False(converter.Handles("pdf"));
        Assert.False(converter.Handles("png"));
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
        // OWN catch clause, which the Word-only fact above cannot.
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
        if (!PowerPointInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(10), () =>
        {
            using var converter = new OfficeConverter();
            byte[] fakeEncryptedPptx = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
            var result = converter.ToPdf(fakeEncryptedPptx, "fake.pptx", Array.Empty<string>(), null);
            Assert.Equal("needs_password", result.Status);
        });
    }

    [Fact]
    public void NoOfficeProcessSurvivesAfterDisposal()
    {
        if (!(WordInstalled || ExcelInstalled || PowerPointInstalled)) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(90), () =>
        {
            var beforeWord = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
            var beforeExcel = Process.GetProcessesByName("EXCEL").Select(p => p.Id).ToHashSet();
            var beforePowerPoint = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).ToHashSet();

            using (var converter = new OfficeConverter())
            {
                if (WordInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.PlainDocxPath), "plain.docx", Array.Empty<string>(), null).Status);
                if (ExcelInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.BookXlsxPath), "book.xlsx", Array.Empty<string>(), null).Status);
                if (PowerPointInstalled)
                    Assert.Equal("ok", converter.ToPdf(File.ReadAllBytes(_fx.DeckPptxPath), "deck.pptx", Array.Empty<string>(), null).Status);
            } // Dispose runs here -- this closing brace is what the fact actually proves

            var newWord = Process.GetProcessesByName("WINWORD").Select(p => p.Id).Where(id => !beforeWord.Contains(id));
            var newExcel = Process.GetProcessesByName("EXCEL").Select(p => p.Id).Where(id => !beforeExcel.Contains(id));
            var newPowerPoint = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).Where(id => !beforePowerPoint.Contains(id));
            Assert.Empty(newWord);
            Assert.Empty(newExcel);
            Assert.Empty(newPowerPoint);
        });
    }

    [Fact]
    public void ABorrowedWordInstanceSurvivesAfterTheConverterFinishes()
    {
        // Hazard 2, tested end-to-end rather than via the fallback decision-
        // function route the brief allows: Word's single-instance COM
        // registration (Task 1 proved this by experiment) is exactly what
        // makes this simulation possible. This fact plays the role of "the
        // user" by starting its OWN Word instance first, using the identical
        // process-diff technique OfficeSession itself relies on -- so this
        // fact's "before" PID is exactly as trustworthy as production's own.
        // Because Word only ever registers ONE running instance, the
        // converter-under-test's OWN CreateInstance call has nowhere else to
        // attach: it MUST borrow this exact instance, which is what lets this
        // fact prove hazard 2's promise for real rather than in isolation.
        if (!WordInstalled) return; // Office not installed on this machine
        WithTimeout(TimeSpan.FromSeconds(30), () =>
        {
            var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
            dynamic userWord = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            var userPid = Process.GetProcessesByName("WINWORD").Select(p => p.Id).First(id => !before.Contains(id));
            try
            {
                using var converter = new OfficeConverter();
                var result = converter.ToPdf(File.ReadAllBytes(_fx.PlainDocxPath), "plain.docx", Array.Empty<string>(), null);
                Assert.Equal("ok", result.Status);

                // Hazard 2's whole point: the converter must never have
                // killed the "user's" instance -- it could only ever have
                // BORROWED it, since Word registers only one at a time.
                Assert.False(Process.GetProcessById(userPid).HasExited,
                    "the converter must never kill an instance it borrowed rather than started");
            }
            finally
            {
                // Tearing down the pretend "user" session is explicitly NOT
                // the converter's job -- that is exactly what this fact
                // proves by doing it here instead.
                try { userWord.Quit(); } catch { /* best effort */ }
                try { Marshal.FinalReleaseComObject(userWord); } catch { /* best effort */ }
            }
        });
    }

    /// <summary>Builds every fixture ONCE for the whole class, via Office
    /// itself, under its own GUID temp folder deleted in Dispose. Each
    /// Build* method uses the same before/after process-list diff technique
    /// OfficeConverter's own (private) session type relies on, and the SAME
    /// shared <see cref="OfficeConverter.ForceKillAfterGracePeriod"/> when it
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

        private static (bool Started, int? Pid) Diff(HashSet<int> before, string processName)
        {
            var newIds = Process.GetProcessesByName(processName).Select(p => p.Id)
                .Where(id => !before.Contains(id)).ToList();
            return (newIds.Count > 0, newIds.Count > 0 ? (int?)newIds[0] : null);
        }

        private void BuildWordFixtures()
        {
            var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            var (started, pid) = Diff(before, "WINWORD");
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
                    if (pid is int p) OfficeConverter.ForceKillAfterGracePeriod(p, GraceMs);
                }
                else
                {
                    try { app.Visible = savedVisible; } catch { /* best effort */ }
                    try { app.DisplayAlerts = savedAlerts; } catch { /* best effort */ }
                }
            }
        }

        private void BuildExcelFixtures()
        {
            var before = Process.GetProcessesByName("EXCEL").Select(p => p.Id).ToHashSet();
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
            var (started, pid) = Diff(before, "EXCEL");
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
                    if (pid is int p) OfficeConverter.ForceKillAfterGracePeriod(p, GraceMs);
                }
                else
                {
                    try { app.Visible = savedVisible; } catch { /* best effort */ }
                    try { app.DisplayAlerts = savedAlerts; } catch { /* best effort */ }
                }
            }
        }

        private void BuildPowerPointFixture()
        {
            var before = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).ToHashSet();
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            var (started, pid) = Diff(before, "POWERPNT");
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
                // PowerPoint: no flags are ever changed here (matches the
                // production class's own "leave it as it is" rule -- Task 1
                // measured Visible=false being refused), so a borrowed
                // instance needs no restoration at all.
                if (started)
                {
                    try { app.Quit(); } catch { /* best effort */ }
                    try { Marshal.FinalReleaseComObject(app); } catch { /* best effort */ }
                    if (pid is int p) OfficeConverter.ForceKillAfterGracePeriod(p, GraceMs);
                }
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
