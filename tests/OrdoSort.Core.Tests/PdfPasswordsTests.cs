using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

/// <summary>The PdfSharp side of the password contract: candidates before
/// the prompt, the prompt only for something encrypted, and a damaged file
/// reported as damaged rather than mistaken for a locked one. Real PdfSharp
/// documents throughout (ZipMergeTests' own fixture voice) — the exception
/// discipline under test is PdfSharp's, so nothing here can be faked.</summary>
public class PdfPasswordsTests
{
    private static byte[] MakePdfBytes(int pageCount = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static byte[] MakeEncryptedPdfBytes(string userPassword)
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPassword;
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    [Fact]
    public void APlainPdfOpensWithoutTouchingCandidatesOrThePrompt()
    {
        var r = PdfPasswords.Open(MakePdfBytes(2), new[] { "irrelevant" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.Equal(2, r.Document!.PageCount);
        r.Document.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void ALockedPdfOpensWithTheCandidateThatMatchesAndReportsItsIndex()
    {
        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope", "secret" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Equal(1, r.MatchedIndex);
        Assert.Equal(1, r.Document!.PageCount);
        r.Document.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void WhenNoCandidateOpensItThePromptIsAskedWithTheItemAndWhereItLives()
    {
        var requests = new List<PasswordRequest>();

        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope" },
            req => { requests.Add(req); return "secret"; }, "report.pdf", "Batch 12.zip");

        Assert.Equal("opened", r.Status);
        Assert.Null(r.MatchedIndex);   // typed, not a candidate
        var req = Assert.Single(requests);
        Assert.Equal("report.pdf", req.Item);
        Assert.Equal("Batch 12.zip", req.Inside);
        Assert.False(req.PreviousAttemptFailed);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void AWrongAnswerIsAskedAgainWithTheFailedFlag()
    {
        var answers = new Queue<string?>(new[] { "bad", "secret" });
        var flags = new List<bool>();

        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), Array.Empty<string>(),
            req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); }, "doc.pdf", null);

        Assert.Equal("opened", r.Status);
        Assert.Equal(new[] { false, true }, flags);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void SkippingThePromptIsNeedsPasswordWithNothingOpen()
    {
        var r = PdfPasswords.Open(MakeEncryptedPdfBytes("secret"), new[] { "nope" }, _ => null, "doc.pdf", null);

        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.Document);
        Assert.Null(r.Stream);
    }

    /// <summary>Random bytes under a .pdf name: no "%PDF", no "/Encrypt"
    /// anywhere. Damage is not a password problem — nobody is asked, and the
    /// reason PdfSharp gave is carried in Message.</summary>
    [Fact]
    public void GarbageIsUnreadableAndNobodyIsAsked()
    {
        var garbage = new byte[512];
        new Random(1234).NextBytes(garbage);

        var r = PdfPasswords.Open(garbage, new[] { "whatever" }, NeverAsked, "doc.pdf", null);

        Assert.Equal("unreadable", r.Status);
        Assert.NotEqual("", r.Message);
    }

    [Fact]
    public void OpenWithPasswordsOnAPlainPdfStillOpensItOnTheFirstCandidate()
    {
        // Unlock proves a file unencrypted BEFORE reaching this loop, so
        // "plain" never actually gets here from Unlock — but PdfSharp opens
        // an unencrypted document under any password, and the loop must not
        // turn that into a lie about which password mattered.
        var r = PdfPasswords.OpenWithPasswords(MakePdfBytes(), new[] { "anything" }, null, "doc.pdf", null);
        Assert.Equal("opened", r.Status);
        Assert.Equal(0, r.MatchedIndex);
        r.Document!.Dispose();
        r.Stream!.Dispose();
    }

    [Fact]
    public void IsProvablyNotEncryptedTellsPlainFromLocked()
    {
        using var plain = new MemoryStream(MakePdfBytes(), writable: false);
        using var locked = new MemoryStream(MakeEncryptedPdfBytes("secret"), writable: false);
        Assert.True(PdfPasswords.IsProvablyNotEncrypted(plain));
        Assert.False(PdfPasswords.IsProvablyNotEncrypted(locked));
    }
}
