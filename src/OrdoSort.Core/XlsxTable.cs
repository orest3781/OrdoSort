using System.IO.Compression;
using System.Xml.Linq;

namespace OrdoSort.Core;

/// <summary>A minimal xlsx reader: first worksheet, values as strings. An xlsx
/// is a zip of XML, and the roster needs nothing Excel-specific — so this is
/// hand-written in the same self-contained style as the CSV parser, the wav
/// writer and the minimal PDFs, rather than pulling a package in. Formatted
/// numbers arrive as their raw value and date cells as their serial number;
/// rosters carry names and ids, so neither matters here.</summary>
internal static class XlsxTable
{
    private static readonly XNamespace Ns =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static List<List<string>> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>The same reader over an open stream — what lets a converter
    /// read an xlsx out of a zip entry's bytes without writing a temp
    /// file.</summary>
    internal static List<List<string>> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var s = sst.Open();
            shared = XDocument.Load(s).Descendants(Ns + "si")
                .Select(si => string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)))
                .ToList();
        }

        var sheet = FirstSheetEntry(zip)
            ?? zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            ?? throw new InvalidDataException("no worksheet inside the workbook");

        var rows = new List<List<string>>();
        using var sheetStream = sheet.Open();
        foreach (var row in XDocument.Load(sheetStream).Descendants(Ns + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(Ns + "c"))
            {
                var column = ColumnIndex((string?)c.Attribute("r") ?? "");
                while (cells.Count < column) cells.Add("");   // Excel omits empty cells

                var type = (string?)c.Attribute("t");
                var value = type == "inlineStr"
                    ? string.Concat(c.Descendants(Ns + "t").Select(t => t.Value))
                    : c.Element(Ns + "v")?.Value ?? "";
                if (type == "s" && int.TryParse(value, out var i))
                {
                    if (i >= 0 && i < shared.Count) value = shared[i];
                    else throw new InvalidDataException($"shared string {i} is out of range");
                }
                cells.Add(value);
            }
            rows.Add(cells);
        }
        // a blank row (Excel writes one for a deleted first line more often
        // than you'd think) must not masquerade as the header row — same
        // filter the CSV path already applies
        return rows.Where(r => r.Any(c => c.Length > 0)).ToList();
    }

    /// <summary>The workbook's real first sheet: the first &lt;sheet&gt; listed
    /// in xl/workbook.xml, resolved through its r:id via
    /// xl/_rels/workbook.xml.rels. "sheet1.xml" is just a part name — nothing
    /// stops Excel from reordering tabs without renaming the underlying
    /// files, so the tab a user thinks of as "first" isn't reliably the part
    /// named sheet1. Returns null on anything unexpected (missing parts,
    /// unresolvable r:id, malformed XML) rather than throwing, so the
    /// caller's sheet1.xml fallback still covers today's simpler workbooks —
    /// including every xlsx these tests already build without a
    /// workbook.xml at all.</summary>
    private static ZipArchiveEntry? FirstSheetEntry(ZipArchive zip)
    {
        try
        {
            if (zip.GetEntry("xl/workbook.xml") is not { } wb) return null;
            if (zip.GetEntry("xl/_rels/workbook.xml.rels") is not { } relsEntry) return null;

            string? rid;
            using (var s = wb.Open())
                rid = (string?)XDocument.Load(s).Descendants(Ns + "sheet")
                    .FirstOrDefault()?.Attribute(RelNs + "id");
            if (rid is null) return null;

            string? target;
            using (var s = relsEntry.Open())
                target = XDocument.Load(s).Descendants(PackageRelNs + "Relationship")
                    .FirstOrDefault(r => (string?)r.Attribute("Id") == rid)
                    ?.Attribute("Target")?.Value;
            if (target is null) return null;

            // Target is a path relative to xl/ (occasionally written as an
            // absolute "/xl/worksheets/sheetN.xml") — normalise both forms
            // to the zip entry name
            var normalized = target.TrimStart('/');
            var entryName = normalized.StartsWith("xl/", StringComparison.Ordinal)
                ? normalized
                : "xl/" + normalized;
            return zip.GetEntry(entryName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"C7" → 2. The letters are a base-26 column, the digits the row.</summary>
    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsAsciiLetterUpper(ch)) break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return Math.Max(0, n - 1);
    }
}
