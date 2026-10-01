using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>merge_archive_originals: off unless config.json says otherwise,
/// so every existing config keeps today's behaviour (a merge leaves its
/// originals where they are).</summary>
public class MergeArchiveSettingTests
{
    [Fact]
    public void ItIsOffByDefault() => Assert.False(new Config().MergeArchiveOriginals);

    [Fact]
    public void AConfigWithoutTheKeyLeavesOriginalsWhereTheyAre() =>
        Assert.False(JsonSerializer.Deserialize<Config>("""{"inbox":"C:/in"}""")!.MergeArchiveOriginals);

    [Fact]
    public void TheKeyTurnsItOnAndIsSavedUnderItsName()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "config.json");

        Config.Save(new Config { MergeArchiveOriginals = true }, path);

        Assert.Contains("\"merge_archive_originals\": true", File.ReadAllText(path));
        Assert.True(Config.Load(path).MergeArchiveOriginals);
    }
}
