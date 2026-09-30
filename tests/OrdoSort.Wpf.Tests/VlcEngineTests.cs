using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Xunit.Abstractions;

namespace OrdoSort.Wpf.Tests;

/// <summary>The bundled libVLC, with the trimmed plugin set the app ships
/// (OrdoSort.Wpf.csproj), plays a phone video: demuxed, decoded and drawn,
/// into memory rather than a window. The one thing the fake player in every
/// other video test can't prove. Integration: it loads native DLLs, so it
/// is left out of the everyday run.</summary>
[Trait("Category", "Integration")]
public class VlcEngineTests
{
    private const uint Width = 32;
    private const uint Height = 24;
    private readonly ITestOutputHelper _output;

    public VlcEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task TheShippedPluginsPlayAPhoneVideo()
    {
        var clock = Stopwatch.StartNew();
        LibVLCSharp.Shared.Core.Initialize();
        using var engine = new LibVLC("--quiet", "--no-audio");
        _output.WriteLine($"engine loaded in {clock.ElapsedMilliseconds} ms");
        clock.Restart();
        var clip = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "taken-20230704.mov");
        using var media = new Media(engine, clip, FromType.FromPath);
        using var player = new MediaPlayer(engine);

        var frame = Marshal.AllocHGlobal((int)(Width * Height * 4));
        var drawn = 0;
        MediaPlayer.LibVLCVideoLockCb lockFrame = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, frame);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoUnlockCb unlockFrame = (_, _, _) => { };
        MediaPlayer.LibVLCVideoDisplayCb showFrame = (_, _) => Interlocked.Increment(ref drawn);
        try
        {
            player.SetVideoFormat("RV32", Width, Height, Width * 4);
            player.SetVideoCallbacks(lockFrame, unlockFrame, showFrame);

            Assert.True(player.Play(media));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref drawn) == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            _output.WriteLine($"first frame after {clock.ElapsedMilliseconds} ms");
            await Task.Run(player.Stop);

            Assert.True(drawn > 0, "no frame was decoded and drawn");
        }
        finally
        {
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(unlockFrame);
            GC.KeepAlive(showFrame);
            Marshal.FreeHGlobal(frame);
        }
    }
}
