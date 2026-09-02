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

/// <summary>A saved Unlock-tool password. The password value is plaintext —
/// the app's own storage form since saved passwords became portable across
/// stations sharing one config.json (2026-08-08 portable-saved-passwords
/// plan; the share's own permissions are the security boundary, not
/// encryption, by the owner's deliberate choice). A value can also still be
/// DPAPI-protected ("dpapi:&lt;base64&gt;") — a leftover from before that
/// change, or hand-rolled — which only decrypts on the machine and account
/// that produced it; OrdoSort.Wpf.ViewModels.UnlockViewModel converts one of
/// those to plaintext automatically the next time the saved-password list
/// is touched (see PasswordVault's own doc comment for the full
/// story).</summary>
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

    /// <summary>Named theme-scheme keys "theme" also accepts, alongside the
    /// legacy auto/light/dark trio. Mirrors OrdoSort.Wpf.Theme.ThemePalette.
    /// Schemes' keys exactly — Core cannot reference Wpf, so there is no
    /// compile-time link between the two lists. OrdoSort.Wpf.Tests.ThemeTests.
    /// ConfigSchemeWhitelistAndRegistryStayInLockstep is the drift guard that
    /// keeps them in sync at test time; extend both lists together whenever a
    /// new scheme ships (ledger, microfilm, manila, carbon, blueprint are
    /// planned next).</summary>
    public static readonly IReadOnlyList<string> SchemeKeys = new[] { "paper", "graphite", "ledger", "microfilm", "manila", "carbon", "blueprint" };

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

    // Merge PDFs (a different tool from Match & merge above, despite the
    // shared "merge" name): which of OrdoSort.Core.MergeTypes.AllGroups are
    // switched on to merge alongside PDFs and zips, toggled in the window
    // itself and remembered here. Always assigned MergeTypes.Save's OWN
    // output and read back only through MergeTypes.Load — never a literal
    // "" or a hand-built CSV — so "" means "never configured" (every group
    // on) while an explicit all-off choice is MergeTypes.NoneStored instead,
    // surviving a reload as all-off rather than reading as never-configured
    // and coming back all-on (see MergeTypes.Save's own doc comment for why
    // that distinction has to survive the round trip).
    [JsonPropertyName("merge_types")] public string MergeTypes { get; set; } = "";

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

    /// <summary>Non-null after <see cref="Load(string,bool)"/> when two of the
    /// four side-file keys resolve to the same file (QC-08). Load does NOT
    /// throw for this — a config problem that blocks startup leaves the user
    /// no in-app recovery (2026-08-07 audit D2), and adding a second one here
    /// would regress against that while fixing this. The next <see cref="Save"/>
    /// or <see cref="TrySave"/> DOES refuse, which is what actually prevents
    /// the data loss; this string only makes the problem visible as data
    /// instead of losing it silently, the same "never throws, problems come
    /// back as data" contract <c>Scanner.DeferredSummary</c> documents. As of
    /// the app-qc-2026-08-21 fix pass, "visible as data" is no longer a
    /// dead end: OrdoSort.Wpf.ViewModels.ShellViewModel.RefreshNotices reads
    /// this field into the same non-blocking notification rail every other
    /// startup-time problem already uses — a config that already carries a
    /// collision (a hand edit, or a save made before this check existed) no
    /// longer starts the app with no indication at all.</summary>
    [JsonIgnore] public string? SideFileCollisionWarning { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Load config, creating it with defaults on first run.</summary>
    public static Config Load(string path) => Load(path, createIfMissing: true);

    /// <summary>Load config. When <paramref name="createIfMissing"/> is
    /// false, a missing file throws <see cref="ConfigMissingException"/>
    /// instead of silently creating AND SAVING a fresh all-defaults file —
    /// for a caller that already holds a loaded Config in memory, where a
    /// missing file at this exact moment is a transient share hiccup or a
    /// peer's own atomic write caught mid-rename, never a genuine first run
    /// (2026-08 audit follow-up, "Gap B": see
    /// ShellViewModel.SaveSavedPasswordsNow's doc comment for the full
    /// story — the default-<c>true</c> overload's own save here is exactly
    /// what let a station opening Unlock while the shared config.json was
    /// transiently missing wipe every peer's Theme/TileVisibility/etc. with
    /// factory defaults). Final review (2026-08-06): this also closes the
    /// gap between a caller's own <c>File.Exists</c> pre-check and its call
    /// to <c>Load</c> — with this overload the check IS the load, so there
    /// is no separate window for the file to vanish in between; a caller no
    /// longer needs (or should keep) its own <c>File.Exists</c>
    /// guard.</summary>
    public static Config Load(string path, bool createIfMissing)
    {
        if (!File.Exists(path))
        {
            if (!createIfMissing)
                throw new ConfigMissingException($"Config file {path} does not exist.");
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
            throw new ConfigException(JsonProblem(path, ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(ReadProblem(path), ex);
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
        if (cfg.Theme is not ("auto" or "light" or "dark") && !SchemeKeys.Contains(cfg.Theme))
            throw new ConfigException(
                $"theme must be one of auto/light/dark or a scheme key " +
                $"({string.Join('/', SchemeKeys)}), got \"{cfg.Theme}\"");
        if (cfg.PollSeconds is < MinPollSeconds or > MaxPollSeconds)
            throw new ConfigException(
                $"poll_seconds must be {MinPollSeconds}-{MaxPollSeconds}, " +
                $"got {cfg.PollSeconds}");
        // ---- split sections: a side file wins; inline (legacy) is the fallback
        if (ReadDoc<DestinationsDoc>(path, cfg.DestinationsFile, "destinations_file") is { } dd)
        {
            cfg.Routes = Clean(dd.Routes);
            cfg.DestinationsFileExtras = dd.Extras ?? new();
        }
        if (ReadDoc<MonitoredFoldersDoc>(path, cfg.MonitoredFoldersFile, "monitored_folders_file") is { } md)
        {
            cfg.WatchFolders = Clean(md.WatchFolders);
            cfg.MonitoredFoldersFileExtras = md.Extras ?? new();
        }
        if (ReadDoc<AlertsDoc>(path, cfg.AlertsFile, "alerts_file") is { } ad)
        {
            cfg.AlertTexts = Clean(ad.AlertTexts);
            cfg.AlertsFileExtras = ad.Extras ?? new();
        }
        if (ReadDoc<BoxLabelsDoc>(path, cfg.BoxLabelsFile, "box_labels_file") is { } bd)
        {
            cfg.LabelClients = Clean(bd.LabelClients);
            cfg.BoxLabelsFileExtras = bd.Extras ?? new();
        }
        cfg.NormalizeSectionItems();
        // Surfaced, never thrown — see SideFileCollisionWarning's own doc
        // comment for why a collision here must not join naming_mode/sort/
        // etc. above in blocking startup. TryFindSideFileCollision, not the
        // throwing check Save/TrySave use, is the point of that split.
        if (TryFindSideFileCollision(path, cfg.DestinationsFile, cfg.MonitoredFoldersFile,
                cfg.AlertsFile, cfg.BoxLabelsFile, out var collKeyA, out var collKeyB, out var collPath))
            cfg.SideFileCollisionWarning =
                $"{collKeyA} and {collKeyB} both point at {collPath}, so only one of them " +
                "actually holds what was last saved there. OrdoSort started anyway, but fix " +
                "this in Settings — Save will refuse until the two point at different files.";
        return cfg;
    }

    /// <summary>Resolve a section-file path: absolute stays; relative lands
    /// beside config.json (the names_file / history_db rule). Unconfined —
    /// see <see cref="ResolveBesideForRead"/> / <see cref="ResolveBesideForWrite"/>
    /// for the confinement-checked callers that guard the four side-file
    /// keys. Kept public because it still answers a narrower question
    /// ("where would this spelling point?") used by the Settings UI to
    /// detect whether a re-typed path is the same physical file.</summary>
    public static string ResolveBeside(string configPath, string sectionPath) =>
        Path.IsPathRooted(sectionPath)
            ? sectionPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, sectionPath);

    /// <summary>Resolve <paramref name="sectionPath"/> and confirm the
    /// result is inside the config's own directory, throwing a
    /// ConfigException naming <paramref name="keyName"/> and the offending
    /// value otherwise. This is the confinement check both
    /// <see cref="ResolveBesideForWrite"/> and <see cref="ResolveBesideForRead"/>
    /// share; the two differ only in whether a fully-qualified absolute
    /// path is allowed to bypass it (see their doc comments).
    ///
    /// Containment is checked on the canonical, fully-resolved path
    /// (Path.GetFullPath on both the candidate and the config directory) —
    /// never a string prefix on the raw input — so a `..` traversal and a
    /// Windows rooted-without-drive path like `\evil.json` (Path.IsPathRooted
    /// reports that true, and Path.GetFullPath resolves it against the
    /// current drive) both normalize before comparison. The config
    /// directory side of the comparison gets a trailing separator before
    /// the StartsWith test, so a same-prefixed SIBLING directory can't slip
    /// through: `C:\configdir-evil\x.json` starts with the string
    /// `C:\configdir` but does not start with `C:\configdir\`, so it is
    /// correctly rejected as outside.</summary>
    private static string ResolveConfined(string configPath, string sectionPath, string keyName)
    {
        var configDir = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        var full = Path.GetFullPath(ResolveBeside(configPath, sectionPath));
        var configDirWithSep = configDir.EndsWith(Path.DirectorySeparatorChar)
            ? configDir
            : configDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(configDirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException(
                $"{keyName} must stay beside the config file, but \"{sectionPath}\" resolves to " +
                $"{full}, which is outside {configDir}. Use a plain filename, or a path nested " +
                "in a subfolder of the config's own directory.");
        }
        return full;
    }

    /// <summary>Resolve a side-file path for WRITING, refusing anything
    /// that resolves outside the config's own directory — an absolute
    /// path, a `..` traversal, or a rooted-without-drive path alike. This
    /// is the fix for the share-write to local-arbitrary-write escalation
    /// (2026-08 audit finding 4.2[A]): on the shared-config deployment this
    /// app supports, anyone who can edit config.json on the share could
    /// otherwise point one of the four side-file keys at any file on every
    /// other station's disk and have it overwritten at that station's next
    /// Save. There is no backward-compatibility carve-out on write — even
    /// though the Settings "Data files" Browse... buttons themselves now
    /// refuse a choice this method would refuse, and store an accepted one
    /// relative to the config directory rather than handing back an
    /// absolute path (SettingsViewModel.PickSideFile, which asks THIS
    /// method to decide, so that guard can never drift from the rule
    /// enforced here — see its own doc comment). That guard only covers a
    /// value picked through Browse... from here on: Microsoft.Win32.
    /// OpenFileDialog itself has no containment check of its own, and a
    /// hand-edited config.json, or one carrying a value saved by a build
    /// from before that guard existed, can still hold an absolute
    /// destination — which is exactly the capability being withdrawn here,
    /// regardless of how it arrived. See <see cref="ResolveBesideForRead"/>
    /// for the read-side half of the split that keeps an already-configured
    /// absolute path loadable.</summary>
    public static string ResolveBesideForWrite(string configPath, string sectionPath, string keyName) =>
        ResolveConfined(configPath, sectionPath, keyName);

    /// <summary>Resolve a side-file path for READING. A fully-qualified
    /// absolute path (one with a drive or UNC root — Path.IsPathFullyQualified,
    /// which is what Microsoft.Win32.OpenFileDialog's Browse... buttons
    /// always hand back for these four keys) is preserved as-is, even
    /// outside the config directory: that is a shipped, UI-reachable
    /// capability, and refusing to read a station's already-working
    /// absolute-pathed side file would silently relocate its data out from
    /// under it. Anything else that resolves outside the config directory —
    /// a `..` traversal, or a Windows rooted-without-drive path like
    /// `\evil.json` — was never something the UI could produce and is
    /// refused exactly like a write, because a malicious shared config.json
    /// could otherwise point a victim station's read at an arbitrary local
    /// file just as easily as a write. See <see cref="ResolveBesideForWrite"/>
    /// for the write-side half of the split.</summary>
    public static string ResolveBesideForRead(string configPath, string sectionPath, string keyName) =>
        Path.IsPathFullyQualified(sectionPath)
            ? ResolveBeside(configPath, sectionPath)
            : ResolveConfined(configPath, sectionPath, keyName);

    public static T? ReadDoc<T>(string configPath, string sectionPath, string keyName = "section file") where T : class
    {
        var full = ResolveBesideForRead(configPath, sectionPath, keyName);
        if (!File.Exists(full)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(full), Opts)
                   ?? throw new ConfigException($"Config file {full} is empty");
        }
        catch (JsonException ex)
        {
            throw new ConfigException(JsonProblem(full, ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(ReadProblem(full), ex);
        }
    }

    /// <summary>Which two of the four side-file keys — if any — resolve to
    /// the same file. Resolved through <see cref="ResolveBesideForWrite"/>,
    /// the exact confinement-checked path <see cref="Save"/> and
    /// <see cref="TrySave"/> actually write through, and compared through
    /// <see cref="PathIdentity"/> rather than a raw string compare
    /// (CONTEXT.md: "path identity is decided in exactly one place") — so
    /// "./destinations.json" and "destinations.json" collide exactly like an
    /// identical spelling. A key whose OWN path escapes confinement is left
    /// out of the comparison: that is a separate refusal, raised
    /// independently wherever the key is actually resolved for real, and
    /// folding it in here would report the wrong pair instead of the real
    /// one. Public (not private) so Settings' HardErrors can run this exact
    /// check against the form's live, not-yet-saved values before OK is
    /// accepted, instead of only finding out from a refused Save.
    ///
    /// QC-08 (2026-08-21 audit): pointing monitored_folders_file at
    /// destinations.json let one Save silently erase every filing
    /// destination, because WriteDoc is a full re-serialization of one doc
    /// type, never a read-modify-write.</summary>
    public static bool TryFindSideFileCollision(string configPath,
        string destinationsFile, string monitoredFoldersFile, string alertsFile, string boxLabelsFile,
        out string keyA, out string keyB, out string collisionPath)
    {
        var keys = new (string Key, string Value)[]
        {
            ("destinations_file", destinationsFile),
            ("monitored_folders_file", monitoredFoldersFile),
            ("alerts_file", alertsFile),
            ("box_labels_file", boxLabelsFile),
        };
        var resolved = new List<(string Key, string Full)>();
        foreach (var (key, value) in keys)
        {
            try { resolved.Add((key, ResolveBesideForWrite(configPath, value, key))); }
            catch (ConfigException) { /* that key's own confinement refusal fires separately */ }
        }
        for (var i = 0; i < resolved.Count; i++)
            for (var j = i + 1; j < resolved.Count; j++)
                if (PathIdentity.Same(resolved[i].Full, resolved[j].Full))
                {
                    keyA = resolved[i].Key;
                    keyB = resolved[j].Key;
                    collisionPath = resolved[i].Full;
                    return true;
                }
        keyA = keyB = collisionPath = "";
        return false;
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
        MergeTypes ??= "";
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

    /// <summary>Refuses a Save/TrySave the same way ResolveConfined already
    /// refuses an escaping path — see <see cref="TryFindSideFileCollision"/>
    /// for what counts as a collision. Must run before ANY of the four
    /// side-file writes: each one is a full re-serialization of one doc
    /// type, so writing even one of a colliding pair before this check ran
    /// would already have destroyed the other's content (QC-08).</summary>
    private static void CheckSideFileUniqueness(Config cfg, string path)
    {
        if (TryFindSideFileCollision(path, cfg.DestinationsFile, cfg.MonitoredFoldersFile,
                cfg.AlertsFile, cfg.BoxLabelsFile, out var keyA, out var keyB, out var collisionPath))
            throw new ConfigException(
                $"{keyA} and {keyB} both resolve to {collisionPath}. Two side-file keys can't " +
                "name the same file — each Save fully rewrites its own file, so the second " +
                "write would silently erase what the first just saved. Point them at different files.");
    }

    /// <summary>Write the main config (without the split sections) and the
    /// Settings-owned side files. box-labels.json is bootstrap-only: created
    /// when missing, never overwritten — its counters belong to the Box
    /// labels tool's exclusive writer (BoxLabelStore). The bootstrap write
    /// goes through <see cref="WriteAtomicNew"/>, not <see cref="WriteAtomic"/>:
    /// it must never fall back to File.Replace, because File.Replace is what
    /// let this bootstrap wait out a peer's BoxLabelStore.Mutate lock and then
    /// clobber the counters that peer had just written (2026-08 audit
    /// finding 1). See WriteAtomicNew's doc comment for the full story.</summary>
    public static void Save(Config cfg, string path)
    {
        CheckSideFileUniqueness(cfg, path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        SaveMain(cfg, path);
        WriteDoc(path, cfg.DestinationsFile, "destinations_file",
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras });
        WriteDoc(path, cfg.MonitoredFoldersFile, "monitored_folders_file",
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras });
        WriteDoc(path, cfg.AlertsFile, "alerts_file",
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras });
        // The Exists probe MUST run on the same confined path as the write,
        // not the unconfined ResolveBeside: probing an unconfined path
        // first (and only confining the write once the probe says
        // "missing") is an oracle — an attacker-controlled box_labels_file
        // pointed at an arbitrary absolute path would silently no-op when
        // that path exists and throw when it doesn't, letting a hostile
        // shared config.json learn whether a given file exists anywhere on
        // the victim's disk. Resolving once, confined, closes that: an
        // escaping box_labels_file is refused unconditionally, the same as
        // the other three keys, before any filesystem probe happens at all.
        var labels = ResolveBesideForWrite(path, cfg.BoxLabelsFile, "box_labels_file");
        if (!File.Exists(labels))
            WriteJsonNew(labels,
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
    /// within one volume, and the config can live on a share. Retries — now
    /// covering the write as well as the replace, see AtomicPlace.Attempts
    /// for the budget and why it's sized the way it is — if the destination
    /// is held open for reading (Config.Load uses File.ReadAllText with no
    /// FileShare.Delete) or a transient failure interrupts the write itself,
    /// giving either a reader or a dropped connection time to let go.
    ///
    /// This is for files where a newer replacement is always correct — the
    /// main config and the destinations/monitored-folders/alerts side files,
    /// all owned exclusively by whichever station last hit Save. It must
    /// NOT be used for box-labels.json's bootstrap write: see
    /// <see cref="WriteAtomicNew"/>.</summary>
    internal static void WriteAtomic(string fullPath, string content)
    {
        // The temp-sibling naming, the retry loop and the cleanup all live in
        // AtomicPlace now — see that module (and CONTEXT.md's "atomic
        // placement") for the two rules this used to re-state. The only thing
        // left here is what to write and how failure reaches the caller.
        //
        // File.WriteAllText's default encoding (UTF-8, no BOM) is unchanged,
        // so this cannot alter a single byte of what lands on disk.
        if (AtomicPlace.TryReplace(fullPath, tmp => File.WriteAllText(tmp, content), out var error))
            return;

        // AtomicPlace reports; this method has always thrown, and TrySave's
        // Attempt catches by TYPE. IOException is inside that catch's set, so
        // a failure still lands in the same arm with the same message. It
        // does narrow the type — an UnauthorizedAccessException now surfaces
        // as IOException — which is invisible here because Attempt treats
        // IOException, UnauthorizedAccessException, SecurityException and
        // DirectoryNotFoundException identically, and ConfigException (the
        // one it DOES treat differently) is raised by path confinement before
        // any write, never by this method.
        throw new IOException(error);
    }

    /// <summary>Create-only atomic write, for files whose ownership belongs to
    /// someone else once they exist — today, only box-labels.json's bootstrap.
    /// Unlike <see cref="WriteAtomic"/>, this NEVER falls back to File.Replace:
    /// it only ever File.Moves the tmp sibling into place, and if the
    /// destination has appeared by the time that move runs, that is a SUCCESS,
    /// not a retry-and-overwrite. The peer that created it — another
    /// station's BoxLabelStore.Mutate, or another station's own bootstrap —
    /// holds newer truth than our snapshot.
    ///
    /// Why this matters: Config.Save's box-labels.json bootstrap guards with
    /// `if (!File.Exists(labels))` before calling this, but that guard and
    /// this write are not atomic together. Station B's Mutate can create the
    /// file, advance a counter, and release its exclusive lock entirely
    /// inside that gap. The old WriteAtomic re-checked File.Exists *inside*
    /// its own retry loop and switched to File.Replace the instant the
    /// destination appeared — so Station A would wait out B's lock via the
    /// retry loop and then silently replace B's freshly written counters
    /// with A's stale in-memory snapshot: a box number already printed on a
    /// physical box gets reissued (2026-08 audit finding 1). File.Move has no
    /// such fallback: it fails outright when the destination exists, and
    /// that failure is exactly the signal this method treats as done.</summary>
    internal static void WriteAtomicNew(string fullPath, string content)
    {
        // Create-only placement, including the peer-wins-is-success rule this
        // method's doc comment above describes, now lives in AtomicPlace —
        // see that module and CONTEXT.md's "atomic placement".
        if (AtomicPlace.TryCreateNew(fullPath, tmp => File.WriteAllText(tmp, content), out var error))
            return;

        // Same reasoning as WriteAtomic: this has always thrown, and
        // TrySave's Attempt catches by type. Note a peer winning the race is
        // NOT a failure and never reaches here.
        throw new IOException(error);
    }

    // BeforeCreateOnlyMove lived here. Its job — let a test plant a peer's
    // file in the gap between a caller's File.Exists guard and this write
    // landing — is now AtomicPlace.BeforeAttempt's, fired on attempt 0. It
    // still carries the destination path for the reason this seam always
    // did: the hook is process-wide and xUnit runs other classes' saves
    // concurrently, so a test must be able to ignore writes it doesn't own.

    // OnRetryForTests lived here. The retry loop it hooked moved to
    // AtomicPlace, and so did the seam: AtomicPlace.BeforeAttempt does the
    // same job for all three placement call sites instead of one, and carries
    // the destination path so a test can ignore placements that aren't its
    // own. The reasoning it documented is preserved there — releasing a held
    // reader from INSIDE the loop's own callback rather than racing it with a
    // second, independently-clocked timer, which is what used to flake on a
    // shared CI runner.

    private static void SaveMain(Config cfg, string path)
    {
        var node = JsonSerializer.SerializeToNode(cfg, Opts)!.AsObject();
        node.Remove("routes");
        node.Remove("watch_folders");
        node.Remove("alert_texts");
        node.Remove("label_clients");
        WriteAtomic(path, node.ToJsonString(Opts) + "\n");
    }

    private static void WriteDoc<T>(string configPath, string sectionPath, string keyName, T doc) =>
        WriteJson(ResolveBesideForWrite(configPath, sectionPath, keyName), doc);

    internal static void WriteJson<T>(string fullPath, T doc) =>
        WriteAtomic(fullPath, JsonSerializer.Serialize(doc, Opts) + "\n");

    /// <summary>Serializes and writes via <see cref="WriteAtomicNew"/> — see
    /// that method for why box-labels.json's bootstrap needs create-only
    /// semantics instead of <see cref="WriteJson"/>.</summary>
    internal static void WriteJsonNew<T>(string fullPath, T doc) =>
        WriteAtomicNew(fullPath, JsonSerializer.Serialize(doc, Opts) + "\n");

    /// <summary>Save that reports failure instead of crashing — each file is
    /// attempted independently and every failure is named.</summary>
    public static bool TrySave(Config cfg, string path, out string error) =>
        TrySave(cfg, path, out error, out _);

    /// <summary>Save that reports failure instead of crashing — each file is
    /// attempted independently and every failure is named in
    /// <paramref name="error"/>, exactly like the 3-arg overload (kept for
    /// source compatibility — every existing caller and test that only asks
    /// for <c>error</c> sees byte-identical text and the same return value).
    ///
    /// <paramref name="refusedSideFileKeys"/> is the extra signal a caller
    /// needs to answer one question the 3-arg overload cannot: was THIS
    /// failure entirely a side-file confinement refusal (a key whose
    /// configured path resolves outside the config's own directory — see
    /// <see cref="ResolveBesideForWrite"/>), as opposed to a real I/O
    /// problem? That distinction matters because a confinement refusal is a
    /// structural property of the CONFIGURED PATH, not of this particular
    /// save attempt: it recurs, byte-for-byte identical, on every future
    /// call until someone edits the path — unlike a locked file or a full
    /// disk, retrying accomplishes nothing. A caller that shows a fresh
    /// "not saved" dialog for it on every unrelated save (ShellViewModel.
    /// SaveConfigNow's tile-visibility toggle or merge-header save,
    /// ApplySettingsAsync's Settings OK) turns ONE bad side-file path into
    /// an every-save failure notice with no actionable next step short of
    /// hand-editing config.json — arguably worse than not warning at all
    /// (2026-08-07 audit, Task 1b).
    ///
    /// Populated ONLY when the confinement refusals fully account for every
    /// failure this call produced (i.e. nothing else went wrong) — empty in
    /// every other case, including "some keys were refused AND something
    /// else also failed": a mixed failure must never let a caller's
    /// once-per-session suppression swallow a brand-new, unrelated problem
    /// just because a stale confinement refusal happened to ride along in
    /// the same call. A caller that ignores this out param (the 3-arg
    /// overload's callers) is unaffected either way.</summary>
    public static bool TrySave(Config cfg, string path, out string error,
        out IReadOnlyList<string> refusedSideFileKeys)
    {
        var errors = new List<string>();
        var refused = new List<string>();

        // Checked before any I/O at all — same reasoning as CheckSideFileUniqueness's
        // own doc comment. Not added to `refused`: that list means "a confinement
        // escape", a different, narrower refusal ShellViewModel treats as
        // structural-and-suppressible; a collision gets its own message every time.
        Attempt(() => CheckSideFileUniqueness(cfg, path), path);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            refusedSideFileKeys = Array.Empty<string>();
            return false;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        Attempt(() => { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); }, path);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            refusedSideFileKeys = Array.Empty<string>();
            return false;
        }

        Attempt(() => SaveMain(cfg, path), path);
        Attempt(() => WriteDoc(path, cfg.DestinationsFile, "destinations_file",
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras }),
            ResolveBeside(path, cfg.DestinationsFile), "destinations_file");
        Attempt(() => WriteDoc(path, cfg.MonitoredFoldersFile, "monitored_folders_file",
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras }),
            ResolveBeside(path, cfg.MonitoredFoldersFile), "monitored_folders_file");
        Attempt(() => WriteDoc(path, cfg.AlertsFile, "alerts_file",
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras }),
            ResolveBeside(path, cfg.AlertsFile), "alerts_file");
        Attempt(() =>
        {
            // As in Save: the Exists probe runs on the SAME confined path
            // as the write — see Save's comment for why an unconfined probe
            // followed by a confined write is an existence oracle.
            var labels = ResolveBesideForWrite(path, cfg.BoxLabelsFile, "box_labels_file");
            if (!File.Exists(labels))
                WriteJsonNew(labels,
                    new BoxLabelsDoc { LabelClients = cfg.LabelClients, Extras = cfg.BoxLabelsFileExtras });
        }, ResolveBeside(path, cfg.BoxLabelsFile), "box_labels_file");

        error = string.Join("; ", errors);
        refusedSideFileKeys = errors.Count == refused.Count && refused.Count > 0
            ? refused
            : Array.Empty<string>();
        return errors.Count == 0;

        void Attempt(Action write, string file, string? keyName = null)
        {
            try { write(); }
            catch (ConfigException ex)
            {
                errors.Add($"Couldn't save settings to {file}: {ex.Message}");
                if (keyName is not null) refused.Add(keyName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or DirectoryNotFoundException)
            {
                errors.Add($"Couldn't save settings to {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Save ONLY the main config.json section — never the three
    /// side files (destinations/monitored-folders/alerts) or the box-labels
    /// bootstrap <see cref="TrySave"/> also writes. For a caller that is only
    /// ever entitled to change a main-section field (today, ShellViewModel.
    /// SaveSavedPasswordsNow's SavedPasswords overlay), routing through the
    /// full <see cref="TrySave"/> was its own bug (final review, Important
    /// 3, 2026-08-06): a station with a legitimately-Browsed ABSOLUTE side-
    /// file path (a shipped Settings capability — see
    /// <see cref="ResolveBesideForWrite"/>'s doc comment) has that path
    /// refused on every write, so TrySave's side-file Attempt for it always
    /// fails and the overall call returns false — even though SaveMain,
    /// which TrySave runs first, already landed the password change on
    /// disk. The caller would then report "not saved" about a write that
    /// partly succeeded, and skip any success notice gated on the return
    /// value. Writing only the main file removes that whole failure surface
    /// for a caller with nothing to say about the side files: it cannot fail
    /// on a side-file path it never touches, cannot lose to a peer's
    /// in-flight side-file write, and cannot re-serialize a hand-edited side
    /// file byte-for-byte-unchanged (which would otherwise trip a peer's
    /// hash-based Settings-conflict prompt over a file this call never
    /// meant to touch at all).</summary>
    public static bool TrySaveMain(Config cfg, string path, out string error)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SaveMain(cfg, path);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or DirectoryNotFoundException or ConfigException)
        {
            error = $"Couldn't save settings to {path}: {ex.Message}";
            return false;
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

    /// <summary>What to say when a config file will not parse. Keeps the
    /// phrase "is not valid JSON" — several suites assert on it, and it is
    /// also the plainest description of the fault — but replaces the parser's
    /// own tail with the one part of it a person can use: the line. See
    /// ConfigException's second constructor for where the raw text goes.</summary>
    private static string JsonProblem(string path, JsonException ex)
    {
        // Two things off the exception are worth a user's time — WHICH setting
        // and WHICH line — and neither is the BytePositionInLine that made the
        // old message unreadable. Path arrives as "$.poll_seconds"; the "$."
        // is JSON-pointer syntax, not something to show anyone.
        var key = ex.Path?.TrimStart('$', '.');
        var line = ex.LineNumber is { } n ? n + 1 : (long?)null;   // counts from 0

        var where = (key, line) switch
        {
            ({ Length: > 0 }, { } l) => $" The problem is at \"{key}\", on line {l}.",
            ({ Length: > 0 }, null) => $" The problem is at \"{key}\".",
            (_, { } l) => $" The problem is on line {l}.",
            _ => "",
        };
        return $"Config file {path} is not valid JSON.{where} " +
               "Open it in a text editor and check that setting — a missing comma or quote, " +
               "or a value of the wrong kind — or delete the file and OrdoSort will write a " +
               "fresh one with default settings.";
    }

    /// <summary>What to say when a config file cannot be opened at all. The
    /// OS message ("The process cannot access the file because it is being
    /// used by another process") names a mechanism, not an action.</summary>
    private static string ReadProblem(string path) =>
        $"Config file {path} could not be read. It may be open in another program, " +
        "or on a drive or share this computer cannot reach right now.";
}

public class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }

    /// <summary>Carries the underlying failure without putting it in front of
    /// the user. <see cref="Exception.Message"/> on this type is a sentence a
    /// person can act on; the parser's or the OS's own wording — which is
    /// where <c>Path: $.inbox | LineNumber: 1 | BytePositionInLine: 0</c> came
    /// from — belongs on the inner exception, so callers can log it in full
    /// while showing the readable half (2026-08-22 UI audit, UI-27).</summary>
    public ConfigException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Thrown by <see cref="Config.Load(string,bool)"/> with
/// <c>createIfMissing: false</c> when the file doesn't exist — a
/// ConfigException, so any existing <c>catch (ConfigException)</c> still
/// catches it, but callers that need to tell "missing" apart from "exists
/// but is unreadable/corrupt" (see ShellViewModel.SaveSavedPasswordsNow, which
/// warns and writes nothing for the former but falls back to a whole-config
/// save for the latter) can catch this more specific type first.</summary>
public sealed class ConfigMissingException : ConfigException
{
    public ConfigMissingException(string message) : base(message) { }
}
