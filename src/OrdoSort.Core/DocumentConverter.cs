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

    /// <summary>What <see cref="Save"/> writes for an empty set, so the
    /// round trip reads back as empty rather than "never configured". Needs
    /// no special case in <see cref="Load"/>: it names no real group, so it
    /// is dropped by the same unknown-name filter that would drop a stray
    /// "hologram" from a later version, and an empty result comes out the
    /// other end exactly the same way. The load-bearing half is
    /// <see cref="Save"/> choosing this over "" — an empty string is
    /// indistinguishable from null/whitespace, both of which mean "nothing
    /// was ever saved" and load as every group on, so a user who unticks
    /// every type would get every type back at the next launch.</summary>
    public const string NoneStored = "none";

    // ".htm"/".html" are a deliberate v1 non-goal, not a silent omission:
    // opening a web document in Word fetches remote resources, which is
    // both a hang surface and a beaconing surface that AutomationSecurity =
    // ForceDisable does not cover, and this repo has a PHI history. rtf,
    // odt and docm stay -- macros in a docm are covered by
    // AutomationSecurity.
    private static readonly Dictionary<string, string[]> ByGroup = new(StringComparer.OrdinalIgnoreCase)
    {
        [Pdf] = ["pdf"],
        [Zip] = ["zip"],
        [Word] = ["docx", "doc", "docm", "rtf", "odt"],
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
    /// saved" and loads as every group on — see <see cref="Save"/> for why a
    /// stored empty set is never one of those three. <see cref="NoneStored"/>
    /// needs no special case here: it names no real group, so it is dropped
    /// by the same unknown-name filter below that drops a stray "hologram"
    /// from a later version, and comes back empty exactly like any other
    /// stored value that names zero known groups.</summary>
    public static ISet<string> Load(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? new HashSet<string>(AllGroups, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(g => ByGroup.ContainsKey(g)),
                StringComparer.OrdinalIgnoreCase);
}
