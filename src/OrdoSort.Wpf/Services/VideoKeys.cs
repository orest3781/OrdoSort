using System.Windows.Input;

namespace OrdoSort.Wpf.Services;

/// <summary>What a video key does.</summary>
public enum VideoCommand
{
    None,
    PlayPause,
    Back5,
    Forward5,
    Back30,
    Forward30,
    FrameBack,
    FrameForward,
    Rewind,
    FastForward,
    Jump,
    Mute,
}

/// <summary>
/// The video keys (spec 2026-09-29-media-filing-loop, section 4). Every one
/// uses Alt, so plain arrows, Space and letters stay free for the name box;
/// not Alt+Space, which Windows keeps for the window menu. Pure, so the whole
/// table is tested without a window.
/// </summary>
public static class VideoKeys
{
    /// <summary>The command for a key press, and for <see cref="VideoCommand.Jump"/>
    /// the tenth to jump to (0 = the start, 9 = 90 %).</summary>
    /// <param name="key">The key, with WPF's <c>Key.System</c> already
    /// resolved to the real key (<c>e.SystemKey</c>) by the caller.</param>
    public static (VideoCommand Command, int Tenth) Map(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Alt) == 0 || (modifiers & ModifierKeys.Control) != 0)
            return (VideoCommand.None, 0);
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        switch (key)
        {
            case Key.Left: return (shift ? VideoCommand.Back30 : VideoCommand.Back5, 0);
            case Key.Right: return (shift ? VideoCommand.Forward30 : VideoCommand.Forward5, 0);
        }
        if (shift) return (VideoCommand.None, 0);
        switch (key)
        {
            case Key.K: return (VideoCommand.PlayPause, 0);
            case Key.J: return (VideoCommand.Rewind, 0);
            case Key.L: return (VideoCommand.FastForward, 0);
            case Key.OemComma: return (VideoCommand.FrameBack, 0);
            case Key.OemPeriod: return (VideoCommand.FrameForward, 0);
            case Key.M: return (VideoCommand.Mute, 0);
        }
        // the top-row digits only: Alt with the number pad is Windows' Alt-code
        // entry, which types a character into the name box when Alt comes up
        if (key is >= Key.D0 and <= Key.D9) return (VideoCommand.Jump, key - Key.D0);
        return (VideoCommand.None, 0);
    }
}
