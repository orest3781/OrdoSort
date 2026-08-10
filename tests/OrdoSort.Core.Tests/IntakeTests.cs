using System.Diagnostics;

namespace OrdoSort.Core.Tests;

public class IntakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "intaketest_" + Guid.NewGuid());
    public IntakeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void FilesPassThroughUnchanged()
    {
        var a = Touch("a.pdf");
        var b = Touch("b.pdf");
        var r = Intake.Expand(new[] { a, b }, recursive: false, extensions: null);
        Assert.Equal(0, r.Ignored);
        Assert.Equal("", r.Error);
        Assert.Equal(new[] { a, b }.OrderBy(x => x, NaturalSort.Instance), r.Files);
    }

    [Fact]
    public void FolderExpandsTopLevelOnlyByDefault()
    {
        var top = Touch("top.pdf");
        Touch(Path.Combine("sub", "nested.pdf"));
        var r = Intake.Expand(new[] { _dir }, recursive: false, extensions: null);
        Assert.Equal(new[] { top }, r.Files);
    }

    [Fact]
    public void FolderExpandsRecursivelyWhenAsked()
    {
        var top = Touch("top.pdf");
        var nested = Touch(Path.Combine("sub", "nested.pdf"));
        var r = Intake.Expand(new[] { _dir }, recursive: true, extensions: null);
        Assert.Equal(new[] { top, nested }.OrderBy(x => x, NaturalSort.Instance), r.Files);
    }

    [Fact]
    public void ExtensionSetFiltersDotlessLowercase()
    {
        var pdf = Touch("keep.pdf");
        Touch("skip.txt");
        var caps = Touch("also.PDF");   // extension match is case-insensitive
        var r = Intake.Expand(new[] { _dir }, recursive: false, new HashSet<string> { "pdf" });
        Assert.Equal(new[] { pdf, caps }.OrderBy(x => x, NaturalSort.Instance), r.Files);
        Assert.Equal(1, r.Ignored);
    }

    [Fact]
    public void EmptyOrNullExtensionSetAcceptsEveryFile()
    {
        var pdf = Touch("a.pdf");
        var txt = Touch("b.txt");
        var r = Intake.Expand(new[] { _dir }, recursive: false, new HashSet<string>());
        Assert.Equal(new[] { pdf, txt }.OrderBy(x => x, NaturalSort.Instance), r.Files);
        Assert.Equal(0, r.Ignored);
    }

    [Fact]
    public void MissingPathIsIgnoredNotThrown()
    {
        var missing = Path.Combine(_dir, "ghost.pdf");
        var r = Intake.Expand(new[] { missing }, recursive: false, extensions: null);
        Assert.Empty(r.Files);
        Assert.Equal(1, r.Ignored);
        Assert.Equal("", r.Error);
    }

    [Fact]
    public void EmptyInputReturnsEmptyResult()
    {
        var r = Intake.Expand(Array.Empty<string>(), recursive: false, extensions: null);
        Assert.Empty(r.Files);
        Assert.Equal(0, r.Ignored);
        Assert.Equal("", r.Error);
    }

    [Fact]
    public void OutputIsSortedInNaturalOrderRegardlessOfInputOrder()
    {
        var f10 = Touch("file10.pdf");
        var f2 = Touch("file2.pdf");
        var r = Intake.Expand(new[] { f10, f2 }, recursive: false, extensions: null);
        Assert.Equal(new[] { f2, f10 }, r.Files);
    }

    /// <summary>Regression for the .NET 8 Directory.EnumerateFiles(path, "*",
    /// SearchOption.AllDirectories) behavior: it aborts the ENTIRE walk with
    /// UnauthorizedAccessException at the first subfolder it can't open —
    /// losing every file already found, not just the unreadable subtree.
    /// aaa_denied sorts before top.csv and zzz_ok alphabetically, so a
    /// top-down walk reaches it first; pre-fix, that abort loses top.csv too,
    /// even though it's a sibling never inside the denied folder.</summary>
    [Fact]
    public void InaccessibleSubfolderSkipsJustThatSubfolder()
    {
        var top = Touch("top.csv");
        var hidden = Touch(Path.Combine("aaa_denied", "hidden.csv"));
        var reachable = Touch(Path.Combine("zzz_ok", "reachable.csv"));
        var deniedDir = Path.Combine(_dir, "aaa_denied");
        var user = Environment.UserDomainName + "\\" + Environment.UserName;

        RunIcacls(deniedDir, "/deny", $"{user}:(OI)(CI)R");
        try
        {
            // Elevated/backup-privilege sessions (an admin console, some CI
            // runners) can bypass a deny ACE outright. If enumerating the
            // denied folder still succeeds here, this fixture can't
            // reproduce the abort on this machine — a vacuous pass beats a
            // false failure.
            bool bypassed;
            try
            {
                Directory.EnumerateFiles(deniedDir).Any();
                bypassed = true;
            }
            catch (UnauthorizedAccessException)
            {
                // expected — the deny ACE bit; fall through to the real assertion.
                bypassed = false;
            }
            if (bypassed) return;

            var r = Intake.Expand(new[] { _dir }, recursive: true, extensions: null);

            Assert.Contains(top, r.Files);
            Assert.Contains(reachable, r.Files);
            Assert.DoesNotContain(hidden, r.Files);
            Assert.Equal("", r.Error);
        }
        finally
        {
            // Always undo the deny — otherwise Dispose()'s Directory.Delete
            // of _dir fails on the still-locked-out aaa_denied subtree.
            RunIcacls(deniedDir, "/remove:d", user);
        }
    }

    private static void RunIcacls(params string[] args)
    {
        var psi = new ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
