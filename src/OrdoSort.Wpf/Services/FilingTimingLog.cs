using System.Globalization;

namespace OrdoSort.Wpf.Services;

/// <summary>
/// One line per filed document, for measuring the filing loop on a real
/// share (config "timing": true; spec 2026-09-29-filing-loop-design.md).
/// Kept in the local profile, never beside a shared config.json, and never
/// names a document: the same privacy line as crash.log (QC-21).
/// </summary>
internal static class FilingTimingLog
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "timing.log");

    /// <summary>The file in use; the test run points it at its own folder.</summary>
    internal static string PathInUse { get; set; } = DefaultPath;

    /// <summary>A tab-separated line: when, what, how long the next page
    /// took to show after the key press, whether the page left came from a
    /// local copy, how long Edge took to let go of the file, and the move.</summary>
    public static string Line(DateTime when, bool setAside, double shownMs, bool shownFromCopy,
        double releaseMs, double moveMs) =>
        string.Join('\t',
            when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            setAside ? "set aside" : "filed",
            $"next page {shownMs.ToString("0", CultureInfo.InvariantCulture)} ms",
            shownFromCopy ? "local copy" : "from inbox",
            $"release {releaseMs.ToString("0", CultureInfo.InvariantCulture)} ms",
            $"move {moveMs.ToString("0", CultureInfo.InvariantCulture)} ms");

    /// <summary>Appends a line; a measurement never gets in the way of
    /// filing, so a failure to write is ignored.</summary>
    public static void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathInUse)!);
            File.AppendAllText(PathInUse, line + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
