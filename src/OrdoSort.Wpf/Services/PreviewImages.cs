using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Services;

/// <summary>Photos Edge can't show itself (HEIC), made into something it can.</summary>
public interface IPreviewImages
{
    /// <summary>True for a file Edge can't open as it is.</summary>
    bool NeedsConversion(string path);

    /// <summary>A file Edge can show in its place: a JPEG, or a page saying why
    /// there is no preview. Never throws.</summary>
    string Prepare(string path);
}

/// <summary>
/// Decodes HEIC through Windows' own imaging (WIC), the codec Windows Photos
/// uses, into a JPEG in the local stage folder. Without Microsoft's HEIF and
/// HEVC extensions Windows can't read HEIC at all; the pane then says so,
/// and the photo still files as normal.
/// </summary>
public sealed class PreviewImages : IPreviewImages, IDisposable
{
    private static readonly string[] Converted = { ".heic", ".heif" };

    /// <summary>The longest side of a preview; a 48-megapixel phone photo is
    /// ten times what the pane can show.</summary>
    private const int MaxSide = 2400;

    /// <summary>How many previews are kept: the one on screen, the ones made
    /// ahead for the next photos, and the last few, so going back is instant.
    /// Older ones are deleted.</summary>
    private const int Kept = 6;

    /// <summary>A crashed instance's folder is swept after this long.</summary>
    private static readonly TimeSpan Orphaned = TimeSpan.FromDays(1);

    private readonly string _folder;
    private readonly bool _ownsFolder;

    /// <param name="folder">Where previews go. When not given, a folder of
    /// this instance's own under the user's local profile, beside (not in)
    /// the read-ahead copies, whose own cleanup would sweep it; deleted on
    /// Dispose.</param>
    public PreviewImages(string? folder = null)
    {
        if (folder is not null)
        {
            _folder = folder;
            return;
        }
        var parent = Path.Combine(Path.GetDirectoryName(DocumentStage.Root) ?? Path.GetTempPath(), "preview");
        SweepOrphans(parent);
        _folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        _ownsFolder = true;
    }

    public void Dispose()
    {
        if (!_ownsFolder) return;
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void SweepOrphans(string parent)
    {
        try
        {
            if (!Directory.Exists(parent)) return;
            foreach (var folder in new DirectoryInfo(parent).GetDirectories())
            {
                if (DateTime.UtcNow - folder.LastWriteTimeUtc < Orphaned) continue;
                try { folder.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool NeedsConversion(string path) =>
        Array.IndexOf(Converted, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    public string Prepare(string path)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            TrimOld();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
        try
        {
            return WriteJpeg(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException
                                       or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return WriteNoPreviewPage(path);
        }
    }

    private string WriteJpeg(string path)
    {
        BitmapSource frame;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            frame = Upright(frame, (decoder.Frames[0].Metadata as BitmapMetadata));
        }
        frame = Shrunk(frame);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        var target = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".jpg");
        using (var output = File.Create(target)) encoder.Save(output);
        return target;
    }

    /// <summary>Turns the photo the way the camera held it: WIC decodes
    /// pixels as stored and leaves the EXIF orientation to the viewer.</summary>
    private static BitmapSource Upright(BitmapSource frame, BitmapMetadata? metadata)
    {
        ushort orientation = 1;
        try
        {
            if (metadata?.GetQuery("System.Photo.Orientation") is ushort value) orientation = value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                       or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return frame;
        }
        Transform? turn = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => Turned(90, flip: true),
            6 => new RotateTransform(90),
            7 => Turned(270, flip: true),
            8 => new RotateTransform(270),
            _ => null,
        };
        return turn is null ? frame : new TransformedBitmap(frame, turn);
    }

    private static Transform Turned(double degrees, bool flip)
    {
        var group = new TransformGroup();
        if (flip) group.Children.Add(new ScaleTransform(-1, 1));
        group.Children.Add(new RotateTransform(degrees));
        return group;
    }

    private static BitmapSource Shrunk(BitmapSource frame)
    {
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest <= MaxSide) return frame;
        var scale = (double)MaxSide / longest;
        return new TransformedBitmap(frame, new ScaleTransform(scale, scale));
    }

    private string WriteNoPreviewPage(string path)
    {
        var name = WebUtility.HtmlEncode(Path.GetFileName(path));
        var html = $$"""
            <!doctype html>
            <meta charset="utf-8">
            <style>
              body { font: 16px "Segoe UI", sans-serif; display: grid; place-items: center;
                     height: 100vh; margin: 0; background: #f4f2ee; color: #2b2a28; }
              @media (prefers-color-scheme: dark) { body { background: #1f1e1c; color: #e8e4dc; } }
              div { max-width: 30em; text-align: center; line-height: 1.5; padding: 0 24px; }
              b { display: block; font-size: 18px; margin-bottom: 8px; }
            </style>
            <div>
              <b>No preview for {{name}}</b>
              Windows can't open HEIC photos until Microsoft's free <i>HEIF Image Extensions</i>
              and <i>HEVC Video Extensions</i> are installed from the Microsoft Store.
              You can still name and file it.
            </div>
            """;
        var target = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(target, html);
        return target;
    }

    private void TrimOld()
    {
        var old = new DirectoryInfo(_folder).GetFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(Kept - 1);
        foreach (var file in old)
        {
            try { file.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
