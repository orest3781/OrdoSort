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
/// Labels makes for its own settings file).</summary>
public sealed class TableLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public TableLayoutStore(string path) => _path = path;

    /// <summary>Where the app's own layouts live. Settable only so the test
    /// assembly can point it at a temp folder.</summary>
    public static string DefaultPath { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "table-columns.json");

    /// <summary>The saved layout for <paramref name="key"/>, or null when
    /// there is none or the file can't be read.</summary>
    public TableLayout? Load(string key) =>
        ReadAll().TryGetValue(key, out var layout) ? layout : null;

    /// <summary>Saves <paramref name="layout"/> under <paramref name="key"/>,
    /// keeping every other key and every column of this key the new layout
    /// doesn't mention.</summary>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to write it.</exception>
    public void Save(string key, TableLayout layout)
    {
        var all = ReadAll();
        var columns = new Dictionary<string, ColumnLayout>();
        if (all.TryGetValue(key, out var old))
            foreach (var column in old.Columns) columns[column.Header] = column;
        foreach (var column in layout.Columns) columns[column.Header] = column;
        all[key] = layout with { Columns = columns.Values.ToList() };

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, Options));
    }

    private Dictionary<string, TableLayout> ReadAll()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, TableLayout>>(File.ReadAllText(_path), Options)
                ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
