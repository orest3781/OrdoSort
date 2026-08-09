namespace OrdoSort.Core;

/// <summary>
/// Combines a batch of externally produced table files — daily per-category
/// sweep CSVs (tab- or comma-delimited) and PECF .xlsx reports — into one
/// table, on top of Task 1's Csv.ReadTable format dispatch. Files in a batch
/// rarely share identical headers (a category added mid-run, a column
/// dropped between exports), so this reads every file's own header row
/// rather than assuming one shared layout, and unions them — first-seen
/// order, ordinal, no case-folding, matching the rest of the repo's
/// no-normalization stance — so a reader can iterate Headers once and find
/// every column any file contributed. Per-file failures (missing file,
/// corrupt xlsx) are isolated to FileErrors exactly like Intake.Expand's
/// Error field: the batch keeps going, and Load itself never throws.
/// </summary>
public static class SweptTable
{
    /// <summary>One data row, already mapped onto the union header set —
    /// every union header is a key, even ones this row's own source file
    /// never had, so callers can index Cells[header] without a
    /// TryGetValue guard.</summary>
    public sealed record Row(IReadOnlyDictionary<string, string> Cells, string SourceFile);

    public sealed record Table(
        IReadOnlyList<string> Headers,
        IReadOnlyList<Row> Rows,
        int FilesRead,
        IReadOnlyList<string> FileErrors);

    /// <summary>Never throws: each path is read independently, and a failure
    /// — a missing file, a corrupt xlsx, anything Csv.ReadTable throws —
    /// becomes one FileErrors entry while the rest of the batch still loads.
    /// Two passes over the files that did read: the first fixes each file's
    /// own column keys (trim, blank-cell and duplicate-name handling) and
    /// grows the union Headers; the second builds each Row against the now-
    /// final union so every row carries every header, ragged source rows
    /// tolerated (short rows fill "", long rows drop the extra cells).</summary>
    public static Table Load(IReadOnlyList<string> paths)
    {
        var headers = new List<string>();
        var headerSeen = new HashSet<string>(StringComparer.Ordinal);
        var fileErrors = new List<string>();
        var filesRead = 0;
        var readFiles = new List<(string Path, List<List<string>> Table, List<string> ColumnKeys)>();

        foreach (var path in paths)
        {
            List<List<string>> table;
            try
            {
                table = Csv.ReadTable(path);
            }
            catch (Exception ex)
            {
                fileErrors.Add($"{path}: {ex.Message}");
                continue;
            }
            filesRead++;
            if (table.Count == 0) continue;   // read fine, just empty — counted, nothing to contribute

            var columnKeys = ColumnKeys(table[0]);
            foreach (var key in columnKeys)
                if (headerSeen.Add(key)) headers.Add(key);

            readFiles.Add((path, table, columnKeys));
        }

        var rows = new List<Row>();
        foreach (var (path, table, columnKeys) in readFiles)
        {
            for (var r = 1; r < table.Count; r++)
            {
                var data = table[r];
                var cells = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
                foreach (var header in headers) cells[header] = "";
                for (var c = 0; c < columnKeys.Count && c < data.Count; c++)
                    cells[columnKeys[c]] = data[c];
                rows.Add(new Row(cells, path));
            }
        }

        return new Table(headers, rows, filesRead, fileErrors);
    }

    /// <summary>One file's header row turned into unique dictionary keys: a
    /// blank cell (after Trim) becomes "Column {i}" (1-based) before
    /// duplicates are even considered, then the first occurrence of any name
    /// keeps it plain and every later occurrence is suffixed "{name} ({i})"
    /// with its own 1-based column index — a repeated header must never
    /// silently overwrite an earlier column's data in the row dictionaries
    /// built from these keys.</summary>
    private static List<string> ColumnKeys(List<string> headerRow)
    {
        var keys = new List<string>(headerRow.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < headerRow.Count; i++)
        {
            var name = headerRow[i].Trim();
            if (name.Length == 0) name = $"Column {i + 1}";
            keys.Add(seen.Add(name) ? name : $"{name} ({i + 1})");
        }
        return keys;
    }
}
