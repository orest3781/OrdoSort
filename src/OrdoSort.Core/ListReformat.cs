using System.Globalization;

namespace OrdoSort.Core;

/// <summary>
/// Turn a pasted spreadsheet column (or row) into one comma-delimited line,
/// one item per line, or a run joined by any delimiter you like — closing up
/// the empty rows as it goes and saying how many it closed. A column copied
/// out of Excel/Sheets lands on the clipboard newline-separated (CRLF, LF, or
/// bare CR depending on where it came from); a row
/// lands tab-separated — both are just "cells", so both separators are split
/// on the same pass. Pure string work, no I/O, and it never throws: a
/// pasted blob is untrusted input by nature, and the worst a bad paste
/// should do here is produce an empty result, never an exception dialog.
/// </summary>
public static class ListReformat
{
    /// <summary>How the surviving items are joined back together.</summary>
    public enum OutputShape
    {
        /// <summary>One line, items separated by commas.</summary>
        CommaLine,
        /// <summary>One item per line (CRLF), so the cleaned-up list pastes
        /// straight back into the spreadsheet column it came from.</summary>
        OnePerLine,
        /// <summary>One line, items separated by <see cref="Options.CustomDelimiter"/>.</summary>
        CustomDelimiter,
    }

    /// <summary><paramref name="SpaceAfterComma"/> applies to whichever
    /// separator is in play and is meaningless (ignored) for
    /// <see cref="OutputShape.OnePerLine"/>. <paramref name="CustomDelimiter"/>
    /// is used verbatim and only when <paramref name="Shape"/> asks for it.</summary>
    public sealed record Options(
        bool Quote = false,
        bool SpaceAfterComma = false,
        bool Dedupe = false,
        OutputShape Shape = OutputShape.CommaLine,
        string CustomDelimiter = "");

    /// <summary>Text is the joined result; Items is how many cells survived
    /// (post-trim, post-dedupe) and made it into Text; DuplicatesDropped is
    /// always 0 when <see cref="Options.Dedupe"/> is off; BlanksRemoved is
    /// how many empty rows/cells were closed up.</summary>
    public sealed record Result(string Text, int Items, int DuplicatesDropped, int BlanksRemoved = 0);

    private static readonly char[] CellSeparators = { '\r', '\n', '\t' };

    public static Result Reformat(string input, Options opt)
    {
        if (string.IsNullOrWhiteSpace(input)) return new Result("", 0, 0);

        // "\r\n" is collapsed to one terminator BEFORE splitting. Splitting
        // on '\r' and '\n' separately would leave an empty entry between them
        // for every CRLF pair, which is invisible when blanks are merely
        // dropped but would double every number blanksRemoved reports below.
        var cells = input.Replace("\r\n", "\n").Split(CellSeparators)
            .Select(TrimCell)
            .ToList();

        // Trailing empties are not gaps the user can see: Excel ends a copied
        // column with a newline, and a dragged selection routinely runs past
        // the data, so counting them would have every clean paste claim it
        // removed a row. Everything up to the last real value does count.
        var lastKept = cells.FindLastIndex(cell => cell.Length > 0);
        var blanksRemoved = 0;
        var items = new List<string>(cells.Count);
        for (var i = 0; i <= lastKept; i++)
        {
            if (cells[i].Length > 0) items.Add(cells[i]);
            else blanksRemoved++;
        }

        var duplicatesDropped = 0;
        if (opt.Dedupe)
        {
            // OrdinalIgnoreCase, first occurrence wins: spreadsheet cells are
            // free-text labels, not case-sensitive identifiers, so "Widget"
            // and "widget" pasted from the same column are the same item to
            // a human skimming the result — keeping whichever spelling
            // appeared first is simpler to reason about than picking a
            // "canonical" casing that isn't actually in the data.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (seen.Add(item)) deduped.Add(item);
                else duplicatesDropped++;
            }
            items = deduped;
        }

        if (opt.Quote)
        {
            // Plain wrap — item becomes 'item'. This is NOT SQL (or any
            // other) escaping: an item that already contains an apostrophe
            // is left untouched inside the quotes, so "O'Brien" becomes
            // 'O'Brien', not 'O''Brien' or 'O\'Brien'. Callers who need a
            // real escaped literal have to do that themselves.
            for (var i = 0; i < items.Count; i++) items[i] = $"'{items[i]}'";
        }

        // OnePerLine hard-codes CRLF rather than Environment.NewLine: the
        // result is bound for the Windows clipboard and a spreadsheet reading
        // it back as one cell per row, not for a text file on this machine.
        var separator = opt.Shape switch
        {
            OutputShape.OnePerLine => "\r\n",
            OutputShape.CustomDelimiter => opt.CustomDelimiter + (opt.SpaceAfterComma ? " " : ""),
            _ => opt.SpaceAfterComma ? ", " : ",",
        };
        var text = string.Join(separator, items);
        return new Result(text, items.Count, duplicatesDropped, blanksRemoved);
    }

    /// <summary>Trim, plus the characters string.Trim leaves behind. A cell
    /// holding only a zero-width space (U+200B), a byte-order mark (U+FEFF) or
    /// a soft hyphen (U+00AD) is empty to every human who looks at it, but
    /// those are Unicode category Format rather than whitespace, so Trim keeps
    /// them and the cell used to reach the output as an item you cannot see.
    ///
    /// Both ENDS only, never the interior: the same category carries the
    /// zero-width joiner that holds an emoji sequence together and the marks
    /// that set text direction, so stripping throughout would quietly corrupt
    /// real values. GetUnicodeCategory(char) reports Surrogate for either half
    /// of a surrogate pair, so this can never cut one in half.</summary>
    private static string TrimCell(string cell)
    {
        static bool Trimmable(char c) =>
            char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

        var start = 0;
        var end = cell.Length - 1;
        while (start <= end && Trimmable(cell[start])) start++;
        while (end >= start && Trimmable(cell[end])) end--;
        return cell.Substring(start, end - start + 1);
    }
}
