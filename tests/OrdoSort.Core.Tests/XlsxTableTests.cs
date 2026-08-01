using System.IO.Compression;
using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The xlsx reader is minimal and hand-written (a zip of XML), in the
/// same self-contained style as the CSV parser. These tests build real
/// workbooks with ZipArchive so nothing depends on Excel being anywhere.</summary>
public class XlsxTableTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoxlsx_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteXlsx(string sheetXml, string? sharedStringsXml = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(content);
        }
        Entry("xl/worksheets/sheet1.xml", sheetXml);
        if (sharedStringsXml is not null) Entry("xl/sharedStrings.xml", sharedStringsXml);
        return path;
    }

    private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    [Fact]
    public void ReadsInlineAndSharedStringsAndNumbers()
    {
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\">" +
            "<c r=\"A1\" t=\"s\"><v>0</v></c>" +                                  // shared: Last
            "<c r=\"B1\" t=\"inlineStr\"><is><t>First</t></is></c>" +
            "<c r=\"C1\" t=\"s\"><v>1</v></c>" +                                  // shared: Control
            "</row>" +
            "<row r=\"2\">" +
            "<c r=\"A2\" t=\"s\"><v>2</v></c>" +                                  // GARCIA
            "<c r=\"B2\" t=\"inlineStr\"><is><t>MARIA</t></is></c>" +
            "<c r=\"C2\"><v>409585208</v></c>" +                                  // number cell
            "</row>" +
            "</sheetData></worksheet>",
            $"<sst {Ns}><si><t>Last</t></si><si><t>Control</t></si><si><t>GARCIA</t></si></sst>");

        var rows = XlsxTable.Read(path);
        Assert.Equal(new[] { "Last", "First", "Control" }, rows[0]);
        Assert.Equal(new[] { "GARCIA", "MARIA", "409585208" }, rows[1]);
    }

    [Fact]
    public void SkippedCellsLandInTheRightColumns()
    {
        // Excel omits empty cells entirely; the r attribute is the truth
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>C</t></is></c></row>" +
            "</sheetData></worksheet>");

        var rows = XlsxTable.Read(path);
        Assert.Equal(new[] { "A", "", "C" }, rows[0]);
    }

    [Fact]
    public void LoadRosterReadsAnXlsxEndToEnd()
    {
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Last</t></is></c>" +
            "<c r=\"B1\" t=\"inlineStr\"><is><t>First</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>Control</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>EVANS</t></is></c>" +
            "<c r=\"B2\" t=\"inlineStr\"><is><t>FRANK</t></is></c>" +
            "<c r=\"C2\"><v>12345</v></c></row>" +
            "</sheetData></worksheet>");

        var roster = MatchMerge.LoadRoster(path, "First", "Last", "Control");
        var person = Assert.Single(roster.Lookup("EVANS", "FRANK"));
        Assert.Equal("12345", person.ControlId);
        Assert.Equal(new[] { "Last", "First", "Control" }, MatchMerge.ReadHeaders(path));
    }

    [Fact]
    public void AGarbageXlsxIsAReadableRosterError()
    {
        var path = Path.Combine(_dir, "junk.xlsx");
        File.WriteAllText(path, "this is not a zip");
        Assert.Throws<RosterException>(() => MatchMerge.LoadRoster(path, "F", "L", "C"));
    }

    /// <summary>The workbook's real first sheet is whichever &lt;sheet&gt;
    /// comes first in xl/workbook.xml, resolved through its r:id in
    /// xl/_rels/workbook.xml.rels — NOT whichever worksheet part happens to
    /// be named sheet1.xml. Excel is happy to reorder tabs without renaming
    /// the underlying parts.</summary>
    [Fact]
    public void FirstSheetComesFromTheWorkbookOrderNotTheFilename()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(content);
            }
            // workbook order says the tab wired to rId2 (-> sheet2.xml) is first
            Entry("xl/workbook.xml",
                $"<workbook {Ns} xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets>" +
                "<sheet name=\"RealFirst\" sheetId=\"1\" r:id=\"rId2\"/>" +
                "<sheet name=\"Decoy\" sheetId=\"2\" r:id=\"rId1\"/>" +
                "</sheets></workbook>");
            Entry("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"x\" Target=\"worksheets/sheet2.xml\"/>" +
                "</Relationships>");
            // decoy: filename sheet1.xml, but the workbook doesn't call it first
            Entry("xl/worksheets/sheet1.xml",
                $"<worksheet {Ns}><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>DecoyHeader</t></is></c></row>" +
                "</sheetData></worksheet>");
            // the real first sheet per workbook order
            Entry("xl/worksheets/sheet2.xml",
                $"<worksheet {Ns}><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Last</t></is></c>" +
                "<c r=\"B1\" t=\"inlineStr\"><is><t>First</t></is></c>" +
                "<c r=\"C1\" t=\"inlineStr\"><is><t>Control</t></is></c></row>" +
                "</sheetData></worksheet>");
        }

        Assert.Equal(new[] { "Last", "First", "Control" }, XlsxTable.Read(path)[0]);
    }

    [Fact]
    public void AnOutOfRangeSharedStringIndexIsAReadableError()
    {
        // headers are clean so LoadRoster gets past header validation and
        // actually reaches the offending data cell — isolates the guard
        // instead of accidentally passing on a "missing header" error
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Last</t></is></c>" +
            "<c r=\"B1\" t=\"inlineStr\"><is><t>First</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>Control</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>5</v></c>" +   // only 1 shared string exists
            "<c r=\"B2\" t=\"inlineStr\"><is><t>X</t></is></c>" +
            "<c r=\"C2\"><v>1</v></c></row>" +
            "</sheetData></worksheet>",
            $"<sst {Ns}><si><t>OnlyOne</t></si></sst>");

        Assert.Throws<RosterException>(() => MatchMerge.LoadRoster(path, "First", "Last", "Control"));
    }

    [Fact]
    public void ANegativeSharedStringIndexIsAlsoRejected()
    {
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Last</t></is></c>" +
            "<c r=\"B1\" t=\"inlineStr\"><is><t>First</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>Control</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>-1</v></c>" +
            "<c r=\"B2\" t=\"inlineStr\"><is><t>X</t></is></c>" +
            "<c r=\"C2\"><v>1</v></c></row>" +
            "</sheetData></worksheet>",
            $"<sst {Ns}><si><t>OnlyOne</t></si></sst>");

        Assert.Throws<RosterException>(() => MatchMerge.LoadRoster(path, "First", "Last", "Control"));
    }

    [Fact]
    public void BlankFirstRowDoesNotBecomeTheHeaderRow()
    {
        var path = WriteXlsx(
            $"<worksheet {Ns}><sheetData>" +
            "<row r=\"1\"></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Last</t></is></c>" +
            "<c r=\"B2\" t=\"inlineStr\"><is><t>First</t></is></c>" +
            "<c r=\"C2\" t=\"inlineStr\"><is><t>Control</t></is></c></row>" +
            "</sheetData></worksheet>");

        Assert.Equal(new[] { "Last", "First", "Control" }, MatchMerge.ReadHeaders(path));
    }

    [Theory]
    [InlineData("roster.xls")]
    [InlineData("roster.xlsm")]
    public void OldExcelFormatsGetAReadableError(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not actually a workbook");
        var ex = Assert.Throws<RosterException>(() => MatchMerge.ReadHeaders(path));
        Assert.Contains(".xlsx or .csv", ex.Message);
    }
}
