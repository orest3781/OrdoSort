/// <summary>Regenerates the three built-in sounds into
/// src\OrdoSort.Wpf\Assets\sounds. Deterministic — same run, byte-identical
/// files — so a change of taste is a change of parameter here rather than an
/// opaque binary someone has to reverse-engineer. Output is mono 16-bit PCM at
/// 44.1kHz: what SoundPlayer accepts and what SoundAssetTests asserts.
///
/// The gestures are borrowed from the NES on purpose, because the shape of
/// each one already means the right thing: a coin for a document arriving, a
/// pipe descent for one going down into the folder it belongs in, and a bump —
/// you hit the block and nothing came out — for setting one aside.</summary>
public static class SoundSet
{
    private const int Rate = 44100;

    /// <summary>NTSC 2A03 clock. A pulse channel's pitch is not continuous: it
    /// comes from an 11-bit timer period, so only certain frequencies exist.
    /// Rounding every note to a real period is most of what makes this sound
    /// like hardware rather than like a synthesiser.</summary>
    private const double Cpu = 1789773.0;

    /// <summary>The frame counter ticks at 60Hz and sweeps only move on a tick,
    /// so pitch slides descend in audible stairs. A smooth glide is the single
    /// thing that most makes imitation chiptune sound like imitation.</summary>
    private const double FrameHz = 60.0;

    /// <summary>One octave below the console's own pitches. Its effects were
    /// bright because the pulse channels carried them while the triangle took
    /// the bass; dropping an octave keeps the gestures recognisable while
    /// giving them the weight this app wants.</summary>
    private const double Octave = 0.5;

    /// <param name="args">args[0] is "sounds"; an optional args[1] is a folder
    /// to also drop a preview of all three in, for auditioning. The preview is
    /// never written beside the assets — the csproj embeds
    /// <c>Assets\sounds\*.wav</c> wholesale, so a stray file there would ship
    /// inside the app.</param>
    public static int Run(string[] args)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(),
            "src", "OrdoSort.Wpf", "Assets", "sounds");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"Not found: {dir}");
            Console.WriteLine("Run this from the repository root.");
            return 1;
        }

        var alert = Coin();
        var filed = PipeDescent();
        var aside = Bump();

        Console.WriteLine($"Writing to {dir}");
        Emit(dir, "ordosort-alert.wav", alert, "new alert    coin");
        Emit(dir, "ordosort-send.wav", filed, "filed        pipe descent");
        Emit(dir, "ordosort-aside.wav", aside, "set aside    bump");

        if (args.Length > 1)
        {
            Directory.CreateDirectory(args[1]);
            Emit(args[1], "preview-all-three.wav",
                Join(alert, 0.45, filed, 0.45, aside), "PREVIEW      all three");
        }

        // the same shape SoundAssetTests enforces, checked here so a bad run
        // is caught where it happened rather than in an unrelated test later
        foreach (var name in new[]
                 { "ordosort-alert.wav", "ordosort-send.wav", "ordosort-aside.wav" })
        {
            var b = File.ReadAllBytes(Path.Combine(dir, name));
            var ok = b.Length > 1000
                     && System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF"
                     && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WAVE"
                     && BitConverter.ToUInt16(b, 20) == 1
                     && BitConverter.ToUInt16(b, 34) is 8 or 16;
            if (!ok) { Console.WriteLine($"  BAD: {name} is not a playable PCM wav"); return 1; }
        }
        Console.WriteLine("  ok    all three are playable 16-bit PCM");
        return 0;
    }

    // ------------------------------------------------------------- the chip

    /// <summary>Snap a frequency to one the 11-bit timer can actually produce.</summary>
    private static double Quantise(double freq)
    {
        var period = Math.Clamp((int)Math.Round(Cpu / (16.0 * freq) - 1), 8, 2047);
        return Cpu / (16.0 * (period + 1));
    }

    /// <summary>Pulse wave. The chip offers exactly four duties; 12.5% is the
    /// thin nasal one, 50% the full square.</summary>
    private static double Pulse(double phase, double duty) =>
        phase - Math.Floor(phase) < duty ? 1.0 : -1.0;

    /// <summary>Volume is 4-bit — sixteen steps, not a smooth ramp.</summary>
    private static double Quantise4Bit(double v) => Math.Round(Math.Clamp(v, 0, 1) * 15) / 15.0;

    /// <summary>Which 60Hz frame a sample falls in; pitch is held across it.</summary>
    private static int Frame(int sample) => (int)(sample / (double)Rate * FrameHz);

    // ----------------------------------------------------------- the sounds

    /// <summary>The coin: a short note, then a higher one held and fading. The
    /// original rings for about 0.44s; trimmed here because this fires once per
    /// arriving document and several can land at once.</summary>
    private static double[] Coin()
    {
        var first = Quantise(987.77 * Octave);     // B5
        var second = Quantise(1318.51 * Octave);   // E6
        const double firstDur = 0.075, total = 0.28;

        var s = new double[(int)(Rate * total)];
        double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = (double)i / Rate;
            phase += (t < firstDur ? first : second) / Rate;
            var env = t < firstDur ? 1.0 : Math.Max(0, 1.0 - (t - firstDur) / (total - firstDur));
            s[i] = Pulse(phase, 0.5) * Quantise4Bit(env) * 0.55;
        }
        return Shape(s, 0.003);
    }

    /// <summary>The pipe: a fast stepped descent — a document going down into
    /// the folder it belongs in.</summary>
    private static double[] PipeDescent()
    {
        const double dur = 0.28;
        const double top = 740.0 * Octave, bottom = 155.0 * Octave;

        var s = new double[(int)(Rate * dur)];
        double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = (double)i / Rate;
            var frameT = Math.Clamp(Frame(i) / FrameHz / dur, 0, 1);
            phase += Quantise(top * Math.Pow(bottom / top, frameT)) / Rate;
            s[i] = Pulse(phase, 0.25) * Quantise4Bit(1.0 - 0.35 * (t / dur)) * 0.60;
        }
        return Shape(s, 0.004);
    }

    /// <summary>The bump: short, low, unresolved — which is precisely what
    /// setting a document aside is. Its fundamental runs below what a laptop
    /// speaker reproduces, so it carries on the harmonics of a narrow pulse,
    /// which is how the console got bass out of small televisions too.</summary>
    private static double[] Bump()
    {
        const double dur = 0.09;
        const double top = 262.0 * Octave, bottom = 131.0 * Octave;

        var s = new double[(int)(Rate * dur)];
        double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = (double)i / Rate;
            var frameT = Math.Clamp(Frame(i) / FrameHz / dur, 0, 1);
            phase += Quantise(top * Math.Pow(bottom / top, frameT)) / Rate;
            s[i] = Pulse(phase, 0.125) * Quantise4Bit(Math.Exp(-t / 0.035)) * 0.45;
        }
        return Shape(s, 0.003);
    }

    // -------------------------------------------------------------- plumbing

    /// <summary>Fade both edges. A pulse wave that starts or stops mid-cycle
    /// clicks, and at these durations the click is louder than the sound.</summary>
    private static double[] Shape(double[] s, double fadeSeconds)
    {
        var n = Math.Max(1, (int)(Rate * fadeSeconds));
        for (var i = 0; i < n && i < s.Length; i++)
        {
            var gain = (double)i / n;
            s[i] *= gain;
            s[^(i + 1)] *= gain;
        }
        return s;
    }

    private static double[] Join(double[] a, double gap1, double[] b, double gap2, double[] c) =>
        a.Concat(new double[(int)(Rate * gap1)])
         .Concat(b).Concat(new double[(int)(Rate * gap2)])
         .Concat(c).ToArray();

    private static void Emit(string dir, string name, double[] samples, string label)
    {
        WriteWav(Path.Combine(dir, name), samples);
        var peak = samples.Max(Math.Abs);
        var db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        Console.WriteLine(
            $"  {label,-26}{samples.Length / (double)Rate,6:N3}s  peak {db,6:N1} dBFS  {name}");
    }

    private static void WriteWav(string path, double[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // PCM header length
        w.Write((short)1);           // format tag 1 = PCM, the only one SoundPlayer takes
        w.Write((short)1);           // mono
        w.Write(Rate);
        w.Write(Rate * 2);           // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var v in samples)
            w.Write((short)Math.Clamp(Math.Round(v * short.MaxValue),
                short.MinValue, short.MaxValue));
    }
}
