using System.Text.RegularExpressions;

namespace OrdoSort.Wpf.Tests;

/// <summary>Table rules v2 hold for every table, not just the ones someone
/// remembered: every DataGrid in src is attached to ExplorerColumns, and
/// every column declares a pixel width (no Auto, no *). A source scan, so
/// a new window can't slip past it.</summary>
public class ExplorerColumnsCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("couldn't find OrdoSort.sln");
    }

    public static TheoryData<string> GridWindows()
    {
        var data = new TheoryData<string>();
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", SearchOption.AllDirectories))
            if (File.ReadAllText(xaml).Contains("<DataGrid ")) data.Add(Path.GetFileName(xaml));
        return data;
    }

    private static string PathOf(string xamlName) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), xamlName, SearchOption.AllDirectories).Single();

    [Theory, MemberData(nameof(GridWindows))]
    public void EveryGridIsAttached(string xamlName)
    {
        var xaml = File.ReadAllText(PathOf(xamlName));
        var code = File.ReadAllText(PathOf(xamlName) + ".cs");
        foreach (Match grid in Regex.Matches(xaml, "<DataGrid [^>]*x:Name=\"(\\w+)\""))
            Assert.Contains($"ExplorerColumns.Attach({grid.Groups[1].Value}", code);
    }

    [Theory, MemberData(nameof(GridWindows))]
    public void EveryColumnHasAPixelWidth(string xamlName)
    {
        var xaml = File.ReadAllText(PathOf(xamlName));
        foreach (Match column in Regex.Matches(xaml, "<DataGrid(Text|CheckBox)Column\\s[^>]*>"))
            Assert.Matches("Width=\"\\d+\"", column.Value);
    }

    [Fact]
    public void TheOldAutoFitIsNotUsedAnywhere()
    {
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("DataGridColumnCap.Track", File.ReadAllText(cs));
    }
}
