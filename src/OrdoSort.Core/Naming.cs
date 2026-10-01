using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>
/// Filename construction. Pure functions, no filesystem access — collision
/// checks go through an injected predicate so callers decide what "exists"
/// means and tests never touch a disk.
///
/// The one mechanical rule: the result keeps the original's extension, once
/// (".pdf" for a PDF, lower case; a photo keeps its own, lower-cased). The
/// typed name is otherwise used verbatim — no sanitization, no case folding.
/// Assembly order: name per mode -> route suffix -> collision counter -> extension
///
/// A photo or GIF is named by date taken instead of by mode (spec
/// 2026-09-29-media-filing-loop): "&lt;date taken&gt;-&lt;typed name&gt;".
/// </summary>
public static partial class Naming
{
    public const string PdfExt = ".pdf";
    public const string ModeInsert = "insert";
    public const string ModeReplace = "replace";
    public const string ModePrefix = "prefix";
    public const string ModeAppend = "append";
    public static readonly string[] Modes =
        { ModeInsert, ModeReplace, ModePrefix, ModeAppend };

    /// <summary>What History records as the mode for a photo named by date
    /// taken. Not a mode a route or Settings can choose.</summary>
    public const string ModeDated = "dated";

    // Inbox contract: any PDF with "--" in the stem (something on each side).
    // Insert mode splices the typed name at the FIRST "--"; the classic
    // YYYYMMDD--ID names are just one instance of the pattern.
    [GeneratedRegex(@"^.+--.+\.pdf$", RegexOptions.IgnoreCase)]
    public static partial Regex InboxRegex();

    // Characters Windows can't put in a filename, plus control chars. The
    // colon is the dangerous one: "SMITH:JOHN" is not rejected by the move —
    // Windows writes the bytes into an NTFS alternate data stream of a 0-byte
    // file "SMITH", so the commit "succeeds" while the document silently
    // vanishes. Reject the whole class up front so any illegal name fails
    // readably with the file left in place.
    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1F]""")]
    private static partial Regex ReservedCharsRegex();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Longest stem a name may have. Windows allows 255 characters
    /// in one file name; this leaves room for an extension and a " (nn)"
    /// collision counter, so a long roster-built name fails here, readably,
    /// not at the move with a raw "path too long" (DW-49).</summary>
    public const int MaxStemLength = 240;

    public sealed record NameResult(
        string Filename,          // final name, including the extension
        string CollisionSuffix,   // "" or " (2)", " (3)", ...  (Explorer style)
        string SuffixApplied,     // "" or the route suffix appended verbatim
        string ModeUsed);         // one of Naming.Modes, or ModeDated

    /// <summary>Strip ONE trailing ".pdf" (case-insensitive). Nothing else.</summary>
    public static string StripPdfExt(string text) =>
        text.EndsWith(PdfExt, StringComparison.OrdinalIgnoreCase)
            ? text[..^PdfExt.Length]
            : text;

    /// <summary>The extension a file keeps: ".pdf" for any PDF, otherwise its
    /// own, lower-cased (".JPG" becomes ".jpg"); "" when it has none.</summary>
    public static string ExtensionOf(string originalFilename)
    {
        if (originalFilename.EndsWith(PdfExt, StringComparison.OrdinalIgnoreCase)) return PdfExt;
        return Path.GetExtension(originalFilename).ToLowerInvariant();
    }

    /// <summary>A file name without its extension (the one
    /// <see cref="ExtensionOf"/> reports).</summary>
    private static string StemOf(string originalFilename)
    {
        var extension = ExtensionOf(originalFilename);
        return extension.Length > 0 ? originalFilename[..^extension.Length] : originalFilename;
    }

    /// <summary>The typed name without a trailing ".pdf" or the file's own
    /// extension: typing "kitchen.jpg" on a .jpg names it "kitchen".</summary>
    private static string StripTypedExt(string typedName, string extension)
    {
        var name = StripPdfExt(typedName);
        if (extension.Length > 0 && extension != PdfExt
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        return name;
    }

    [GeneratedRegex(@"^(?<date>\d{8})(?:[-_ ]+(?<rest>.*))?$")]
    private static partial Regex TypedDateRegex();

    /// <summary>A photo's stem: "&lt;date taken&gt;-&lt;typed name&gt;". A blank
    /// name keeps the original stem, as for PDFs. A typed name that starts
    /// with a real 8-digit date uses that date instead (the way to correct a
    /// camera with the wrong clock).</summary>
    public static string ApplyDatedName(string originalFilename, string typedName, string takenStamp)
    {
        var extension = ExtensionOf(originalFilename);
        var name = StripTypedExt(typedName, extension).Trim();
        if (name.Length == 0) return StemOf(originalFilename);
        var typedDate = TypedDateRegex().Match(name);
        if (typedDate.Success && BulkRename.IsRealDate(typedDate.Groups["date"].Value))
        {
            var rest = typedDate.Groups["rest"].Value.Trim();
            return rest.Length > 0 ? $"{typedDate.Groups["date"].Value}-{rest}" : typedDate.Groups["date"].Value;
        }
        return $"{takenStamp}-{name}";
    }

    /// <summary>True when the typed name commits without renaming
    /// (blank/whitespace, or just ".pdf", which strips to nothing).</summary>
    public static bool IsBlankName(string typedName) =>
        string.IsNullOrWhiteSpace(StripPdfExt(typedName));

    /// <summary><see cref="IsBlankName(string)"/>, also counting the file's own
    /// extension typed on its own (".jpg" on a photo) as blank.</summary>
    public static bool IsBlankName(string typedName, string originalFilename) =>
        string.IsNullOrWhiteSpace(StripTypedExt(typedName, ExtensionOf(originalFilename)));

    /// <summary>Route's own naming_mode wins; absent means inherit global.</summary>
    public static string ResolveMode(string? routeMode, string globalMode)
    {
        var mode = routeMode ?? globalMode;
        if (Array.IndexOf(Modes, mode) < 0)
            throw new ArgumentException($"Unknown naming mode: '{mode}'");
        return mode;
    }

    /// <summary>Filename STEM after applying the typed name per mode. A blank
    /// name preserves the original stem in every mode.</summary>
    public static string ApplyName(string originalFilename, string typedName, string mode)
    {
        if (Array.IndexOf(Modes, mode) < 0)
            throw new ArgumentException($"Unknown naming mode: '{mode}'");
        var name = StripTypedExt(typedName, ExtensionOf(originalFilename));
        var stem = StemOf(originalFilename);
        if (string.IsNullOrWhiteSpace(name))
            return stem;
        switch (mode)
        {
            case ModeReplace: return name;
            case ModePrefix: return $"{name}-{stem}";
            case ModeAppend: return $"{stem}-{name}";
            default:  // insert: the typed name replaces the FIRST "--"
            {
                var split = stem.IndexOf("--", StringComparison.Ordinal);
                if (split <= 0 || split + 2 >= stem.Length)
                    throw new ArgumentException(
                        $"Insert mode needs '--' in the filename, got '{originalFilename}'");
                return $"{stem[..split]}-{name}-{stem[(split + 2)..]}";
            }
        }
    }

    /// <summary>Throw if <paramref name="stem"/> can't be a Windows filename.
    /// Legal names — spaces, apostrophes, hyphens, unicode — pass untouched.
    /// (Trailing dots/spaces in the STEM are fine: the extension that always
    /// follows keeps them mid-name, where Windows preserves them.)</summary>
    public static void RejectIllegal(string stem)
    {
        var bad = ReservedCharsRegex().Match(stem);
        if (bad.Success)
            throw new ArgumentException(
                $"The name can't contain '{bad.Value}' — Windows forbids the " +
                "characters  < > : \" / \\ | ? *  in filenames.");
        if (stem.Length > MaxStemLength)
            throw new ArgumentException(
                $"The name is too long ({stem.Length} characters) — Windows allows " +
                $"at most {MaxStemLength} here. Shorten it.");
        // Windows ignores spaces before the extension when it matches a
        // device name, so "CON .pdf" is CON on older versions (DW-19).
        var deviceName = stem.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(deviceName))
            throw new ArgumentException(
                $"\"{stem}\" is a reserved Windows device name — pick another.");
    }

    /// <summary>Assemble the final target filename for a commit. The
    /// <paramref name="exists"/> predicate is called with candidate filenames
    /// (including the extension) and returns true while a candidate is taken;
    /// the collision counter starts at " (2)" and goes after the route suffix.
    /// With <paramref name="takenStamp"/> (a photo's YYYYMMDD) the name is
    /// built by <see cref="ApplyDatedName"/> in every mode.</summary>
    public static NameResult BuildTarget(
        string originalFilename, string typedName,
        string? routeMode, string globalMode,
        string routeSuffix, bool appendSuffix,
        Func<string, bool> exists,
        string? takenStamp = null)
    {
        string mode;
        string stem;
        if (takenStamp is null)
        {
            mode = ResolveMode(routeMode, globalMode);
            stem = ApplyName(originalFilename, typedName, mode);
        }
        else
        {
            mode = ModeDated;
            stem = ApplyDatedName(originalFilename, typedName, takenStamp);
        }
        var extension = ExtensionOf(originalFilename);

        var suffixApplied = "";
        if (appendSuffix && !string.IsNullOrEmpty(routeSuffix))
        {
            suffixApplied = routeSuffix;
            stem += suffixApplied;
        }

        RejectIllegal(stem);  // colon etc. -> readable error, file stays put

        var collisionSuffix = "";
        var filename = stem + extension;
        var counter = 2;
        while (exists(filename))
        {
            collisionSuffix = $" ({counter})";
            filename = stem + collisionSuffix + extension;
            counter++;
        }
        return new NameResult(filename, collisionSuffix, suffixApplied, mode);
    }
}
