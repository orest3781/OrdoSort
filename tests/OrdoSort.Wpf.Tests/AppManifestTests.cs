using System.Xml.Linq;

namespace OrdoSort.Wpf.Tests;

/// <summary>DW-48: both apps opt in to Windows long paths. Record folders on
/// a share nest deep, and a roster-built name can be long; without the
/// opt-in, a path past 260 characters fails in the parts of Windows that
/// still enforce the old limit (file dialogs, the shell) even where .NET's
/// own file calls would cope. The opt-in only takes effect where the
/// machine's LongPathsEnabled policy is on, and costs nothing where it
/// isn't.</summary>
public class AppManifestTests
{
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Theory]
    [InlineData("OrdoSort.Wpf")]
    [InlineData("BoxLabels.App")]
    public void TheAppDeclaresItselfLongPathAware(string project)
    {
        var manifest = XDocument.Load(Path.Combine(Repo.Root, "src", project, "app.manifest"));

        var longPathAware = manifest.Descendants(WindowsSettings2016 + "longPathAware").SingleOrDefault();

        Assert.NotNull(longPathAware);
        Assert.Equal("true", longPathAware!.Value.Trim());
    }
}
