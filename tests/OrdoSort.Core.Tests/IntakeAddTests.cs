namespace OrdoSort.Core.Tests;

/// <summary>Like PathIdentityTests: no temp directory. Intake.Add takes its
/// existence check as a predicate rather than calling File.Exists, so the
/// whole module — dedupe policy, rejection reasons, and the status line every
/// tool shows — is provable without touching a disk. Intake.Expand's own
/// tests (IntakeTests) still use real files, because expansion genuinely
/// walks a folder.</summary>
public class IntakeAddTests
{
    private static readonly ISet<string> Pdfs = new HashSet<string> { "pdf" };
    private static bool Everything(string _) => true;
    private static bool Nothing(string _) => false;

    [Fact]
    public void ACaseOnlyDuplicateIsRejected()
    {
        var r = Intake.Add(
            existing: new[] { @"C:\jobs\scan.pdf" },
            incoming: new[] { @"C:\jobs\SCAN.PDF" },
            exists: Everything);

        Assert.Empty(r.Files);
        Assert.Equal(1, r.AlreadyListed);
        Assert.Equal(1, r.Ignored);
    }

    [Fact]
    public void ATrailingSeparatorDoesNotMakeASecondEntry()
    {
        var r = Intake.Add(new[] { @"C:\jobs" }, new[] { @"C:\jobs\" });

        Assert.Empty(r.Files);
        Assert.Equal(1, r.AlreadyListed);
    }

    /// <summary>One drop holding both spellings adds one entry — every
    /// tool's hand-written version already behaved this way against its own
    /// growing list.</summary>
    [Fact]
    public void TheBatchIsDedupedAgainstItselfNotJustTheExistingList()
    {
        var r = Intake.Add(
            Array.Empty<string>(),
            new[] { @"C:\jobs\scan.pdf", @"C:\jobs\SCAN.pdf", @"C:\jobs\other.pdf" },
            exists: Everything);

        Assert.Equal(2, r.Files.Count);
        Assert.Equal(1, r.AlreadyListed);
    }

    [Fact]
    public void WhatIsStoredIsTheCanonicalForm()
    {
        var r = Intake.Add(Array.Empty<string>(), new[] { @"C:\jobs\2026\..\scan.pdf" }, exists: Everything);

        Assert.Equal(@"C:\jobs\scan.pdf", Assert.Single(r.Files));
    }

    [Fact]
    public void TheFourRejectionReasonsAreCountedApart()
    {
        var r = Intake.Add(
            existing: new[] { @"C:\jobs\already.pdf" },
            incoming: new[]
            {
                @"C:\jobs\ALREADY.pdf",       // already listed
                @"C:\jobs\notes.txt",         // wrong type
                @"C:\jobs\ghost.pdf",         // missing
                "C:\\jobs\\bad\0name.pdf",    // unusable
                @"C:\jobs\good.pdf",          // the only keeper
            },
            extensions: Pdfs,
            exists: p => !p.EndsWith("ghost.pdf", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\jobs\good.pdf", Assert.Single(r.Files));
        Assert.Equal(1, r.AlreadyListed);
        Assert.Equal(1, r.WrongType);
        Assert.Equal(1, r.Missing);
        Assert.Equal(1, r.Unusable);
        Assert.Equal(4, r.Ignored);
    }

    /// <summary>Null means "don't check" — FilenameList, Turnaround and
    /// Production add source roots without an existence check on purpose.</summary>
    [Fact]
    public void ANullPredicateSkipsTheExistenceCheckEntirely()
    {
        var r = Intake.Add(Array.Empty<string>(), new[] { @"C:\jobs\ghost.pdf" }, exists: null);

        Assert.Single(r.Files);
        Assert.Equal(0, r.Missing);
    }

    [Fact]
    public void NothingToReportIsAnEmptyNote()
    {
        var r = Intake.Add(Array.Empty<string>(), new[] { @"C:\jobs\a.pdf" }, exists: Everything);

        Assert.Equal(0, r.Ignored);
        Assert.Equal("", r.Note("PDF"));
    }

    [Fact]
    public void TheNoteNamesTheReasonInsteadOfHedgingAcrossAllOfThem()
    {
        var dupe = Intake.Add(new[] { @"C:\jobs\a.pdf" }, new[] { @"C:\jobs\A.pdf" }, exists: Everything);
        Assert.Equal("nothing added — 1 already listed", dupe.Note("PDF"));

        var mixed = Intake.Add(
            Array.Empty<string>(),
            new[] { @"C:\jobs\a.pdf", @"C:\jobs\notes.txt" },
            extensions: Pdfs, exists: Everything);
        Assert.Equal("1 added · 1 ignored (1 isn't a PDF)", mixed.Note("PDF"));

        var plural = Intake.Add(
            Array.Empty<string>(),
            new[] { @"C:\jobs\a.pdf", @"C:\jobs\n1.txt", @"C:\jobs\n2.txt" },
            extensions: Pdfs, exists: Everything);
        Assert.Equal("1 added · 2 ignored (2 aren't PDFs)", plural.Note("PDF"));
    }

    [Fact]
    public void TheNounIsTheToolsOwn()
    {
        var r = Intake.Add(
            Array.Empty<string>(), new[] { @"C:\jobs\notes.txt" },
            extensions: new HashSet<string> { "zip" }, exists: Everything);

        Assert.Equal("nothing added — 1 isn't a zip", r.Note("zip"));
    }

    [Fact]
    public void EverythingMissingReadsAsMissingNotAsWrongType()
    {
        var r = Intake.Add(
            Array.Empty<string>(), new[] { @"C:\jobs\a.pdf", @"C:\jobs\b.pdf" },
            extensions: Pdfs, exists: Nothing);

        Assert.Empty(r.Files);
        Assert.Equal("nothing added — 2 don't exist", r.Note("PDF"));
    }
}
