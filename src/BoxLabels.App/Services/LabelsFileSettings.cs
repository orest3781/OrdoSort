using System.Text.Json;
using System.Text.Json.Serialization;
using OrdoSort.Core;

namespace BoxLabelsApp.Services;

/// <summary>What this app remembers: where the shared box-labels.json lives,
/// and the Auto/Light/Dark choice.</summary>
public sealed class LabelsFileDoc
{
    [JsonPropertyName("box_labels_file")] public string BoxLabelsFile { get; set; } = "";

    /// <summary>"auto", "light" or "dark", the same values OrdoSort's
    /// config.json "theme" takes.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "auto";

    /// <summary>Keys this version doesn't know, kept so a hand-written or
    /// newer file survives being saved by this one.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extras { get; set; }
}

/// <summary>Reads and writes those settings, and works out which file to
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
        var value = ReadDoc(settingsPath).BoxLabelsFile?.Trim() ?? "";
        return value.Length == 0 ? "" : Config.ResolveBeside(settingsPath, value);
    }

    /// <summary>The remembered theme: "auto", "light" or "dark". Pre-rebrand
    /// scheme names migrate the way OrdoSort's config does
    /// (<see cref="Config.MigrateTheme"/>); anything else, a missing file or
    /// a damaged one is "auto", for the same reason <see cref="Read"/>
    /// forgives a damaged file.</summary>
    public static string ReadTheme(string settingsPath)
    {
        var theme = Config.MigrateTheme(ReadDoc(settingsPath).Theme);
        return theme is "light" or "dark" ? theme : "auto";
    }

    /// <summary>Remember this box-labels.json for next launch. Throws on a
    /// genuinely unwritable location — the caller reports that, because
    /// silently forgetting the choice would make the app ask again on every
    /// single launch with no explanation. Other settings in the file are
    /// kept.</summary>
    public static void Write(string settingsPath, string boxLabelsFile)
    {
        var doc = ReadDoc(settingsPath);
        doc.BoxLabelsFile = boxLabelsFile;
        WriteDoc(settingsPath, doc);
    }

    /// <summary>Remember the Auto/Light/Dark choice. Throws on an unwritable
    /// location, like <see cref="Write"/>; the remembered store path is
    /// kept.</summary>
    /// <param name="theme">"auto", "light" or "dark".</param>
    /// <exception cref="ArgumentException">Any other value.</exception>
    public static void WriteTheme(string settingsPath, string theme)
    {
        if (theme is not ("auto" or "light" or "dark"))
            throw new ArgumentException($"theme must be auto, light or dark, got \"{theme}\"", nameof(theme));
        var doc = ReadDoc(settingsPath);
        doc.Theme = theme;
        WriteDoc(settingsPath, doc);
    }

    /// <summary>The whole file, or a fresh one when it is missing or
    /// damaged. Every writer starts here, so saving one setting never erases
    /// the other.</summary>
    private static LabelsFileDoc ReadDoc(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return new LabelsFileDoc();
            return JsonSerializer.Deserialize<LabelsFileDoc>(File.ReadAllText(settingsPath), Opts)
                ?? new LabelsFileDoc();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LabelsFileDoc();
        }
    }

    private static void WriteDoc(string settingsPath, LabelsFileDoc doc)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(doc, Opts) + "\n");
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
