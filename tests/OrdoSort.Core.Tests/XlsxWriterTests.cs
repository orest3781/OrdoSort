using System.IO.Compression;
using System.Xml.Linq;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The writer's output must be a real xlsx: readable back through
/// XlsxTable AND structurally complete enough for Excel (content types,
/// package rels, workbook rels — the parts the reader's minimal fallback
/// path tolerates being absent, but Excel does not).</summary>
public class XlsxWriterTests : IDisposable
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Ct =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly string _dir = Directory.CreateTempSubdirectory("ordoxw_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string Write(params XlsxWriter.Sheet[] sheets)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        XlsxWriter.Write(path, sheets);
        return path;
    }

    private static XDocument Part(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var s = zip.GetEntry(entry)!.Open();
        return XDocument.Load(s);
    }

    [Fact]
    public void StringsAndNumbersRoundTripThroughTheReader()
    {
        var path = Write(new XlsxWriter.Sheet("Summary", new object?[][]
        {
            new object?[] { "Label", "Count", "Percent" },
            new object?[] { "0-1 days", 20953, 96.6 },
        }));
        var rows = XlsxTable.Read(path);
        Assert.Equal(new[] { "Label", "Count", "Percent" }, rows[0]);
        Assert.Equal(new[] { "0-1 days", "20953", "96.6" }, rows[1]);
    }

    [Fact]
    public void XmlSpecialCharactersInCellTextSurvive()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { "a<b&c>\"d'" },
        }));
        Assert.Equal("a<b&c>\"d'", XlsxTable.Read(path)[0][0]);
    }

    [Fact]
    public void NullCellsAreOmittedAndLaterColumnsStayPut()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { "A", null, "C" },
        }));
        // XlsxTable back-fills the omitted cell from the r= reference
        Assert.Equal(new[] { "A", "", "C" }, XlsxTable.Read(path)[0]);
    }

    [Fact]
    public void NumericCellsCarryNoTypeAttribute()
    {
        var path = Write(new XlsxWriter.Sheet("S", new object?[][]
        {
            new object?[] { 42, "x" },
        }));
        var cells = Part(path, "xl/worksheets/sheet1.xml")
            .Descendants(Main + "c").ToList();
        Assert.Null(cells[0].Attribute("t"));                       // number
        Assert.Equal("inlineStr", (string?)cells[1].Attribute("t")); // string
    }

    [Fact]
    public void TwoSheetsProduceTwoNamedPartsInOrder()
    {
        var path = Write(
            new XlsxWriter.Sheet("Summary", new object?[][] { new object?[] { "s" } }),
            new XlsxWriter.Sheet("Documents", new object?[][] { new object?[] { "d" } }));

        var names = Part(path, "xl/workbook.xml").Descendants(Main + "sheet")
            .Select(s => (string?)s.Attribute("name")).ToList();
        Assert.Equal(new[] { "Summary", "Documents" }, names);

        Assert.Equal("d", (string)Part(path, "xl/worksheets/sheet2.xml")
            .Descendants(Main + "t").Single());
        // XlsxTable resolves the FIRST sheet through the workbook rels
        Assert.Equal("s", XlsxTable.Read(path)[0][0]);
    }

    /// <summary>The parts Excel refuses to open a package without — the
    /// reader tolerates their absence, so the round-trip test alone can't
    /// prove they exist.</summary>
    [Fact]
    public void PackageCarriesContentTypesAndRels()
    {
        var path = Write(
            new XlsxWriter.Sheet("A", new object?[][] { new object?[] { 1 } }),
            new XlsxWriter.Sheet("B", new object?[][] { new object?[] { 2 } }));
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        Assert.NotNull(zip.GetEntry("xl/_rels/workbook.xml.rels"));
        var overrides = Part(path, "[Content_Types].xml").Descendants(Ct + "Override")
            .Select(o => (string?)o.Attribute("PartName")).ToList();
        Assert.Contains("/xl/workbook.xml", overrides);
        Assert.Contains("/xl/worksheets/sheet1.xml", overrides);
        Assert.Contains("/xl/worksheets/sheet2.xml", overrides);
    }

    [Fact]
    public void ColumnsPastZUseTwoLetterReferences()
    {
        var row = Enumerable.Range(0, 28).Select(i => (object?)$"c{i}").ToArray();
        var path = Write(new XlsxWriter.Sheet("S", new[] { row }));
        Assert.Equal(row.Select(v => (string)v!), XlsxTable.Read(path)[0]);
    }

    /// <summary>C2 fix: Write used to open the destination via
    /// ZipFile.Open(path, ZipArchiveMode.Create), which is FileMode.CreateNew
    /// under the hood — the second export to the same suggested filename
    /// (e.g. turnaround-20260816.xlsx, exported twice in one day) threw "file
    /// already exists" instead of overwriting. Now routed through
    /// AtomicPlace.TryReplace (temp sibling + swap-in, same idiom Zipper.cs's
    /// Save-As branch already uses), so writing twice to the same path must
    /// succeed both times and the second write's content must win.</summary>
    [Fact]
    public void WritingTwiceToTheSamePathOverwritesInsteadOfThrowing()
    {
        var path = Path.Combine(_dir, "turnaround.xlsx");

        XlsxWriter.Write(path, new[]
        {
            new XlsxWriter.Sheet("S", new object?[][] { new object?[] { "first" } }),
        });
        var ex = Record.Exception(() => XlsxWriter.Write(path, new[]
        {
            new XlsxWriter.Sheet("S", new object?[][] { new object?[] { "second" } }),
        }));

        Assert.Null(ex);
        Assert.Equal("second", XlsxTable.Read(path)[0][0]);
    }
}
