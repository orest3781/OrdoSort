using System.Diagnostics;
using System.Runtime.InteropServices;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Office converter's rules that never start Office, so they
/// run in the everyday check. Tests that drive real Word, Excel or
/// PowerPoint are in <see cref="OfficeConverterTests"/> (Integration).</summary>
public sealed class OfficeConverterUnitTests
{
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
    public void SweepTempDirsDeletesAnExistingDirectoryAndIgnoresOneAlreadyGone()
    {
        // Minor 5: OfficeConverter.Dispose sweeps whichever generated temp
        // folders ToPdf's own finally was unable to delete -- typically
        // because the inner document.Close() threw (swallowed) and Office
        // was still holding the file open at that moment, which the class's
        // own doc comment names as exactly the failure class this repo's
        // PHI history is made of. Hermetic and Office-independent, unlike
        // every fact below this one: proves the sweep mechanism itself
        // without needing to provoke that specific COM failure for real,
        // which nothing in this file can do deterministically (see
        // ARestorationFailureIsRecordedNotSwallowed's own comment on what
        // IS reachable here).
        var survivor = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "officeconverter-sweep-test-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllBytes(Path.Combine(survivor, "leftover.docx"), new byte[] { 1, 2, 3 });
        var alreadyGone = Path.Combine(Path.GetTempPath(), "officeconverter-sweep-test-" + Guid.NewGuid().ToString("N"));

        OfficeConverter.SweepTempDirs(new[] { survivor, alreadyGone });

        Assert.False(Directory.Exists(survivor));
    }

    [Fact]
    public void HandlesExcludesLegacyPptBecauseNoSafePasswordPathExistsForIt()
    {
        if (!OfficeConverterTests.PowerPointInstalled) return; // Office not installed on this machine
        using var converter = new OfficeConverter();
        Assert.False(converter.Handles("ppt"),
            "legacy .ppt is deliberately excluded -- no password parameter exists to open one safely, and its OLE2 container gives no byte-level signal the way pptx's ZIP-vs-CFBF split does");
    }

    [Fact]
    public void HandlesExcludesCsvAndTsvBecauseTableToPdfAlreadyHandlesThemWithoutOffice()
    {
        if (!OfficeConverterTests.ExcelInstalled) return; // Office not installed on this machine
        using var converter = new OfficeConverter();
        Assert.False(converter.Handles("csv"),
            "TableToPdf already converts csv deterministically without Office, and Task 8's chain puts this converter first");
        Assert.False(converter.Handles("tsv"),
            "TableToPdf already converts tsv deterministically without Office, and Task 8's chain puts this converter first");
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
        if (!OfficeConverterTests.PowerPointInstalled) return; // Office not installed on this machine
        OfficeConverterTests.WithTimeout(TimeSpan.FromSeconds(10), () =>
        {
            using var converter = new OfficeConverter();
            byte[] fakeEncryptedPptx = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
            var result = converter.ToPdf(fakeEncryptedPptx, "fake.pptx", Array.Empty<string>(), null);
            Assert.Equal("error", result.Status);
            Assert.Contains("password", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PowerPoint", result.Message, StringComparison.OrdinalIgnoreCase);
        });
    }
}
