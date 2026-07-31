using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoset_" + Guid.NewGuid());
    private readonly FakeDialogs _dialogs = new();

    public SettingsViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private Config LoadFromJson(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        return Config.Load(path);
    }

    [Fact]
    public void UnknownKeysAndToolStateSurviveOkByConstruction()
    {
        // the clone-then-patch build makes it impossible for the settings
        // dialog to wipe keys it doesn't know about
        var cfg = LoadFromJson("""
            {
              "inbox": "c:/faxes",
              "custom_top_level_key": {"kept": true},
              "merge_headers": {"first": "FirstName"},
              "saved_passwords": [{"label": "Payer", "password": "dpapi:abc"}],
              "routes": [{"label": "Invoices", "path": "c:/inv", "custom_route_key": 7}]
            }
            """);
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.Inbox = "c:/faxes-new";
        _dialogs.ConfirmAnswer = true;   // path warnings -> save anyway

        Assert.True(vm.TryBuildResult());
        var result = vm.Result!;
        Assert.Equal("c:/faxes-new", result.Inbox);
        Assert.True(result.Extras.ContainsKey("custom_top_level_key"));
        Assert.Equal("FirstName", result.MergeHeaders["first"]);
        Assert.Equal("dpapi:abc", Assert.Single(result.SavedPasswords).Password);
        Assert.True(Assert.Single(result.Routes).Extras.ContainsKey("custom_route_key"));

        // and the original object was never mutated
        Assert.Equal("c:/faxes", cfg.Inbox);
    }

    [Fact]
    public void DuplicateEffectiveHotkeysBlockOk()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "A", Path = _dir, Hotkey = "Ctrl+3" },
                new Route { Label = "B", Path = _dir, Hotkey = "ctrl+3" },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.False(vm.TryBuildResult());
        Assert.Contains("Ctrl+3", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void FallbackHotkeyCollisionsAreCaughtToo()
    {
        // route 0's fallback is Ctrl+1; route 1 explicitly claims Ctrl+1
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "A", Path = _dir },
                new Route { Label = "B", Path = _dir, Hotkey = "Ctrl+1" },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.False(vm.TryBuildResult());
    }

    [Fact]
    public void BlankHotkeyShowsTheAutomaticKeyAsPlaceholder()
    {
        // the route list shows the effective key (Ctrl+N by position) even
        // when the hotkey box is blank — the box says so via ghost text
        var cfg = new Config { Routes = { new Route { Label = "A", Path = _dir } } };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("Ctrl+1 · automatic", vm.Routes[0].HotkeyPlaceholder);

        vm.AddRouteCommand.Execute(null);            // a fresh destination
        Assert.Equal("Ctrl+2 · automatic", vm.Routes[1].HotkeyPlaceholder);

        vm.Routes[1].Hotkey = "Ctrl+F2";             // explicit key: no ghost
        Assert.Equal("", vm.Routes[1].HotkeyPlaceholder);

        vm.Routes[1].Hotkey = "";                    // cleared: automatic again
        Assert.Equal("Ctrl+2 · automatic", vm.Routes[1].HotkeyPlaceholder);
    }

    [Fact]
    public void ReservedHotkeyBlocksOkAndGetsALiveNote()
    {
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Hotkey = "Ctrl+K" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Contains("Set aside", vm.Routes[0].HotkeyNote);   // live, before OK
        Assert.False(vm.TryBuildResult());
        Assert.Contains("Set aside", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void BareKeyHotkeyBlocksOkWithAModifierHint()
    {
        // "K" parses but WPF can't gesture it — before this check it silently
        // fell back to the slot default and the typed key did nothing
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Hotkey = "K" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Contains("modifier", vm.Routes[0].HotkeyNote);
        Assert.False(vm.TryBuildResult());
        Assert.Contains("Ctrl+K", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void NumpadAndTopRowDigitsCountAsTheSameHotkey()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "A", Path = _dir },   // fallback Ctrl+1
                new Route { Label = "B", Path = _dir, Hotkey = "Ctrl+NumPad1" },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.False(vm.TryBuildResult());
        Assert.Contains("both answer to Ctrl+1", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void DuplicateLabelsUnparseableHotkeyAndBadColorBlockOk()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "Same", Path = _dir, Hotkey = "NotAKey+X", Color = "nope" },
                new Route { Label = "same", Path = _dir, Hotkey = "Ctrl+2" },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.False(vm.TryBuildResult());
        var msg = Assert.Single(_dialogs.Warnings).Message;
        Assert.Contains("both called", msg);
        Assert.Contains("hotkey", msg);
        Assert.Contains("not a color", msg);
    }

    [Fact]
    public void BadFontSizeBlocksOk()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs) { UiFontSizeText = "5" };
        Assert.False(vm.TryBuildResult());
        Assert.Contains("6 to 72", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void SeparatorWithSpaceBlocksOk()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs) { WordSeparator = " - " };
        Assert.False(vm.TryBuildResult());
    }

    [Fact]
    public void PollIntervalLoadsSavesAndValidates()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir, PollSeconds = 30 }, _dialogs);
        Assert.Equal("30", vm.PollSecondsText);

        vm.PollSecondsText = "5";
        Assert.True(vm.TryBuildResult());
        Assert.Equal(5, vm.Result!.PollSeconds);

        vm.PollSecondsText = "2";                       // below the floor
        Assert.False(vm.TryBuildResult());
        Assert.Contains("5 to 600", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public void UnreachableRouteIsAWarningNotAnError()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = Path.Combine(_dir, "missing") } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);

        _dialogs.ConfirmAnswer = false;   // decline "Save anyway?"
        Assert.False(vm.TryBuildResult());

        _dialogs.ConfirmAnswer = true;
        Assert.True(vm.TryBuildResult());
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public void PlaintextPasswordsGetProtectedOnSave()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            SavedPasswords = { new SavedPassword { Label = "Old", Password = "plain" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.True(vm.TryBuildResult());
        var saved = Assert.Single(vm.Result!.SavedPasswords);
        Assert.True(PasswordVault.IsProtected(saved.Password));
        Assert.Equal("plain", PasswordVault.Reveal(saved.Password));
    }

    [Fact]
    public void AddPasswordStoresProtected()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        vm.AddPassword("Payer B", "s3cret");
        Assert.True(vm.TryBuildResult());
        var saved = Assert.Single(vm.Result!.SavedPasswords);
        Assert.Equal("Payer B", saved.Label);
        Assert.Equal("s3cret", PasswordVault.Reveal(saved.Password));
    }

    [Fact]
    public void FilingExampleTracksModeCaseAndSeparator()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.Contains("20240115-SMITH JOHN-12345.pdf", vm.FilingExample);

        vm.WordSeparator = "-";
        Assert.Contains("20240115-SMITH-JOHN-12345.pdf", vm.FilingExample);

        vm.InsertMode = false;
        Assert.Contains("SMITH-JOHN.pdf", vm.FilingExample);

        vm.UppercaseNames = false;
        Assert.Contains("Smith-John.pdf", vm.FilingExample);
    }

    [Fact]
    public void FilingExampleWarnsOnAnIllegalSeparatorInsteadOfThrowing()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs)
        { WordSeparator = ":" };
        Assert.StartsWith("⚠", vm.FilingExample);
    }

    [Fact]
    public void DuplicateHotkeyGetsALiveNote()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "Invoices", Path = _dir },          // fallback Ctrl+1
                new Route { Label = "Statements", Path = _dir, Hotkey = "Ctrl+2" },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("", vm.Routes[1].HotkeyNote);

        vm.Routes[1].Hotkey = "Ctrl+1";   // now collides with route 0's fallback
        Assert.Contains("already used by \"Invoices\"", vm.Routes[1].HotkeyNote);
        Assert.True(vm.Routes[1].HasHotkeyNote);

        vm.Routes[1].Hotkey = "Ctrl+2";
        Assert.Equal("", vm.Routes[1].HotkeyNote);
    }

    [Fact]
    public void RoutePreviewMatchesTheProcessingButtonComposition()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route
                {
                    Label = "Invoices", Path = _dir, Color = "#2e7d32",
                    Suffix = "_INVOICE", AppendSuffix = true,
                },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        var r = vm.Routes[0];
        Assert.Equal("Invoices   ·   _INVOICE   ·   Ctrl+1", r.PreviewLabel);
        Assert.Equal(new OrdoSort.Wpf.Theme.Rgb(46, 125, 50), r.PreviewBack);
        Assert.True(OrdoSort.Wpf.Theme.ThemePalette.ContrastRatio(
            r.PreviewFore, r.PreviewBack) >= 4.5);

        r.AppendSuffix = false;   // live: suffix drops out of the preview
        Assert.Equal("Invoices   ·   Ctrl+1", r.PreviewLabel);
    }

    [Fact]
    public void HistoryDbBrowseUsesTheOpenStylePicker()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        _dialogs.NextFilePath = Path.Combine(_dir, "audit.sqlite");
        vm.BrowseHistoryDbCommand.Execute(null);
        Assert.Equal(Path.Combine(_dir, "audit.sqlite"), vm.HistoryDb);

        _dialogs.NextFilePath = null;   // cancel keeps the old value
        vm.BrowseHistoryDbCommand.Execute(null);
        Assert.Equal(Path.Combine(_dir, "audit.sqlite"), vm.HistoryDb);
    }

    [Fact]
    public void PathNotesSurfaceProblemsLive()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs);
        Assert.Contains("no inbox folder set", vm.InboxNote);

        vm.Inbox = Path.Combine(_dir, "missing");
        Assert.Contains("doesn't exist", vm.InboxNote);

        vm.Inbox = _dir;
        Assert.Equal("", vm.InboxNote);

        vm.HistoryDb = "history.sqlite";
        Assert.Contains("relative", vm.HistoryDbNote);
        vm.HistoryDb = Path.Combine(_dir, "new-audit.sqlite");
        Assert.Contains("new database will be created", vm.HistoryDbNote);
    }

    [Fact]
    public void WatchFoldersReorderWithTheCommands()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            WatchFolders =
            {
                new WatchFolder { Label = "A", Path = _dir },
                new WatchFolder { Label = "B", Path = _dir },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[1];
        Assert.False(vm.WatchDownCommand.CanExecute(null));
        Assert.True(vm.WatchUpCommand.CanExecute(null));

        vm.WatchUpCommand.Execute(null);
        Assert.Equal("B", vm.WatchFolders[0].Label);
        Assert.True(vm.TryBuildResult());
        Assert.Equal(new[] { "B", "A" }, vm.Result!.WatchFolders.Select(w => w.Label));
    }

    [Fact]
    public void AddPasswordRefusesBlankFields()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.False(vm.AddPassword("", "secret"));
        Assert.False(vm.AddPassword("Payer", ""));
        Assert.Empty(vm.Passwords);
        Assert.True(vm.AddPassword("Payer", "secret"));
        Assert.Single(vm.Passwords);
    }

    [Fact]
    public void WatchFolderProblemNotesSurfaceLive()
    {
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "W", Path = Path.Combine(_dir, "missing") } },
        }, _dialogs);
        Assert.Contains("doesn't exist", vm.WatchFolders[0].Problem);

        vm.WatchFolders[0].Path = _dir;
        Assert.Equal("", vm.WatchFolders[0].Problem);
        vm.WatchFolders[0].Path = "";
        Assert.Contains("no folder", vm.WatchFolders[0].Problem);
    }

    [Fact]
    public void CreateWatchFolderMakesTheDirectoryAndClearsTheNote()
    {
        var missing = Path.Combine(_dir, "new", "deep");
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "W", Path = missing } },
        }, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[0];
        Assert.Contains("doesn't exist", vm.SelectedWatch.Problem);

        vm.CreateWatchFolderCommand.Execute(null);
        Assert.True(Directory.Exists(missing));
        Assert.Equal("", vm.SelectedWatch.Problem);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public void OpenFolderOnAMissingPathWarnsInsteadOfThrowing()
    {
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = Path.Combine(_dir, "nope") } },
        }, _dialogs);
        vm.SelectedRoute = vm.Routes[0];
        vm.OpenRouteFolderCommand.Execute(null);
        Assert.Single(_dialogs.Warnings);
    }

    [Fact]
    public void DuplicateRouteCopiesEverythingButTheHotkey()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            Routes =
            {
                new Route
                {
                    Label = "Invoices", Path = _dir, Hotkey = "Ctrl+5",
                    Suffix = "_INV", AppendSuffix = true, Color = "#2e7d32",
                    NamingMode = "replace",
                },
                new Route { Label = "Other", Path = _dir },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedRoute = vm.Routes[0];
        vm.DuplicateRouteCommand.Execute(null);

        Assert.Equal(3, vm.Routes.Count);
        var copy = vm.Routes[1];               // inserted right after the original
        Assert.Same(copy, vm.SelectedRoute);   // and selected for tweaking
        Assert.Equal("Invoices copy", copy.Label);
        Assert.Equal(_dir, copy.Path);
        Assert.Equal("_INV", copy.Suffix);
        Assert.True(copy.AppendSuffix);
        Assert.Equal("#2e7d32", copy.Color);
        Assert.Equal("replace", copy.NamingMode);
        Assert.Equal("", copy.Hotkey);         // a copied hotkey would collide
        Assert.Equal("Ctrl+2", copy.GestureText);   // fallback for its slot
    }

    [Fact]
    public void GestureTextTracksReordering()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            Routes =
            {
                new Route { Label = "A", Path = _dir },
                new Route { Label = "B", Path = _dir },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("Ctrl+1", vm.Routes[0].GestureText);
        Assert.Equal("Ctrl+2", vm.Routes[1].GestureText);

        vm.Routes.Move(1, 0);   // same path drag-drop reordering uses
        Assert.Equal("B", vm.Routes[0].Label);
        Assert.Equal("Ctrl+1", vm.Routes[0].GestureText);
        Assert.Equal("Ctrl+2", vm.Routes[1].GestureText);
    }

    [Fact]
    public void FiletypeCheckboxesReadAndWriteTheConfigString()
    {
        var w = new WatchEditVm { Filetypes = "pdf, tif" };
        Assert.False(w.AnyType);
        Assert.True(w.TypePdf);
        Assert.True(w.TypeTiff);    // "tif" alone lights the TIFF group
        Assert.False(w.TypeJpeg);
        Assert.Equal("", w.OtherTypes);

        w.TypeJpeg = true;          // group adds both extensions
        Assert.Equal("pdf, tif, jpg, jpeg", w.Filetypes);

        w.TypePdf = false;
        Assert.Equal("tif, jpg, jpeg", w.Filetypes);
    }

    [Fact]
    public void AnyTypeClearsAndUncheckingItDefaultsToPdf()
    {
        var w = new WatchEditVm { Filetypes = "pdf, png" };
        w.AnyType = true;
        Assert.Equal("", w.Filetypes);
        Assert.True(w.AnyType);

        w.AnyType = false;
        Assert.Equal("pdf", w.Filetypes);
    }

    [Fact]
    public void OtherTypesMergeWithoutTouchingTheCheckboxGroups()
    {
        var w = new WatchEditVm { Filetypes = "pdf, docx" };
        Assert.Equal("docx", w.OtherTypes);   // hand-edited config keeps working
        Assert.True(w.TypePdf);

        w.OtherTypes = "xps, docx";
        Assert.Equal("pdf, docx, xps", w.Filetypes);
        Assert.True(w.TypePdf);

        w.TypeTiff = true;                    // toggling a group keeps others
        Assert.Equal("pdf, tif, tiff, docx, xps", w.Filetypes);
        Assert.Equal("docx, xps", w.OtherTypes);
    }

    [Fact]
    public void TilePreviewShowsTheRealFolderState()
    {
        var watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(watched, "URGENT-fax.pdf"), "x");

        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            AlertTexts = { "URGENT" },
            WatchFolders =
            {
                new WatchFolder { Label = "Failed", Path = watched, Color = "#1565c0" },
            },
        }, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[0];

        Assert.True(vm.TilePreviewVisible);
        Assert.Equal("Failed", vm.TilePreviewLabel);
        Assert.Equal("2 ⚠", vm.TilePreviewCount);
        Assert.Equal(OrdoSort.Wpf.Theme.ThemePalette.Light.Danger, vm.TilePreviewBack);
        Assert.Contains("alerting right now", vm.TilePreviewHint);

        // clearing the alert terms live drops the alert state and the color
        vm.AlertTextsText = "";
        Assert.Equal("2", vm.TilePreviewCount);
        Assert.Equal(new OrdoSort.Wpf.Theme.Rgb(21, 101, 192), vm.TilePreviewBack);
        Assert.Equal("", vm.TilePreviewHint);
    }

    [Fact]
    public void TilePreviewExplainsEmptyAndMissingFolders()
    {
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "W", Path = Path.Combine(_dir, "gone") } },
        }, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[0];
        Assert.Equal("⚠", vm.TilePreviewCount);
        Assert.Contains("not available", vm.TilePreviewHint);

        vm.SelectedWatch.Path = _dir;   // exists, empty of matching files? _dir has dirs only
        Assert.Contains("only appears", vm.TilePreviewHint);

        vm.SelectedWatch = null;
        Assert.False(vm.TilePreviewVisible);
    }

    [Fact]
    public void ThemeModeRoundTripsThroughTheRadiosIntoTheResult()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.True(vm.ThemeAuto);

        vm.ThemeDark = true;
        Assert.True(vm.ThemeDark);
        Assert.False(vm.ThemeAuto);
        Assert.True(vm.TryBuildResult());
        Assert.Equal("dark", vm.Result!.Theme);

        var vm2 = new SettingsViewModel(vm.Result, _dialogs);
        Assert.True(vm2.ThemeDark);
    }

    [Fact]
    public void AlertTermsParseFromLinesAndCommas()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs)
        {
            AlertTextsText = "URGENT\nSTAT, callback\n\n",
        };
        Assert.True(vm.TryBuildResult());
        Assert.Equal(new[] { "URGENT", "STAT", "callback" }, vm.Result!.AlertTexts);
    }

    [Fact]
    public void ResultSurvivesAConfigRoundTripOnDisk()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs)
        {
            UiFontFamily = "Verdana",
            UiFontSizeText = "16",
            WordSeparator = "-",
        };
        Assert.True(vm.TryBuildResult());
        var path = Path.Combine(_dir, "saved.json");
        Config.Save(vm.Result!, path);
        var back = Config.Load(path);
        Assert.Equal("Verdana", back.UiFontFamily);
        Assert.Equal(16, back.UiFontSize);
        Assert.Equal("-", back.WordSeparator);
    }

    [Fact]
    public void DataFilePathsRoundTripThroughSettings()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","destinations_file":"shared/dests.json"}""");
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("shared/dests.json", vm.DestinationsFile);
        vm.AlertsFile = "team-alerts.json";

        Assert.True(vm.TryBuildResult());
        var built = vm.Result!;
        Assert.Equal("shared/dests.json", built.DestinationsFile);
        Assert.Equal("team-alerts.json", built.AlertsFile);
        Assert.Equal("monitored-folders.json", built.MonitoredFoldersFile); // untouched default
    }

    [Fact]
    public void SettingsSavePreservesAlertsFileExtras()
    {
        // Settings-OK JSON-clones the original config to build the result;
        // the four XxxFileExtras dictionaries are [JsonIgnore] (they belong
        // to the side-file doc types, not config.json's own shape) and so
        // don't survive that clone by construction — they have to be carried
        // through by hand, or every Settings OK erases hand-added keys from
        // the side files.
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);   // creates alerts.json etc.
        var alertsPath = Path.Combine(_dir, "alerts.json");
        File.WriteAllText(alertsPath,
            """{"alert_texts":[],"hand_added_key":"keep me"}""");

        var cfg = Config.Load(cfgPath);
        Assert.True(cfg.AlertsFileExtras.ContainsKey("hand_added_key"));

        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        Assert.True(vm.TryBuildResult());
        Config.Save(vm.Result!, cfgPath);

        var onDisk = File.ReadAllText(alertsPath);
        Assert.Contains("hand_added_key", onDisk);
        Assert.Contains("keep me", onDisk);
    }

    [Fact]
    public void RepointingADestinationsFileAtAnExistingFileAdoptsItInstead()
    {
        // the spec: re-pointing a section path at an EXISTING file means
        // that file becomes the truth — not the editor's in-memory list,
        // which may just be whatever this window happened to load with
        var cfgPath = Path.Combine(_dir, "config.json");
        var original = new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = _dir } },
        };
        Config.Save(original, cfgPath);
        var cfg = Config.Load(cfgPath);   // now backed by destinations.json, Routes == [A]

        var sharedDir = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(sharedDir);
        File.WriteAllText(Path.Combine(sharedDir, "team.json"),
            """{"routes":[{"label":"TEAM","path":"C:/team"}],"team_key":1}""");

        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        vm.DestinationsFile = "shared/team.json";

        Assert.True(vm.TryBuildResult());
        var built = vm.Result!;
        Assert.Equal(new[] { "TEAM" }, built.Routes.Select(r => r.Label));
        Assert.Equal("shared/team.json", built.DestinationsFile);
        Assert.True(built.DestinationsFileExtras.ContainsKey("team_key"));
    }

    [Fact]
    public void RepointingADestinationsFileAtABrokenExistingFileKeepsTheBuiltRoutes()
    {
        // a target that exists but fails to parse must not throw out of
        // TryBuildResult — the built (editor) values are kept, and Save is
        // left to surface the broken file rather than blocking OK on it
        var cfgPath = Path.Combine(_dir, "config.json");
        var original = new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = _dir } },
        };
        Config.Save(original, cfgPath);
        var cfg = Config.Load(cfgPath);

        var sharedDir = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(sharedDir);
        File.WriteAllText(Path.Combine(sharedDir, "broken.json"), "{ not json");

        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        vm.DestinationsFile = "shared/broken.json";

        Assert.True(vm.TryBuildResult());
        Assert.Equal(new[] { "A" }, vm.Result!.Routes.Select(r => r.Label));
    }

    [Fact]
    public void RepointingToTheSamePhysicalFileByAbsolutePathKeepsInSessionEdits()
    {
        // Regression: a file dialog hands back an ABSOLUTE path, while the
        // stored default is relative. Pointing DestinationsFile at the same
        // physical file spelled the other way must not read as "changed" —
        // comparing raw strings did, and silently replaced a route just
        // added in this editing session with the stale on-disk list.
        var cfgPath = Path.Combine(_dir, "config.json");
        var original = new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = _dir } },
        };
        Config.Save(original, cfgPath);
        var cfg = Config.Load(cfgPath);   // DestinationsFile == "destinations.json" (relative)

        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        vm.AddRouteCommand.Execute(null);          // an in-session edit: route B
        vm.SelectedRoute!.Label = "B";
        vm.SelectedRoute!.Path = _dir;

        var sameFileAbsolute = Path.Combine(_dir, "destinations.json");
        vm.DestinationsFile = sameFileAbsolute;    // same physical file, different spelling

        Assert.True(vm.TryBuildResult());
        Assert.Equal(new[] { "A", "B" }, vm.Result!.Routes.Select(r => r.Label));
    }

    [Fact]
    public void SettingsSaveNeverRewritesBoxLabels()
    {
        // Arrange a real temp config dir with a box-labels file holding a counter
        var dir = Directory.CreateTempSubdirectory("ordoset_").FullName;
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            Config.Save(new Config(), cfgPath);
            BoxLabelStore.Mutate(Path.Combine(dir, "box-labels.json"), d =>
                { d.LabelClients.Add(new LabelClient { Id = "ACME", NextNumber = 42 }); return 0; });

            var cfg = Config.Load(cfgPath);
            cfg.LabelClients = new();              // settings-era stale view

            // Config.Save AND Config.TrySave both carry the bootstrap-only
            // guard independently (TrySave is the one the app actually calls
            // from ApplySettings/SaveConfigNow) — both need pinning here.
            Config.Save(cfg, cfgPath);
            Assert.Equal(42, BoxLabelStore.Read(Path.Combine(dir, "box-labels.json"))
                .LabelClients.Single().NextNumber);

            Assert.True(Config.TrySave(cfg, cfgPath, out var error));
            Assert.Equal("", error);
            Assert.Equal(42, BoxLabelStore.Read(Path.Combine(dir, "box-labels.json"))
                .LabelClients.Single().NextNumber);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DataFileNotesSurfaceLiveState()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);   // writes destinations.json etc. with 0 entries
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        Assert.Equal("0 entries", vm.DestinationsFileNote);

        vm.DestinationsFile = "";
        Assert.Equal("blank = the default beside config.json", vm.DestinationsFileNote);

        vm.DestinationsFile = "missing-dests.json";
        Assert.Contains("will be created on save", vm.DestinationsFileNote);
    }
}

public class ApplySettingsTests
{
    [Fact]
    public void FreshConfigForSettingsRereadsSharedSideFilesFromDisk()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json + section files now exist on disk

        // simulate an admin hand-editing the shared alerts file while the app runs
        File.WriteAllText(Path.Combine(fx.Dir, "alerts.json"), """{"alert_texts": ["ADMIN-EDIT"]}""");

        var fresh = fx.Shell.FreshConfigForSettings();
        Assert.Contains("ADMIN-EDIT", fresh.AlertTexts);
    }

    [Fact]
    public void ChangedDbPathReopensHistoryWithFreshBackupDir()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var newDbDir = Path.Combine(fx.Dir, "elsewhere");
        Directory.CreateDirectory(newDbDir);
        var newDb = Path.Combine(newDbDir, "audit.sqlite");

        var clone = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        clone.HistoryDb = newDb;
        fx.Shell.ApplySettings(clone);

        Assert.Equal(newDb, fx.Shell.History.Path);
        Assert.True(File.Exists(newDb));
        Assert.Equal(OrdoSort.Wpf.ViewModels.Screen.Ready, fx.Shell.Screen);
    }

    [Fact]
    public void WordSeparatorTakesEffectImmediately()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();

        var clone = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        clone.WordSeparator = "-";
        fx.Shell.ApplySettings(clone);

        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        Assert.Equal("SMITH-JOHN", fx.Shell.TypedName);
    }

    [Fact]
    public void ToolStateSavesRefreshSharedSectionsFromDiskFirst()
    {
        // A tool-state save (here: Match & merge remembering its header
        // mapping) runs a full TrySave, which rewrites all three
        // Settings-owned side files from _cfg. _cfg is whatever this run
        // started with, so without refreshing from disk first, this save
        // would silently revert an admin's intervening hand-edit to the
        // shared alerts file.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json + section files now exist on disk

        File.WriteAllText(Path.Combine(fx.Dir, "alerts.json"),
            """{"alert_texts": ["ADMIN-TERM"]}""");

        fx.Shell.SaveMergeHeaders(new Dictionary<string, string> { ["first"] = "First name" });

        var onDisk = File.ReadAllText(Path.Combine(fx.Dir, "alerts.json"));
        Assert.Contains("ADMIN-TERM", onDisk);
        Assert.Equal("First name", Config.Load(fx.CfgPath).MergeHeaders["first"]); // the save itself still landed
    }
}
