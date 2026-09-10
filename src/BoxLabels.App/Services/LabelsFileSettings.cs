using System.Text.Json;
using System.Text.Json.Serialization;
using OrdoSort.Core;

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
    /// be a dead end for someone with no way to fix it.
    ///
    /// A RELATIVE value is resolved against the settings file's own folder,
    /// not the working directory. The README tells people to hand-write this
    /// file when pre-pointing a station, so "box-labels.json" is a spelling
    /// that will genuinely appear — and resolved against the working
    /// directory it would name a different store depending on how the app was
    /// launched. <see cref="Config.ResolveBeside"/> is the same helper
    /// OrdoSort applies to the same key name in its own config.</summary>
    public static string Read(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return "";
            var doc = JsonSerializer.Deserialize<LabelsFileDoc>(
                File.ReadAllText(settingsPath), Opts);
            var value = doc?.BoxLabelsFile?.Trim() ?? "";
            return value.Length == 0 ? "" : Config.ResolveBeside(settingsPath, value);
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

    /// <summary>Whether the folder holding <paramref name="labelsFile"/> can
    /// be reached right now.
    ///
    /// A missing FILE inside a reachable folder is ordinary — the store
    /// creates it. An unreachable FOLDER is not: carrying on would create a
    /// private empty store on the first claim, and every station's box
    /// numbers would start again at 1.</summary>
    public static bool FolderReachable(string labelsFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(labelsFile));
            return string.IsNullOrEmpty(dir) || Directory.Exists(dir);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;   // a malformed remembered path is not reachable
        }
    }

    /// <summary>What this run should do about the store, decided without
    /// touching a dialog so the rules can be tested.
    ///
    /// <paramref name="folderReachable"/> is injected for the same reason —
    /// pass <see cref="FolderReachable"/> in the app.</summary>
    public static LabelsFileDecision Decide(string[] args, string settingsPath,
        Func<string, bool> folderReachable)
    {
        if (FromArgs(args) is { } fromArgs)
            return folderReachable(fromArgs)
                ? new(LabelsFilePrompt.None, fromArgs, PersistChoice: false)
                // PersistChoice stays FALSE here, and that is the whole point:
                // a --file run must not repoint the saved setting, not even
                // when the path it names is unreachable and the user picks
                // another one to get going. Doing so silently repointed the
                // app for every later double-click — the defect this
                // function was extracted to make testable.
                : new(LabelsFilePrompt.Unreachable, fromArgs, PersistChoice: false);

        var remembered = Read(settingsPath);
        if (remembered.Length == 0)
            return new(LabelsFilePrompt.FirstRun, "", PersistChoice: true);

        return folderReachable(remembered)
            ? new(LabelsFilePrompt.None, remembered, PersistChoice: false)
            : new(LabelsFilePrompt.Unreachable, remembered, PersistChoice: true);
    }
}

/// <summary>What the user needs to be told before being asked to pick.</summary>
public enum LabelsFilePrompt
{
    /// <summary>Nothing to ask — open the store and go.</summary>
    None,

    /// <summary>Nothing is remembered: explain what the file is, then pick.</summary>
    FirstRun,

    /// <summary>Something is remembered but its folder is gone: say which
    /// path failed, then offer to pick another.</summary>
    Unreachable,
}

/// <summary>The outcome of <see cref="LabelsFileSettings.Decide"/>.</summary>
/// <param name="Prompt">What to show before picking, if anything.</param>
/// <param name="Path">
/// When <see cref="Prompt"/> is <see cref="LabelsFilePrompt.None"/>, the store
/// to open. When <see cref="LabelsFilePrompt.Unreachable"/>, the path that
/// could NOT be reached — for the message, not for opening. Empty for
/// <see cref="LabelsFilePrompt.FirstRun"/>.
/// </param>
/// <param name="PersistChoice">
/// Whether a file the user then picks should be remembered for next launch.
/// False for anything a "--file" argument started, which is a one-off run.
/// </param>
public readonly record struct LabelsFileDecision(
    LabelsFilePrompt Prompt, string Path, bool PersistChoice);
