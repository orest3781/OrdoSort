namespace OrdoSort.Core;

/// <summary>
/// Turn a pasted spreadsheet column (or row) into a single comma-delimited
/// line. A column copied out of Excel/Sheets lands on the clipboard newline-
/// separated (CRLF, LF, or bare CR depending on where it came from); a row
/// lands tab-separated — both are just "cells", so both separators are split
/// on the same pass. Pure string work, no I/O, and it never throws: a
/// pasted blob is untrusted input by nature, and the worst a bad paste
/// should do here is produce an empty result, never an exception dialog.
/// </summary>
public static class ListReformat
{
    public sealed record Options(bool Quote = false, bool SpaceAfterComma = false, bool Dedupe = false);

    /// <summary>Text is the joined result; Items is how many cells survived
    /// (post-trim, post-dedupe) and made it into Text; DuplicatesDropped is
    /// always 0 when <see cref="Options.Dedupe"/> is off.</summary>
    public sealed record Result(string Text, int Items, int DuplicatesDropped);

    private static readonly char[] CellSeparators = { '\r', '\n', '\t' };

    public static Result Reformat(string input, Options opt)
    {
        if (string.IsNullOrWhiteSpace(input)) return new Result("", 0, 0);

        // Splitting on '\r' and '\n' separately (rather than treating "\r\n"
        // as one token) leaves an empty entry between them for a CRLF pair —
        // harmless, since it's trimmed to "" and dropped right below, same as
        // any other blank line in the paste.
        var items = input.Split(CellSeparators)
            .Select(cell => cell.Trim())
            .Where(cell => cell.Length > 0)
            .ToList();

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

        var separator = opt.SpaceAfterComma ? ", " : ",";
        var text = string.Join(separator, items);
        return new Result(text, items.Count, duplicatesDropped);
    }
}
