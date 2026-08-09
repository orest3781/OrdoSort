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
            else if (c == '"') inQuotes = true;
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
    /// anything else as delimited text read as UTF-8. Throws plain
    /// exceptions only — dialog-ready wrapping (rejecting old Excel formats,
    /// translating failures into a user-facing message) is the caller's
    /// concern.</summary>
    internal static List<List<string>> ReadTable(string path) =>
        path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? XlsxTable.Read(path)
            : Parse(File.ReadAllText(path, Encoding.UTF8));
}
