using System.Globalization;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>The document's own date, read off the front of its FileName cell
/// — the hub's single home for all three conventions found in live PECF
/// exports (spec rule 1): "20260722-…" (the standard form),
/// "07.15.2026 …" and "07152026 …" (the two ECAA forms — parsed rather than
/// rejected so that re-including ECAA later yields real dates, not a wall of
/// exclusions). Anything else is null: a name with no recoverable date is
/// counted and shown, never guessed (spec rule 5). Supersedes
/// TurnaroundTime.ExtractDocDate for the hub; the old window keeps the old
/// method until Phase 4 retires it.</summary>
public static partial class DocumentDate
{
    [GeneratedRegex(@"^(\d{8})-")]
    private static partial Regex DashForm();

    [GeneratedRegex(@"^(\d{2}\.\d{2}\.\d{4})\s")]
    private static partial Regex DottedForm();

    [GeneratedRegex(@"^(\d{8})\s")]
    private static partial Regex SpaceForm();

    /// <summary>Cells sometimes carry a full path rather than a bare name,
    /// so Path.GetFileName runs first regardless — mirroring
    /// TurnaroundTime.ExtractDocDate. First matching shape wins; only the
    /// space form has a second reading (MMddyyyy, then yyyyMMdd — a 20xx
    /// prefix is an impossible month, a valid month prefix is an impossible
    /// year, so the two readings essentially never both parse).</summary>
    public static DateOnly? Parse(string filenameCell)
    {
        var name = Path.GetFileName(filenameCell);

        var dash = DashForm().Match(name);
        if (dash.Success) return TryExact(dash.Groups[1].Value, "yyyyMMdd");

        var dotted = DottedForm().Match(name);
        if (dotted.Success) return TryExact(dotted.Groups[1].Value, "MM.dd.yyyy");

        var space = SpaceForm().Match(name);
        if (space.Success)
            return TryExact(space.Groups[1].Value, "MMddyyyy")
                ?? TryExact(space.Groups[1].Value, "yyyyMMdd");

        return null;
    }

    private static DateOnly? TryExact(string text, string format) =>
        DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
