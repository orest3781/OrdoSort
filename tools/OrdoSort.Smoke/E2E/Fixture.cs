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

    public void Dispose()
    {
        // Delete the guid parent, not just the scenario dir, so nothing is
        // left behind under %TEMP%.
        var parent = Path.GetDirectoryName(Root);
        try { if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
        catch { /* a locked temp file is not a product defect */ }
    }
}
