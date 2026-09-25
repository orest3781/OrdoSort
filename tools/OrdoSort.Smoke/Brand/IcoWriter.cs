namespace OrdoSort.Smoke.Brand;

/// <summary>Writes a Windows .ico file whose frames are PNG images.
///
/// WPF can read .ico files but has no encoder for them, and the format is
/// small enough to write by hand: a 6-byte header, one 16-byte directory
/// entry per frame, then the frames. PNG frames are valid at every size on
/// Windows Vista and later, and are what the previous icons used.</summary>
public static class IcoWriter
{
    /// <summary>Writes the frames, in the order given, to <paramref name="output"/>.</summary>
    /// <param name="output">A writable stream.</param>
    /// <param name="frames">Each frame's square pixel size (1-256) and its
    /// PNG bytes.</param>
    /// <exception cref="ArgumentException">No frames, or a size outside 1-256.</exception>
    public static void Write(Stream output, IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("an icon needs at least one frame", nameof(frames));
        foreach (var (size, _) in frames)
            if (size is < 1 or > 256)
                throw new ArgumentException($"icon frame sizes must be 1-256, got {size}", nameof(frames));

        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Count);

        var offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            // A dimension of 256 does not fit a byte; the format spells it 0.
            writer.Write((byte)(size == 256 ? 0 : size));   // width
            writer.Write((byte)(size == 256 ? 0 : size));   // height
            writer.Write((byte)0);            // palette colours: none
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) writer.Write(png);
    }
}
