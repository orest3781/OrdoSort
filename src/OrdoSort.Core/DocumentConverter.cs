namespace OrdoSort.Core;

/// <summary>Turns a document that is not a PDF into one, so
/// <see cref="PdfMerge"/> can merge it like any other.
///
/// Bytes in, bytes out, deliberately: PdfMerge already buffers every source
/// in memory, and its ZipSlip immunity rests on the rule that a zip entry's
/// own name never reaches a filesystem API. An implementation that needs a
/// real file on disk (Office can only open one) writes a temp file under a
/// name IT generates — never the entry's — and deletes it.
///
/// Implementations inherit PdfMerge's promise: never throw. Every failure
/// comes back as a <see cref="ConversionResult"/>.</summary>
public interface IDocumentConverter
{
    /// <param name="extension">Dot-less and lowercase, as Intake produces.</param>
    bool Handles(string extension);

    /// <summary>Convert, asking for a password the way the rest of the app
    /// does. <paramref name="candidates"/> are tried before
    /// <paramref name="ask"/> is called at all; a null ask means "never
    /// prompt".</summary>
    ConversionResult ToPdf(byte[] source, string displayName,
                           IReadOnlyList<string> candidates,
                           Func<PasswordRequest, string?>? ask);
}

/// <summary><see cref="Status"/> is "ok" | "needs_password" | "unsupported"
/// | "error". "unsupported" is a converter-internal signal meaning "not
/// mine" — what lets a chain fall through to the next implementation. It is
/// never a user-facing outcome: when NOTHING handles a type, the merge
/// reports "error" naming why.</summary>
public sealed record ConversionResult(string Status, byte[]? Pdf,
                                      string Message = "", string? Item = null);

/// <summary>The file types the merge window can take, grouped the way the
/// user switches them on and off. Groups rather than extensions are what is
/// stored and toggled, so adding an extension to a group later needs no
/// config migration.</summary>
public static class MergeTypes
{
    public const string Pdf = "pdf", Zip = "zip", Word = "word",
        Excel = "excel", PowerPoint = "powerpoint", Images = "images", Text = "text";

    /// <summary>What <see cref="Save"/> writes for an empty set, and the one
    /// stored value <see cref="Load"/> reads back as empty. Null, "", and
    /// whitespace all mean something different — "nothing was ever saved" —
    /// and load as every group. Without this sentinel the two cases are the
    /// same string ("", once <see cref="Save"/> joins zero groups) and a user
    /// who unticks every type gets every type back the next time the app
    /// starts.</summary>
    public const string NoneStored = "none";

    private static readonly Dictionary<string, string[]> ByGroup = new(StringComparer.OrdinalIgnoreCase)
    {
        [Pdf] = ["pdf"],
        [Zip] = ["zip"],
        [Word] = ["docx", "doc", "docm", "rtf", "odt", "htm", "html"],
        [Excel] = ["xlsx", "xls", "xlsm", "ods", "csv", "tsv"],
        [PowerPoint] = ["pptx", "ppt"],
        [Images] = ["jpg", "jpeg", "png", "tif", "tiff", "bmp", "gif"],
        [Text] = ["txt", "log", "md", "json"],
    };

    /// <summary>Every group, in the order the window shows them.</summary>
    public static IReadOnlyList<string> AllGroups { get; } =
        [Pdf, Zip, Word, Excel, PowerPoint, Images, Text];

    public static IReadOnlyList<string> ExtensionsOf(string group) =>
        ByGroup.TryGetValue(group, out var list) ? list : Array.Empty<string>();

    /// <summary>The group a file belongs to, or null when this window cannot
    /// merge it at all (an .exe, a .mp4) — which is a refusal at intake, not
    /// a toggle.</summary>
    public static string? GroupOf(string extension)
    {
        foreach (var (group, extensions) in ByGroup)
            if (extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return group;
        return null;
    }

    /// <summary>Every extension of every group — the set Intake accepts.</summary>
    public static ISet<string> AllExtensions { get; } =
        new HashSet<string>(ByGroup.Values.SelectMany(e => e), StringComparer.OrdinalIgnoreCase);

    /// <summary>Round-trips the enabled groups through config's existing
    /// comma-list convention (see Config's monitored-folder "filetypes").
    /// Unknown names are dropped rather than failing: a config written by a
    /// later version must not break an earlier one. An empty
    /// <paramref name="groups"/> writes <see cref="NoneStored"/> instead of
    /// "" — see <see cref="Load"/> for why that distinction has to survive
    /// the round trip.</summary>
    public static string Save(IEnumerable<string> groups)
    {
        var chosen = groups.ToList();
        return chosen.Count == 0 ? NoneStored : string.Join(",", chosen);
    }

    /// <summary>Null, empty, or all-whitespace means "nothing was ever
    /// saved" and loads as every group on. <see cref="NoneStored"/> is
    /// checked first and separately, so the user's deliberate "everything
    /// off" is never folded into that same default.</summary>
    public static ISet<string> Load(string? stored)
    {
        if (string.Equals(stored, NoneStored, StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(stored)
            ? new HashSet<string>(AllGroups, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(g => ByGroup.ContainsKey(g)),
                StringComparer.OrdinalIgnoreCase);
    }
}
