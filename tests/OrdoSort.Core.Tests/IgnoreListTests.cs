using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>IgnoreList backs both dashboards' set-aside checklists (spec
/// decision 7): membership is ordinal — never case-folded, matching the
/// repo's no-normalization stance — and the persisted list must round-trip
/// through Config so a restart can't silently re-include a value.</summary>
public class IgnoreListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoign_" + Guid.NewGuid());
    public IgnoreListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void MembershipIsOrdinal()
    {
        var list = new IgnoreList(new[] { "ECAA" });
        Assert.True(list.IsIgnored("ECAA"));
        Assert.False(list.IsIgnored("ecaa"));   // a different value, not a different casing
        Assert.False(list.IsIgnored("Email"));
    }

    [Fact]
    public void AnEmptyListIgnoresNothing()
    {
        var list = new IgnoreList(Array.Empty<string>());
        Assert.False(list.IsIgnored("ECAA"));
        Assert.Empty(list.Ignored);
    }

    [Fact]
    public void DuplicateIgnoredValuesCollapseFirstSeenOrder()
    {
        var list = new IgnoreList(new[] { "ECAA", "PORTAL", "ECAA" });
        Assert.Equal(new[] { "ECAA", "PORTAL" }, list.Ignored);
    }

    [Fact]
    public void DiscoverCountsAndFlagsEveryDistinctValue()
    {
        var list = new IgnoreList(new[] { "ECAA" });
        var entries = list.Discover(new[] { "Email", "FAX", "Email", "ECAA", "Email" });
        Assert.Equal(new[]
        {
            new IgnoreList.Entry("Email", 3, false),
            new IgnoreList.Entry("ECAA", 1, true),
            new IgnoreList.Entry("FAX", 1, false),
        }, entries);   // count descending, then ordinal — "ECAA" < "FAX"
    }

    [Fact]
    public void TatIgnoredSourcesRoundTripsThroughConfigWithItsExactJsonName()
    {
        var cfg = new Config { TatIgnoredSources = { "ECAA", "PORTAL" } };
        var path = Path.Combine(_dir, "t.json");
        Config.Save(cfg, path);
        Assert.Contains("\"tat_ignored_sources\"", File.ReadAllText(path));
        var back = Config.Load(path);
        Assert.Equal(new[] { "ECAA", "PORTAL" }, back.TatIgnoredSources);
    }
}
