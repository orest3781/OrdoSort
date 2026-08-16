using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OrdoSort.Core;

/// <summary>A minimal xlsx writer — XlsxTable's counterpart, hand-written in
/// the same self-contained style rather than pulling a package in. Unlike
/// the reader (which tolerates the barest zip a test can build), this must
/// write a package Excel itself opens: [Content_Types].xml, the package
/// rels, xl/workbook.xml, its rels, and one worksheet part per sheet.
/// Strings are inline (no shared-string table — these exports are written
/// once and read by a human, not diffed for size), numbers are real numeric
/// cells so Excel doesn't flag every count with a green triangle. No
/// styling, no column widths, no date cells — callers pre-format dates as
/// strings, which is how every date already reaches the UI anyway.</summary>
public static class XlsxWriter
{
    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows);

    /// <summary>C2 fix (reports-hub-phase2 final fix wave, 2026-08-16):
    /// previously opened the destination directly via
    /// <c>ZipFile.Open(path, ZipArchiveMode.Create)</c>, which maps to
    /// <c>FileMode.CreateNew</c> — atomic, but it throws "file already
    /// exists" the moment a caller writes to the same path twice, which is
    /// exactly what happens exporting to the suggested
    /// <c>turnaround-&lt;yyyyMMdd&gt;.xlsx</c> name a second time in one day
    /// (TurnaroundPageViewModel.ExportAsync). Routed through
    /// <see cref="AtomicPlace.TryReplace"/> instead — the same temp-sibling
    /// + swap-in idiom Zipper.cs's own Save-As branch already uses for the
    /// identical reason (see that class's CreateZip doc comment) — rather
    /// than re-deriving a second FileMode.Create-based overwrite by hand:
    /// overwrite now succeeds, AND a failure partway through leaves whatever
    /// was previously at <paramref name="path"/> untouched instead of a
    /// half-written file, which the old direct-open version did not
    /// guarantee either. Throws IOException on failure, same as the old
    /// ZipFile.Open path did — every caller already catches
    /// IOException/UnauthorizedAccessException around this call
    /// (TurnaroundPageViewModel.ExportAsync).</summary>
    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        if (!AtomicPlace.TryReplace(path, tmp => WriteCore(tmp, sheets), out var error))
            throw new IOException($"couldn't write the workbook: {error}");
    }

    private static void WriteCore(string path, IReadOnlyList<Sheet> sheets)
    {
        // tmp is a GUID-named sibling AtomicPlace itself picked (never a
        // contested name), so FileMode.Create vs .CreateNew makes no
        // difference here — Create is used because it's the natural
        // "open for writing a fresh file" primitive once atomicity is
        // AtomicPlace's job, not this method's.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var contentTypes = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        var workbook = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var workbookRels = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (var i = 0; i < sheets.Count; i++)
        {
            var n = i + 1;
            contentTypes.Append(
                $"<Override PartName=\"/xl/worksheets/sheet{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            workbook.Append(
                $"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
            workbookRels.Append(
                $"<Relationship Id=\"rId{n}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
            Entry(zip, $"xl/worksheets/sheet{n}.xml", SheetXml(sheets[i]));
        }

        contentTypes.Append("</Types>");
        workbook.Append("</sheets></workbook>");
        workbookRels.Append("</Relationships>");

        Entry(zip, "[Content_Types].xml", contentTypes.ToString());
        Entry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        Entry(zip, "xl/workbook.xml", workbook.ToString());
        Entry(zip, "xl/_rels/workbook.xml.rels", workbookRels.ToString());
    }

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            var row = sheet.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (value is null) continue;   // omitted; the reader back-fills from r=
                var cellRef = $"{ColumnRef(c)}{r + 1}";
                if (value is int or long or double or decimal)
                {
                    var text = value switch
                    {
                        double d => d.ToString("R", CultureInfo.InvariantCulture),
                        decimal m => m.ToString(CultureInfo.InvariantCulture),
                        long l => l.ToString(CultureInfo.InvariantCulture),
                        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
                    };
                    sb.Append($"<c r=\"{cellRef}\"><v>{text}</v></c>");
                }
                else
                {
                    sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(Escape(value.ToString() ?? ""))
                      .Append("</t></is></c>");
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>0 → "A", 25 → "Z", 26 → "AA" — the inverse of
    /// XlsxTable.ColumnIndex.</summary>
    private static string ColumnRef(int index)
    {
        var s = "";
        for (var i = index; i >= 0; i = i / 26 - 1)
            s = (char)('A' + i % 26) + s;
        return s;
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
