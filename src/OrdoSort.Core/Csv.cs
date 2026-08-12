using System.Text;

namespace OrdoSort.Core;

/// <summary>Shared CSV plumbing: a minimal RFC-4180-ish parser (quoted fields,
/// embedded commas/quotes/newlines), an Excel formula-injection guard for
/// writing, and the .xlsx-vs-delimited-text format dispatch — hand-written in
/// the same self-contained style as XlsxTable, since both the roster reader
/// and the history exporter need exactly this and nothing more.</summary>
internal static class Csv
{
    /// <summary>Parse delimited text into rows of fields. The delimiter is
    /// sniffed from the first line (up to the first '\n', before any
    /// parsing): tabs and commas are counted, and tab wins only if it
    /// strictly outnumbers comma — otherwise, including the no-delimiter
    /// single-column case, comma wins. RFC-4180-ish: quoted fields may embed
    /// commas, quotes ("") and newlines. Blank rows are filtered.</summary>
    internal static List<List<string>> Parse(string text)
    {
        var delimiter = SniffDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            // A quote opens a quoted field only at the START of a field, per
            // RFC 4180 — anywhere else it is a literal character, which is
            // what Excel does too. Without the length check, one stray quote
            // used as an inch mark (`3" pipe`) or around a nickname
            // (`Robert "Bob" Smith`) flipped the parser into quote mode with
            // no closing quote ahead of it, so the delimiters and newlines of
            // every REMAINING row were appended as literal text into that one
            // cell: the rest of the file silently collapsed into a single
            // field and those rows vanished from the returned table, with no
            // exception and nothing to signal the loss. A dropped roster row
            // means a real person stops being matchable; a dropped sweep row
            // means a document disappears from a production or TAT count.
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == '"') field.Append(c);
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0)).ToList();
    }

    /// <summary>Count tabs vs. commas in the first line only; a naive
    /// character count, not delimiter-aware — a comma-heavy line still reads
    /// as comma even if a quoted field on that line happens to contain a
    /// tab.</summary>
    private static char SniffDelimiter(string text)
    {
        var newline = text.IndexOf('\n');
        var firstLine = newline >= 0 ? text.AsSpan(0, newline) : text.AsSpan();
        var tabs = 0;
        var commas = 0;
        foreach (var c in firstLine)
        {
            if (c == '\t') tabs++;
            else if (c == ',') commas++;
        }
        return tabs > commas ? '\t' : ',';
    }

    /// <summary>Excel formula-injection guard: a value starting with =, +, -,
    /// @, tab or CR gets a leading apostrophe so opening the file can't
    /// execute anything; a value containing a comma, quote or newline gets
    /// quoted, with embedded quotes doubled.</summary>
    internal static string EscapeField(string value)
    {
        // formula-injection guard first
        if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0)
            value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>Join fields into one CSV row, escaping each.</summary>
    internal static string WriteRow(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(EscapeField));

    /// <summary>Read a table from disk: .xlsx via the zip/XML reader,
    /// anything else as delimited text. Throws plain exceptions only —
    /// dialog-ready wrapping (rejecting old Excel formats, translating
    /// failures into a user-facing message) is the caller's concern.</summary>
    internal static List<List<string>> ReadTable(string path) =>
        path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? XlsxTable.Read(path)
            : Parse(ReadText(File.ReadAllBytes(path)));

    /// <summary>Decode delimited text: an explicit BOM wins, otherwise the
    /// bytes are decoded as UTF-8 and, only if that is not valid UTF-8, as
    /// Latin-1.
    ///
    /// This used to be a bare <c>File.ReadAllText(path, Encoding.UTF8)</c>.
    /// That decodes a file with no BOM as UTF-8 regardless of what it
    /// actually is, and .NET's default UTF8Encoding does not throw on
    /// invalid bytes — it substitutes U+FFFD per bad run. Excel's plain
    /// "CSV (Comma delimited)" export writes the system ANSI codepage, not
    /// UTF-8, so a roster or sweep exported that way turned GARCÍA into
    /// GARC�A. Nothing failed and nothing was reported; the name was simply
    /// wrong from then on, and MatchMerge builds its exact-match key from
    /// that string, so an automatic merge silently degraded into a manual
    /// no-match. Decoding strictly and falling back keeps the accented
    /// bytes intact instead.
    ///
    /// Latin-1 is the fallback rather than Windows-1252 because it needs no
    /// extra encoding provider and agrees with 1252 across the whole
    /// accented-letter range (0xA0-0xFF); they differ only at 0x80-0x9F,
    /// which holds punctuation like smart quotes rather than letters.</summary>
    internal static string ReadText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
