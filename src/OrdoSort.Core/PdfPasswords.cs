using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// The one place that knows what "wrong password" looks like to PdfSharp.
/// Unlock carried this loop privately for its probe and its buffered unlock;
/// PdfMerge needs the same loop for a loose PDF and for one inside a zip,
/// with the prompt added. Written once here, over <see cref="Passwords.Resolve"/>,
/// so the exception discipline cannot drift between the three callers.
///
/// The discipline, verbatim from Unlock.ProbeReadiness's own doc comment:
/// <see cref="PdfReaderException"/> is a wrong password for that one
/// candidate — try the next; anything else — including a failure while
/// touching a page — is unreadable and stops. Collapsing these would report
/// a damaged file as merely needing a password.
///
/// Every successful open is followed by touching every page (VerifyReadable's
/// technique, see Unlock.cs) so a document whose page dictionaries are
/// broken is reported here, by the open, rather than later by AddPage —
/// exact parity with what Unlock's probe already does, and measured there to
/// cost nothing observable.
/// </summary>
public static class PdfPasswords
{
    /// <summary>"opened": <see cref="Document"/> and the <see cref="Stream"/>
    /// it reads from are the caller's to keep alive — BOTH, until whatever
    /// was built from the pages has been saved, because PdfSharp's Import
    /// mode resolves page objects from the source lazily — and then dispose.
    /// <see cref="MatchedIndex"/> is the winning candidate's position, or
    /// null when the password was typed at the prompt or none was needed.
    /// "needs_password": nothing worked and the prompt was skipped.
    /// "unreadable": <see cref="Message"/> says why.</summary>
    public sealed record OpenOutcome(string Status, PdfDocument? Document = null, MemoryStream? Stream = null,
        int? MatchedIndex = null, string Message = "");

    /// <summary>Shared by <see cref="Unlock.ProbeReadiness"/> and
    /// Unlock.UnlockBuffered — both need the identical no-password encryption
    /// check. Opening WITH a password cannot answer "is this encrypted",
    /// because a correctly decrypted document reports itself unencrypted
    /// just like one that never was. Returns true only when opening without
    /// a password succeeded AND proved the document unencrypted; false means
    /// "couldn't prove that" — encrypted, or damaged in a way that looks the
    /// same from here — and the caller falls through to its own
    /// password-based path either way. <paramref name="stream"/> must be
    /// freshly positioned at 0; this does not rewind it, so callers pass a
    /// stream they are about to discard.</summary>
    public static bool IsProvablyNotEncrypted(Stream stream)
    {
        try
        {
            using var probe = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return !probe.SecuritySettings.IsEncrypted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Open a PDF that may or may not be locked: no password first —
    /// an unencrypted document must never reach the prompt — then
    /// <see cref="OpenWithPasswords"/>. A document that fails to open without
    /// a password and carries no /Encrypt dictionary is damaged, not locked,
    /// and is reported unreadable without anyone being asked.</summary>
    public static OpenOutcome Open(byte[] bytes, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, string item, string? inside)
    {
        var plain = TryOpenPlain(bytes, out var plainFailure);
        if (plain is not null) return plain;
        if (!LooksEncrypted(bytes)) return new OpenOutcome("unreadable", Message: plainFailure);
        return OpenWithPasswords(bytes, candidates, ask, item, inside);
    }

    /// <summary>The candidate loop: every password in order, then the
    /// prompt, each attempt an open-plus-page-touch over a fresh view of
    /// <paramref name="bytes"/>. The source is read from disk exactly ONCE
    /// by the caller regardless of how many candidates are tried — the
    /// discipline Unlock.UnlockBuffered's doc comment explains (three
    /// separate opens over a share meant three full transfers).</summary>
    public static OpenOutcome OpenWithPasswords(byte[] bytes, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, string item, string? inside)
    {
        PdfDocument? opened = null;
        MemoryStream? openedStream = null;
        var unreadable = "";

        var resolution = Passwords.Resolve(candidates, ask, item, inside, password =>
        {
            var stream = new MemoryStream(bytes, writable: false);
            PdfDocument? doc = null;
            try
            {
                doc = PdfReader.Open(stream, password, PdfDocumentOpenMode.Import);
                for (var p = 0; p < doc.PageCount; p++) { var _ = doc.Pages[p]; }
                opened = doc;
                openedStream = stream;
                return PasswordTry.Opened;
            }
            catch (PdfReaderException)
            {
                doc?.Dispose();
                stream.Dispose();
                return PasswordTry.WrongPassword;
            }
            catch (Exception ex)
            {
                doc?.Dispose();
                stream.Dispose();
                unreadable = ex.Message;
                return PasswordTry.Unreadable;
            }
        });

        return resolution.Status switch
        {
            "opened" => new OpenOutcome("opened", opened, openedStream, resolution.MatchedIndex),
            "needs_password" => new OpenOutcome("needs_password"),
            _ => new OpenOutcome("unreadable", Message: unreadable),
        };
    }

    /// <summary>Opens with no password and touches every page; null when
    /// that fails for any reason, with the reason in <paramref name="failure"/>.
    /// A document that opens here but still reports itself encrypted (an
    /// owner-password-only PDF) counts as opened: Import mode reads it
    /// without the owner password, which is all a merge needs.</summary>
    private static OpenOutcome? TryOpenPlain(byte[] bytes, out string failure)
    {
        var stream = new MemoryStream(bytes, writable: false);
        PdfDocument? doc = null;
        try
        {
            doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            for (var p = 0; p < doc.PageCount; p++) { var _ = doc.Pages[p]; }
            failure = "";
            return new OpenOutcome("opened", doc, stream);
        }
        catch (Exception ex)
        {
            doc?.Dispose();
            stream.Dispose();
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>An encrypted PDF's trailer names its /Encrypt dictionary; a
    /// document with no such token anywhere in its bytes cannot be locked,
    /// however badly it failed to open. A plain byte scan, deliberately —
    /// parsing a file that has just failed to parse is not an option.</summary>
    private static bool LooksEncrypted(byte[] bytes)
    {
        var token = Encoding.ASCII.GetBytes("/Encrypt");
        return bytes.AsSpan().IndexOf(token) >= 0;
    }
}
