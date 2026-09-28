using System.Xml.Linq;

namespace OrdoSort.Wpf.Tests;

/// <summary>DW-44: the fix that keeps a highlighted drop-down row readable is
/// measured by <see cref="HighlightContrastTests.HighlightedComboBoxItemTextMeetsWcagAa"/>
/// for three shapes of combo box, in every scheme: plain items (no item
/// template), KvpValueTemplate and FontChoiceTemplate. The app has more combo
/// boxes than that, and the rest were covered only by someone having traced
/// that each is one of the three. This does the tracing on every build: a
/// combo box in any window that shows its items another way (its own item
/// template, DisplayMemberPath) fails here, naming the file, until it is
/// measured too.</summary>
public class ComboBoxShapeCoverageTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The item templates the contrast theory measures.</summary>
    private static readonly string[] MeasuredTemplates =
    {
        "{StaticResource KvpValueTemplate}",
        "{StaticResource FontChoiceTemplate}",
    };

    [Fact]
    public void EveryComboBoxShowsItsItemsInAShapeTheContrastTestsMeasure()
    {
        var files = Directory.GetFiles(Path.Combine(Repo.Root, "src"), "*.xaml", SearchOption.AllDirectories);
        var unmeasured = new List<string>();
        var seen = 0;
        foreach (var file in files)
        {
            foreach (var combo in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Wpf + "ComboBox"))
            {
                seen++;
                var where = $"{Path.GetFileName(file)} line {((System.Xml.IXmlLineInfo)combo).LineNumber}";
                var template = (string?)combo.Attribute("ItemTemplate");
                if (template is not null && !MeasuredTemplates.Contains(template))
                    unmeasured.Add($"{where}: ItemTemplate {template}");
                if (combo.Attribute("DisplayMemberPath") is not null)
                    unmeasured.Add($"{where}: DisplayMemberPath");
                if (combo.Element(Wpf + "ComboBox.ItemTemplate") is not null)
                    unmeasured.Add($"{where}: its own ItemTemplate");
            }
        }

        Assert.True(seen >= 10, $"found only {seen} combo boxes: is the XAML scan still looking in the right place?");
        Assert.True(unmeasured.Count == 0,
            "combo boxes whose highlighted row nothing measures:\n" + string.Join("\n", unmeasured));
    }
}
