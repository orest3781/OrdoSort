using System.IO.Compression;
using System.Text;
using OrdoSort.Core;
using PdfSharp.Pdf;

namespace OrdoSort.Smoke.E2E;

/// <summary>An isolated temp directory for one scenario, plus builders for
/// every kind of input the tools take. Fixtures are generated in code so the
/// repo carries no binary test assets — the same approach
/// UnlockProbeTests.MakeEncrypted already uses for encrypted PDFs.
///
/// Everything a scenario writes must land under Root. Disposal deletes the
/// tree; failures there are reported but never change a run's verdict,
/// because a locked temp file is not a product defect.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; }

    private Fixture(string root) => Root = root;

    public static Fixture Create(string scenarioName)
    {
        var safe = string.Concat(scenarioName.Select(
            c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var root = Path.Combine(
            Path.GetTempPath(), "ordo_e2e_" + Guid.NewGuid().ToString("N"), safe);
        Directory.CreateDirectory(root);
        return new Fixture(root);
    }

    /// <summary>Create and return a subdirectory of Root.</summary>
    public string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string Resolve(string relativePath)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public string Pdf(string relativePath, string text = "SAMPLE")
    {
        var path = Resolve(relativePath);
        MinimalPdf.Write(path, text);
        return path;
    }

    /// <summary>Same shape as UnlockProbeTests.MakeEncrypted: a real
    /// PdfSharp document with user and owner passwords set.</summary>
    public string EncryptedPdf(string relativePath, string userPassword, int pages = 1)
    {
        var path = Resolve(relativePath);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPassword;
        doc.Save(path);
        return path;
    }

    /// <summary>Random bytes under a .pdf name — PdfSharp cannot even find
    /// the "%PDF" prefix, which is the damaged-file case the tools must
    /// report rather than crash on.</summary>
    public string CorruptPdf(string relativePath)
    {
        var path = Resolve(relativePath);
        var bytes = new byte[512];
        new Random(20260809).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string Zip(string relativePath, params (string entryName, string sourcePath)[] entries)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, source) in entries)
            archive.CreateEntryFromFile(source, name);
        return path;
    }

    /// <summary>Entry names written verbatim — no sanitising — so a
    /// traversal name like @"..\..\escaped.txt" survives into the archive.
    /// That is the only way to build the zip-slip fixture honestly.</summary>
    public string RawZip(string relativePath, params (string entryName, byte[] bytes)[] entries)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var s = entry.Open();
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    public string EmptyZip(string relativePath)
    {
        var path = Resolve(relativePath);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        return path;
    }

    public string Text(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A minimal xlsx: a zip of two XML parts plus the package
    /// relationships, with every cell a shared string.
    ///
    /// Written by hand because OrdoSort.Core's XlsxTable is a READER only
    /// (internal static, one Read method) — there is no writer to reuse, and
    /// adding one to ship just to serve fixtures would put test-only code in
    /// the product. The shape mirrors what XlsxTable.Read looks for:
    /// xl/sharedStrings.xml and a first worksheet whose cells carry t="s"
    /// indices into it. The round-trip is asserted in
    /// E2EHarnessTests.XlsxFixtureRoundTripsThroughSweptTable — if this
    /// drifts from the reader, that test fails rather than a report
    /// scenario mysteriously finding zero rows.</summary>
    public string Xlsx(string relativePath, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var path = Resolve(relativePath);

        var all = new List<IReadOnlyList<string>> { headers };
        all.AddRange(rows);

        // Shared-string table: every distinct cell value, in first-seen order.
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in all.SelectMany(r => r))
            if (!index.ContainsKey(value)) { index[value] = strings.Count; strings.Add(value); }

        static string Esc(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static string Col(int i)
        {
            var name = "";
            for (i++; i > 0; i = (i - 1) / 26) name = (char)('A' + (i - 1) % 26) + name;
            return name;
        }

        var sst = new StringBuilder();
        sst.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sst.Append($"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{strings.Count}" uniqueCount="{strings.Count}">""");
        foreach (var s in strings) sst.Append($"<si><t>{Esc(s)}</t></si>");
        sst.Append("</sst>");

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < all.Count; r++)
        {
            sheet.Append($"""<row r="{r + 1}">""");
            for (var c = 0; c < all[r].Count; c++)
                sheet.Append($"""<c r="{Col(c)}{r + 1}" t="s"><v>{index[all[r][c]]}</v></c>""");
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        const string contentTypes = """
            <?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>
            """;
        const string rootRels = """
            <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """;
        const string workbook = """
            <?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
            """;
        const string workbookRels = """
            <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
            """;

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string content)
        {
            using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Part("[Content_Types].xml", contentTypes);
        Part("_rels/.rels", rootRels);
        Part("xl/workbook.xml", workbook);
        Part("xl/_rels/workbook.xml.rels", workbookRels);
        Part("xl/sharedStrings.xml", sst.ToString());
        Part("xl/worksheets/sheet1.xml", sheet.ToString());
        return path;
    }

    public void Dispose()
    {
        // Delete the guid parent, not just the scenario dir, so nothing is
        // left behind under %TEMP%.
        var parent = Path.GetDirectoryName(Root);
        try { if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
        catch { /* a locked temp file is not a product defect */ }
    }
}
