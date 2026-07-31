namespace OrdoSort.Core.Tests;

/// <summary>New naming modes: pickup rule, config keys, load validation.</summary>
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
    [InlineData("template", "plain.pdf", true)]
    [InlineData("template", "not-a-pdf.txt", false)]
    public void PickupRequiresTheMarkerOnlyInInsertMode(string mode, string file, bool eligible) =>
        Assert.Equal(eligible, Scanner.Eligible(file, mode));

    [Fact]
    public void NamingTemplateKeysRoundTrip()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = new Config { NamingMode = "template", NamingTemplate = "{date}-{name}" };
        cfg.Routes.Add(new Route { Label = "A", Path = "C:/a",
            NamingMode = "template", NamingTemplate = "{name}!" });
        Config.Save(cfg, path);
        var back = Config.Load(path);
        Assert.Equal("{date}-{name}", back.NamingTemplate);
        Assert.Equal("{name}!", back.Routes.Single().NamingTemplate);
    }

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
    public void GlobalTemplateModeWithBadTemplateFailsLoadReadably()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{bogus}"}""");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(path));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void RouteTemplatesAreNotValidatedAtLoad()
    {
        var path = Path.Combine(_dir, "route.json");
        File.WriteAllText(path, """
            {"inbox":"C:/in","routes":[
              {"label":"A","path":"C:/a","naming_mode":"template","naming_template":"{bogus}"}]}
            """);
        Assert.Equal("{bogus}", Config.Load(path).Routes.Single().NamingTemplate);
    }
}
