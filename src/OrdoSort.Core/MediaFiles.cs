using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace OrdoSort.Core;

/// <summary>Where a photo's date came from, most trustworthy first.</summary>
public enum DateTakenSource
{
    /// <summary>The camera wrote it into the file (EXIF, QuickTime).</summary>
    Metadata,
    /// <summary>A date in the file name (IMG_20260929_…, WhatsApp, screenshots).</summary>
    FileName,
    /// <summary>The file's last-modified time; often the date it was copied.</summary>
    Modified,
    /// <summary>The file couldn't be read at all (a share down, a file held
    /// by a sync client): today's date stands in, and is never kept, so the
    /// next read tries again.</summary>
    Unreadable,
}

/// <summary>The date a photo was taken, and where that came from.</summary>
public sealed record DateTaken(DateTime Date, DateTakenSource Source)
{
    /// <summary>YYYYMMDD, as it goes in the file name.</summary>
    public string Stamp => Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>Reads the date a photo was taken. An interface so the filing loop
/// can be tested without real photos.</summary>
public interface IDateTakenReader
{
    /// <summary>Never throws: a file with no readable date falls back to its
    /// modified time, and a file that can't be read at all to today
    /// (<see cref="DateTakenSource.Unreadable"/>).</summary>
    DateTaken Read(string path);
}

/// <summary>What kind of file the filing loop is showing.</summary>
public enum MediaKind { None, Photo, Video }

/// <summary>What a media file needs at the move: the date for its name and
/// the folder under the route it goes into ("" for the route's own).</summary>
public sealed record MediaTarget(string TakenStamp, string Subfolder);

/// <summary>
/// Photos, GIFs and videos in the filing loop (spec
/// 2026-09-29-media-filing-loop): which files count, and the date each was
/// taken.
/// </summary>
public static partial class MediaFiles
{
    /// <summary>True when <paramref name="path"/> is a photo, GIF or video
    /// the loop files: its kind is turned on and its extension is listed.</summary>
    public static bool IsMedia(string path, MediaSettings? media) => KindOf(path, media) != MediaKind.None;

    /// <summary>Photo or video by its listed extension; <see cref="MediaKind.None"/>
    /// for a PDF, anything unlisted, or a kind that is turned off.</summary>
    public static MediaKind KindOf(string path, MediaSettings? media)
    {
        if (media is null) return MediaKind.None;
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return MediaKind.None;
        if (media.Enabled && Listed(extension, media.Extensions)) return MediaKind.Photo;
        if (media.VideosEnabled && Listed(extension, media.VideoExtensions)) return MediaKind.Video;
        return MediaKind.None;
    }

    private static bool Listed(string extension, IEnumerable<string> extensions)
    {
        foreach (var listed in extensions)
            if (string.Equals(NormalizeExtension(listed), extension, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>What a filed media file needs: null for anything that isn't
    /// media, so a PDF files exactly as before.</summary>
    public static MediaTarget? TargetFor(string path, MediaSettings? media, Func<DateTaken> taken)
    {
        if (!IsMedia(path, media)) return null;
        var subfolder = media!.Folder == MediaSettings.FolderMedia ? MediaSettings.MediaFolderName : "";
        return new MediaTarget(taken().Stamp, subfolder);
    }

    /// <summary>".jpg" from "jpg", ".JPG" or " .jpg "; "" for blank text.</summary>
    public static string NormalizeExtension(string text)
    {
        var trimmed = (text ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return "";
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    /// <summary>An extension list as typed in Settings ("jpg, .png  gif"),
    /// cleaned: each once, in the order typed. PDF is never a media type.</summary>
    public static List<string> ParseExtensions(string text)
    {
        var result = new List<string>();
        foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var extension = NormalizeExtension(part);
            if (extension.Length < 2 || extension == Naming.PdfExt) continue;
            if (!result.Contains(extension)) result.Add(extension);
        }
        return result;
    }

    // A year from 1990 on, then month and day, with the same separator (or
    // none) between each part. Not inside a longer run of digits, except a
    // 6-digit time straight after (IMG20260929123456).
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<sep>[-_.]?)(?<m>\d{2})\k<sep>(?<d>\d{2})(?=\D|$|\d{6}(?:\D|$))")]
    private static partial Regex FileNameDateRegex();

    /// <summary>The first real date in a file name, or null: IMG_20260929_…,
    /// PXL_20260929…, VID-20260929-WA0001, "Screenshot 2026-09-29 at …".
    /// Eight digits that aren't a real date (an ID) don't count.</summary>
    public static DateTime? FromFileName(string fileName)
    {
        foreach (Match match in FileNameDateRegex().Matches(fileName ?? ""))
        {
            var digits = match.Groups["y"].Value + match.Groups["m"].Value + match.Groups["d"].Value;
            if (!BulkRename.IsRealDate(digits)) continue;
            var date = DateTime.ParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture);
            if (date.Year < 1990) continue;
            return date;
        }
        return null;
    }
}

/// <summary>Reads the date taken with MetadataExtractor (EXIF for photos,
/// the QuickTime header for phone videos), then the file name, then the
/// modified time.</summary>
public sealed class DateTakenReader : IDateTakenReader
{
    private readonly Func<DateTime> _today;

    /// <param name="today">The clock for a file that can't be read at all;
    /// the system date when not given.</param>
    public DateTakenReader(Func<DateTime>? today = null) => _today = today ?? (() => DateTime.Today);

    public DateTaken Read(string path)
    {
        var fromMetadata = FromMetadata(path);
        if (fromMetadata is { } taken) return new DateTaken(taken.Date, DateTakenSource.Metadata);
        var fromName = MediaFiles.FromFileName(Path.GetFileNameWithoutExtension(path));
        if (fromName is { } named) return new DateTaken(named, DateTakenSource.FileName);
        try
        {
            if (File.Exists(path)) return new DateTaken(File.GetLastWriteTime(path).Date, DateTakenSource.Modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new DateTaken(_today().Date, DateTakenSource.Unreadable);
    }

    /// <summary>The camera's own date, or null. Any file the library can't
    /// parse (a GIF, a truncated JPEG) is simply "no date".</summary>
    private static DateTime? FromMetadata(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
#pragma warning disable CA1031 // the parser walks untrusted bytes from any camera and can fail any way
        catch (Exception)
        {
            // a corrupt or unusual file is "no date in it", never a photo
            // that can't be filed
            return null;
        }
#pragma warning restore CA1031

        foreach (var exif in directories.OfType<ExifSubIfdDirectory>())
        {
            // local camera time, as the camera clock showed it
            if (exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original) && Plausible(original))
                return original;
            if (exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized) && Plausible(digitized))
                return digitized;
        }
        foreach (var movie in directories.OfType<QuickTimeMovieHeaderDirectory>())
        {
            // stored in UTC; the date is the one where it was shot
            if (movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && Plausible(created))
                return DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
        }
        // IFD0's DateTime is when the file was last edited, not taken: left
        // out, so a re-saved IMG_20190704_… keeps its file-name date
        return null;
    }

    /// <summary>Cameras with a flat clock battery write 1970 or 1904 (the
    /// QuickTime epoch), which is no date at all.</summary>
    private static bool Plausible(DateTime date) => date.Year >= 1990 && date <= DateTime.Now.AddDays(2);
}
