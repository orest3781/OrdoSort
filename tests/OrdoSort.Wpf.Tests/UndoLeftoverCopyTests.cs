using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-04 at the shell: an Undo that got the document back but could
/// not delete the filed copy is an undo, with a warning about the extra copy.
/// The screen must follow the session back to the restored document; before
/// this, the error ended the undo early and the next document stayed on
/// screen while the session had moved back.</summary>
public class UndoLeftoverCopyTests : IDisposable
{
    public void Dispose() => Commit.SurvivingSourceHookForTests = null;

    [Fact]
    public async Task AnUndoThatLeavesTheFiledCopyBehindShowsTheDocumentAgainAndWarns()
    {
        using var fx = new ShellFixture();
        var first = fx.AddInboxFile("20240101--111111.pdf");
        fx.AddInboxFile("20240102--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal("20240102--222222.pdf", fx.Shell.CurrentFilename);

        var filed = Path.Combine(fx.RouteDir, "20240101-SMITH JOHN-111111.pdf");
        var bytes = File.ReadAllBytes(filed);
        // Only for this test's own file: the hook is shared by every test in
        // the process, and other shell tests file and undo in parallel.
        Commit.SurvivingSourceHookForTests = moved =>
        {
            if (string.Equals(Path.GetFullPath(moved), Path.GetFullPath(filed), StringComparison.OrdinalIgnoreCase))
                File.WriteAllBytes(filed, bytes);
        };

        await fx.Shell.OnUndoAsync();

        Assert.True(File.Exists(first));
        Assert.Equal("20240101--111111.pdf", fx.Shell.CurrentFilename);
        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("could not be removed", warning.Message);
        Assert.Contains(filed, warning.Message);
    }
}
