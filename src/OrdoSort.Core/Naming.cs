using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>
/// Filename construction. Pure functions, no filesystem access — collision
/// checks go through an injected predicate so callers decide what "exists"
/// means and tests never touch a disk.
///
/// The one mechanical rule: the result ends in exactly one ".pdf". The typed
/// name is otherwise used verbatim — no sanitization, no case folding.
/// Assembly order: name per mode -> route suffix -> collision counter -> .pdf
/// </summary>
public static partial class Naming
{
    public const string PdfExt = ".pdf";
    public const string ModeInsert = "insert";
    public const string ModeReplace = "replace";
    public const string ModePrefix = "prefix";
    public const string ModeAppend = "append";
    public const string ModeTemplate = "template";
    public static readonly string[] Modes =
        { ModeInsert, ModeReplace, ModePrefix, ModeAppend, ModeTemplate };

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

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex TemplateTokenRegex();

    private static readonly HashSet<string> TemplateTokens = new() { "name", "original", "date" };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public sealed record NameResult(
        string Filename,          // final name, including .pdf
        string CollisionSuffix,   // "" or " (2)", " (3)", ...  (Explorer style)
        string SuffixApplied,     // "" or the route suffix appended verbatim
        string ModeUsed);         // "insert" | "replace"

    /// <summary>Strip ONE trailing ".pdf" (case-insensitive). Nothing else.</summary>
    public static string StripPdfExt(string text) =>
        text.EndsWith(PdfExt, StringComparison.OrdinalIgnoreCase)
            ? text[..^PdfExt.Length]
            : text;

    /// <summary>True when the typed name commits without renaming
    /// (blank/whitespace, or just ".pdf", which strips to nothing).</summary>
    public static bool IsBlankName(string typedName) =>
        string.IsNullOrWhiteSpace(StripPdfExt(typedName));

    /// <summary>Route's own naming_mode wins; absent means inherit global.</summary>
    public static string ResolveMode(string? routeMode, string globalMode)
    {
        var mode = routeMode ?? globalMode;
        if (Array.IndexOf(Modes, mode) < 0)
            throw new ArgumentException($"Unknown naming mode: '{mode}'");
        return mode;
    }

    /// <summary>"" when the template is usable; else a readable error. A
    /// template must contain at least one known token, no unknown tokens,
    /// and no stray braces.</summary>
    public static string ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "The template is empty — use tokens like {name}, {original}, {date}.";
        var known = 0;
        foreach (Match m in TemplateTokenRegex().Matches(template))
        {
            if (!TemplateTokens.Contains(m.Groups[1].Value))
                return $"Unknown token {{{m.Groups[1].Value}}} — the tokens are " +
                       "{name}, {original} and {date}.";
            known++;
        }
        if (known == 0)
            return "The template needs at least one token: {name}, {original} or {date}.";
        var leftover = TemplateTokenRegex().Replace(template, "");
        if (leftover.Contains('{') || leftover.Contains('}'))
            return "The template has an unmatched { or } — brace tokens must be " +
                   "{name}, {original} or {date}.";
        return "";
    }

    /// <summary>The template that goes with the EFFECTIVE mode: a route in
    /// template mode uses its own template, falling back to the global one
    /// when its own is absent.</summary>
    public static string ResolveTemplate(string? routeMode, string? routeTemplate, string globalTemplate) =>
        routeMode == ModeTemplate
            ? (string.IsNullOrEmpty(routeTemplate) ? globalTemplate : routeTemplate)
            : globalTemplate;

    /// <summary>Filename STEM after applying the typed name per mode. A blank
    /// name preserves the original stem in every mode.</summary>
    public static string ApplyName(string originalFilename, string typedName,
        string mode, string template = "", DateTime? today = null)
    {
        if (Array.IndexOf(Modes, mode) < 0)
            throw new ArgumentException($"Unknown naming mode: '{mode}'");
        var name = StripPdfExt(typedName);
        var stem = StripPdfExt(originalFilename);
        if (string.IsNullOrWhiteSpace(name))
            return stem;
        switch (mode)
        {
            case ModeReplace: return name;
            case ModePrefix: return $"{name}-{stem}";
            case ModeAppend: return $"{stem}-{name}";
            case ModeTemplate:
            {
                var error = ValidateTemplate(template);
                if (error.Length > 0) throw new ArgumentException(error);
                var date = (today ?? DateTime.Now).ToString("yyyyMMdd");
                return TemplateTokenRegex().Replace(template, m => m.Groups[1].Value switch
                {
                    "name" => name,
                    "original" => stem,
                    _ => date,
                });
            }
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
    /// (Trailing dots/spaces in the STEM are fine: the ".pdf" that always
    /// follows keeps them mid-name, where Windows preserves them.)</summary>
    public static void RejectIllegal(string stem)
    {
        var bad = ReservedCharsRegex().Match(stem);
        if (bad.Success)
            throw new ArgumentException(
                $"The name can't contain '{bad.Value}' — Windows forbids the " +
                "characters  < > : \" / \\ | ? *  in filenames.");
        var deviceName = stem.Split('.')[0];
        if (ReservedNames.Contains(deviceName))
            throw new ArgumentException(
                $"\"{stem}\" is a reserved Windows device name — pick another.");
    }

    /// <summary>Assemble the final target filename for a commit. The
    /// <paramref name="exists"/> predicate is called with candidate filenames
    /// (including .pdf) and returns true while a candidate is taken; the
    /// collision counter starts at " (2)" and goes after the route suffix.</summary>
    public static NameResult BuildTarget(
        string originalFilename, string typedName,
        string? routeMode, string globalMode,
        string routeSuffix, bool appendSuffix,
        Func<string, bool> exists,
        string? routeTemplate = null, string globalTemplate = "",
        DateTime? today = null)
    {
        var mode = ResolveMode(routeMode, globalMode);
        var template = routeMode == ModeTemplate
            ? ResolveTemplate(routeMode, routeTemplate, globalTemplate)
            : globalTemplate;
        var stem = ApplyName(originalFilename, typedName, mode, template, today);

        var suffixApplied = "";
        if (appendSuffix && !string.IsNullOrEmpty(routeSuffix))
        {
            suffixApplied = routeSuffix;
            stem += suffixApplied;
        }

        RejectIllegal(stem);  // colon etc. -> readable error, file stays put

        var collisionSuffix = "";
        var filename = stem + PdfExt;
        var counter = 2;
        while (exists(filename))
        {
            collisionSuffix = $" ({counter})";
            filename = stem + collisionSuffix + PdfExt;
            counter++;
        }
        return new NameResult(filename, collisionSuffix, suffixApplied, mode);
    }
}
