namespace OrdoSort.Core;

/// <summary>One page of a table: which source columns it shows, how wide
/// each is, and which source rows fall on it. <see cref="HeaderRow"/> is
/// repeated at the top of every page — a page of values with no headings is
/// unreadable.</summary>
public sealed record TablePage(
    IReadOnlyList<int> Columns, IReadOnlyList<double> Widths,
    IReadOnlyList<int> Rows, int HeaderRow = 0);

/// <summary>Laying a table onto pages, as arithmetic — no PdfSharp, no
/// fonts, no I/O, so it can be checked exhaustively. The same compose/draw
/// seam <see cref="BoxLabels.ComposeDrawing"/> uses: this decides, the
/// renderer draws.</summary>
public static class TablePages
{
    /// <summary>Split <paramref name="table"/> into pages that fit
    /// <paramref name="pageWidth"/> x <paramref name="pageHeight"/>.
    ///
    /// Columns are sized to their widest cell, measured by
    /// <paramref name="measure"/> — injected so a test can use character
    /// counts and a renderer can use real text metrics. Columns that do not
    /// all fit are split into consecutive groups, each repeated down the
    /// table's rows, so a wide sheet reads across then onward rather than
    /// being silently cropped. A single column too wide for the page still
    /// takes a page of its own: a group is never empty, which is also what
    /// stops this looping forever.</summary>
    public static IReadOnlyList<TablePage> Paginate(
        IReadOnlyList<IReadOnlyList<string>> table,
        double pageWidth, double pageHeight, double rowHeight,
        Func<string, double> measure)
    {
        if (table.Count == 0) return Array.Empty<TablePage>();
        var columnCount = table.Max(r => r.Count);
        if (columnCount == 0) return Array.Empty<TablePage>();

        var widths = new double[columnCount];
        foreach (var row in table)
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], measure(row[c] ?? ""));

        var groups = new List<List<int>>();
        var current = new List<int>();
        var used = 0.0;
        for (var c = 0; c < columnCount; c++)
        {
            if (current.Count > 0 && used + widths[c] > pageWidth)
            {
                groups.Add(current);
                current = new List<int>();
                used = 0;
            }
            current.Add(c);
            used += widths[c];
        }
        groups.Add(current);

        // One header row is repeated on every page, so the body gets what is
        // left of the height.
        var bodyRowsPerPage = Math.Max(1, (int)(pageHeight / rowHeight) - 1);
        var bodyRows = Enumerable.Range(1, table.Count - 1).ToList();

        var pages = new List<TablePage>();
        foreach (var group in groups)
        {
            var groupWidths = group.Select(c => widths[c]).ToList();
            if (bodyRows.Count == 0)
            {
                pages.Add(new TablePage(group, groupWidths, Array.Empty<int>()));
                continue;
            }
            for (var start = 0; start < bodyRows.Count; start += bodyRowsPerPage)
                pages.Add(new TablePage(group, groupWidths,
                    bodyRows.Skip(start).Take(bodyRowsPerPage).ToList()));
        }
        return pages;
    }
}
