using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Videos in the filing loop (spec 2026-09-29-media-filing-loop,
/// release 2), headless: real Session, History and temp folders; the video
/// player, the Edge viewer and the date reader are fakes.</summary>
public class VideoLoopTests
{
    private sealed class FixedDates : IDateTakenReader
    {
        public DateTaken Read(string path) => new(new DateTime(2023, 7, 4), DateTakenSource.Metadata);
    }

    private static ShellFixture Started(FakeVideoPlayer video, params string[] files)
    {
        var fx = new ShellFixture(cfg =>
        {
            cfg.Media.Enabled = true;
            cfg.Media.VideosEnabled = true;
        }, dates: new FixedDates(), video: video);
        foreach (var file in files) fx.AddInboxFile(file, "clip");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        return fx;
    }

    [Fact]
    public void AVideoIsShownInTheVideoPaneNotEdge()
    {
        var video = new FakeVideoPlayer();
        using var fx = Started(video, "IMG_0042.MOV");

        Assert.True(fx.Shell.IsVideoShown);
        Assert.False(fx.Shell.IsDocumentShown);
        Assert.EndsWith("IMG_0042.MOV", Assert.Single(video.Opened));
        Assert.Empty(fx.Viewer.Shown);
        Assert.Equal("Taken 4 Jul 2023 · from the photo", fx.Shell.TakenLine);
    }

    [Fact]
    public async Task AVideoIsNamedByDateAndFiledIntoMedia()
    {
        var video = new FakeVideoPlayer();
        using var fx = Started(video, "IMG_0042.MOV");
        fx.Shell.TypedName = "BACKYARD";
        Assert.Equal("20230704-BACKYARD.mov", fx.Shell.Preview);

        await fx.Shell.OnRouteAsync(0);

        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20230704-BACKYARD.mov")));
    }

    /// <summary>Shown straight from the inbox (no local copy), the player
    /// must let go of the file before it moves, as Edge must. The fake holds
    /// the file as a real player does, so a move that didn't wait would fail.</summary>
    [Fact]
    public async Task ThePlayerLetsGoOfTheFileBeforeItMoves()
    {
        var video = new FakeVideoPlayer { LockFiles = true };
        using var fx = Started(video, "a.mov", "b.mov");

        fx.Shell.TypedName = "ONE";
        await fx.Shell.OnRouteAsync(0);

        var released = video.Calls.IndexOf("release");
        var nextOpened = video.Calls.IndexOf("open " + Path.Combine(fx.Inbox, "b.mov"));
        Assert.InRange(released, 0, nextOpened - 1);
        Assert.False(File.Exists(Path.Combine(fx.Inbox, "a.mov")));
        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20230704-ONE.mov")));
    }

    [Fact]
    public async Task APdfAfterAVideoGoesBackToEdgeAndStopsThePlayer()
    {
        var video = new FakeVideoPlayer();
        var fx = new ShellFixture(cfg => cfg.Media.VideosEnabled = true, dates: new FixedDates(), video: video);
        using var _ = fx;
        fx.AddInboxFile("0001.mov", "clip");            // sorts before the PDF
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.True(fx.Shell.IsVideoShown);

        await fx.Shell.OnRouteAsync(0);

        Assert.False(fx.Shell.IsVideoShown);
        Assert.EndsWith("20240115--111111.pdf", fx.Viewer.Shown.Last());
        Assert.False(video.IsOpen);
    }

    [Fact]
    public async Task TheVideoPaneEmptiesWhenTheSessionEnds()
    {
        var video = new FakeVideoPlayer();
        using var fx = Started(video, "a.mov");

        await fx.Shell.OnRouteAsync(0);

        Assert.True(fx.Shell.IsDone);
        Assert.False(fx.Shell.IsVideoShown);
        Assert.False(video.IsOpen);
    }

    [Fact]
    public void TheEngineWarmsUpOnlyWhenASessionHasAVideo()
    {
        var withVideo = new FakeVideoPlayer();
        using (Started(withVideo, "a.mov")) Assert.Equal(1, withVideo.WarmUps);

        var withoutVideo = new FakeVideoPlayer();
        var fx = new ShellFixture(cfg => cfg.Media.VideosEnabled = true, video: withoutVideo);
        using var _ = fx;
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.Equal(0, withoutVideo.WarmUps);
    }

    [Fact]
    public async Task UndoBringsAFiledVideoBackIntoThePlayer()
    {
        var video = new FakeVideoPlayer();
        using var fx = Started(video, "a.mov");
        fx.Shell.TypedName = "ONE";
        await fx.Shell.OnRouteAsync(0);

        await fx.Shell.OnUndoAsync();

        Assert.True(File.Exists(Path.Combine(fx.Inbox, "a.mov")));
        Assert.True(fx.Shell.IsVideoShown);
        Assert.EndsWith("a.mov", video.Opened.Last());
    }

    [Fact]
    public void VideosStayOutUntilTurnedOn()
    {
        var video = new FakeVideoPlayer();
        var fx = new ShellFixture(cfg => cfg.Media.Enabled = true, video: video);
        using var _ = fx;
        fx.AddInboxFile("a.mov", "clip");
        fx.Shell.Initialize();

        Assert.Equal("1 other file ignored", fx.Shell.DetailLine);
    }
}
