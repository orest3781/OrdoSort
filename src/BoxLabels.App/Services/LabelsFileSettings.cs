using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoxLabelsApp.Services;

/// <summary>The one thing this app has to remember: where the shared
/// box-labels.json lives.</summary>
public sealed class LabelsFileDoc
{
    [JsonPropertyName("box_labels_file")] public string BoxLabelsFile { get; set; } = "";
}

/// <summary>Reads and writes that one setting, and works out which file to
/// open for a given run.
///
/// The file sits beside the exe, the way OrdoSort's config.json does, so the
/// whole app is a folder you can copy to a station and pre-point at the share
/// before handing it over.
///
/// Every method here is a pure function over paths — no dialogs, no
/// Application — so the resolution rules can be tested without a UI.</summary>
public static class LabelsFileSettings
{
    public const string FileName = "box-labels-app.json";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where the settings file sits for a given install folder.</summary>
    public static string PathIn(string appDirectory) => Path.Combine(appDirectory, FileName);

    /// <summary>The remembered box-labels.json path, or "" when there isn't
    /// one yet.
    ///
    /// A missing file is first run. A damaged one is treated the same way
    /// rather than thrown: this setting is a convenience the user can re-pick
    /// in one click, and refusing to start over a corrupt one-key file would
    /// be a dead end for someone with no way to fix it.</summary>
    public static string Read(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return "";
            var doc = JsonSerializer.Deserialize<LabelsFileDoc>(
                File.ReadAllText(settingsPath), Opts);
            return doc?.BoxLabelsFile?.Trim() ?? "";
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Remember this box-labels.json for next launch. Throws on a
    /// genuinely unwritable location — the caller reports that, because
    /// silently forgetting the choice would make the app ask again on every
    /// single launch with no explanation.</summary>
    public static void Write(string settingsPath, string boxLabelsFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(settingsPath,
            JsonSerializer.Serialize(new LabelsFileDoc { BoxLabelsFile = boxLabelsFile }, Opts) + "\n");
    }

    /// <summary>The "--file &lt;path&gt;" argument, or null when it isn't
    /// there. Mirrors OrdoSort's own "--config &lt;path&gt;".</summary>
    public static string? FromArgs(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--file" && args[i + 1].Trim().Length > 0)
                return args[i + 1].Trim();
        return null;
    }

    /// <summary>Which box-labels.json this run should open: the command line
    /// wins, then whatever was remembered, and "" means there is nothing to
    /// go on and the user has to be asked.
    ///
    /// A "--file" path is deliberately NOT remembered. It is for running
    /// against a different store once — a test copy, a second client's share
    /// — and quietly rewriting the saved setting would leave the app pointed
    /// somewhere the user never chose the next time they double-clicked
    /// it.</summary>
    public static string Resolve(string[] args, string settingsPath) =>
        FromArgs(args) ?? Read(settingsPath);
}
