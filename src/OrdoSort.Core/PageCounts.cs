using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// Count the pages in a PDF. Never throws — every failure comes back as a
/// null Pages with a short Error, the same discipline Unlock.cs uses for its
/// own PdfSharp calls (see that file's doc comment for why Import mode, not
/// the [Obsolete] InformationOnly, is the right open mode here too).
/// </summary>
public static class PageCounts
{
    public sealed record CountResult(string Path, int? Pages, string Error = "");

    public static CountResult Count(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return new(path, doc.PageCount);
        }
        catch (FileNotFoundException)
        {
            return new(path, null, "file not found");
        }
        catch (DirectoryNotFoundException)
        {
            return new(path, null, "file not found");
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return new(path, null, "open in another program — couldn't count");
        }
        catch (PdfReaderException)
        {
            // PdfSharp throws this both for a document encrypted with a user
            // password (Open was given none) and for some corrupt files —
            // nothing cheap distinguishes the two cases from here, so the
            // message says both rather than guessing.
            return new(path, null, "password-protected or unreadable — couldn't count");
        }
        catch (Exception ex)
        {
            return new(path, null, $"couldn't read it: {ex.Message}");
        }
    }

    /// <summary>Same idiom as Unlock.IsInUse: Windows reports a file held by
    /// another process as a sharing violation (32) or a lock violation (33).</summary>
    private static bool IsInUse(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33;
}
