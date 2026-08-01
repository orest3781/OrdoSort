using System.Collections.ObjectModel;
using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>An editable route row (master-detail in the Routes section).</summary>
public sealed class RouteEditVm : ObservableObject
{
    private string _label = "", _path = "", _hotkey = "", _suffix = "", _color = "";
    private bool _appendSuffix;
    private string _namingMode = "";   // "" = session default
    private string _namingTemplate = "";   // "" = inherit the global template

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }
    public string Suffix { get => _suffix; set => Set(ref _suffix, value); }
    public bool AppendSuffix { get => _appendSuffix; set => Set(ref _appendSuffix, value); }
    public string NamingMode { get => _namingMode; set => Set(ref _namingMode, value); }
    public string NamingTemplate { get => _namingTemplate; set => Set(ref _namingTemplate, value); }

    public string Path
    {
        get => _path;
        set { if (Set(ref _path, value)) Raise(nameof(Problem)); }
    }

    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) Raise(nameof(ColorValid)); }
    }

    /// <summary>Live destination check, shown in the detail form.</summary>
    public string Problem => Config.ValidateRoute(new Route { Path = Path });
    public bool ColorValid => Color.Length == 0 || ThemePalette.ParseColor(Color) is not null;

    internal void RefreshProblem() => Raise(nameof(Problem));

    // Derived by SettingsViewModel.RecomputeRouteDerived (it knows the route's
    // position, which decides the Ctrl+1-9 fallback and duplicate detection).
    private string _previewLabel = "";
    public string PreviewLabel { get => _previewLabel; private set => Set(ref _previewLabel, value); }

    /// <summary>The effective hotkey, shown right-aligned in the route list.</summary>
    private string _gestureText = "";
    public string GestureText { get => _gestureText; private set => Set(ref _gestureText, value); }

    private Rgb _previewBack;
    public Rgb PreviewBack { get => _previewBack; private set => Set(ref _previewBack, value); }

    private Rgb _previewFore;
    public Rgb PreviewFore { get => _previewFore; private set => Set(ref _previewFore, value); }

    private string _hotkeyNote = "";
    public string HotkeyNote
    {
        get => _hotkeyNote;
        private set { if (Set(ref _hotkeyNote, value)) Raise(nameof(HasHotkeyNote)); }
    }
    public bool HasHotkeyNote => HotkeyNote.Length > 0;

    /// <summary>Ghost text inside an EMPTY hotkey box: the automatic key this
    /// route gets from its list position ("Ctrl+1 · automatic") — so the list
    /// showing a key while the box sits blank isn't a mystery.</summary>
    private string _hotkeyPlaceholder = "";
    public string HotkeyPlaceholder { get => _hotkeyPlaceholder; private set => Set(ref _hotkeyPlaceholder, value); }

    internal void SetDerived(string previewLabel, Rgb back, Rgb fore,
        string hotkeyNote, string gestureText)
    {
        PreviewLabel = previewLabel;
        PreviewBack = back;
        PreviewFore = fore;
        HotkeyNote = hotkeyNote;
        GestureText = gestureText;
        HotkeyPlaceholder = Hotkey.Trim().Length == 0 && gestureText.Length > 0
            ? $"{gestureText} · automatic"
            : "";
    }

    /// <summary>Unknown per-route keys from the original config, carried
    /// through so a hand-edited key survives Settings-OK.</summary>
    public Dictionary<string, JsonElement> Extras { get; init; } = new();

    public static RouteEditVm From(Route r) => new()
    {
        Label = r.Label,
        Path = r.Path,
        Hotkey = r.Hotkey,
        Suffix = r.Suffix,
        AppendSuffix = r.AppendSuffix,
        NamingMode = r.NamingMode ?? "",
        NamingTemplate = r.NamingTemplate ?? "",
        Color = r.Color ?? "",
        Extras = new Dictionary<string, JsonElement>(r.Extras),
    };

    public Route ToRoute() => new()
    {
        Label = Label.Trim(),
        Path = Path.Trim(),
        Hotkey = Hotkey.Trim(),
        Suffix = Suffix,
        AppendSuffix = AppendSuffix,
        NamingMode = NamingMode.Length == 0 ? null : NamingMode,
        NamingTemplate = NamingTemplate.Trim().Length == 0 ? null : NamingTemplate.Trim(),
        Color = Color.Length == 0 ? null : Color.Trim(),
        Extras = new Dictionary<string, JsonElement>(Extras),
    };
}

/// <summary>An editable watch-folder row (Dashboard section).</summary>
public sealed class WatchEditVm : ObservableObject
{
    private string _label = "", _path = "", _filetypes = "", _color = "", _section = "";
    private bool _recursive;

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Path
    {
        get => _path;
        set { if (Set(ref _path, value)) Raise(nameof(Problem)); }
    }

    /// <summary>Dashboard group this folder appears under — blank uses the
    /// Section-heading field above the folder list.</summary>
    public string Section { get => _section; set => Set(ref _section, value); }

    public string Filetypes
    {
        get => _filetypes;
        set { if (Set(ref _filetypes, value)) RaiseTypeFlags(); }
    }
    public bool Recursive { get => _recursive; set => Set(ref _recursive, value); }

    // ---- file types as checkboxes -----------------------------------------
    // The config string stays the source of truth (round-trips anything a
    // hand-edited config says); these are just friendlier handles on it.
    private static readonly string[] PdfGroup = { "pdf" };
    private static readonly string[] TiffGroup = { "tif", "tiff" };
    private static readonly string[] JpegGroup = { "jpg", "jpeg" };
    private static readonly string[] PngGroup = { "png" };
    private static readonly string[] CanonicalOrder = { "pdf", "tif", "tiff", "jpg", "jpeg", "png" };

    private HashSet<string> Types => FolderMonitor.ParseFiletypes(Filetypes);

    /// <summary>Blank list = the folder counts every file.</summary>
    public bool AnyType
    {
        get => Types.Count == 0;
        set
        {
            if (value) Filetypes = "";
            else if (Types.Count == 0) Filetypes = "pdf";   // the sane default here
        }
    }

    public bool TypePdf { get => Has(PdfGroup); set => Toggle(PdfGroup, value); }
    public bool TypeTiff { get => Has(TiffGroup); set => Toggle(TiffGroup, value); }
    public bool TypeJpeg { get => Has(JpegGroup); set => Toggle(JpegGroup, value); }
    public bool TypePng { get => Has(PngGroup); set => Toggle(PngGroup, value); }

    /// <summary>Extensions outside the checkbox groups ("docx, xps").</summary>
    public string OtherTypes
    {
        get => string.Join(", ", Types.Where(t => !CanonicalOrder.Contains(t)).OrderBy(t => t));
        set
        {
            var keep = Types.Where(t => CanonicalOrder.Contains(t));
            Filetypes = JoinCanonical(keep.Concat(FolderMonitor.ParseFiletypes(value)));
        }
    }

    private bool Has(string[] group) => group.Any(Types.Contains);

    private void Toggle(string[] group, bool on)
    {
        var types = Types;
        foreach (var t in group)
        {
            if (on) types.Add(t);
            else types.Remove(t);
        }
        Filetypes = JoinCanonical(types);
    }

    private static string JoinCanonical(IEnumerable<string> types)
    {
        var set = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        var ordered = CanonicalOrder.Where(set.Contains)
            .Concat(set.Where(t => !CanonicalOrder.Contains(t.ToLowerInvariant())).OrderBy(t => t));
        return string.Join(", ", ordered);
    }

    private void RaiseTypeFlags()
    {
        Raise(nameof(AnyType));
        Raise(nameof(TypePdf));
        Raise(nameof(TypeTiff));
        Raise(nameof(TypeJpeg));
        Raise(nameof(TypePng));
        Raise(nameof(OtherTypes));
    }

    /// <summary>Live folder check (parity with the route detail panel).</summary>
    public string Problem
    {
        get
        {
            var p = Path.Trim();
            if (p.Length == 0) return "no folder chosen yet";
            return Directory.Exists(p) ? "" : $"folder doesn't exist: {p}";
        }
    }

    internal void RefreshProblem() => Raise(nameof(Problem));

    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) Raise(nameof(ColorValid)); }
    }

    public bool ColorValid => Color.Length == 0 || ThemePalette.ParseColor(Color) is not null;

    public Dictionary<string, JsonElement> Extras { get; init; } = new();

    public static WatchEditVm From(WatchFolder w) => new()
    {
        Label = w.Label,
        Path = w.Path,
        Filetypes = w.Filetypes,
        Recursive = w.Recursive,
        Color = w.Color ?? "",
        Section = w.Section,
        Extras = new Dictionary<string, JsonElement>(w.Extras),
    };

    public WatchFolder ToWatchFolder() => new()
    {
        Label = Label.Trim(),
        Path = Path.Trim(),
        Filetypes = Filetypes.Trim(),
        Recursive = Recursive,
        Color = Color.Length == 0 ? null : Color.Trim(),
        Section = Section.Trim(),
        Extras = new Dictionary<string, JsonElement>(Extras),
    };
}

/// <summary>A saved Unlock password row; the stored value is DPAPI-protected
/// on save if it isn't already.</summary>
public sealed class PasswordEditVm : ObservableObject
{
    private string _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Stored { get; set; } = "";
    public string StatusText => PasswordVault.IsProtected(Stored)
        ? "encrypted" : "plain text — will be encrypted on save";
}

/// <summary>One row of the sound picker: which sound plays for one moment.
/// The four choices (OrdoSort / Windows / Custom .wav / Silent) map to the
/// config spec; Test plays the current choice so you hear it before saving.</summary>
public sealed class SoundChoiceVm : ObservableObject
{
    private readonly SoundEvent _evt;
    private readonly IDialogService _dialogs;
    private readonly ISoundService _sounds;

    public string Name { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand BrowseCommand { get; }

    public SoundChoiceVm(string name, SoundEvent evt, string spec,
        IDialogService dialogs, ISoundService sounds)
    {
        Name = name;
        _evt = evt;
        _dialogs = dialogs;
        _sounds = sounds;
        (_choice, _customPath) = FromSpec(spec);
        TestCommand = new RelayCommand(() => _sounds.Play(_evt, Spec));
        BrowseCommand = new RelayCommand(PickFile);
    }

    // 0 OrdoSort · 1 Windows · 2 Custom · 3 Silent — the ComboBox SelectedIndex
    private int _choice;
    public int Choice
    {
        get => _choice;
        set
        {
            if (!Set(ref _choice, value)) return;
            if (value == 2 && _customPath.Length == 0) PickFile();   // "Custom" → prompt
            Raise(nameof(CustomLabel));
            Raise(nameof(ShowCustom));
        }
    }

    private string _customPath = "";
    public string CustomLabel => _customPath.Length > 0
        ? System.IO.Path.GetFileName(_customPath) : "(choose a .wav…)";
    public bool ShowCustom => _choice == 2;

    /// <summary>The config spec for the current choice.</summary>
    public string Spec => _choice switch
    {
        1 => "windows",
        2 => _customPath,
        3 => "none",
        _ => "",
    };

    private static (int, string) FromSpec(string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0 || spec.Equals("default", StringComparison.OrdinalIgnoreCase))
            return (0, "");
        if (spec.Equals("windows", StringComparison.OrdinalIgnoreCase)) return (1, "");
        if (spec.Equals("none", StringComparison.OrdinalIgnoreCase)) return (3, "");
        return (2, spec);
    }

    private void PickFile()
    {
        var path = _dialogs.AskOpenFile("Wave audio (*.wav)|*.wav");
        if (path is not null) { _customPath = path; Raise(nameof(CustomLabel)); }
    }
}

/// <summary>The Settings window's brain. Edits copies; OK validates (hard
/// errors block, warnings ask "Save anyway?") and produces a NEW Config built
/// by JSON-cloning the original — so every unedited field and every unknown
/// key survives by construction.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    // KeyValuePair: WPF binds properties, not tuple fields
    public static readonly KeyValuePair<string, string>[] FontChoices =
    {
        new("", "(system default)"),
        new("Segoe UI", "Segoe UI"),
        new("Tahoma", "Tahoma"),
        new("Verdana", "Verdana"),
        new("Consolas", "Consolas"),
        new("Cascadia Mono", "Cascadia Mono"),
    };

    public static readonly KeyValuePair<string, string>[] SortChoices =
    {
        new("size_desc", "Largest first"),
        new("size_asc", "Smallest first"),
        new("mtime_desc", "Newest first"),
        new("mtime_asc", "Oldest first"),
        new("filename_asc", "Filename A to Z"),
        new("filename_desc", "Filename Z to A"),
    };

    public static readonly KeyValuePair<string, string>[] ModeChoices =
    {
        new("", "(use the Filing setting)"),
        new("insert", "Insert at the \"--\""),
        new("replace", "Full replace"),
        new("prefix", "Prefix"),
        new("append", "Append"),
        new("template", "Custom template"),
    };

    /// <summary>Curated tile/button palette (all pair with black or white at
    /// WCAG AA via IdealForeground).</summary>
    public static readonly string[] SwatchColors =
    {
        "#2e7d32", "#1565c0", "#c0392b", "#6a1b9a", "#ef6c00", "#00838f",
        "#37474f", "#795548", "#9e9d24", "#d81b60", "#283593", "#00695c",
    };

    private readonly Config _original;
    private readonly IDialogService _dialogs;
    private readonly Func<ThemePalette> _palette;
    private readonly string? _cfgPath;

    public Config? Result { get; private set; }

    public SettingsViewModel(Config current, IDialogService dialogs,
        Func<ThemePalette>? palette = null, string? cfgPath = null,
        ISoundService? sounds = null)
    {
        _original = current;
        _dialogs = dialogs;
        _palette = palette ?? (() => ThemePalette.Light);
        _cfgPath = cfgPath;

        SoundsEnabled = current.Sounds.Enabled;
        NewAlertSound = new SoundChoiceVm("New alert", SoundEvent.NewAlert,
            current.Sounds.NewAlert, dialogs, sounds ?? new NullSoundService());
        FiledSound = new SoundChoiceVm("Session done", SoundEvent.Filed,
            current.Sounds.Filed, dialogs, sounds ?? new NullSoundService());
        SetAsideSound = new SoundChoiceVm("Set aside", SoundEvent.SetAside,
            current.Sounds.SetAside, dialogs, sounds ?? new NullSoundService());
        ErrorSound = new SoundChoiceVm("Couldn't file", SoundEvent.Error,
            current.Sounds.Error, dialogs, sounds ?? new NullSoundService());

        Inbox = current.Inbox;
        Deferred = current.Deferred;
        NamesFile = current.NamesFile;
        HistoryDb = current.HistoryDb;
        DestinationsFile = current.DestinationsFile;
        MonitoredFoldersFile = current.MonitoredFoldersFile;
        AlertsFile = current.AlertsFile;
        BoxLabelsFile = current.BoxLabelsFile;
        MonitorTitle = current.MonitorTitle;
        FilingMode = current.NamingMode;
        NamingTemplate = current.NamingTemplate;
        SortKey = current.Sort;
        EnterCommits = current.EnterCommits;
        UppercaseNames = current.UppercaseNames;
        WordSeparator = current.WordSeparator;
        FlashAlerts = current.FlashAlerts;
        PollSecondsText = current.PollSeconds.ToString();
        foreach (var t in current.AlertTexts) AlertTerms.Add(t);
        UiFontFamily = current.UiFontFamily;
        UiFontSizeText = current.UiFontSize == 0 ? "" : current.UiFontSize.ToString();
        _themeMode = current.Theme;

        Routes = new ObservableCollection<RouteEditVm>(current.Routes.Select(RouteEditVm.From));
        WatchFolders = new ObservableCollection<WatchEditVm>(current.WatchFolders.Select(WatchEditVm.From));
        Passwords = new ObservableCollection<PasswordEditVm>(current.SavedPasswords
            .Select(p => new PasswordEditVm { Label = p.Label, Stored = p.Password }));

        AddRouteCommand = new RelayCommand(() =>
        {
            var vm = new RouteEditVm { Label = "New destination" };
            Routes.Add(vm);
            SelectedRoute = vm;
        });
        RemoveRouteCommand = new RelayCommand(
            () => { if (SelectedRoute is { } r) Routes.Remove(r); SelectedRoute = Routes.FirstOrDefault(); },
            () => SelectedRoute is not null);
        DuplicateRouteCommand = new RelayCommand(() =>
        {
            if (SelectedRoute is not { } src) return;
            // a sibling to tweak: everything copied except the hotkey (a
            // duplicate hotkey would collide the moment it lands)
            var copy = RouteEditVm.From(src.ToRoute());
            copy.Label = src.Label.Trim() + " copy";
            copy.Hotkey = "";
            Routes.Insert(Routes.IndexOf(src) + 1, copy);
            SelectedRoute = copy;
        }, () => SelectedRoute is not null);
        RouteUpCommand = new RelayCommand(() => MoveRoute(-1), () => CanMoveRoute(-1));
        RouteDownCommand = new RelayCommand(() => MoveRoute(+1), () => CanMoveRoute(+1));

        AddWatchCommand = new RelayCommand(() =>
        {
            var vm = new WatchEditVm { Label = "New folder" };
            WatchFolders.Add(vm);
            SelectedWatch = vm;
        });
        RemoveWatchCommand = new RelayCommand(
            () => { if (SelectedWatch is { } w) WatchFolders.Remove(w); SelectedWatch = WatchFolders.FirstOrDefault(); },
            () => SelectedWatch is not null);
        WatchUpCommand = new RelayCommand(() => MoveWatch(-1), () => CanMoveWatch(-1));
        WatchDownCommand = new RelayCommand(() => MoveWatch(+1), () => CanMoveWatch(+1));

        RemovePasswordCommand = new RelayCommand(
            () => { if (SelectedPassword is { } p) Passwords.Remove(p); SelectedPassword = Passwords.FirstOrDefault(); },
            () => SelectedPassword is not null);

        AddAlertCommand = new RelayCommand(() =>
        {
            var text = NewAlertText;
            NewAlertText = "";
            var terms = text.Split(new[] { ',', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var term in terms)
            {
                if (term.Length == 0) continue;
                if (!AlertTerms.Any(t => string.Equals(t, term, StringComparison.CurrentCultureIgnoreCase)))
                    AlertTerms.Add(term);
            }
        });
        RemoveAlertCommand = new RelayCommand<string>(t => AlertTerms.Remove(t));
        AlertTerms.CollectionChanged += (_, _) => RecomputeTilePreview();

        BrowseInboxCommand = new RelayCommand(() => Inbox = _dialogs.BrowseFolder(Inbox) ?? Inbox);
        BrowseDeferredCommand = new RelayCommand(() => Deferred = _dialogs.BrowseFolder(Deferred) ?? Deferred);
        BrowseNamesFileCommand = new RelayCommand(() =>
            NamesFile = _dialogs.AskOpenFile("Name lists (*.txt)|*.txt|All files (*.*)|*.*") ?? NamesFile);
        // open-style picker: choosing the EXISTING audit db must not trigger
        // the save dialog's "already exists — replace?" prompt
        BrowseHistoryDbCommand = new RelayCommand(() =>
            HistoryDb = _dialogs.AskFilePath("SQLite database (*.sqlite)|*.sqlite|All files (*.*)|*.*",
                System.IO.Path.GetFileName(HistoryDb)) ?? HistoryDb);
        BrowseDestinationsFileCommand = new RelayCommand(() =>
            DestinationsFile = _dialogs.AskOpenFile("JSON files (*.json)|*.json|All files (*.*)|*.*") ?? DestinationsFile);
        BrowseMonitoredFoldersFileCommand = new RelayCommand(() =>
            MonitoredFoldersFile = _dialogs.AskOpenFile("JSON files (*.json)|*.json|All files (*.*)|*.*") ?? MonitoredFoldersFile);
        BrowseAlertsFileCommand = new RelayCommand(() =>
            AlertsFile = _dialogs.AskOpenFile("JSON files (*.json)|*.json|All files (*.*)|*.*") ?? AlertsFile);
        BrowseBoxLabelsFileCommand = new RelayCommand(() =>
            BoxLabelsFile = _dialogs.AskOpenFile("JSON files (*.json)|*.json|All files (*.*)|*.*") ?? BoxLabelsFile);
        BrowseRoutePathCommand = new RelayCommand(() =>
        {
            if (SelectedRoute is { } r) r.Path = _dialogs.BrowseFolder(r.Path) ?? r.Path;
        });
        BrowseWatchPathCommand = new RelayCommand(() =>
        {
            if (SelectedWatch is { } w) w.Path = _dialogs.BrowseFolder(w.Path) ?? w.Path;
        });
        CreateRouteFolderCommand = new RelayCommand(() =>
            CreateFolder(SelectedRoute?.Path, () => SelectedRoute?.RefreshProblem()));
        OpenRouteFolderCommand = new RelayCommand(() => OpenFolderOrExplain(SelectedRoute?.Path));
        CreateWatchFolderCommand = new RelayCommand(() =>
            CreateFolder(SelectedWatch?.Path, () =>
            {
                SelectedWatch?.RefreshProblem();
                RecomputeTilePreview();
            }));
        OpenWatchFolderCommand = new RelayCommand(() => OpenFolderOrExplain(SelectedWatch?.Path));

        SelectedRoute = Routes.FirstOrDefault();
        SelectedWatch = WatchFolders.FirstOrDefault();
        SelectedPassword = Passwords.FirstOrDefault();

        // live route previews + duplicate-hotkey notes: recompute whenever a
        // route field or the route order changes. Move actions re-deliver the
        // item in NewItems — hooking it again would stack subscriptions.
        foreach (var r in Routes) HookRoute(r);
        Routes.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move
                && e.NewItems is not null)
                foreach (RouteEditVm r in e.NewItems) HookRoute(r);
            RecomputeRouteDerived();
        };
        RecomputeRouteDerived();

        // live tile preview: recompute when the selected watch folder's fields
        // or the alert terms change
        foreach (var w in WatchFolders) HookWatch(w);
        WatchFolders.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move
                && e.NewItems is not null)
                foreach (WatchEditVm w in e.NewItems) HookWatch(w);
            RecomputeTilePreview();
        };
        RecomputeTilePreview();
    }

    private void HookWatch(WatchEditVm w) =>
        w.PropertyChanged += (_, _) => RecomputeTilePreview();

    private void HookRoute(RouteEditVm r) =>
        r.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RouteEditVm.Label) or nameof(RouteEditVm.Hotkey)
                or nameof(RouteEditVm.Suffix) or nameof(RouteEditVm.AppendSuffix)
                or nameof(RouteEditVm.Color))
                RecomputeRouteDerived();
            if (e.PropertyName is nameof(RouteEditVm.NamingMode) or nameof(RouteEditVm.NamingTemplate)
                && ReferenceEquals(r, SelectedRoute))
                Raise(nameof(RouteFilingExample));
        };

    /// <summary>The Destinations tab's live preview of what this route's
    /// EFFECTIVE naming mode + template (its own override, falling back to
    /// the Filing tab's global setting) does to a sample file — mirrors
    /// FilingExample but for whichever route is selected.</summary>
    public string RouteFilingExample
    {
        get
        {
            if (SelectedRoute is not { } r) return "";
            var routeMode = r.NamingMode.Length == 0 ? null : r.NamingMode;
            try
            {
                // same mode-appropriate sample rule as FilingExample, but keyed
                // off the route's EFFECTIVE mode (its own override, falling
                // back to the Filing tab's global setting) — not just the
                // global one, so an overridden route previews correctly too.
                var effectiveMode = Naming.ResolveMode(routeMode, FilingMode);
                var sample = effectiveMode is Naming.ModeInsert or Naming.ModeReplace
                    ? "20240115--12345.pdf" : "scan001.pdf";
                var result = Naming.BuildTarget(
                    sample, "Smith John",
                    routeMode: routeMode, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false,
                    routeTemplate: r.NamingTemplate, globalTemplate: NamingTemplate);
                return result.Filename;
            }
            catch (ArgumentException ex)
            {
                return "⚠ " + ex.Message;
            }
        }
    }

    /// <summary>What each route button will actually look like and answer to —
    /// the same composition rules the Processing screen uses — plus a live
    /// "already used by" note the moment two routes claim one keystroke.</summary>
    private void RecomputeRouteDerived()
    {
        var p = _palette();
        var claimed = new Dictionary<string, string>();
        for (var i = 0; i < Routes.Count; i++)
        {
            var r = Routes[i];
            var gesture = HotkeyParser.ToGesture(r.Hotkey)
                ?? (i < 9 ? new System.Windows.Input.KeyGesture(
                        System.Windows.Input.Key.D1 + i,
                        System.Windows.Input.ModifierKeys.Control) : null);
            var gestureText = gesture is null ? "" : HotkeyParser.Display(gesture);

            // the note mirrors the OK-time hard errors, live, worst first
            var note = "";
            var rawHotkey = r.Hotkey.Trim();
            if (rawHotkey.Length > 0 && !HotkeyParser.TryParse(rawHotkey, out _, out _))
                note = $"can't understand \"{rawHotkey}\"";
            else if (rawHotkey.Length > 0 && HotkeyParser.ToGesture(rawHotkey) is null)
                note = $"needs a modifier — try Ctrl+{rawHotkey}";
            else if (gesture is not null
                     && HotkeyParser.ReservedUse(gesture.Modifiers, gesture.Key) is { } use)
                note = $"{gestureText} already means {use} in the app";
            if (gestureText.Length > 0)
            {
                if (note.Length == 0 && claimed.TryGetValue(gestureText, out var other))
                    note = $"{gestureText} is already used by \"{other}\"";
                else
                    claimed[gestureText] = r.Label.Trim();
            }

            var custom = ThemePalette.ParseColor(r.Color);
            var back = custom ?? p.Surface;
            var fore = custom is { } c ? ThemePalette.IdealForeground(c) : p.Text;
            var label = r.Label
                + (r.AppendSuffix && r.Suffix.Length > 0 ? $"   ·   {r.Suffix}" : "")
                + (gestureText.Length > 0 ? $"   ·   {gestureText}" : "");
            r.SetDerived(label, back, fore, note, gestureText);
        }
    }

    // ------------------------------------------------------------- scalars
    private string _inbox = "";
    public string Inbox
    {
        get => _inbox;
        set { if (Set(ref _inbox, value)) Raise(nameof(InboxNote)); }
    }

    private string _deferred = "";
    public string Deferred
    {
        get => _deferred;
        set { if (Set(ref _deferred, value)) Raise(nameof(DeferredNote)); }
    }

    private string _namesFile = "";
    public string NamesFile
    {
        get => _namesFile;
        set { if (Set(ref _namesFile, value)) Raise(nameof(NamesFileNote)); }
    }

    private string _historyDb = "";
    public string HistoryDb
    {
        get => _historyDb;
        set { if (Set(ref _historyDb, value)) Raise(nameof(HistoryDbNote)); }
    }

    // ---- split-config section files (destinations/monitored folders/alerts/
    // box labels) — Settings edits the path, not the section's contents
    private string _destinationsFile = "";
    public string DestinationsFile
    {
        get => _destinationsFile;
        set { if (Set(ref _destinationsFile, value)) Raise(nameof(DestinationsFileNote)); }
    }

    private string _monitoredFoldersFile = "";
    public string MonitoredFoldersFile
    {
        get => _monitoredFoldersFile;
        set { if (Set(ref _monitoredFoldersFile, value)) Raise(nameof(MonitoredFoldersFileNote)); }
    }

    private string _alertsFile = "";
    public string AlertsFile
    {
        get => _alertsFile;
        set { if (Set(ref _alertsFile, value)) Raise(nameof(AlertsFileNote)); }
    }

    private string _boxLabelsFile = "";
    public string BoxLabelsFile
    {
        get => _boxLabelsFile;
        set { if (Set(ref _boxLabelsFile, value)) Raise(nameof(BoxLabelsFileNote)); }
    }

    // Live per-field notes — the OK-time warnings, surfaced as you type
    public string InboxNote => FolderNote(Inbox,
        "no inbox folder set — there will be nothing to process");
    public string DeferredNote => FolderNote(Deferred, "");

    public string NamesFileNote
    {
        get
        {
            var p = NamesFile.Trim();
            if (p.Length == 0) return "";
            if (!Path.IsPathRooted(p)) return "relative — resolved beside the config file";
            return File.Exists(p) ? "" : "file doesn't exist yet (optional — it seeds the name suggestions)";
        }
    }

    public string HistoryDbNote
    {
        get
        {
            var p = HistoryDb.Trim();
            if (p.Length == 0) return "";
            if (!Path.IsPathRooted(p)) return "relative — kept beside the config file";
            if (File.Exists(p)) return "";
            var dir = Path.GetDirectoryName(p);
            return dir is not null && !Directory.Exists(dir)
                ? $"folder doesn't exist: {dir}"
                : "a new database will be created here";
        }
    }

    // Live per-field notes for the four section-file paths: entry count when
    // the file's readable, "will be created" when it's missing, the
    // ConfigException message when it's there but broken.
    public string DestinationsFileNote => DataFileNote(DestinationsFile,
        sp => (Config.ReadDoc<DestinationsDoc>(_cfgPath!, sp) ?? new DestinationsDoc()).Routes.Count);

    public string MonitoredFoldersFileNote => DataFileNote(MonitoredFoldersFile,
        sp => (Config.ReadDoc<MonitoredFoldersDoc>(_cfgPath!, sp) ?? new MonitoredFoldersDoc()).WatchFolders.Count);

    public string AlertsFileNote => DataFileNote(AlertsFile,
        sp => (Config.ReadDoc<AlertsDoc>(_cfgPath!, sp) ?? new AlertsDoc()).AlertTexts.Count);

    public string BoxLabelsFileNote => DataFileNote(BoxLabelsFile,
        sp => (Config.ReadDoc<BoxLabelsDoc>(_cfgPath!, sp) ?? new BoxLabelsDoc()).LabelClients.Count);

    /// <summary>Shared live-note logic for the four section-file path boxes:
    /// blank means the section default: not resolvable (no config path to
    /// resolve beside) reads as an unusable path; missing means it'll be
    /// created from the in-memory list on save; present means show what's
    /// really in it (or the readable parse error, rather than a crash).</summary>
    private string DataFileNote(string sectionPath, Func<string, int> countEntries)
    {
        var p = sectionPath.Trim();
        if (p.Length == 0) return "blank = the default beside config.json";
        if (_cfgPath is not { } cfgPath) return "not a usable path";
        string full;
        try { full = Config.ResolveBeside(cfgPath, p); }
        catch (Exception) { return "not a usable path"; }
        if (!File.Exists(full))
            return "will be created on save — the current list is written there";
        try { return $"{countEntries(p)} entries"; }
        catch (ConfigException ex) { return ex.Message; }
    }

    private static string FolderNote(string path, string blankMeans)
    {
        var p = path.Trim();
        if (p.Length == 0) return blankMeans;
        if (!Path.IsPathRooted(p)) return "relative — resolved beside the config file";
        return Directory.Exists(p) ? "" : $"folder doesn't exist: {p}";
    }

    /// <summary>One-click fix for "folder doesn't exist" — creates the whole
    /// tree; failures come back as a dialog, not a crash.</summary>
    private void CreateFolder(string? path, Action refresh)
    {
        var p = path?.Trim() ?? "";
        if (p.Length == 0)
        {
            _dialogs.Warn("Pick a folder path first.", "OrdoSort");
            return;
        }
        try
        {
            Directory.CreateDirectory(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            _dialogs.Warn($"Couldn't create {p}: {ex.Message}", "OrdoSort");
            return;
        }
        refresh();
    }

    private void OpenFolderOrExplain(string? path)
    {
        var p = path?.Trim() ?? "";
        if (p.Length > 0 && Directory.Exists(p))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
        else
            _dialogs.Warn("That folder doesn't exist yet.", "OrdoSort");
    }

    public RelayCommand OpenBackupsCommand => _openBackups ??= new RelayCommand(() =>
    {
        var db = HistoryDb.Trim();
        if (!Path.IsPathRooted(db))
        {
            var baseDir = _cfgPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(_cfgPath));
            if (baseDir is null) return;
            db = Path.Combine(baseDir, db);
        }
        var backups = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, "backups");
        if (Directory.Exists(backups))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(backups) { UseShellExecute = true });
        else
            _dialogs.Info("No backups yet — the first one is taken the next time the app starts.",
                "OrdoSort");
    });
    private RelayCommand? _openBackups;

    private string _monitorTitle = "";
    public string MonitorTitle { get => _monitorTitle; set => Set(ref _monitorTitle, value); }

    private string _filingMode = Naming.ModeInsert;
    public string FilingMode
    {
        get => _filingMode;
        set
        {
            if (Set(ref _filingMode, value))
            {
                Raise(nameof(ModeInsert));
                Raise(nameof(ModeReplace));
                Raise(nameof(ModePrefix));
                Raise(nameof(ModeAppend));
                Raise(nameof(ModeTemplate));
                Raise(nameof(FilingExample));
                Raise(nameof(TemplateNote));
                Raise(nameof(RouteFilingExample));
            }
        }
    }

    public bool ModeInsert { get => FilingMode == Naming.ModeInsert; set { if (value) FilingMode = Naming.ModeInsert; } }
    public bool ModeReplace { get => FilingMode == Naming.ModeReplace; set { if (value) FilingMode = Naming.ModeReplace; } }
    public bool ModePrefix { get => FilingMode == Naming.ModePrefix; set { if (value) FilingMode = Naming.ModePrefix; } }
    public bool ModeAppend { get => FilingMode == Naming.ModeAppend; set { if (value) FilingMode = Naming.ModeAppend; } }
    public bool ModeTemplate { get => FilingMode == Naming.ModeTemplate; set { if (value) FilingMode = Naming.ModeTemplate; } }

    private string _namingTemplate = "";
    public string NamingTemplate
    {
        get => _namingTemplate;
        set
        {
            if (Set(ref _namingTemplate, value))
            {
                Raise(nameof(FilingExample));
                Raise(nameof(TemplateNote));
                Raise(nameof(RouteFilingExample));
            }
        }
    }

    /// <summary>Live validation of the custom template, shown under the
    /// template box — blank while a different mode is selected or the
    /// template is already valid.</summary>
    public string TemplateNote => FilingMode == Naming.ModeTemplate
        ? Naming.ValidateTemplate(NamingTemplate)
        : "";

    private string _sortKey = "size_desc";
    public string SortKey { get => _sortKey; set => Set(ref _sortKey, value); }

    private bool _enterCommits;
    public bool EnterCommits { get => _enterCommits; set => Set(ref _enterCommits, value); }

    private bool _uppercaseNames;
    public bool UppercaseNames
    {
        get => _uppercaseNames;
        set { if (Set(ref _uppercaseNames, value)) Raise(nameof(FilingExample)); }
    }

    private string _wordSeparator = "";
    public string WordSeparator
    {
        get => _wordSeparator;
        set { if (Set(ref _wordSeparator, value)) Raise(nameof(FilingExample)); }
    }

    /// <summary>One live example tying mode + UPPERCASE + separator together —
    /// exactly what a document for Smith John would file as with these settings.</summary>
    public string FilingExample
    {
        get
        {
            var name = "Smith John";
            if (UppercaseNames) name = name.ToUpperInvariant();
            if (WordSeparator.Length > 0 && !WordSeparator.Contains(' '))
                name = name.Replace(" ", WordSeparator);
            // Insert/Replace both work on the classic "--" scanner name;
            // Prefix/Append/Template don't need (or want) a marker, and
            // showing one there would contradict the scan001.pdf examples
            // in the radio labels right above this box.
            var sample = FilingMode is Naming.ModeInsert or Naming.ModeReplace
                ? "20240115--12345.pdf" : "scan001.pdf";
            try
            {
                var result = Naming.BuildTarget(
                    sample, name,
                    routeMode: null, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false,
                    globalTemplate: NamingTemplate);
                // before → after, with the typed name in the middle — the
                // transformation is the whole story
                return $"{sample}  +  \"Smith John\"  →  {result.Filename}";
            }
            catch (ArgumentException ex)
            {
                return "⚠ " + ex.Message;
            }
        }
    }


    private bool _flashAlerts;
    public bool FlashAlerts { get => _flashAlerts; set => Set(ref _flashAlerts, value); }

    private string _pollSecondsText = Config.DefaultPollSeconds.ToString();
    public string PollSecondsText { get => _pollSecondsText; set => Set(ref _pollSecondsText, value); }

    // ---- sounds ----
    private bool _soundsEnabled;
    public bool SoundsEnabled { get => _soundsEnabled; set => Set(ref _soundsEnabled, value); }

    public SoundChoiceVm NewAlertSound { get; private set; } = null!;
    public SoundChoiceVm FiledSound { get; private set; } = null!;
    public SoundChoiceVm SetAsideSound { get; private set; } = null!;
    public SoundChoiceVm ErrorSound { get; private set; } = null!;

    public ObservableCollection<string> AlertTerms { get; } = new();

    private string _newAlertText = "";
    public string NewAlertText { get => _newAlertText; set => Set(ref _newAlertText, value); }

    // ------------------------------------------------------ live tile preview
    private bool _tilePreviewVisible;
    public bool TilePreviewVisible { get => _tilePreviewVisible; private set => Set(ref _tilePreviewVisible, value); }

    private string _tilePreviewLabel = "";
    public string TilePreviewLabel { get => _tilePreviewLabel; private set => Set(ref _tilePreviewLabel, value); }

    private string _tilePreviewCount = "";
    public string TilePreviewCount { get => _tilePreviewCount; private set => Set(ref _tilePreviewCount, value); }

    private string _tilePreviewNote = "";
    public string TilePreviewNote
    {
        get => _tilePreviewNote;
        private set { if (Set(ref _tilePreviewNote, value)) Raise(nameof(HasTilePreviewNote)); }
    }
    public bool HasTilePreviewNote => TilePreviewNote.Length > 0;

    private Rgb _tilePreviewBack;
    public Rgb TilePreviewBack { get => _tilePreviewBack; private set => Set(ref _tilePreviewBack, value); }

    private Rgb _tilePreviewFore;
    public Rgb TilePreviewFore { get => _tilePreviewFore; private set => Set(ref _tilePreviewFore, value); }

    private string _tilePreviewHint = "";
    public string TilePreviewHint { get => _tilePreviewHint; private set => Set(ref _tilePreviewHint, value); }

    /// <summary>The dashboard card exactly as Ready will show it — computed
    /// against the REAL folder, so the count, the alert state, and the
    /// subfolder note are live while you configure.</summary>
    private void RecomputeTilePreview()
    {
        var w = SelectedWatch;
        TilePreviewVisible = w is not null;
        if (w is null) return;

        var status = FolderMonitor.Status(w.ToWatchFolder(), AlertTerms.ToList());
        var p = _palette();
        var baseBack = ThemePalette.ParseColor(w.Color) ?? p.TileDefaultBg;
        TilePreviewBack = status.Alerting ? p.Danger : baseBack;
        TilePreviewFore = status.Alerting ? p.DangerText : ThemePalette.IdealForeground(baseBack);
        TilePreviewLabel = w.Label;
        TilePreviewCount = status.Error.Length > 0
            ? "⚠"
            : $"{status.Count}" + (status.Alerting ? " ⚠" : "");
        TilePreviewNote = status.AlertFolders.Count > 0
            ? "in " + string.Join(", ", status.AlertFolders)
            : "";
        TilePreviewHint = status.Error.Length > 0
            ? status.Error
            : !status.HasFiles
                ? "the tile only appears while the folder holds matching files"
                : status.Alerting
                    ? "alerting right now — this is the flash color"
                    : "";
    }

    private string _uiFontFamily = "";
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    private string _uiFontSizeText = "";
    public string UiFontSizeText { get => _uiFontSizeText; set => Set(ref _uiFontSizeText, value); }

    // ------------------------------------------------------------- theme
    private string _themeMode = "auto";
    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (Set(ref _themeMode, value))
            {
                Raise(nameof(ThemeAuto));
                Raise(nameof(ThemeLight));
                Raise(nameof(ThemeDark));
            }
        }
    }

    public bool ThemeAuto { get => ThemeMode == "auto"; set { if (value) ThemeMode = "auto"; } }
    public bool ThemeLight { get => ThemeMode == "light"; set { if (value) ThemeMode = "light"; } }
    public bool ThemeDark { get => ThemeMode == "dark"; set { if (value) ThemeMode = "dark"; } }

    // ----------------------------------------------------------- collections
    public ObservableCollection<RouteEditVm> Routes { get; }
    public ObservableCollection<WatchEditVm> WatchFolders { get; }
    public ObservableCollection<PasswordEditVm> Passwords { get; }

    private RouteEditVm? _selectedRoute;
    public RouteEditVm? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (Set(ref _selectedRoute, value))
            {
                RemoveRouteCommand.RaiseCanExecuteChanged();
                RouteUpCommand.RaiseCanExecuteChanged();
                RouteDownCommand.RaiseCanExecuteChanged();
                DuplicateRouteCommand.RaiseCanExecuteChanged();
                Raise(nameof(RouteFilingExample));
            }
        }
    }

    private WatchEditVm? _selectedWatch;
    public WatchEditVm? SelectedWatch
    {
        get => _selectedWatch;
        set
        {
            if (Set(ref _selectedWatch, value))
            {
                RemoveWatchCommand.RaiseCanExecuteChanged();
                WatchUpCommand.RaiseCanExecuteChanged();
                WatchDownCommand.RaiseCanExecuteChanged();
                RecomputeTilePreview();
                Raise(nameof(SectionChoices));
            }
        }
    }

    /// <summary>Sections already in use by the OTHER monitored folders —
    /// the pick-or-type dropdown for the selected folder's Section box.</summary>
    public IEnumerable<string> SectionChoices =>
        WatchFolders.Where(w => !ReferenceEquals(w, SelectedWatch))
               .Select(w => w.Section.Trim())
               .Where(s => s.Length > 0)
               .Distinct(StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    private bool CanMoveWatch(int delta)
    {
        if (SelectedWatch is null) return false;
        var j = WatchFolders.IndexOf(SelectedWatch) + delta;
        return j >= 0 && j < WatchFolders.Count;
    }

    private void MoveWatch(int delta)
    {
        if (SelectedWatch is null) return;
        var i = WatchFolders.IndexOf(SelectedWatch);
        WatchFolders.Move(i, i + delta);
        WatchUpCommand.RaiseCanExecuteChanged();
        WatchDownCommand.RaiseCanExecuteChanged();
    }

    private PasswordEditVm? _selectedPassword;
    public PasswordEditVm? SelectedPassword
    {
        get => _selectedPassword;
        set { if (Set(ref _selectedPassword, value)) RemovePasswordCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand AddRouteCommand { get; }
    public RelayCommand RemoveRouteCommand { get; }
    public RelayCommand DuplicateRouteCommand { get; }
    public RelayCommand CreateRouteFolderCommand { get; }
    public RelayCommand OpenRouteFolderCommand { get; }
    public RelayCommand CreateWatchFolderCommand { get; }
    public RelayCommand OpenWatchFolderCommand { get; }
    public RelayCommand RouteUpCommand { get; }
    public RelayCommand RouteDownCommand { get; }
    public RelayCommand AddWatchCommand { get; }
    public RelayCommand RemoveWatchCommand { get; }
    public RelayCommand WatchUpCommand { get; }
    public RelayCommand WatchDownCommand { get; }
    public RelayCommand RemovePasswordCommand { get; }
    public RelayCommand AddAlertCommand { get; }
    public RelayCommand<string> RemoveAlertCommand { get; }
    public RelayCommand BrowseInboxCommand { get; }
    public RelayCommand BrowseDeferredCommand { get; }
    public RelayCommand BrowseNamesFileCommand { get; }
    public RelayCommand BrowseHistoryDbCommand { get; }
    public RelayCommand BrowseDestinationsFileCommand { get; }
    public RelayCommand BrowseMonitoredFoldersFileCommand { get; }
    public RelayCommand BrowseAlertsFileCommand { get; }
    public RelayCommand BrowseBoxLabelsFileCommand { get; }
    public RelayCommand BrowseRoutePathCommand { get; }
    public RelayCommand BrowseWatchPathCommand { get; }

    /// <summary>False when either field is blank — the window shows a nudge
    /// instead of silently doing nothing.</summary>
    public bool AddPassword(string label, string plain)
    {
        if (label.Trim().Length == 0 || plain.Length == 0) return false;
        Passwords.Add(new PasswordEditVm
        {
            Label = label.Trim(),
            Stored = PasswordVault.Protect(plain),
        });
        return true;
    }

    private bool CanMoveRoute(int delta)
    {
        if (SelectedRoute is null) return false;
        var i = Routes.IndexOf(SelectedRoute);
        var j = i + delta;
        return j >= 0 && j < Routes.Count;
    }

    private void MoveRoute(int delta)
    {
        if (SelectedRoute is null) return;
        var i = Routes.IndexOf(SelectedRoute);
        Routes.Move(i, i + delta);
        RouteUpCommand.RaiseCanExecuteChanged();
        RouteDownCommand.RaiseCanExecuteChanged();
    }

    // ----------------------------------------------------------- validation
    /// <summary>Problems that block OK outright.</summary>
    public List<string> HardErrors()
    {
        var errors = new List<string>();

        if (UiFontSizeText.Trim().Length > 0
            && (!int.TryParse(UiFontSizeText.Trim(), out var size) || size is < 6 or > 72))
            errors.Add("Base text size must be a number from 6 to 72 (or blank for the default).");

        if (WordSeparator.Contains(' '))
            errors.Add("The word separator can't contain a space.");

        if (FilingMode == Naming.ModeTemplate)
        {
            var templateError = Naming.ValidateTemplate(NamingTemplate);
            if (templateError.Length > 0) errors.Add(templateError);
        }

        if (!int.TryParse(PollSecondsText.Trim(), out var poll)
            || poll < Config.MinPollSeconds || poll > Config.MaxPollSeconds)
            errors.Add($"Folder check interval must be a number from " +
                       $"{Config.MinPollSeconds} to {Config.MaxPollSeconds} seconds.");

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Routes)
        {
            var label = r.Label.Trim();
            if (label.Length == 0)
                errors.Add("Every destination button needs a label.");
            else if (!labels.Add(label))
                errors.Add($"Two destination buttons are both called \"{label}\".");

            var rawHotkey = r.Hotkey.Trim();
            if (rawHotkey.Length > 0 && !HotkeyParser.TryParse(rawHotkey, out _, out _))
                errors.Add($"\"{label}\": can't understand the hotkey \"{r.Hotkey}\".");
            else if (rawHotkey.Length > 0 && HotkeyParser.ToGesture(rawHotkey) is null)
                errors.Add($"\"{label}\": the hotkey \"{r.Hotkey}\" needs a modifier — try \"Ctrl+{rawHotkey}\".");
            if (!r.ColorValid)
                errors.Add($"\"{label}\": \"{r.Color}\" is not a color (try #2e7d32).");

            // a route in template mode is validated here too — not just the
            // global template — using the same fallback ResolveTemplate uses
            // at commit time: the route's own template, or the Filing tab's
            // global one when the route's box is blank.
            if (r.NamingMode == Naming.ModeTemplate)
            {
                var routeTemplate = r.NamingTemplate.Trim();
                var effectiveTemplate = routeTemplate.Length == 0 ? NamingTemplate : routeTemplate;
                var templateError = Naming.ValidateTemplate(effectiveTemplate);
                if (templateError.Length > 0)
                    errors.Add($"{label}: template — {templateError}");
            }
        }

        // duplicate EFFECTIVE hotkeys — the same keystroke can't file two ways
        var seen = new Dictionary<string, string>();
        for (var i = 0; i < Routes.Count; i++)
        {
            var gesture = HotkeyParser.ToGesture(Routes[i].Hotkey)
                ?? (i < 9 ? new System.Windows.Input.KeyGesture(
                        System.Windows.Input.Key.D1 + i,
                        System.Windows.Input.ModifierKeys.Control) : null);
            if (gesture is null) continue;
            var text = HotkeyParser.Display(gesture);
            if (HotkeyParser.ReservedUse(gesture.Modifiers, gesture.Key) is { } use)
                errors.Add($"\"{Routes[i].Label}\": {text} already means {use} everywhere in the app.");
            if (seen.TryGetValue(text, out var other))
                errors.Add($"\"{Routes[i].Label}\" and \"{other}\" both answer to {text}.");
            else
                seen[text] = Routes[i].Label;
        }

        foreach (var w in WatchFolders)
        {
            if (w.Label.Trim().Length == 0)
                errors.Add("Every monitored folder needs a label.");
            if (!w.ColorValid)
                errors.Add($"\"{w.Label}\": \"{w.Color}\" is not a color (try #c0392b).");
        }

        return errors;
    }

    /// <summary>Problems worth a "Save anyway?" — unreachable folders mostly
    /// (they may simply be offline right now).</summary>
    public List<string> Warnings()
    {
        var warnings = new List<string>();
        if (Inbox.Trim().Length == 0)
            warnings.Add("No inbox folder is set — there will be nothing to process.");
        else if (!Directory.Exists(Inbox.Trim()))
            warnings.Add($"The inbox folder doesn't exist: {Inbox.Trim()}");
        if (Deferred.Trim().Length > 0 && !Directory.Exists(Deferred.Trim()))
            warnings.Add($"The set-aside folder doesn't exist: {Deferred.Trim()}");
        foreach (var r in Routes)
        {
            var problem = Config.ValidateRoute(r.ToRoute());
            if (problem.Length > 0) warnings.Add($"\"{r.Label.Trim()}\": {problem}");
        }
        foreach (var w in WatchFolders)
        {
            if (w.Path.Trim().Length > 0 && !Directory.Exists(w.Path.Trim()))
                warnings.Add($"\"{w.Label.Trim()}\": folder doesn't exist: {w.Path.Trim()}");
        }
        return warnings;
    }

    // ---------------------------------------------------------------- build
    /// <summary>Validate, then produce <see cref="Result"/>. Hard errors show
    /// a dialog and return false; warnings ask "Save anyway?".</summary>
    public bool TryBuildResult()
    {
        var errors = HardErrors();
        if (errors.Count > 0)
        {
            _dialogs.Warn("These need fixing first:\n\n • " + string.Join("\n • ", errors),
                "OrdoSort — check the settings");
            return false;
        }

        var warnings = Warnings();
        if (warnings.Count > 0 && !_dialogs.Confirm(
                " • " + string.Join("\n • ", warnings) + "\n\nSave anyway?",
                "OrdoSort — possible problems"))
            return false;

        // JSON-clone the original so EVERY unedited field and unknown key
        // survives by construction (no hand-maintained carry-through list).
        var cfg = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(_original))!;

        // The four side-file Extras dictionaries are [JsonIgnore] (they hold
        // JsonElement values tied to the doc types, not config.json's own
        // shape), so the clone above drops them back to empty. Settings
        // never edits a side file's unknown keys directly — carry the
        // ORIGINAL's copies through by hand, or every Settings OK would quietly
        // erase hand-added keys from destinations.json/monitored-folders.json/
        // alerts.json/box-labels.json on the next save.
        cfg.DestinationsFileExtras = _original.DestinationsFileExtras;
        cfg.MonitoredFoldersFileExtras = _original.MonitoredFoldersFileExtras;
        cfg.AlertsFileExtras = _original.AlertsFileExtras;
        cfg.BoxLabelsFileExtras = _original.BoxLabelsFileExtras;

        cfg.Inbox = Inbox.Trim();
        cfg.Deferred = Deferred.Trim();
        cfg.NamesFile = NamesFile.Trim();
        cfg.HistoryDb = HistoryDb.Trim();
        cfg.DestinationsFile = DestinationsFile.Trim().Length == 0
            ? Config.DefaultDestinationsFile : DestinationsFile.Trim();
        cfg.MonitoredFoldersFile = MonitoredFoldersFile.Trim().Length == 0
            ? Config.DefaultMonitoredFoldersFile : MonitoredFoldersFile.Trim();
        cfg.AlertsFile = AlertsFile.Trim().Length == 0
            ? Config.DefaultAlertsFile : AlertsFile.Trim();
        cfg.BoxLabelsFile = BoxLabelsFile.Trim().Length == 0
            ? Config.DefaultBoxLabelsFile : BoxLabelsFile.Trim();
        cfg.MonitorTitle = MonitorTitle.Trim();
        cfg.NamingMode = FilingMode;
        cfg.NamingTemplate = NamingTemplate.Trim();
        cfg.Sort = SortKey;
        cfg.EnterCommits = EnterCommits;
        cfg.UppercaseNames = UppercaseNames;
        cfg.WordSeparator = WordSeparator;
        cfg.FlashAlerts = FlashAlerts;
        cfg.PollSeconds = int.TryParse(PollSecondsText.Trim(), out var ps)
            ? Math.Clamp(ps, Config.MinPollSeconds, Config.MaxPollSeconds)
            : Config.DefaultPollSeconds;
        cfg.AlertTexts = AlertTerms.ToList();
        cfg.Sounds.Enabled = SoundsEnabled;
        cfg.Sounds.NewAlert = NewAlertSound.Spec;
        cfg.Sounds.Filed = FiledSound.Spec;
        cfg.Sounds.SetAside = SetAsideSound.Spec;
        cfg.Sounds.Error = ErrorSound.Spec;
        cfg.UiFontFamily = UiFontFamily;
        cfg.UiFontSize = UiFontSizeText.Trim().Length == 0 ? 0 : int.Parse(UiFontSizeText.Trim());
        cfg.Theme = ThemeMode;
        cfg.Routes = Routes.Select(r => r.ToRoute()).ToList();
        cfg.WatchFolders = WatchFolders.Select(w => w.ToWatchFolder()).ToList();
        cfg.SavedPasswords = Passwords.Select(p => new SavedPassword
        {
            Label = p.Label,
            // legacy plaintext gets protected the first time Settings saves
            Password = PasswordVault.IsProtected(p.Stored)
                ? p.Stored
                : PasswordVault.Protect(p.Stored),
        }).ToList();

        // A section re-pointed at a path that already holds a file: that
        // file — not whatever this window happened to load with — becomes
        // the truth (box-labels.json is excluded; it's already protected as
        // a bootstrap-only write with its own exclusive writer).
        AdoptRepointedSection<DestinationsDoc>(_original.DestinationsFile, cfg.DestinationsFile, d =>
        {
            cfg.Routes = d.Routes ?? new();
            cfg.DestinationsFileExtras = d.Extras ?? new();
        });
        AdoptRepointedSection<MonitoredFoldersDoc>(_original.MonitoredFoldersFile, cfg.MonitoredFoldersFile, d =>
        {
            cfg.WatchFolders = d.WatchFolders ?? new();
            cfg.MonitoredFoldersFileExtras = d.Extras ?? new();
        });
        AdoptRepointedSection<AlertsDoc>(_original.AlertsFile, cfg.AlertsFile, d =>
        {
            cfg.AlertTexts = d.AlertTexts ?? new();
            cfg.AlertsFileExtras = d.Extras ?? new();
        });

        Result = cfg;
        return true;
    }

    /// <summary>When a section's path box was changed to point at a file
    /// that already exists, that file's contents become the truth for the
    /// built config — an admin's shared file must win over whatever this
    /// editor happened to load with. A target that exists but fails to
    /// parse leaves the editor's built values alone (the save path surfaces
    /// the broken file; TryBuildResult must never throw over it). No-op when
    /// there's no config path to resolve beside, or the path didn't
    /// genuinely change: "changed" is decided by RESOLVED path, not the raw
    /// string — a file dialog hands back an absolute path, and the stored
    /// default is relative, so the same physical file can arrive spelled two
    /// different ways. Comparing the raw strings would misread that as a
    /// re-point and silently swap the editor's in-session edits for the
    /// stale on-disk list.</summary>
    private void AdoptRepointedSection<TDoc>(string originalPath, string newPath,
        Action<TDoc> apply) where TDoc : class
    {
        if (_cfgPath is null) return;
        string full, originalFull;
        try
        {
            full = Config.ResolveBeside(_cfgPath, newPath.Trim());
            originalFull = Config.ResolveBeside(_cfgPath, originalPath.Trim());
        }
        catch (Exception) { return; }
        var changed = !string.Equals(full, originalFull, StringComparison.OrdinalIgnoreCase);
        if (!changed) return;
        if (!File.Exists(full)) return;
        TDoc? doc;
        try { doc = Config.ReadDoc<TDoc>(_cfgPath, newPath); }
        catch (ConfigException) { return; }   // broken target: keep the built values
        if (doc is not null) apply(doc);
    }
}
