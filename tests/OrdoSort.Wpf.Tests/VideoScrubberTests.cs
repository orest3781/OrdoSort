using System.Windows.Input;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Scrubbing a video (spec 2026-09-29-media-filing-loop, section 4):
/// the key table, and what each key, the wheel and the timeline do to the
/// player. A fake player and a hand-moved clock, so nothing plays.</summary>
public class VideoScrubberTests
{
    // ---------------------------------------------------------- the keys

    [Theory]
    [InlineData(Key.K, ModifierKeys.Alt, VideoCommand.PlayPause, 0)]
    [InlineData(Key.Left, ModifierKeys.Alt, VideoCommand.Back5, 0)]
    [InlineData(Key.Right, ModifierKeys.Alt, VideoCommand.Forward5, 0)]
    [InlineData(Key.Left, ModifierKeys.Alt | ModifierKeys.Shift, VideoCommand.Back30, 0)]
    [InlineData(Key.Right, ModifierKeys.Alt | ModifierKeys.Shift, VideoCommand.Forward30, 0)]
    [InlineData(Key.OemComma, ModifierKeys.Alt, VideoCommand.FrameBack, 0)]
    [InlineData(Key.OemPeriod, ModifierKeys.Alt, VideoCommand.FrameForward, 0)]
    [InlineData(Key.J, ModifierKeys.Alt, VideoCommand.Rewind, 0)]
    [InlineData(Key.L, ModifierKeys.Alt, VideoCommand.FastForward, 0)]
    [InlineData(Key.M, ModifierKeys.Alt, VideoCommand.Mute, 0)]
    [InlineData(Key.D3, ModifierKeys.Alt, VideoCommand.Jump, 3)]
    [InlineData(Key.D0, ModifierKeys.Alt, VideoCommand.Jump, 0)]
    public void EachVideoKeyUsesAlt(Key key, ModifierKeys modifiers, VideoCommand command, int tenth) =>
        Assert.Equal((command, tenth), VideoKeys.Map(key, modifiers));

    /// <summary>The name box keeps every key it types with, and the filing
    /// shortcuts (Ctrl+…) are never taken.</summary>
    [Theory]
    [InlineData(Key.K, ModifierKeys.None)]
    [InlineData(Key.Left, ModifierKeys.None)]
    [InlineData(Key.Space, ModifierKeys.Alt)]            // Windows' window menu
    [InlineData(Key.K, ModifierKeys.Control)]            // set aside
    [InlineData(Key.D1, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.K, ModifierKeys.Alt | ModifierKeys.Shift)]
    [InlineData(Key.Q, ModifierKeys.Alt)]
    [InlineData(Key.NumPad7, ModifierKeys.Alt)]          // Alt-code entry would type a character
    public void OtherKeysAreLeftAlone(Key key, ModifierKeys modifiers) =>
        Assert.Equal(VideoCommand.None, VideoKeys.Map(key, modifiers).Command);

    // ---------------------------------------------------------- the player

    private static (VideoScrubber Scrubber, FakeVideoPlayer Player, ManualTimeProvider Clock) Opened()
    {
        var player = new FakeVideoPlayer();
        var clock = new ManualTimeProvider();
        var scrubber = new VideoScrubber(player, ui: null, time: clock);
#pragma warning disable xUnit1031 // the fake completes at once
        scrubber.ShowAsync(@"C:\inbox\clip.mov").GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        return (scrubber, player, clock);
    }

    [Fact]
    public void AVideoOpensPausedMutedAtTheStart()
    {
        var (scrubber, player, _) = Opened();

        Assert.Equal(new[] { @"C:\inbox\clip.mov" }, player.Opened);
        Assert.False(scrubber.IsPlaying);
        Assert.True(scrubber.IsMuted);
        Assert.Equal("0:00 / 2:00", scrubber.TimeText);
    }

    [Fact]
    public void KPlaysThenPauses()
    {
        var (scrubber, player, _) = Opened();

        scrubber.Handle(VideoCommand.PlayPause);
        Assert.True(player.IsPlaying);
        Assert.True(scrubber.IsPlaying);

        scrubber.Handle(VideoCommand.PlayPause);
        Assert.False(player.IsPlaying);
    }

    [Theory]
    [InlineData(VideoCommand.Forward5, 65)]
    [InlineData(VideoCommand.Back5, 55)]
    [InlineData(VideoCommand.Forward30, 90)]
    [InlineData(VideoCommand.Back30, 30)]
    public void TheArrowKeysJump(VideoCommand command, int expectedSeconds)
    {
        var (scrubber, player, _) = Opened();
        player.Position = TimeSpan.FromSeconds(60);

        scrubber.Handle(command);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), player.Position);
    }

    [Fact]
    public void JumpsStopAtBothEnds()
    {
        var (scrubber, player, _) = Opened();
        player.Position = TimeSpan.FromSeconds(3);

        scrubber.Handle(VideoCommand.Back30);
        Assert.Equal(TimeSpan.Zero, player.Position);

        player.Position = TimeSpan.FromSeconds(110);
        scrubber.Handle(VideoCommand.Forward30);
        Assert.Equal(player.Length, player.Position);
    }

    [Fact]
    public void TheFrameKeysStepOneFrameAndPause()
    {
        var (scrubber, player, _) = Opened();
        scrubber.Handle(VideoCommand.PlayPause);
        player.Position = TimeSpan.FromSeconds(10);

        scrubber.Handle(VideoCommand.FrameBack);
        Assert.False(player.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(10) - player.FrameTime, player.Position);

        scrubber.Handle(VideoCommand.FrameForward);
        Assert.Equal(TimeSpan.FromSeconds(10), player.Position);
    }

    [Fact]
    public void LPlaysFasterOnEachPress()
    {
        var (scrubber, player, _) = Opened();

        scrubber.Handle(VideoCommand.FastForward);
        Assert.True(player.IsPlaying);
        Assert.Equal(1f, player.Rate);
        Assert.Equal("", scrubber.SpeedText);

        scrubber.Handle(VideoCommand.FastForward);
        Assert.Equal(2f, player.Rate);
        scrubber.Handle(VideoCommand.FastForward);
        scrubber.Handle(VideoCommand.FastForward);
        scrubber.Handle(VideoCommand.FastForward);
        Assert.Equal(8f, player.Rate);
        Assert.Equal("8×", scrubber.SpeedText);
    }

    /// <summary>VLC can't play backwards: rewind is a jump back on every tick
    /// of the clock, faster on each press, and stops at the start.</summary>
    [Fact]
    public void JRewindsOnTheClockFasterOnEachPress()
    {
        var (scrubber, player, clock) = Opened();
        player.Position = TimeSpan.FromSeconds(30);

        scrubber.Handle(VideoCommand.Rewind);
        Assert.False(player.IsPlaying);
        Assert.Equal("rewind 1×", scrubber.SpeedText);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(29), player.Position);

        scrubber.Handle(VideoCommand.Rewind);
        Assert.Equal("rewind 2×", scrubber.SpeedText);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(27), player.Position);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Equal("", scrubber.SpeedText);
    }

    [Fact]
    public void KLeavesJAndLSpeedsAndPauses()
    {
        var (scrubber, player, _) = Opened();
        scrubber.Handle(VideoCommand.FastForward);
        scrubber.Handle(VideoCommand.FastForward);

        scrubber.Handle(VideoCommand.PlayPause);

        Assert.False(player.IsPlaying);
        Assert.Equal(1f, player.Rate);
        Assert.Equal("", scrubber.SpeedText);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 60)]
    [InlineData(9, 108)]
    public void TheNumberKeysJumpToATenth(int tenth, int expectedSeconds)
    {
        var (scrubber, player, _) = Opened();

        scrubber.Handle(VideoCommand.Jump, tenth);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), player.Position);
    }

    [Fact]
    public void MTogglesTheSound()
    {
        var (scrubber, player, _) = Opened();

        scrubber.Handle(VideoCommand.Mute);
        Assert.False(player.IsMuted);
        Assert.Equal("sound on", scrubber.MuteText);

        scrubber.Handle(VideoCommand.Mute);
        Assert.True(player.IsMuted);
    }

    [Theory]
    [InlineData(-120, false, 12)]     // wheel down: forward 2 s
    [InlineData(120, false, 8)]       // wheel up: back
    [InlineData(-240, true, 30)]      // two notches with Shift: 20 s
    public void TheWheelScrubs(int delta, bool shift, int expectedSeconds)
    {
        var (scrubber, player, _) = Opened();
        player.Position = TimeSpan.FromSeconds(10);

        scrubber.Wheel(delta, shift);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), player.Position);
    }

    [Fact]
    public void DraggingTheTimelineSeeksAndThePollLeavesTheThumbAlone()
    {
        var (scrubber, player, clock) = Opened();

        scrubber.BeginDrag();
        scrubber.PositionSeconds = 45;
        Assert.Equal(TimeSpan.FromSeconds(45), player.Position);
        player.Position = TimeSpan.FromSeconds(44);       // the engine lags a little
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(45, scrubber.PositionSeconds);

        scrubber.EndDrag();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(44, scrubber.PositionSeconds);
    }

    [Fact]
    public void ThePollFollowsThePlayingVideo()
    {
        var (scrubber, player, clock) = Opened();
        player.Position = TimeSpan.FromSeconds(75);

        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal("1:15 / 2:00", scrubber.TimeText);
    }

    [Fact]
    public void KeysDoNothingOnceTheVideoIsReleased()
    {
        var (scrubber, player, _) = Opened();
        scrubber.ReleaseAsync();
        var calls = player.Calls.Count;

        Assert.False(scrubber.Handle(VideoCommand.PlayPause));
        Assert.False(scrubber.Wheel(-120, false));
        Assert.Equal(calls, player.Calls.Count);
    }

    /// <summary>A release that overtakes a video still opening (the engine
    /// still loading) wins: the video doesn't come alive afterwards.</summary>
    [Fact]
    public async Task AReleaseOvertakingAnOpenWins()
    {
        var player = new FakeVideoPlayer { HoldOpen = new TaskCompletionSource() };
        var scrubber = new VideoScrubber(player, time: new ManualTimeProvider());

        var show = scrubber.ShowAsync("clip.mov");
        await scrubber.ReleaseAsync();
        player.HoldOpen.SetResult();
        await show;

        Assert.False(scrubber.Handle(VideoCommand.PlayPause));
    }

    [Fact]
    public void AVideoThatCantPlaySaysWhy()
    {
        var player = new FakeVideoPlayer { FailNextOpen = "Can't play this video." };
        var scrubber = new VideoScrubber(player, time: new ManualTimeProvider());

        scrubber.ShowAsync("bad.mov");

        Assert.True(scrubber.HasProblem);
        Assert.Equal("Can't play this video.", scrubber.Problem);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(75, "1:15")]
    [InlineData(3725, "1:02:05")]
    public void TimesReadLikeAPlayer(double seconds, string expected) =>
        Assert.Equal(expected, VideoScrubber.Clock(seconds));
}
