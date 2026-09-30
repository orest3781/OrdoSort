using System.Globalization;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>
/// Scrubbing a video in the filing loop (spec 2026-09-29-media-filing-loop,
/// section 4): the keys, the mouse wheel and the timeline, over one
/// <see cref="IVideoPlayer"/>. Polls the player a few times a second while a
/// video is open, which also drives rewind: VLC can't play backwards, so
/// rewinding is a jump back on every tick.
/// </summary>
public sealed class VideoScrubber : ObservableObject, IDisposable
{
    /// <summary>J/L speeds, as in video editors.</summary>
    internal static readonly float[] Speeds = { 1f, 2f, 4f, 8f };

    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan SmallStep = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan BigStep = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WheelStep = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ShiftWheelStep = TimeSpan.FromSeconds(10);

    private readonly IVideoPlayer _player;
    private readonly SynchronizationContext? _ui;
    private readonly ITimer _timer;

    /// <summary>-1 when not rewinding, else an index into <see cref="Speeds"/>.</summary>
    private int _rewind = -1;
    /// <summary>The speed index while playing forward under J/L.</summary>
    private int _forward;
    private bool _active;
    /// <summary>Counts shows and releases: a show that a release overtook
    /// while its video opened doesn't come alive afterwards.</summary>
    private int _shows;
    /// <summary>The timeline's thumb is being dragged: the poll leaves its
    /// value alone, or the thumb would jump back under the pointer.</summary>
    private bool _dragging;

    /// <param name="ui">Where timeline updates are raised; inline when null (tests).</param>
    /// <param name="time">The clock the timeline polls on.</param>
    public VideoScrubber(IVideoPlayer player, SynchronizationContext? ui = null, TimeProvider? time = null)
    {
        _player = player;
        _ui = ui;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => OnTimer(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _player.Failed += message => OnUi(() => Problem = message);
    }

    /// <summary>The player, for the window's own video surface.</summary>
    public IVideoPlayer Player => _player;

    // ------------------------------------------------------------ state

    private double _positionSeconds;
    /// <summary>The timeline's value. Set by dragging it: jumps there.</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!Set(ref _positionSeconds, value)) return;
            _player.Seek(TimeSpan.FromSeconds(value));
            RaiseTimeText();
        }
    }

    private double _lengthSeconds;
    public double LengthSeconds { get => _lengthSeconds; private set => Set(ref _lengthSeconds, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private bool _isMuted = true;
    public bool IsMuted
    {
        get => _isMuted;
        private set { if (Set(ref _isMuted, value)) Raise(nameof(MuteText)); }
    }

    public string MuteText => IsMuted ? "muted" : "sound on";

    /// <summary>"0:12 / 1:30".</summary>
    public string TimeText => $"{Clock(_positionSeconds)} / {Clock(_lengthSeconds)}";

    private string _speedText = "";
    /// <summary>"2×", "rewind 4×", or empty at normal speed.</summary>
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    private string _problem = "";
    /// <summary>Why the video can't be shown; empty while it plays.</summary>
    public string Problem
    {
        get => _problem;
        private set { if (Set(ref _problem, value)) Raise(nameof(HasProblem)); }
    }

    public bool HasProblem => Problem.Length > 0;

    // ------------------------------------------------------------ opening

    /// <summary>A video came on screen: open it paused on its first frame.</summary>
    public async Task ShowAsync(string path)
    {
        ResetSpeed();
        Problem = "";
        _positionSeconds = 0;
        Raise(nameof(PositionSeconds));
        LengthSeconds = 0;
        RaiseTimeText();
        var show = ++_shows;
        await _player.OpenAsync(path);
        if (show != _shows) return;
        _active = true;
        _timer.Change(Tick, Tick);
        Refresh();
    }

    /// <summary>The video left the screen: stop polling and let go of its file.</summary>
    public Task ReleaseAsync()
    {
        _shows++;
        _active = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ResetSpeed();
        IsPlaying = false;
        return _player.ReleaseAsync();
    }

    // ------------------------------------------------------------ commands

    /// <summary>Carries out a video key. False for <see cref="VideoCommand.None"/>
    /// or while no video is open, so the key goes on to the window.</summary>
    public bool Handle(VideoCommand command, int tenth = 0)
    {
        if (!_active || command == VideoCommand.None) return false;
        switch (command)
        {
            case VideoCommand.PlayPause: PlayPause(); break;
            case VideoCommand.Back5: SeekBy(-SmallStep); break;
            case VideoCommand.Forward5: SeekBy(SmallStep); break;
            case VideoCommand.Back30: SeekBy(-BigStep); break;
            case VideoCommand.Forward30: SeekBy(BigStep); break;
            case VideoCommand.FrameBack: StepFrame(-1); break;
            case VideoCommand.FrameForward: StepFrame(1); break;
            case VideoCommand.Rewind: Rewind(); break;
            case VideoCommand.FastForward: FastForward(); break;
            case VideoCommand.Jump: JumpTo(tenth); break;
            case VideoCommand.Mute: SetMuted(!_player.IsMuted); break;
        }
        Refresh();
        return true;
    }

    /// <summary>The mouse wheel over the video: down goes forward, as the
    /// timeline reads left to right. <paramref name="delta"/> is WPF's, 120
    /// a notch.</summary>
    public bool Wheel(int delta, bool shift)
    {
        if (!_active || delta == 0) return false;
        var notches = -delta / 120.0;
        var step = shift ? ShiftWheelStep : WheelStep;
        SeekBy(TimeSpan.FromSeconds(step.TotalSeconds * notches));
        Refresh();
        return true;
    }

    /// <summary>K: play or pause; out of J/L speeds, back to normal and paused.</summary>
    private void PlayPause()
    {
        if (_rewind >= 0 || _player.Rate != 1f)
        {
            ResetSpeed();
            _player.SetRate(1f);
            _player.Pause();
            return;
        }
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    private void SeekBy(TimeSpan by)
    {
        var target = _player.Position + by;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_player.Length > TimeSpan.Zero && target > _player.Length) target = _player.Length;
        _player.Seek(target);
    }

    private void StepFrame(int direction)
    {
        ResetSpeed();
        if (_player.Rate != 1f) _player.SetRate(1f);
        if (direction > 0)
        {
            _player.NextFrame();
            return;
        }
        _player.Pause();
        SeekBy(-_player.FrameTime);
    }

    /// <summary>J: rewind, faster on each press.</summary>
    private void Rewind()
    {
        _forward = 0;
        if (_player.Rate != 1f) _player.SetRate(1f);
        _player.Pause();
        _rewind = _rewind < 0 ? 0 : Math.Min(_rewind + 1, Speeds.Length - 1);
        SpeedText = $"rewind {Speeds[_rewind].ToString(CultureInfo.InvariantCulture)}×";
    }

    /// <summary>L: play forward, faster on each press while already playing.</summary>
    private void FastForward()
    {
        var wasForward = _rewind < 0 && _player.IsPlaying;
        _rewind = -1;
        _forward = wasForward ? Math.Min(_forward + 1, Speeds.Length - 1) : 0;
        _player.SetRate(Speeds[_forward]);
        _player.Play();
        SpeedText = _forward == 0 ? "" : $"{Speeds[_forward].ToString(CultureInfo.InvariantCulture)}×";
    }

    private void JumpTo(int tenth)
    {
        if (_player.Length <= TimeSpan.Zero) return;
        _player.Seek(TimeSpan.FromTicks(_player.Length.Ticks / 10 * Math.Clamp(tenth, 0, 9)));
    }

    private void SetMuted(bool muted)
    {
        _player.SetMuted(muted);
        IsMuted = muted;
    }

    private void ResetSpeed()
    {
        _rewind = -1;
        _forward = 0;
        SpeedText = "";
    }

    // ------------------------------------------------------------ polling

    private void OnTimer() => OnUi(() =>
    {
        if (!_active) return;
        if (_rewind >= 0)
        {
            SeekBy(-TimeSpan.FromTicks((long)(Tick.Ticks * Speeds[_rewind])));
            if (_player.Position <= TimeSpan.Zero) ResetSpeed();
        }
        Refresh();
    });

    /// <summary>The timeline's thumb went down.</summary>
    public void BeginDrag() => _dragging = true;

    /// <summary>The timeline's thumb came up: the poll follows the video again.</summary>
    public void EndDrag() => _dragging = false;

    /// <summary>Reads the player into the timeline without seeking it.</summary>
    private void Refresh()
    {
        // the length first: a timeline whose maximum is still 0 would clamp
        // the new position to 0 and write that back as a seek
        LengthSeconds = _player.Length.TotalSeconds;
        if (!_dragging)
        {
            _positionSeconds = _player.Position.TotalSeconds;
            Raise(nameof(PositionSeconds));
        }
        IsPlaying = _player.IsPlaying;
        IsMuted = _player.IsMuted;
        RaiseTimeText();
    }

    private void RaiseTimeText() => Raise(nameof(TimeText));

    internal static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void OnUi(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose() => _timer.Dispose();
}
