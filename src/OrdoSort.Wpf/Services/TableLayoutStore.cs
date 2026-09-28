using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Wpf.Services;

/// <summary>One column's remembered state.</summary>
/// <param name="Header">The column's header text: its identity.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Visible">False when the user hid it.</param>
/// <param name="DisplayIndex">Its position after any drag-reorder.</param>
public sealed record ColumnLayout(string Header, double Width, bool Visible, int DisplayIndex);

/// <summary>A table's remembered layout: its columns and its sort.</summary>
public sealed record TableLayout(IReadOnlyList<ColumnLayout> Columns, string? SortHeader,
    ListSortDirection? SortDirection);

/// <summary>Remembers each window's table layout on this PC, the way
/// Explorer remembers a folder's view (table rules v2, rule 7).
///
/// Lives under %LOCALAPPDATA%, never in config.json: several stations
/// share one config.json, and one person's column widths are not
/// everyone's. A missing or damaged file reads as "nothing saved": a
/// layout preference must never stop a window opening (the same call Box
/// Labels makes for its own settings file). A damaged file is reported and,
/// on the next save, kept beside the new one as <c>.damaged</c>; a file that
/// can't be read right now is never saved over.</summary>
public sealed class TableLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Action<Exception> _reportDamaged;

    /// <param name="path">The layout file.</param>
    /// <param name="reportDamaged">Told when the file is damaged or can't be
    /// read, since both read as "nothing saved"; defaults to ignoring it.</param>
    public TableLayoutStore(string path, Action<Exception>? reportDamaged = null)
    {
        _path = path;
        _reportDamaged = reportDamaged ?? (_ => { });
    }

    /// <summary>Where the app's own layouts live. Settable only so the test
    /// assembly can point it at a temp folder.</summary>
    public static string DefaultPath { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "table-columns.json");

    /// <summary>The saved layout for <paramref name="key"/>, or null when
    /// there is none or the file can't be read.</summary>
    public TableLayout? Load(string key)
    {
        try
        {
            return Read() is { } all && all.TryGetValue(key, out var layout) ? layout : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _reportDamaged(e);
            return null;
        }
    }

    /// <summary>Saves <paramref name="layout"/> under <paramref name="key"/>,
    /// keeping every other key and every column of this key the new layout
    /// doesn't mention.</summary>
    /// <exception cref="IOException">The file can't be read or written. A
    /// file that can't be read is left as it is, so the other windows'
    /// layouts survive.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to read or write it.</exception>
    public void Save(string key, TableLayout layout)
    {
        var all = Read();
        if (all is null)
        {
            File.Copy(_path, _path + ".damaged", overwrite: true);
            all = new();
        }
        var columns = new Dictionary<string, ColumnLayout>();
        if (all.TryGetValue(key, out var old))
            foreach (var column in old.Columns) columns[column.Header] = column;
        foreach (var column in layout.Columns) columns[column.Header] = column;
        all[key] = layout with { Columns = columns.Values.ToList() };

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Written beside the file and moved over it, so a crash or a full
        // disk mid-write leaves the old layouts, not half a file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Every saved layout; empty when there is no file, null when
    /// the file is damaged (reported).</summary>
    /// <exception cref="IOException">The file can't be read right now.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to read it.</exception>
    private Dictionary<string, TableLayout>? Read()
    {
        if (!File.Exists(_path)) return new();
        var text = File.ReadAllText(_path);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, TableLayout>>(text, Options)
                ?? throw new JsonException("the layout file holds null");
        }
        catch (JsonException e)
        {
            _reportDamaged(e);
            return null;
        }
    }
}
