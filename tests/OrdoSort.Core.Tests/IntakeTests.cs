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
}
