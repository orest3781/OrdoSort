using System.Text.Json;

namespace OrdoSort.Wpf.Services;

/// <summary>The File list's view options that are not columns. The columns
/// are remembered with the table layout (<see cref="TableLayoutStore"/>).</summary>
/// <param name="IncludeSubfolders">Read the folders' subfolders too.</param>
/// <param name="IncludeExtension">Show names with their extensions.</param>
/// <param name="Descending">Z to A rather than A to Z.</param>
/// <param name="ExtensionFilter">The "Only these types" text.</param>
public sealed record FilenameListOptions(bool IncludeSubfolders, bool IncludeExtension, bool Descending,
    string ExtensionFilter);

/// <summary>Remembers the File list's view options on this PC between
/// sessions (FL-07), in a file beside the table layouts and for the same
/// reason: several stations share config.json, and one person's view is not
/// everyone's. A missing or damaged file reads as "nothing saved" and a
/// failed save is reported, never thrown: a view preference must never stop
/// the window opening or closing.</summary>
public sealed class FilenameListOptionsStore
{
    private readonly string _path;
    private readonly Action<Exception> _report;

    /// <param name="path">The options file.</param>
    /// <param name="report">Told when the file can't be read or written.</param>
    public FilenameListOptionsStore(string path, Action<Exception> report)
    {
        _path = path;
        _report = report;
    }

    /// <summary>Beside <see cref="TableLayoutStore.DefaultPath"/>, so the test
    /// assembly's redirect of that folder covers this file too.</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(TableLayoutStore.DefaultPath)!, "filename-list.json");

    /// <summary>The saved options, or null when there are none or the file
    /// can't be read (reported).</summary>
    public FilenameListOptions? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<FilenameListOptions>(File.ReadAllText(_path))
                ?? throw new JsonException("the options file holds null");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _report(e);
            return null;
        }
    }

    /// <summary>Saves <paramref name="options"/>; a failure is reported.</summary>
    public void Save(FilenameListOptions options)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Written beside the file and moved over it, so a crash mid-write
            // leaves the old options, not half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _report(e);
        }
    }
}
