using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace OrdoSort.Wpf.Services;

/// <summary>
/// The video pane's player, on libVLC (spec 2026-09-29-media-filing-loop,
/// release 2): plays iPhone HEVC .mov, .avi and .mkv without any codec
/// installed on the PC.
///
/// libVLC is loaded on the first video of a session (or by <see cref="WarmUp"/>
/// when a session has one queued), never at app start, so a PDF-only session
/// costs nothing.
///
/// Three libVLC rules shape the code:
/// - Stop blocks until the video window is torn down, and that window is a
///   child of this WPF window, so the UI thread must be free to answer: Stop
///   runs on the thread pool and is awaited, never waited on.
/// - Opens and stops must not overlap (a late Stop would kill the next
///   video), so they take turns in one lane, and an open that a release
///   overtook while it waited (the engine still loading) never plays.
/// - Nothing may call back into the player from inside one of its own
///   events, so those hand off to the thread pool.
/// </summary>
public sealed class VlcVideoPlayer : IVideoPlayer, IDisposable
{
    private static readonly TimeSpan GuessFrame = TimeSpan.FromSeconds(1d / 30);

    private readonly VideoView _view;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lane = new(1, 1);
    private Task<LibVLC>? _engine;
    private MediaPlayer? _player;
    private Media? _media;
    private int _generation;
    private volatile bool _muted = true;
    private volatile bool _disposed;

    public VlcVideoPlayer(VideoView view) => _view = view;

    public event Action<string>? Failed;

    /// <summary>Starts loading libVLC in the background; the first video then
    /// opens without waiting for it.</summary>
    public void WarmUp() => _ = Engine();

    private Task<LibVLC> Engine()
    {
        lock (_gate)
        {
            return _engine ??= Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                // no on-screen title or volume text over the video; a video
                // pauses on its last frame rather than stopping, so it can
                // still be scrubbed back
                return new LibVLC("--no-osd", "--no-video-title-show", "--play-and-pause", "--quiet",
                    "--no-sub-autodetect-file", "--avcodec-hw=d3d11va", "--file-caching=1500");
            });
        }
    }

    public async Task OpenAsync(string path)
    {
        var generation = Interlocked.Increment(ref _generation);
        await _lane.WaitAsync();
        try
        {
            LibVLC engine;
            try
            {
                engine = await Engine();
            }
            catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException
                                           or InvalidOperationException or TypeInitializationException)
            {
                lock (_gate) _engine = null;
                Failed?.Invoke("The video player couldn't start: " + ex.Message);
                return;
            }
            // released, or another video asked for, while the engine loaded
            if (_disposed || generation != Volatile.Read(ref _generation)) return;
            var player = PlayerFor(engine);
            await StopCurrentAsync(player);
            if (_disposed || generation != Volatile.Read(ref _generation)) return;
            var media = new Media(engine, path, FromType.FromPath, ":start-paused");
            _media = media;
            if (!player.Play(media)) Failed?.Invoke("Can't play this video.");
            player.Mute = _muted;
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <summary>One player for the session, on the view the window gave:
    /// made on the UI thread, where the view lives.</summary>
    private MediaPlayer PlayerFor(LibVLC engine)
    {
        if (_player is not null) return _player;
        var player = new MediaPlayer(engine) { EnableKeyInput = false, EnableMouseInput = false };
        player.EncounteredError += (_, _) =>
            Failed?.Invoke("Can't play this video: it may be damaged, or in a format this player doesn't read.");
        // libVLC sets its own mute when the sound starts; put ours back, off
        // its event thread
        player.Playing += (_, _) => ThreadPool.QueueUserWorkItem(_ => ApplyMute());
        player.Paused += (_, _) => ThreadPool.QueueUserWorkItem(_ => ApplyMute());
        _view.MediaPlayer = player;
        _player = player;
        return player;
    }

    private void ApplyMute()
    {
        if (_disposed) return;
        try
        {
            var player = _player;
            if (player is not null && player.Mute != _muted) player.Mute = _muted;
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Stops what is open, off the UI thread, and forgets it.</summary>
    private async Task StopCurrentAsync(MediaPlayer player)
    {
        var media = _media;
        _media = null;
        if (media is null) return;
        await Task.Run(() =>
        {
            player.Stop();
            media.Dispose();
        });
    }

    public async Task ReleaseAsync()
    {
        // an open still waiting for the engine gives up rather than playing
        Interlocked.Increment(ref _generation);
        await _lane.WaitAsync();
        try
        {
            if (_player is { } player && !_disposed) await StopCurrentAsync(player);
        }
        finally
        {
            _lane.Release();
        }
    }

    public bool IsOpen => _media is not null;
    public bool IsPlaying => _media is not null && (_player?.IsPlaying ?? false);

    public TimeSpan Position =>
        _player is { } player && _media is not null ? TimeSpan.FromMilliseconds(Math.Max(0, player.Time)) : TimeSpan.Zero;

    public TimeSpan Length =>
        _player is { } player && _media is not null ? TimeSpan.FromMilliseconds(Math.Max(0, player.Length)) : TimeSpan.Zero;

    public TimeSpan FrameTime
    {
        get
        {
            var fps = _media is null ? 0 : _player?.Fps ?? 0;
            return fps is > 1 and < 1000 ? TimeSpan.FromSeconds(1 / fps) : GuessFrame;
        }
    }

    public bool IsMuted => _muted;
    public float Rate => _player?.Rate ?? 1f;

    public void Play()
    {
        if (_media is not null) _player?.SetPause(false);
    }

    public void Pause()
    {
        if (_media is not null) _player?.SetPause(true);
    }

    public void Seek(TimeSpan position)
    {
        if (_player is null || _media is null) return;
        var ms = (long)Math.Max(0, position.TotalMilliseconds);
        var length = _player.Length;
        if (length > 0) ms = Math.Min(ms, length);
        _player.Time = ms;
    }

    public void SetRate(float rate)
    {
        if (_media is not null) _player?.SetRate(rate);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_player is not null) _player.Mute = muted;
    }

    public void NextFrame()
    {
        if (_media is not null) _player?.NextFrame();
    }

    /// <summary>Detaches the view at once, on the UI thread; stops and frees
    /// the engine on the thread pool, after anything still in the lane, so
    /// the window closing never waits on libVLC.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        var player = _player;
        var engine = _engine;
        if (player is not null) _view.MediaPlayer = null;
        _ = Task.Run(async () =>
        {
            await _lane.WaitAsync();
            try
            {
                var media = _media;
                _media = null;
                _player = null;
                player?.Stop();
                player?.Dispose();
                media?.Dispose();
                if (engine is { IsCompletedSuccessfully: true }) engine.Result.Dispose();
            }
            finally
            {
                _lane.Release();
            }
        });
    }
}
