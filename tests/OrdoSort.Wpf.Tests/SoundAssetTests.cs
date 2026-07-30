using System.Windows;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>SoundService swallows every playback failure by design — a missing
/// or renamed asset degrades to silence with no error anywhere. Nothing else
/// would catch it, so the embedded set is checked here: every moment that
/// claims a built-in sound must actually resolve to a .wav SoundPlayer can
/// play.</summary>
public class SoundAssetTests
{
    /// <summary>WPF registers the pack: scheme lazily, and these tests run
    /// without an Application, so touch the helper before building the URI.</summary>
    private static void EnsurePackScheme() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    private static byte[] Load(string name)
    {
        EnsurePackScheme();
        var info = Application.GetResourceStream(SoundService.ResourceUri(name));
        Assert.True(info is not null, $"{name}.wav is not embedded in the assembly");
        using var ms = new MemoryStream();
        info!.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(SoundEvent.NewAlert)]
    [InlineData(SoundEvent.Filed)]
    [InlineData(SoundEvent.SetAside)]
    public void EveryMomentWithABuiltInSoundResolvesToAPlayableWav(SoundEvent evt)
    {
        var name = SoundService.ResourceName(evt);
        Assert.True(name is not null, $"{evt} claims a built-in sound but names no file");

        var bytes = Load(name!);
        Assert.True(bytes.Length > 1000, $"{name}.wav is only {bytes.Length} bytes");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));

        // SoundPlayer accepts PCM only (format tag 1). A float or compressed
        // wav would load fine here and then throw into Play's empty catch.
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 20));
        var bits = BitConverter.ToUInt16(bytes, 34);
        Assert.True(bits is 8 or 16, $"{name}.wav is {bits}-bit; SoundPlayer wants 8 or 16");
    }

    [Fact]
    public void ErrorUsesTheSystemChimeRatherThanAnAsset()
    {
        // deliberate: the OS critical sound says it better than a bespoke one,
        // so Error must NOT name a file that would then need shipping
        Assert.Null(SoundService.ResourceName(SoundEvent.Error));
    }

    [Fact]
    public void EverySoundEventIsAccountedFor()
    {
        // a new SoundEvent must be a deliberate choice: an asset or the chime
        foreach (SoundEvent evt in Enum.GetValues<SoundEvent>())
        {
            var name = SoundService.ResourceName(evt);
            if (name is null) continue;      // system chime — fine
            Assert.NotEmpty(Load(name));     // otherwise it must really be there
        }
    }
}
