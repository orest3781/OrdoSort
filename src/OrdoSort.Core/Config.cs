using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

/// <summary>A single filing destination.</summary>
public sealed class Route
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "";
    [JsonPropertyName("append_suffix")] public bool AppendSuffix { get; set; }
    [JsonPropertyName("suffix")] public string Suffix { get; set; } = "";
    [JsonPropertyName("naming_mode")] public string? NamingMode { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }

    // Hand-edited per-route keys survive a load/save round trip
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement> Extras { get; set; } = new();
}

/// <summary>A folder shown as a tile on the Ready dashboard while it holds
/// files — another process's work queue, a "failed" folder, etc.</summary>
public sealed class WatchFolder
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("recursive")] public bool Recursive { get; set; }
    [JsonPropertyName("filetypes")] public string Filetypes { get; set; } = "";   // "pdf" or "pdf,txt"; blank = any
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("section")] public string Section { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement> Extras { get; set; } = new();
}

/// <summary>One box-label client: its own retention offset (created date +
/// days = destruction date) and a resettable running label number.</summary>
public sealed class LabelClient
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("destroy_days")] public int DestroyDays { get; set; } = 30;
    [JsonPropertyName("next_number")] public long NextNumber { get; set; } = 1;

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement> Extras { get; set; } = new();
}

/// <summary>Which sound plays for each moment. Each value is a small spec:
/// "" = the built-in OrdoSort sound, "none" = silent, "windows" = a fitting
/// Windows system chime, or a path to a .wav (the ICQ-"uh oh" slot).</summary>
public sealed class SoundSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("new_alert")] public string NewAlert { get; set; } = "";
    [JsonPropertyName("filed")] public string Filed { get; set; } = "none";
    [JsonPropertyName("set_aside")] public string SetAside { get; set; } = "none";
    [JsonPropertyName("error")] public string Error { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement> Extras { get; set; } = new();
}

/// <summary>A saved Unlock-tool password. The password value is either
/// DPAPI-protected ("dpapi:&lt;base64&gt;", written by the app) or legacy
/// plaintext (hand-edited).</summary>
public sealed class SavedPassword
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

/// <summary>config.json load/save/defaults/validation. Unknown top-level keys
/// survive a load/save round trip (kept in <see cref="Extras"/>).</summary>
public sealed class Config
{
    public static readonly string[] Sorts =
    {
        "filename_asc", "filename_desc", "mtime_asc", "mtime_desc",
        "size_asc", "size_desc",
    };

    [JsonPropertyName("inbox")] public string Inbox { get; set; } = "";
    [JsonPropertyName("deferred")] public string Deferred { get; set; } = "";
    [JsonPropertyName("names_file")] public string NamesFile { get; set; } = "names.txt";
    [JsonPropertyName("history_db")] public string HistoryDb { get; set; } = "history.sqlite";
    [JsonPropertyName("naming_mode")] public string NamingMode { get; set; } = "insert";
    [JsonPropertyName("sort")] public string Sort { get; set; } = "size_desc";
    [JsonPropertyName("enter_commits")] public bool EnterCommits { get; set; } = true;
    [JsonPropertyName("uppercase_names")] public bool UppercaseNames { get; set; } = true;
    [JsonPropertyName("routes")] public List<Route> Routes { get; set; } = new();

    // Match & merge: which spreadsheet headers hold the names + the id
    [JsonPropertyName("merge_headers")] public Dictionary<string, string> MergeHeaders { get; set; } = new();

    // Match & merge: the roster picked last time, auto-loaded while it exists,
    // and which of its headers Review matches shows (empty = the mapped
    // name and id columns)
    [JsonPropertyName("merge_roster")] public string MergeRoster { get; set; } = "";
    [JsonPropertyName("merge_columns")] public List<string> MergeColumns { get; set; } = new();

    // Ready dashboard: monitored-folder tiles + filename alerts
    [JsonPropertyName("watch_folders")] public List<WatchFolder> WatchFolders { get; set; } = new();
    [JsonPropertyName("alert_texts")] public List<string> AlertTexts { get; set; } = new();
    [JsonPropertyName("monitor_title")] public string MonitorTitle { get; set; } = "Monitored folders";
    [JsonPropertyName("flash_alerts")] public bool FlashAlerts { get; set; } = true;

    /// <summary>The poll cadence used when config.json doesn't say otherwise.
    /// Referenced by the watcher and the Settings screen so there is exactly
    /// one place this number lives.</summary>
    public const int DefaultPollSeconds = 15;

    public const int MinPollSeconds = 5;
    public const int MaxPollSeconds = 600;

    /// <summary>How often (seconds) to re-check monitored folders and backstop
    /// SMB-dropped inbox notifications. The inbox/set-aside folders are also
    /// file-watched (near-instant); this poll is what catches watch-folder
    /// arrivals, so lower = snappier alerts, higher = gentler on a share.</summary>
    [JsonPropertyName("poll_seconds")] public int PollSeconds { get; set; } = DefaultPollSeconds;

    /// <summary>Monitored-folder tile visibility: "active" (tiles appear only
    /// while a folder holds files — the default), "all" (every tile stays,
    /// even at zero), or "hidden" (no tiles, and the folder sweep is skipped
    /// entirely). Unknown values read as "active".</summary>
    [JsonPropertyName("tile_visibility")] public string TileVisibility { get; set; } = "active";

    // Appearance (key names are stable, so existing configs round-trip)
    [JsonPropertyName("ui_font_family")] public string UiFontFamily { get; set; } = "";
    [JsonPropertyName("ui_font_size")] public int UiFontSize { get; set; }   // 0 = default

    // "auto" follows the Windows light/dark preference; "light"/"dark" force it
    [JsonPropertyName("theme")] public string Theme { get; set; } = "auto";

    // Typed space becomes this string in the name box ("" = keep spaces)
    [JsonPropertyName("word_separator")] public string WordSeparator { get; set; } = "";

    // The unlock tool has no setting: an unlocked PDF always keeps its name and
    // place, and the locked original always moves to a dated locked_archive
    // folder. "unlock_suffix" was retired with that choice; an existing config
    // still carrying the key keeps it harmlessly, since saving clones unknown
    // keys through.
    [JsonPropertyName("saved_passwords")] public List<SavedPassword> SavedPasswords { get; set; } = new();
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonPropertyName("sounds")] public SoundSettings Sounds { get; set; } = new();

    // ---- split config: where each section lives (relative = beside config.json)
    public const string DefaultDestinationsFile = "destinations.json";
    public const string DefaultMonitoredFoldersFile = "monitored-folders.json";
    public const string DefaultAlertsFile = "alerts.json";
    public const string DefaultBoxLabelsFile = "box-labels.json";

    [JsonPropertyName("destinations_file")] public string DestinationsFile { get; set; } = DefaultDestinationsFile;
    [JsonPropertyName("monitored_folders_file")] public string MonitoredFoldersFile { get; set; } = DefaultMonitoredFoldersFile;
    [JsonPropertyName("alerts_file")] public string AlertsFile { get; set; } = DefaultAlertsFile;
    [JsonPropertyName("box_labels_file")] public string BoxLabelsFile { get; set; } = DefaultBoxLabelsFile;

    // Unknown top-level keys of each side file, carried for round-trip
    [JsonIgnore] public Dictionary<string, JsonElement> DestinationsFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> MonitoredFoldersFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> AlertsFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> BoxLabelsFileExtras { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Load config, creating it with defaults on first run.</summary>
    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new Config();
            try
            {
                Save(fresh, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                // Every other failure in Load is a ConfigException, and
                // App.OnStartup catches exactly that. An unguarded write
                // failure here escaped startup entirely and left a windowless,
                // un-closeable process (2026-08-04 audit 3.1).
                throw new ConfigException(
                    $"OrdoSort couldn't create its settings file at {path}: {ex.Message}\n\n" +
                    "This usually means the folder is read-only — for example if OrdoSort was " +
                    "installed under Program Files. Move it somewhere you can write, such as " +
                    "your Documents folder, or start it with --config pointing at a writable path.");
            }
            return fresh;
        }
        Config cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Opts)
                  ?? throw new ConfigException($"Config file {path} is empty");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Config file {path} is not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Config file {path} could not be read: {ex.Message}");
        }
        cfg.Normalize();
        if (Array.IndexOf(Naming.Modes, cfg.NamingMode) < 0)
            throw new ConfigException(
                $"naming_mode must be one of {string.Join('/', Naming.Modes)}, got \"{cfg.NamingMode}\"");
        if (Array.IndexOf(Sorts, cfg.Sort) < 0)
            throw new ConfigException($"sort must be one of {string.Join('/', Sorts)}, " +
                                      $"got \"{cfg.Sort}\"");
        if (cfg.UiFontSize is not 0 and (< 6 or > 72))
            throw new ConfigException(
                $"ui_font_size must be 0 (default) or 6-72, got {cfg.UiFontSize}");
        if (cfg.WordSeparator.Contains(' '))
            throw new ConfigException(
                "word_separator must not contain a space — substitution would loop forever");
        if (cfg.Theme is not ("auto" or "light" or "dark"))
            throw new ConfigException(
                $"theme must be one of auto/light/dark, got \"{cfg.Theme}\"");
        if (cfg.PollSeconds is < MinPollSeconds or > MaxPollSeconds)
            throw new ConfigException(
                $"poll_seconds must be {MinPollSeconds}-{MaxPollSeconds}, " +
                $"got {cfg.PollSeconds}");

        // ---- split sections: a side file wins; inline (legacy) is the fallback
        if (ReadDoc<DestinationsDoc>(path, cfg.DestinationsFile) is { } dd)
        {
            cfg.Routes = Clean(dd.Routes);
            cfg.DestinationsFileExtras = dd.Extras ?? new();
        }
        if (ReadDoc<MonitoredFoldersDoc>(path, cfg.MonitoredFoldersFile) is { } md)
        {
            cfg.WatchFolders = Clean(md.WatchFolders);
            cfg.MonitoredFoldersFileExtras = md.Extras ?? new();
        }
        if (ReadDoc<AlertsDoc>(path, cfg.AlertsFile) is { } ad)
        {
            cfg.AlertTexts = Clean(ad.AlertTexts);
            cfg.AlertsFileExtras = ad.Extras ?? new();
        }
        if (ReadDoc<BoxLabelsDoc>(path, cfg.BoxLabelsFile) is { } bd)
        {
            cfg.LabelClients = Clean(bd.LabelClients);
            cfg.BoxLabelsFileExtras = bd.Extras ?? new();
        }
        cfg.NormalizeSectionItems();
        return cfg;
    }

    /// <summary>Resolve a section-file path: absolute stays; relative lands
    /// beside config.json (the names_file / history_db rule).</summary>
    public static string ResolveBeside(string configPath, string sectionPath) =>
        Path.IsPathRooted(sectionPath)
            ? sectionPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, sectionPath);

    public static T? ReadDoc<T>(string configPath, string sectionPath) where T : class
    {
        var full = ResolveBeside(configPath, sectionPath);
        if (!File.Exists(full)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(full), Opts)
                   ?? throw new ConfigException($"Config file {full} is empty");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Config file {full} is not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Config file {full} could not be read: {ex.Message}");
        }
    }

    /// <summary>An explicit JSON null means the same thing as an absent key:
    /// use the default. System.Text.Json only applies a property initializer
    /// when the key is MISSING, so `"routes": null` would otherwise leave a
    /// null field that crashes later — at the first keystroke, not at load.
    /// Hand-editing config.json is a supported workflow, so every nullable
    /// field gets put back to its declared default here.</summary>
    private void Normalize()
    {
        Inbox ??= "";
        Deferred ??= "";
        NamesFile ??= "names.txt";
        HistoryDb ??= "history.sqlite";
        // Value-typed keys need nothing here: a JSON null on an int or bool
        // can't be deserialized at all, so it already surfaces as a readable
        // ConfigException. Normalizing them would only mask an explicit
        // out-of-range 0 that validation is supposed to reject.
        NamingMode ??= "insert";
        // The "template" naming mode was removed (2026-08). A config that
        // still carries it loads as "replace" — the closest surviving
        // semantics — so an old file never fails validation over a mode
        // that no longer exists. (Its old template value, if any, rides
        // along untyped in Extras.)
        if (NamingMode == "template") NamingMode = "replace";
        Sort ??= "size_desc";
        MonitorTitle ??= "Monitored folders";
        TileVisibility ??= "active";
        UiFontFamily ??= "";
        Theme ??= "auto";
        WordSeparator ??= "";

        DestinationsFile ??= DefaultDestinationsFile;
        MonitoredFoldersFile ??= DefaultMonitoredFoldersFile;
        AlertsFile ??= DefaultAlertsFile;
        BoxLabelsFile ??= DefaultBoxLabelsFile;

        Routes = Clean(Routes);
        WatchFolders = Clean(WatchFolders);
        SavedPasswords = Clean(SavedPasswords);
        LabelClients = Clean(LabelClients);
        AlertTexts = Clean(AlertTexts);
        MergeHeaders ??= new();
        MergeRoster ??= "";
        MergeColumns ??= new();
        Extras ??= new();

        Sounds ??= new();
        Sounds.NewAlert ??= "";
        Sounds.Filed ??= "none";
        Sounds.SetAside ??= "none";
        Sounds.Error ??= "";
        Sounds.Extras ??= new();

        foreach (var p in SavedPasswords) { p.Label ??= ""; p.Password ??= ""; }
        NormalizeSectionItems();
    }

    /// <summary>Per-item null-hardening for Routes, WatchFolders, and LabelClients.
    /// Called by Normalize() and also by Load() after reading side files to ensure
    /// consistency regardless of source.</summary>
    internal void NormalizeSectionItems()
    {
        foreach (var r in Routes)
        {
            r.Label ??= ""; r.Path ??= ""; r.Hotkey ??= ""; r.Suffix ??= "";
            r.Extras ??= new();
            // removed-mode migration — see the note in Normalize()
            if (r.NamingMode == "template") r.NamingMode = "replace";
        }
        foreach (var w in WatchFolders)
        {
            w.Label ??= ""; w.Path ??= ""; w.Filetypes ??= ""; w.Section ??= "";
            w.Extras ??= new();
        }
        foreach (var c in LabelClients) { c.Id ??= ""; c.Extras ??= new(); }
    }

    /// <summary>A null list becomes empty; a list with null ENTRIES (a stray
    /// comma in hand-edited JSON) drops them rather than keeping a null item.</summary>
    private static List<T> Clean<T>(List<T>? items) where T : class =>
        items is null ? new() : items.Where(i => i is not null).ToList();

    /// <summary>Write the main config (without the split sections) and the
    /// Settings-owned side files. box-labels.json is bootstrap-only: created
    /// when missing, never overwritten — its counters belong to the Box
    /// labels tool's exclusive writer (BoxLabelStore).</summary>
    public static void Save(Config cfg, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        SaveMain(cfg, path);
        WriteDoc(path, cfg.DestinationsFile,
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras });
        WriteDoc(path, cfg.MonitoredFoldersFile,
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras });
        WriteDoc(path, cfg.AlertsFile,
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras });
        var labels = ResolveBeside(path, cfg.BoxLabelsFile);
        if (!File.Exists(labels))
            WriteJson(labels,
                new BoxLabelsDoc { LabelClients = cfg.LabelClients, Extras = cfg.BoxLabelsFileExtras });
    }

    /// <summary>Write via a sibling temp file, then replace — so an observer
    /// of <paramref name="fullPath"/> sees the complete old content or the
    /// complete new content, never a truncated or empty file. The previous
    /// File.WriteAllText truncated in place; a crash or a full disk in the
    /// gap destroyed a valid config, and on a shared config that took every
    /// station down until someone repaired it by hand (2026-08-04 audit 2.3).
    ///
    /// The temp file is a sibling, never %TEMP%: replace is only atomic
    /// within one volume, and the config can live on a share. Retries up to
    /// 500ms if the destination is held open for reading (Config.Load uses
    /// File.ReadAllText with no FileShare.Delete), allowing readers to
    /// release the handle so the atomic replace completes.</summary>
    internal static void WriteAtomic(string fullPath, string content)
    {
        var tmp = fullPath + ".tmp";
        // Encoding matches File.WriteAllText's default (UTF-8, no BOM), so
        // this change cannot alter a single byte of what lands on disk.
        File.WriteAllText(tmp, content);
        try
        {
            // File.Replace preserves the destination's ACLs and is the
            // strongest primitive Windows offers, but it REQUIRES the
            // destination to exist — hence the fallback for first creation.
            // Retry on access errors since the destination might be open for
            // reading, and the retries allow readers to release the handle.
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    if (File.Exists(fullPath))
                        File.Replace(tmp, fullPath, destinationBackupFileName: null);
                    else
                        File.Move(tmp, fullPath);
                    return;
                }
                catch (IOException) when (attempt < 49) { }
                catch (UnauthorizedAccessException) when (attempt < 49) { }
                System.Threading.Thread.Sleep(10);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private static void SaveMain(Config cfg, string path)
    {
        var node = JsonSerializer.SerializeToNode(cfg, Opts)!.AsObject();
        node.Remove("routes");
        node.Remove("watch_folders");
        node.Remove("alert_texts");
        node.Remove("label_clients");
        WriteAtomic(path, node.ToJsonString(Opts) + "\n");
    }

    private static void WriteDoc<T>(string configPath, string sectionPath, T doc) =>
        WriteJson(ResolveBeside(configPath, sectionPath), doc);

    internal static void WriteJson<T>(string fullPath, T doc) =>
        WriteAtomic(fullPath, JsonSerializer.Serialize(doc, Opts) + "\n");

    /// <summary>Save that reports failure instead of crashing — each file is
    /// attempted independently and every failure is named.</summary>
    public static bool TrySave(Config cfg, string path, out string error)
    {
        var errors = new List<string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        Attempt(() => { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); }, path);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return false;
        }

        Attempt(() => SaveMain(cfg, path), path);
        Attempt(() => WriteDoc(path, cfg.DestinationsFile,
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras }),
            ResolveBeside(path, cfg.DestinationsFile));
        Attempt(() => WriteDoc(path, cfg.MonitoredFoldersFile,
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras }),
            ResolveBeside(path, cfg.MonitoredFoldersFile));
        Attempt(() => WriteDoc(path, cfg.AlertsFile,
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras }),
            ResolveBeside(path, cfg.AlertsFile));
        Attempt(() =>
        {
            var labels = ResolveBeside(path, cfg.BoxLabelsFile);
            if (!File.Exists(labels))
                WriteJson(labels, new BoxLabelsDoc { LabelClients = cfg.LabelClients, Extras = cfg.BoxLabelsFileExtras });
        }, ResolveBeside(path, cfg.BoxLabelsFile));

        error = string.Join("; ", errors);
        return errors.Count == 0;

        void Attempt(Action write, string file)
        {
            try { write(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or DirectoryNotFoundException)
            {
                errors.Add($"Couldn't save settings to {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Readable error for one unusable destination, or "" if good.</summary>
    public static string ValidateRoute(Route route)
    {
        var raw = route.Path?.Trim() ?? "";
        if (raw.Length == 0) return "no destination path configured";
        if (!Directory.Exists(raw))
            return File.Exists(raw)
                ? $"destination is not a folder: {raw}"
                : $"destination does not exist: {raw}";
        return ProbeWritable(raw);
    }

    /// <summary>Empty string if we can create files in dest, else a readable
    /// error. Actually creates and removes a probe file — os.access lies on
    /// Windows and over SMB.</summary>
    public static string ProbeWritable(string dest)
    {
        var probe = System.IO.Path.Combine(dest, $".ordosort_probe_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"destination not writable: {ex.Message}";
        }
    }
}

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}
