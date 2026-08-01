namespace OrdoSort.Core.Tests;

/// <summary>Naming modes: pickup rule, config keys, removed-mode migration.</summary>
public class NamingConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordonamecfg_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("insert", "plain.pdf", false)]
    [InlineData("insert", "a--b.pdf", true)]
    [InlineData("replace", "plain.pdf", true)]
    [InlineData("prefix", "plain.pdf", true)]
    [InlineData("append", "plain.pdf", true)]
    [InlineData("append", "not-a-pdf.txt", false)]
    public void PickupRequiresTheMarkerOnlyInInsertMode(string mode, string file, bool eligible) =>
        Assert.Equal(eligible, Scanner.Eligible(file, mode));

    [Fact]
    public void NewModesPassLoadValidation()
    {
        foreach (var mode in new[] { "prefix", "append" })
        {
            var path = Path.Combine(_dir, $"{mode}.json");
            File.WriteAllText(path, $$"""{"inbox":"C:/in","naming_mode":"{{mode}}"}""");
            Assert.Equal(mode, Config.Load(path).NamingMode);
        }
    }

    [Fact]
    public void GlobalTemplateModeMigratesToReplaceAtLoad()
    {
        // the "template" naming mode was removed 2026-08 — a config that
        // still says it must load quietly as "replace" (the closest
        // surviving semantics), never brick startup with a validation error
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{date}-{name}"}""");
        Assert.Equal("replace", Config.Load(path).NamingMode);
    }

    [Fact]
    public void RouteTemplateModeMigratesToReplaceAtLoad()
    {
        // per-route overrides migrate too — including routes arriving via
        // the destinations.json side file, the live path since the split
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"inbox":"C:/in"}""");
        File.WriteAllText(Path.Combine(_dir, "destinations.json"), """
            {"routes":[{"label":"A","path":"C:/a","naming_mode":"template","naming_template":"{name}!"}]}
            """);
        var route = Config.Load(path).Routes.Single();
        Assert.Equal("replace", route.NamingMode);
        // the orphaned key is untyped now — it survives in Extras, not lost
        Assert.True(route.Extras.ContainsKey("naming_template"));
    }

    [Fact]
    public void OrphanedNamingTemplateKeysSurviveAsInertExtras()
    {
        // naming_template is no longer a typed key; a hand-edited leftover
        // rides the Extras round trip like any unknown key — not stripped
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{date}-{name}"}""");
        var back = Config.Load(path);
        Config.Save(back, path);
        Assert.Contains("naming_template", File.ReadAllText(path));
        Assert.Equal("replace", Config.Load(path).NamingMode);
    }
}
