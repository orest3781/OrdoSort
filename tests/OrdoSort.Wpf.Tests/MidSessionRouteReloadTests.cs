using System.Text.Json;
using System.Text.Json.Nodes;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>A save mid-session (Match &amp; merge, a merge-type toggle)
/// re-reads the shared destinations from disk. A peer station that reordered
/// them must not change where this session's buttons file.</summary>
public class MidSessionRouteReloadTests
{
    private static ShellFixture TwoRouteSession(out string taxDir, out string payDir)
    {
        string tax = "", pay = "";
        var fx = new ShellFixture(cfg =>
        {
            var root = Path.GetDirectoryName(cfg.Inbox)!;
            tax = Path.Combine(root, "tax");
            pay = Path.Combine(root, "payroll");
            Directory.CreateDirectory(tax);
            Directory.CreateDirectory(pay);
            cfg.Routes.Clear();
            cfg.Routes.Add(new Route { Label = "Tax", Path = tax, Color = "#2e7d32" });
            cfg.Routes.Add(new Route { Label = "Payroll", Path = pay, Color = "#1565c0", Suffix = "PAY", AppendSuffix = true });
        });
        taxDir = tax;
        payDir = pay;
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk
        fx.Shell.StartProcessing();
        return fx;
    }

    /// <summary>A peer swaps the order of the destinations behind this
    /// station's back, then a tool on this station saves its own state —
    /// which pulls the peer's destinations into the live config.</summary>
    private static void PeerReversesDestinationsThenAToolSaves(ShellFixture fx)
    {
        var node = JsonNode.Parse(File.ReadAllText(fx.CfgPath))!.AsObject();
        var routes = node["routes"]!.AsArray();
        var first = routes[0]!.DeepClone();
        var second = routes[1]!.DeepClone();
        node["routes"] = new JsonArray(second, first);
        File.WriteAllText(fx.CfgPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        fx.Shell.SaveConfigNow();
    }

    [Fact]
    public async Task APeerReorderingDestinationsMidSessionDoesNotRedirectTheButton()
    {
        using var fx = TwoRouteSession(out var taxDir, out var payDir);
        PeerReversesDestinationsThenAToolSaves(fx);
        Assert.Equal("Payroll", fx.Shell.Cfg.Routes[0].Label);   // the live config did change

        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);   // the button still labelled "Tax"

        Assert.True(File.Exists(Path.Combine(taxDir, "20240115-SMITH JOHN-111111.pdf")));
        Assert.Empty(Directory.GetFiles(payDir));
        Assert.Equal("✓  Filed to Tax", fx.Shell.LastActionText);
    }

    [Fact]
    public void ThePreviewFollowsTheSessionsButtonNotTheReloadedConfig()
    {
        using var fx = TwoRouteSession(out _, out _);
        PeerReversesDestinationsThenAToolSaves(fx);

        // Enter targets button 0 ("Tax", no suffix) — the preview must not
        // pick up Payroll's suffix just because Payroll is now first on disk.
        fx.Shell.TypedName = "SMITH JOHN";
        Assert.Equal("20240115-SMITH JOHN-111111.pdf", fx.Shell.Preview);
    }
}
