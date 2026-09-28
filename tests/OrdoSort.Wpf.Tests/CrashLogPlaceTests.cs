namespace OrdoSort.Wpf.Tests;

/// <summary>QC-21: crash.log was written beside config.json, on the shared
/// folder every station reads, and it records full exception text: document
/// names and paths included. It now lives in this PC's own OrdoSort folder,
/// where the table layouts and the preview profile already are. Joins the
/// shared collection because it changes the static <c>App._crashDir</c>.</summary>
[Collection(HighlightContrastTests.Name)]
public class CrashLogPlaceTests : UiTest, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordocrashplace_").FullName;

    public CrashLogPlaceTests(HighlightContrastFixture fx) : base(fx) { }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void CrashLogIsKeptOnThisPcNotBesideTheSharedConfig() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrdoSort"),
            App.DefaultCrashDir);

    /// <summary>That folder may not exist yet on a PC that never ran the
    /// preview; the first crash must still be recorded.</summary>
    [Fact]
    public void TheFirstCrashCreatesTheFolder()
    {
        var before = App._crashDir;
        var folder = Path.Combine(_dir, "not", "yet", "there");
        App._crashDir = folder;
        try
        {
            Assert.True(App.LogCrash(new InvalidOperationException("the first crash")));
            Assert.Contains("the first crash", File.ReadAllText(Path.Combine(folder, "crash.log")));
        }
        finally { App._crashDir = before; }
    }
}
