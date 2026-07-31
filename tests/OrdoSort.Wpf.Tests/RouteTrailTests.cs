using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>The trail rule marks the route this session filed to last. Unlike
/// the ⏎ badge it does NOT depend on enter_commits — the rule is a record of
/// where the last document went, not a promise about what Enter will do.</summary>
public class RouteTrailTests
{
    /// <summary>A fixture with a second, valid destination — the default has
    /// only one route, which cannot show "this one and not the others".</summary>
    private static ShellFixture TwoRoutes() => new(c =>
    {
        var second = Path.Combine(Path.GetDirectoryName(c.Inbox)!, "routed2");
        Directory.CreateDirectory(second);
        c.Routes.Add(new Route { Label = "Second", Path = second });
    });

    [Fact]
    public void NothingIsMarkedBeforeAnythingIsFiled()
    {
        using var fx = TwoRoutes();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.All(fx.Shell.Routes, r => Assert.False(r.IsLastUsed));
    }

    [Fact]
    public async Task FilingMarksThatRouteAndOnlyThatRoute()
    {
        using var fx = TwoRoutes();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(1);

        Assert.False(fx.Shell.Routes[0].IsLastUsed);
        Assert.True(fx.Shell.Routes[1].IsLastUsed);
    }

    [Fact]
    public async Task TheMarkMovesWithTheMostRecentFiling()
    {
        using var fx = TwoRoutes();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(1);
        fx.Shell.TypedName = "DOE JANE";
        await fx.Shell.OnRouteAsync(0);

        Assert.True(fx.Shell.Routes[0].IsLastUsed);
        Assert.False(fx.Shell.Routes[1].IsLastUsed);
    }

    [Fact]
    public async Task TheRuleTracksTheRouteEvenWhenEnterCommitsIsOff()
    {
        using var fx = new ShellFixture(c =>
        {
            c.EnterCommits = false;
            var second = Path.Combine(Path.GetDirectoryName(c.Inbox)!, "routed2");
            Directory.CreateDirectory(second);
            c.Routes.Add(new Route { Label = "Second", Path = second });
        });
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(1);

        // the ⏎ badge stays on the first destination — first-destination
        // mode never follows the button that was actually pressed …
        Assert.True(fx.Shell.Routes[0].IsEnterTarget);
        Assert.False(fx.Shell.Routes[1].IsEnterTarget);
        // … but the trail still records where the document went
        Assert.True(fx.Shell.Routes[1].IsLastUsed);
        Assert.False(fx.Shell.Routes[0].IsLastUsed);
    }
}
