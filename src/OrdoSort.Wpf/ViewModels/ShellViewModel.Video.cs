using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>
/// Videos in the filing loop (spec 2026-09-29-media-filing-loop, release 2).
/// A video is named and filed like a photo; what differs is the pane: the
/// video player takes the Edge pane's place while a video is on screen, and
/// its file is let go of before it moves, as Edge's is.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The video pane's timeline and keys.</summary>
    public VideoScrubber Video { get; }

    private bool _isVideoShown;
    /// <summary>The document on screen is a video: the window shows the video
    /// pane instead of Edge.</summary>
    public bool IsVideoShown
    {
        get => _isVideoShown;
        private set { if (Set(ref _isVideoShown, value)) Raise(nameof(IsDocumentShown)); }
    }

    /// <summary>The Edge pane is the one on screen.</summary>
    public bool IsDocumentShown => !IsVideoShown;

    private bool IsVideo(string? path) =>
        path is not null && MediaFiles.KindOf(path, _cfg.Media) == MediaKind.Video;

    /// <summary>Starts loading the video engine when a session has a video
    /// queued, so the first one opens without waiting for it.</summary>
    private void WarmUpVideoIfQueued()
    {
        foreach (var path in _session.Queue)
        {
            if (!IsVideo(path)) continue;
            Video.Player.WarmUp();
            return;
        }
    }

    /// <summary>Shows a video in the video pane, paused on its first frame.</summary>
    private async Task ShowVideoAsync(string source)
    {
        // Edge would otherwise keep the last PDF or photo open behind the video
        _viewer.Blank();
        IsVideoShown = true;
        await Video.ShowAsync(source);
    }

    /// <summary>Leaving the video pane for a PDF or photo: the player stops
    /// in the background, and a move that follows waits for it (through
    /// <c>_release</c>, as for Edge), so a local copy is never deleted, nor
    /// an inbox file moved, while the player still has it open.</summary>
    private void LeaveVideo()
    {
        if (!IsVideoShown) return;
        IsVideoShown = false;
        _release = Task.WhenAll(_release, Video.ReleaseAsync());
    }

    /// <summary>Nothing on either pane (Ready, Done, a closed session).</summary>
    private void BlankPanes()
    {
        _viewer.Blank();
        LeaveVideo();
    }

    /// <summary>What lets go of the document on screen before it moves: the
    /// video player for a video, Edge for anything else.</summary>
    private Task ReleaseShownAsync() => IsVideoShown ? Video.ReleaseAsync() : _viewer.ReleaseAsync();
}
