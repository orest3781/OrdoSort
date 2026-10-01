namespace OrdoSort.Wpf.Services;

/// <summary>
/// The video pane (spec 2026-09-29-media-filing-loop, release 2). One player
/// for the session; each video replaces the last. An interface so the
/// filing loop and the scrub keys are tested without the VLC engine.
/// Every member may be called from the UI thread and returns quickly: the
/// engine's own slow calls (opening, stopping) run off it.
/// </summary>
public interface IVideoPlayer
{
    /// <summary>Shows <paramref name="path"/> paused on its first frame, with
    /// the sound as <see cref="IsMuted"/> says. Replaces whatever was open.</summary>
    Task OpenAsync(string path);

    /// <summary>Stops and lets go of the open file, so it can be moved.
    /// Nothing when nothing is open.</summary>
    Task ReleaseAsync();

    /// <summary>Starts loading the engine in the background, so the first
    /// video opens without waiting for it.</summary>
    void WarmUp();

    bool IsOpen { get; }
    bool IsPlaying { get; }

    /// <summary>Where the video is; zero when nothing is open.</summary>
    TimeSpan Position { get; }

    /// <summary>How long the video is; zero until the engine knows.</summary>
    TimeSpan Length { get; }

    /// <summary>One frame's time: 1/fps, or a 30 fps guess until known.</summary>
    TimeSpan FrameTime { get; }

    /// <summary>Kept across videos for the session; starts muted.</summary>
    bool IsMuted { get; }

    /// <summary>Playback speed, 1 for normal.</summary>
    float Rate { get; }

    void Play();
    void Pause();

    /// <summary>Jumps to <paramref name="position"/>, clamped to the video.</summary>
    void Seek(TimeSpan position);

    void SetRate(float rate);
    void SetMuted(bool muted);

    /// <summary>One frame on, paused.</summary>
    void NextFrame();

    /// <summary>The video couldn't be opened or played, with a reason to show.</summary>
    event Action<string>? Failed;
}

/// <summary>For a shell with no video pane (tests, and any window that never
/// shows videos): a video opens as a failure the pane can report.</summary>
public sealed class NoVideoPlayer : IVideoPlayer
{
    public Task OpenAsync(string path)
    {
        Failed?.Invoke("Videos can't be shown here.");
        return Task.CompletedTask;
    }

    public Task ReleaseAsync() => Task.CompletedTask;
    public void WarmUp() { }
    public bool IsOpen => false;
    public bool IsPlaying => false;
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan Length => TimeSpan.Zero;
    public TimeSpan FrameTime => TimeSpan.FromSeconds(1d / 30);
    public bool IsMuted => true;
    public float Rate => 1f;
    public void Play() { }
    public void Pause() { }
    public void Seek(TimeSpan position) { }
    public void SetRate(float rate) { }
    public void SetMuted(bool muted) { }
    public void NextFrame() { }
    public event Action<string>? Failed;
}
