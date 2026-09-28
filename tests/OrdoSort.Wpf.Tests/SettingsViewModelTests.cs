using System.Text.Json;
using System.Threading;
using OrdoSort.Core;
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
    public async Task UnknownKeysAndToolStateSurviveOkByConstruction()
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

        Assert.True(await vm.TryBuildResultAsync());
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
    public async Task DuplicateEffectiveHotkeysBlockOk()
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
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("Ctrl+3", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task FallbackHotkeyCollisionsAreCaughtToo()
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
        Assert.False(await vm.TryBuildResultAsync());
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
    public async Task ReservedHotkeyBlocksOkAndGetsALiveNote()
    {
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Hotkey = "Ctrl+K" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Contains("Set aside", vm.Routes[0].HotkeyNote);   // live, before OK
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("Set aside", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task BareKeyHotkeyBlocksOkWithAModifierHint()
    {
        // "K" parses but WPF can't gesture it — before this check it silently
        // fell back to the slot default and the typed key did nothing
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Hotkey = "K" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Contains("modifier", vm.Routes[0].HotkeyNote);
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("Ctrl+K", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task NumpadAndTopRowDigitsCountAsTheSameHotkey()
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
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("both answer to Ctrl+1", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task DuplicateLabelsUnparseableHotkeyAndBadColorBlockOk()
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
        Assert.False(await vm.TryBuildResultAsync());
        var msg = Assert.Single(_dialogs.Warnings).Message;
        Assert.Contains("both called", msg);
        Assert.Contains("hotkey", msg);
        Assert.Contains("not a color", msg);
    }

    [Fact]
    public async Task BadFontSizeBlocksOk()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs) { UiFontSizeText = "5" };
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("6 to 72", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task SeparatorWithSpaceBlocksOk()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs) { WordSeparator = " - " };
        Assert.False(await vm.TryBuildResultAsync());
    }

    [Fact]
    public async Task PollIntervalLoadsSavesAndValidates()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir, PollSeconds = 30 }, _dialogs);
        Assert.Equal("30", vm.PollSecondsText);

        vm.PollSecondsText = "5";
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal(5, vm.Result!.PollSeconds);

        vm.PollSecondsText = "2";                       // below the floor
        Assert.False(await vm.TryBuildResultAsync());
        Assert.Contains("5 to 600", Assert.Single(_dialogs.Warnings).Message);
    }

    [Fact]
    public async Task UnreachableRouteIsAWarningNotAnError()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "A", Path = Path.Combine(_dir, "missing") } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);

        _dialogs.ConfirmAnswer = false;   // decline "Save anyway?"
        Assert.False(await vm.TryBuildResultAsync());

        _dialogs.ConfirmAnswer = true;
        Assert.True(await vm.TryBuildResultAsync());
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task SavedPasswordsPassThroughUntouchedSettingsNoLongerOwnsThem()
    {
        // the saved-passwords editor moved to the Unlock window's Manage
        // saved… dialog — Settings must leave config.json's saved_passwords
        // exactly as it found them, protected or not, added or not
        var cfg = new Config
        {
            Inbox = _dir,
            SavedPasswords = { new SavedPassword { Label = "Old", Password = "plain" } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.True(await vm.TryBuildResultAsync());
        var saved = Assert.Single(vm.Result!.SavedPasswords);
        Assert.Equal("Old", saved.Label);
        Assert.Equal("plain", saved.Password);   // untouched — not upgraded to protected
    }

    [Fact]
    public void FilingExampleTracksModeCaseAndSeparator()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.Contains("20240115-SMITH JOHN-12345.pdf", vm.FilingExample);

        vm.WordSeparator = "-";
        Assert.Contains("20240115-SMITH-JOHN-12345.pdf", vm.FilingExample);

        vm.ModeReplace = true;
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
    public void FilingExampleUsesAMarkerFreeSampleForPrefixAndAppend()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);

        vm.ModePrefix = true;
        Assert.Contains("scan001", vm.FilingExample);
        Assert.DoesNotContain("--", vm.FilingExample);

        vm.ModeInsert = true;
        Assert.Contains("20240115", vm.FilingExample);
    }

    [Fact]
    public async Task FourFilingModesRoundTrip()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","naming_mode":"append","enter_commits":true}""");
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.True(vm.ModeAppend);
        Assert.True(vm.EnterCommits);

        vm.ModePrefix = true;
        vm.EnterCommits = false;   // toggled live — must land in the built config too

        Assert.True(await vm.TryBuildResultAsync());
        var built = vm.Result!;
        Assert.Equal("prefix", built.NamingMode);
        Assert.False(built.EnterCommits);
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

    // ---- RouteFilingExample (Destinations tab live filename preview) —
    // must mirror ShellViewModel.UpdatePreview's real BuildTarget call
    // (route Suffix/AppendSuffix), not silently drop the suffix.

    [Fact]
    public void RouteFilingExampleIncludesTheRoutesSuffixWhenAppended()
    {
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Suffix = "-KYPT", AppendSuffix = true } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedRoute = vm.Routes[0];
        Assert.EndsWith("-KYPT.pdf", vm.RouteFilingExample);
    }

    [Fact]
    public void RouteFilingExampleOmitsTheSuffixWhenAppendSuffixIsOff()
    {
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Suffix = "-KYPT", AppendSuffix = false } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedRoute = vm.Routes[0];
        Assert.DoesNotContain("-KYPT", vm.RouteFilingExample);
    }

    [Fact]
    public void RouteFilingExampleRaisesLiveWhenTheSelectedRoutesSuffixOrAppendSuffixChanges()
    {
        var cfg = new Config
        {
            Routes = { new Route { Label = "A", Path = _dir, Suffix = "-KYPT", AppendSuffix = true } },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedRoute = vm.Routes[0];

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedRoute.Suffix = "-OTHER";
        Assert.Contains(nameof(vm.RouteFilingExample), raised);

        raised.Clear();
        vm.SelectedRoute.AppendSuffix = false;
        Assert.Contains(nameof(vm.RouteFilingExample), raised);
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

    // -------------------------------------- PickSideFile (Browse... for
    // box-labels) — 2026-08-07 audit,
    // Task 1b round 1: this method's own branching, not the shared
    // Config.ResolveBesideForWrite confinement check it calls, is what these
    // tests are proving, exercised through the one side-file path box left
    // (box labels).

    [Fact]
    public void BrowsingToAPathInsideTheConfigDirectoryStoresItAsAPlainFilename()
    {
        // Round 2 fix: this used to assert the DIALOG's absolute answer was
        // stored verbatim. On a deployment that shares one config.json
        // across stations, an absolute path is only ever correct on the
        // station that Browsed it — wrong (and eventually refused-on-write,
        // same bug class as the outside-the-directory case below) on every
        // other one. Stored relative to the config directory instead.
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        var insidePath = Path.Combine(_dir, "picked-labels.json");
        _dialogs.NextOpenFile = insidePath;
        vm.BrowseBoxLabelsFileCommand.Execute(null);

        Assert.Equal("picked-labels.json", vm.BoxLabelsFile);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public void BrowsingToAFileInASubfolderOfTheConfigDirectoryStoresTheSubfolderRelativePath()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        var insidePath = Path.Combine(_dir, "archive", "picked-labels.json");
        _dialogs.NextOpenFile = insidePath;
        vm.BrowseBoxLabelsFileCommand.Execute(null);

        var expected = Path.Combine("archive", "picked-labels.json");
        Assert.Equal(expected, vm.BoxLabelsFile);
        Assert.Empty(_dialogs.Warnings);

        // The whole point of storing it relative: reading it back beside
        // THIS config file resolves to the exact same physical path a save
        // would have written it to — round-trips through a subfolder too,
        // not just a plain filename.
        Assert.Equal(Path.GetFullPath(insidePath),
            Config.ResolveBesideForRead(cfgPath, vm.BoxLabelsFile, "box_labels_file"));
    }

    [Fact]
    public void BrowsingOpensThePickerBesideTheConfigFile()
    {
        // The dialog itself starts in the config directory — so the common
        // case (picking a file that's already sitting beside config.json)
        // needs no relativizing arithmetic to land on a plain filename.
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        _dialogs.NextOpenFile = Path.Combine(_dir, "picked-labels.json");
        vm.BrowseBoxLabelsFileCommand.Execute(null);

        Assert.Equal(Path.GetFullPath(_dir), _dialogs.LastOpenFileInitialDirectory);
    }

    [Fact]
    public void BrowsingToAnAbsolutePathOutsideTheConfigDirectoryIsRejectedAndKeepsTheCurrentValue()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        var before = vm.BoxLabelsFile;   // the default, "box-labels.json"

        var outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ordoset_outside_" + Guid.NewGuid())).FullName;
        try
        {
            _dialogs.NextOpenFile = Path.Combine(outsideDir, "evil-labels.json");
            vm.BrowseBoxLabelsFileCommand.Execute(null);

            Assert.Equal(before, vm.BoxLabelsFile);   // refused: field left unchanged
            var warning = Assert.Single(_dialogs.Warnings);
            Assert.Contains("box_labels_file", warning.Message);
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }

    // -------------------------------------- an already-configured bad value
    // (from a hand edit, or from before this fix existed) is visible in
    // Settings, not just refused silently at the next Save. Fixing the
    // picker above does nothing for a value that got in some OTHER way —
    // these three pin the note side of that, using DIRECT property sets
    // (never BrowseBoxLabelsFileCommand) so the confinement-refused value
    // arrives the same way a hand edit or a pre-fix config would.

    [Fact]
    public void AnAlreadyConfiguredOutsideAbsolutePathShowsAsNeedingAttention()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        var outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ordoset_outside_" + Guid.NewGuid())).FullName;
        try
        {
            // Config.Load happily reads an absolute path outside the config
            // directory (READ keeps an already-working station's data
            // loadable on purpose) — but a Save would refuse it. The note
            // must say so without the user ever touching Save, in the same
            // words the save-time refusal uses.
            vm.BoxLabelsFile = Path.Combine(outsideDir, "evil-labels.json");

            Assert.True(vm.BoxLabelsFileNoteNeedsAttention);
            Assert.Contains("box_labels_file", vm.BoxLabelsFileNote);
            Assert.Contains("must stay beside the config file", vm.BoxLabelsFileNote);
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AnAbsolutePathInsideTheConfigDirectoryIsNotFlaggedSinceASaveWouldSucceed()
    {
        // Guards the other direction: the note must track EXACTLY what
        // ResolveBesideForWrite refuses, not merely "looks like an absolute
        // path" — an absolute-but-inside spelling (a hand edit, or a value
        // from before this fix's picker guard existed) saves just fine, so
        // flagging it would be a false alarm nobody could clear.
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        vm.BoxLabelsFile = Path.Combine(_dir, "box-labels.json");

        Assert.False(vm.BoxLabelsFileNoteNeedsAttention);
    }

    [Fact]
    public void FixingAnOutsidePathBackToARelativeOneClearsTheAttentionFlag()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        var outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ordoset_outside_" + Guid.NewGuid())).FullName;
        try
        {
            vm.BoxLabelsFile = Path.Combine(outsideDir, "evil-labels.json");
            Assert.True(vm.BoxLabelsFileNoteNeedsAttention);

            vm.BoxLabelsFile = "box-labels.json";

            Assert.False(vm.BoxLabelsFileNoteNeedsAttention);
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void BrowsingWithNoConfigPathYetKeepsTheCurrentValueInsteadOfAcceptingTheRawPick()
    {
        // No cfgPath means PickSideFile has nothing to resolve beside, so it
        // can't run the confinement check a real save would — that is NOT
        // license to hand back the picker's raw, unvalidated absolute path.
        // Round 1 fix: this branch used to return the unvalidated `picked`
        // path while the doc comment right above it already claimed the
        // field's current value was kept, same as a cancelled dialog.
        var vm = new SettingsViewModel(new Config(), _dialogs);   // cfgPath left null
        var before = vm.BoxLabelsFile;

        _dialogs.NextOpenFile = @"C:\anywhere\box-labels.json";
        vm.BrowseBoxLabelsFileCommand.Execute(null);

        Assert.Equal(before, vm.BoxLabelsFile);
    }

    [Fact]
    public void PathNotesSurfaceProblemsLive()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs);
        // blank needs no I/O — resolved synchronously, no wait
        Assert.Contains("no inbox folder set", vm.InboxNote);

        vm.Inbox = Path.Combine(_dir, "missing");
        WaitFor(() => vm.InboxNote.Contains("doesn't exist"), "InboxNote should report the missing folder");

        vm.Inbox = _dir;
        WaitFor(() => vm.InboxNote == "", "InboxNote should clear once the real folder resolves");

        // relative-path answers also need no I/O — synchronous
        vm.HistoryDb = "history.sqlite";
        Assert.Contains("relative", vm.HistoryDbNote);
        vm.HistoryDb = Path.Combine(_dir, "new-audit.sqlite");
        WaitFor(() => vm.HistoryDbNote.Contains("new database will be created"),
            "HistoryDbNote should report a new database once the real check resolves");
    }

    // ---- 2026-08 audit finding C2: a relative Inbox/Deferred value must
    // resolve beside config.json (Config.ResolveBeside — the names_file/
    // history_db rule), and the Settings note's own existence probe must
    // check that SAME resolved location, not the raw typed spelling.

    [Fact]
    public void RelativeInboxNoteChecksExistenceBesideTheConfigFileNotTheWorkingDirectory()
    {
        // _dir (a fresh temp guid folder) is never the test process's
        // working directory — asserted here per the "make sure they
        // differ" rule: a fixture where config.json's directory and the
        // working directory coincide would pass whether or not resolution
        // is actually wired to the config file.
        Assert.NotEqual(
            Path.GetFullPath(_dir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar));

        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        var missingResolved = Path.Combine(_dir, "relative-inbox-missing");
        vm.Inbox = "relative-inbox-missing";

        // doesn't exist yet anywhere — the Problem note must name the
        // RESOLVED (beside-config) location, not the raw typed spelling.
        WaitFor(() => vm.InboxNoteNeedsAttention, "InboxNote should flag the missing folder");
        Assert.Contains(missingResolved, vm.InboxNote);

        // a DIFFERENT relative value whose resolved folder is created ONLY
        // beside the config file, never beside the working directory —
        // property setters only re-probe on an actual value change, so this
        // (rather than creating missingResolved after the fact) is what
        // drives a fresh check.
        var presentResolved = Path.Combine(_dir, "relative-inbox-present");
        Directory.CreateDirectory(presentResolved);
        vm.Inbox = "relative-inbox-present";
        // NeedsAttention alone would race the transient neutral "" state
        // Resolve applies before the debounced probe finishes — wait for
        // the settled text instead.
        WaitFor(() => vm.InboxNote.Contains("relative"),
            "InboxNote should report resolved-relative once the folder exists beside the config file");
        Assert.False(vm.InboxNoteNeedsAttention);
    }

    [Fact]
    public void WarningsCheckExistenceAtTheSameResolvedLocationAsTheLiveNote()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config(), cfgPath);
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);
        var resolved = Path.Combine(_dir, "relative-deferred");

        vm.Inbox = _dir;              // real, so only Deferred's warning is under test
        vm.Deferred = "relative-deferred";

        var before = Assert.Single(vm.Warnings(), w => w.Contains("set-aside folder doesn't exist"));
        Assert.Contains(resolved, before);   // names the RESOLVED location

        // the same folder the live note would resolve to — creating it
        // beside config.json (never beside the working directory) must
        // clear the Save-time warning too
        Directory.CreateDirectory(resolved);
        Assert.DoesNotContain(vm.Warnings(), w => w.Contains("set-aside folder doesn't exist"));
    }

    [Fact]
    public void WarningsFlagABlankSetAsideFolderTheSameWayAsABlankInbox()
    {
        // Two lines apart in Warnings() (SettingsViewModel.cs), a blank
        // inbox warned ("No inbox folder is set…") and a blank Deferred
        // warned nothing at all — the `deferred.Length > 0 && …` guard
        // simply skipped the blank case instead of flagging it (QC-02,
        // 2026-08-21 audit). Match the inbox warning's own shape: still a
        // Warning, not a HardError, since a user who never skips doesn't
        // need a blocked OK over an optional field. Since Q2-43 it is asked
        // only when this edit is what blanked it (SettingsOkWarningsTests).
        var cfg = new Config { Inbox = _dir, Deferred = _dir };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.Deferred = "";

        Assert.Contains(vm.Warnings(), w => w.Contains("set-aside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARelativeInboxAndDeferredSurviveSaveWithTheirRawSpellingUnchanged()
    {
        // Config.TrySaveMain's own doc comment: never silently rewrite a
        // relative value to absolute on save — resolution happens only at
        // the point of use, and a shared config.json under other stations
        // must never be touched by resolving THIS station's copy.
        var cfg = new Config { Inbox = _dir };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.Inbox = "relative-inbox";
        vm.Deferred = "relative-deferred";
        _dialogs.ConfirmAnswer = true;   // both are "missing" warnings — save anyway

        Assert.True(await vm.TryBuildResultAsync());

        Assert.Equal("relative-inbox", vm.Result!.Inbox);
        Assert.Equal("relative-deferred", vm.Result.Deferred);
    }

    [Fact]
    public async Task WatchFoldersReorderWithTheCommands()
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
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal(new[] { "B", "A" }, vm.Result!.WatchFolders.Select(w => w.Label));
    }

    [Fact]
    public async Task WatchFolderSectionRoundTripsThroughSettings()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            WatchFolders =
            {
                new WatchFolder { Label = "Failed", Path = _dir, Section = "Failed queues" },
                new WatchFolder { Label = "New", Path = _dir },   // blank section
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("Failed queues", vm.WatchFolders[0].Section);
        Assert.Equal("", vm.WatchFolders[1].Section);

        vm.WatchFolders[1].Section = "Incoming";

        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal(new[] { "Failed queues", "Incoming" },
            vm.Result!.WatchFolders.Select(w => w.Section));
    }

    [Fact]
    public void SectionChoicesListsTheOtherFoldersDistinctSections()
    {
        var cfg = new Config
        {
            Inbox = _dir,
            WatchFolders =
            {
                new WatchFolder { Label = "A", Path = _dir, Section = "Incoming" },
                new WatchFolder { Label = "B", Path = _dir, Section = "incoming" },   // case-dup
                new WatchFolder { Label = "C", Path = _dir },                        // blank
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[2];

        Assert.Equal(new[] { "Incoming" }, vm.SectionChoices);
    }

    [Fact]
    public void WatchFolderProblemNotesSurfaceLive()
    {
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "W", Path = Path.Combine(_dir, "missing") } },
        }, _dialogs);
        // real Directory.Exists checks are debounced and off the UI thread
        // (Task 2) — poll for the eventual result instead of asserting the
        // instant construction/the setter returns.
        WaitFor(() => vm.WatchFolders[0].Problem.Contains("doesn't exist"),
            "a missing watch folder should eventually report the problem");

        vm.WatchFolders[0].Path = _dir;
        WaitFor(() => vm.WatchFolders[0].Problem == "",
            "an existing watch folder should eventually clear the problem");

        // blank needs no I/O — resolved synchronously, no wait
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
        WaitFor(() => vm.SelectedWatch.Problem.Contains("doesn't exist"),
            "a missing watch folder should eventually report the problem");

        vm.CreateWatchFolderCommand.Execute(null);
        Assert.True(Directory.Exists(missing));
        WaitFor(() => vm.SelectedWatch.Problem == "",
            "the note should clear once the folder exists and the re-triggered check resolves");
        Assert.Empty(_dialogs.Warnings);
    }

    // ---- relative destination / monitored-folder paths resolve beside
    // config.json in Settings too, the same place filing and the dashboard
    // look — not against the folder the app happened to start in.

    [Fact]
    public void ARelativeWatchFolderIsCheckedBesideTheConfigFile()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(Path.Combine(_dir, "watch", "failed"));
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "Failed", Path = Path.Combine("watch", "failed") } },
        }, _dialogs, cfgPath: cfgPath);

        WaitFor(() => vm.WatchFolders[0].Problem == "",
            "a relative folder that exists beside config.json must not read as missing");
        Assert.DoesNotContain(vm.Warnings(), w => w.Contains("Failed"));
    }

    [Fact]
    public void AMissingRelativeWatchFolderWarnsWithTheFullPathItWillUse()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            WatchFolders = { new WatchFolder { Label = "Gone", Path = "not-here" } },
        }, _dialogs, cfgPath: cfgPath);

        Assert.Contains(vm.Warnings(), w => w.Contains(Path.Combine(_dir, "not-here")));
    }

    [Fact]
    public void CreateFolderMakesARelativeDestinationOrWatchFolderBesideTheConfigFile()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            Routes = { new Route { Label = "Invoices", Path = Path.Combine("routes", "invoices") } },
            WatchFolders = { new WatchFolder { Label = "Failed", Path = Path.Combine("watch", "failed") } },
        }, _dialogs, cfgPath: cfgPath);
        vm.SelectedRoute = vm.Routes[0];
        vm.SelectedWatch = vm.WatchFolders[0];

        vm.CreateRouteFolderCommand.Execute(null);
        vm.CreateWatchFolderCommand.Execute(null);

        Assert.True(Directory.Exists(Path.Combine(_dir, "routes", "invoices")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "watch", "failed")));
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

        // TilePreviewVisible/Label are cheap and update instantly (Task 1);
        // Count/Back/Hint are status-derived and wait on the debounced,
        // off-thread FolderMonitor.Status probe — poll for them instead of
        // asserting immediately (see TilePreviewProbeTests for the dedicated
        // promptness/selection-guard coverage of that fix).
        Assert.True(vm.TilePreviewVisible);
        Assert.Equal("Failed", vm.TilePreviewLabel);
        WaitFor(() => vm.TilePreviewCount == "2 ⚠", "the tile preview should eventually count both files and flag the alert");
        Assert.Equal(OrdoSort.Wpf.Theme.ThemePalette.Light.Danger, vm.TilePreviewBack);
        Assert.Contains("alerting right now", vm.TilePreviewHint);

        // clearing the alert terms live drops the alert state and the color
        vm.AlertTerms.Clear();
        WaitFor(() => vm.TilePreviewCount == "2", "the tile preview should eventually drop the alert flag once the term is cleared");
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
        WaitFor(() => vm.TilePreviewCount == "⚠", "the tile preview should eventually flag the missing folder");
        Assert.Contains("not available", vm.TilePreviewHint);

        vm.SelectedWatch.Path = _dir;   // exists, empty of matching files? _dir has dirs only
        WaitFor(() => vm.TilePreviewHint.Contains("only appears"),
            "the tile preview should eventually explain an existing-but-empty folder");

        vm.SelectedWatch = null;
        Assert.False(vm.TilePreviewVisible);
    }

    [Fact]
    public async Task ThemeModeRoundTripsThroughTheRadiosIntoTheResult()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.True(vm.ThemeAuto);

        vm.ThemeDark = true;
        Assert.True(vm.ThemeDark);
        Assert.False(vm.ThemeAuto);
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal("dark", vm.Result!.Theme);

        var vm2 = new SettingsViewModel(vm.Result, _dialogs);
        Assert.True(vm2.ThemeDark);
    }

    [Theory]
    [InlineData("auto", true, null)]
    [InlineData("", true, null)]
    [InlineData("bogus-not-a-scheme", true, null)]
    [InlineData("light", false, "light")]
    [InlineData("dark", false, "dark")]
    [InlineData("DARK", false, "dark")]   // FindScheme is case-insensitive
    // Config.Load migrates retired keys before Settings ever sees them; a
    // raw one reaching the dialog anyway is just unknown, so Auto.
    [InlineData("ledger", true, null)]
    public void ThemeSeedingSelectsExactlyOneCard(
        string seed, bool expectAuto, string? expectSchemeKey)
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir, Theme = seed }, _dialogs);

        Assert.Equal(expectAuto, vm.AutoSelected);
        var selected = vm.SchemeOptions.Where(o => o.IsSelected).ToList();
        if (expectSchemeKey is null)
        {
            Assert.Empty(selected);   // Auto card owns the selection instead
        }
        else
        {
            var only = Assert.Single(selected);
            Assert.Equal(expectSchemeKey, only.Key);
        }
    }

    [Fact]
    public void SchemeOptionsMatchesTheRegistryCountAndOrder()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);

        Assert.Equal(
            OrdoSort.Wpf.Theme.ThemePalette.Schemes.Select(s => s.Key),
            vm.SchemeOptions.Select(o => o.Key));
        Assert.Equal(
            OrdoSort.Wpf.Theme.ThemePalette.Schemes.Select(s => s.DisplayName),
            vm.SchemeOptions.Select(o => o.DisplayName));
        Assert.Equal(
            OrdoSort.Wpf.Theme.ThemePalette.Schemes.Select(s => s.IsDark),
            vm.SchemeOptions.Select(o => o.IsDark));
    }

    [Fact]
    public async Task SelectingASchemeOptionDeselectsAutoAndEverySibling()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);
        Assert.True(vm.AutoSelected);

        var light = vm.SchemeOptions.Single(o => o.Key == "light");
        light.IsSelected = true;

        Assert.True(light.IsSelected);
        Assert.False(vm.AutoSelected);
        Assert.All(vm.SchemeOptions.Where(o => !ReferenceEquals(o, light)), o => Assert.False(o.IsSelected));
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal("light", vm.Result!.Theme);

        var dark = vm.SchemeOptions.Single(o => o.Key == "dark");
        dark.IsSelected = true;
        Assert.True(dark.IsSelected);
        Assert.False(light.IsSelected);
        Assert.False(vm.AutoSelected);

        vm.AutoSelected = true;
        Assert.True(vm.AutoSelected);
        Assert.All(vm.SchemeOptions, o => Assert.False(o.IsSelected));
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal("auto", vm.Result!.Theme);
    }

    [Fact]
    public async Task AlertChipsSeedAddDedupeRemoveAndRoundTrip()
    {
        var vm = new SettingsViewModel(new Config
        {
            Inbox = _dir,
            AlertTexts = { "URGENT", "FAX" },
        }, _dialogs);
        Assert.Equal(new[] { "URGENT", "FAX" }, vm.AlertTerms);   // seeded in order

        vm.NewAlertText = "  legal  ";
        vm.AddAlertCommand.Execute(null);
        Assert.Equal("", vm.NewAlertText);
        Assert.Equal(new[] { "URGENT", "FAX", "legal" }, vm.AlertTerms);   // trimmed on add

        vm.NewAlertText = "urgent";               // case-dup
        vm.AddAlertCommand.Execute(null);
        Assert.Equal(3, vm.AlertTerms.Count);     // no-op, box cleared
        Assert.Equal("", vm.NewAlertText);

        vm.RemoveAlertCommand.Execute("FAX");
        Assert.True(await vm.TryBuildResultAsync());
        var built = vm.Result!;
        Assert.Equal(new[] { "URGENT", "legal" }, built.AlertTexts);
    }

    [Fact]
    public void AddAlertCommandSplitsOnCommasAndNewlines()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs);

        vm.NewAlertText = "STAT, callback";
        vm.AddAlertCommand.Execute(null);
        Assert.Equal("", vm.NewAlertText);
        Assert.Equal(new[] { "STAT", "callback" }, vm.AlertTerms);

        vm.AlertTerms.Add("URGENT");
        vm.NewAlertText = "urgent, NEW";   // "urgent" is a case-dup, "NEW" is not
        vm.AddAlertCommand.Execute(null);
        Assert.Equal(new[] { "STAT", "callback", "URGENT", "NEW" }, vm.AlertTerms);
    }

    [Fact]
    public async Task ResultSurvivesAConfigRoundTripOnDisk()
    {
        var vm = new SettingsViewModel(new Config { Inbox = _dir }, _dialogs)
        {
            UiFontFamily = "Verdana",
            UiFontSizeText = "16",
            WordSeparator = "-",
        };
        Assert.True(await vm.TryBuildResultAsync());
        var path = Path.Combine(_dir, "saved.json");
        Config.Save(vm.Result!, path);
        var back = Config.Load(path);
        Assert.Equal("Verdana", back.UiFontFamily);
        Assert.Equal(16, back.UiFontSize);
        Assert.Equal("-", back.WordSeparator);
    }

    [Fact]
    public async Task BoxLabelsFilePathRoundTripsThroughSettings()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","box_labels_file":"shared/labels.json"}""");
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.Equal("shared/labels.json", vm.BoxLabelsFile);
        vm.BoxLabelsFile = "team-labels.json";

        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal("team-labels.json", vm.Result!.BoxLabelsFile);
    }

    [Fact]
    public async Task SettingsSaveWritesDestinationsMonitoredFoldersAndAlertsIntoConfigJson()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        Config.Save(new Config { Inbox = _dir }, cfgPath);
        var vm = new SettingsViewModel(Config.Load(cfgPath), _dialogs, cfgPath: cfgPath);
        vm.AddRouteCommand.Execute(null);
        vm.SelectedRoute!.Label = "Invoices";
        // its own folder: a destination that is the inbox is refused (Q2-03)
        vm.SelectedRoute!.Path = Directory.CreateDirectory(Path.Combine(_dir, "invoices")).FullName;
        vm.AlertTerms.Add("URGENT");

        Assert.True(await vm.TryBuildResultAsync());
        Config.Save(vm.Result!, cfgPath);

        var onDisk = File.ReadAllText(cfgPath);
        Assert.Contains("Invoices", onDisk);
        Assert.Contains("URGENT", onDisk);
        Assert.False(File.Exists(Path.Combine(_dir, "destinations.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "alerts.json")));
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
        Config.Save(new Config(), cfgPath);   // writes box-labels.json with 0 entries
        var cfg = Config.Load(cfgPath);
        var vm = new SettingsViewModel(cfg, _dialogs, cfgPath: cfgPath);

        // Config.ReadDoc is a real file read, debounced and off the UI
        // thread (Task 2) — even the just-loaded initial value needs a poll.
        WaitFor(() => vm.BoxLabelsFileNote == "0 entries",
            "the freshly-saved box-labels.json should read back as 0 entries");

        // blank needs no I/O — resolved synchronously, no wait
        vm.BoxLabelsFile = "";
        Assert.Equal("blank = the default beside config.json", vm.BoxLabelsFileNote);

        vm.BoxLabelsFile = "missing-labels.json";
        WaitFor(() => vm.BoxLabelsFileNote.Contains("will be created on save"),
            "a missing box-labels file should eventually report it'll be created");
    }

    // ---- Dashboard tab rework: grouped folder list as section manager ----

    private static Config WatchCfg(params (string Label, string Section)[] folders)
    {
        var cfg = new Config();
        foreach (var (label, section) in folders)
            cfg.WatchFolders.Add(new WatchFolder { Label = label, Path = "C:/x", Section = section });
        return cfg;
    }

    [Fact]
    public void WatchRowsGroupInFirstSeenOrderWithFoldersUnderTheirHeaders()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", ""), ("C", "night ")), _dialogs);

        // first-seen order: "Night" (from A), then the default group (from B);
        // C's "night " folds into "Night" case-insensitively, first-seen casing wins
        var rows = vm.WatchRows.ToList();
        Assert.Equal(5, rows.Count);
        var h0 = Assert.IsType<WatchSectionVm>(rows[0]);
        Assert.Equal("Night", h0.Header);
        Assert.False(h0.IsDefault);
        Assert.Equal("A", Assert.IsType<WatchEditVm>(rows[1]).Label);
        Assert.Equal("C", Assert.IsType<WatchEditVm>(rows[2]).Label);
        var h1 = Assert.IsType<WatchSectionVm>(rows[3]);
        Assert.True(h1.IsDefault);
        Assert.Equal("B", Assert.IsType<WatchEditVm>(rows[4]).Label);
    }

    [Fact]
    public void TheDefaultGroupAlwaysExistsAndPinsFirstWhenEmpty()
    {
        var cfg = WatchCfg(("A", "Night"));
        cfg.MonitorTitle = "Monitored folders";
        var vm = new SettingsViewModel(cfg, _dialogs);

        var h = Assert.IsType<WatchSectionVm>(vm.WatchRows[0]);
        Assert.True(h.IsDefault);
        Assert.Equal("Monitored folders", h.Header);
    }

    [Fact]
    public void RenameRewritesEveryMemberAndOnlyMembers()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day"), ("C", "night")), _dialogs);

        var h = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.Header == "Night");
        vm.BeginSectionRename(h);
        Assert.Equal("Night", h.EditText);
        h.EditText = "  Overnight  ";
        vm.CommitSectionRename(h);

        Assert.Equal("Overnight", vm.WatchFolders[0].Section);
        Assert.Equal("Day", vm.WatchFolders[1].Section);
        Assert.Equal("Overnight", vm.WatchFolders[2].Section);
        Assert.Contains(vm.WatchRows.OfType<WatchSectionVm>(), x => x.Header == "Overnight");
    }

    [Fact]
    public void RenameOntoAnExistingSectionMergesTheGroups()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day")), _dialogs);

        var h = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.Header == "Night");
        vm.BeginSectionRename(h);
        h.EditText = "day";
        vm.CommitSectionRename(h);

        // one merged group; A's section is the typed text, folded with B's by case
        var named = vm.WatchRows.OfType<WatchSectionVm>().Where(x => !x.IsDefault).ToList();
        Assert.Single(named);
        Assert.Equal("day", vm.WatchFolders[0].Section);
    }

    [Fact]
    public void RenameToBlankMovesTheGroupIntoTheDefault()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night")), _dialogs);

        var h = vm.WatchRows.OfType<WatchSectionVm>().Single(x => !x.IsDefault);
        vm.BeginSectionRename(h);
        h.EditText = "   ";
        vm.CommitSectionRename(h);

        Assert.Equal("", vm.WatchFolders[0].Section);
        Assert.DoesNotContain(vm.WatchRows.OfType<WatchSectionVm>(), x => !x.IsDefault);
    }

    [Fact]
    public async Task RenamingTheDefaultHeaderEditsMonitorTitle()
    {
        var cfg = WatchCfg(("A", ""));
        cfg.MonitorTitle = "Monitored folders";
        var vm = new SettingsViewModel(cfg, _dialogs);

        var h = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.BeginSectionRename(h);
        Assert.Equal("Monitored folders", h.EditText);
        h.EditText = "Work queues";
        vm.CommitSectionRename(h);

        Assert.Equal("Work queues", vm.MonitorTitle);
        Assert.Equal("", vm.WatchFolders[0].Section);   // members untouched
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal("Work queues", vm.Result!.MonitorTitle);
    }

    [Fact]
    public void DropOnAFolderAdoptsItsSectionAndPosition()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day"), ("C", "Day")), _dialogs);

        vm.DropWatch(vm.WatchFolders[0], vm.WatchFolders[2]);   // A onto C

        Assert.Equal("Day", vm.WatchFolders.Single(w => w.Label == "A").Section);
        Assert.Equal(new[] { "B", "C", "A" },
            vm.WatchFolders.Select(w => w.Label).ToArray());
        var selected = vm.SelectedWatch;
        Assert.NotNull(selected);
        Assert.Equal("A", selected.Label);
    }

    // A drag starts with a click, so the dragged row is ALREADY selected:
    // re-selecting it after the drop is a no-op Set, and Up/Down were never
    // told to re-query — drag the first row to the bottom and Up stayed
    // greyed out. A WPF button only re-reads CanExecute on
    // CanExecuteChanged, so that event is the behaviour under test.

    [Fact]
    public void DraggingTheSelectedRouteTellsUpAndDownToRequery()
    {
        var cfg = new Config
        {
            Routes =
            {
                new Route { Label = "A", Path = _dir },
                new Route { Label = "B", Path = _dir },
                new Route { Label = "C", Path = _dir },
            },
        };
        var vm = new SettingsViewModel(cfg, _dialogs);
        var a = vm.Routes[0];
        vm.SelectedRoute = a;
        Assert.False(vm.RouteUpCommand.CanExecute(null));
        var upRequeried = false;
        var downRequeried = false;
        vm.RouteUpCommand.CanExecuteChanged += (_, _) => upRequeried = true;
        vm.RouteDownCommand.CanExecuteChanged += (_, _) => downRequeried = true;

        vm.DropRoute(a, over: null);   // dropped below the last row

        Assert.Equal(new[] { "B", "C", "A" }, vm.Routes.Select(r => r.Label).ToArray());
        Assert.Same(a, vm.SelectedRoute);
        Assert.True(upRequeried);
        Assert.True(downRequeried);
        Assert.True(vm.RouteUpCommand.CanExecute(null));
        Assert.False(vm.RouteDownCommand.CanExecute(null));
    }

    [Fact]
    public void DraggingTheSelectedFolderTellsUpAndDownToRequery()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Day"), ("B", "Day"), ("C", "Day")), _dialogs);
        var a = vm.WatchFolders[0];
        vm.SelectedWatch = a;
        var upRequeried = false;
        var downRequeried = false;
        vm.WatchUpCommand.CanExecuteChanged += (_, _) => upRequeried = true;
        vm.WatchDownCommand.CanExecuteChanged += (_, _) => downRequeried = true;

        vm.DropWatch(a, vm.WatchFolders[2]);   // A onto C

        Assert.Equal(new[] { "B", "C", "A" }, vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.True(upRequeried);
        Assert.True(downRequeried);
        Assert.True(vm.WatchUpCommand.CanExecute(null));
    }

    [Fact]
    public void DropOnAHeaderJoinsThatGroupAndTheDefaultHeaderClearsTheSection()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "")), _dialogs);

        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.DropWatch(vm.WatchFolders[0], def);   // A into the default group

        Assert.Equal("", vm.WatchFolders.Single(w => w.Label == "A").Section);

        // FIX 2026-08-07 (section-dropdown Task 1, session-sticky sections):
        // this used to assert `night is null` ("Night emptied out, so its
        // header is gone") — that WAS the reported bug's other half (moving
        // the last folder out of a section erased it with no way back). An
        // emptied section now stays for the rest of the Settings session,
        // offered again so a folder can be moved back into it without
        // retyping the name.
        var night = vm.WatchRows.OfType<WatchSectionVm>().SingleOrDefault(x => !x.IsDefault);
        Assert.NotNull(night);
        Assert.Equal("Night", night!.Header);
        Assert.DoesNotContain(vm.WatchFolders, w => w.Section == "Night");
        Assert.Contains("Night", vm.SectionChoices);
    }

    [Fact]
    public void TypingANewSectionOnTheSelectedFolderCreatesItsGroupLive()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "")), _dialogs);

        vm.WatchFolders[0].Section = "Fresh";

        Assert.Contains(vm.WatchRows.OfType<WatchSectionVm>(),
            x => !x.IsDefault && x.Header == "Fresh");
        // the default group stays visible even though it emptied
        Assert.Contains(vm.WatchRows.OfType<WatchSectionVm>(), x => x.IsDefault);
    }

    [Fact]
    public void SelectingAHeaderRowBouncesBackToTheFolder()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night")), _dialogs);
        var folder = vm.WatchFolders[0];
        vm.SelectedWatch = folder;

        vm.SelectedWatchRow = vm.WatchRows.OfType<WatchSectionVm>().First();

        Assert.Same(folder, vm.SelectedWatch);
        Assert.Same(folder, vm.SelectedWatchRow);
    }

    // ---- contextual creation (per-header ＋ / Add folder / Add section) ----

    [Fact]
    public void PerHeaderAddCreatesTheFolderInsideThatSection()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Night"), ("C", "Day")), _dialogs);

        var night = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.Header == "Night");
        vm.AddFolderToSection(night);

        Assert.Equal(new[] { "A", "B", "New folder", "C" },
            vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.Equal("Night", vm.WatchFolders[2].Section);
        Assert.Equal("New folder", vm.SelectedWatch!.Label);
    }

    [Fact]
    public void PerHeaderAddOnTheEmptyDefaultGroupClearsTheSection()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night")), _dialogs);

        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.AddFolderToSection(def);

        Assert.Equal("", vm.SelectedWatch!.Section);
        Assert.Equal(2, vm.WatchFolders.Count);
    }

    [Fact]
    public void AddFolderInheritsTheSelectedFoldersSectionAndLandsAfterIt()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day")), _dialogs);
        vm.SelectedWatch = vm.WatchFolders[0];

        vm.AddWatchCommand.Execute(null);

        Assert.Equal(new[] { "A", "New folder", "B" },
            vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.Equal("Night", vm.WatchFolders[1].Section);
        Assert.Equal("New folder", vm.SelectedWatch!.Label);
    }

    [Fact]
    public void AddFolderWithNothingSelectedAppendsIntoTheDefaultGroup()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs);

        vm.AddWatchCommand.Execute(null);

        Assert.Equal("", Assert.Single(vm.WatchFolders).Section);
        Assert.NotNull(vm.SelectedWatch);
    }

    [Fact]
    public void AddSectionGeneratesUniqueNamesAndOpensTheHeaderForRename()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "new SECTION")), _dialogs);

        var header = vm.AddSection();

        Assert.NotNull(header);
        Assert.Equal("New section 2", header!.Header);
        Assert.True(header.IsEditing);
        Assert.Equal("New section 2", header.EditText);
        Assert.Equal("New section 2", vm.SelectedWatch!.Section);
        Assert.Equal("New folder", vm.SelectedWatch.Label);
    }

    // ---- removing a section (2026-08-15) --------------------------------
    // Reported as "there is no way to remove sections in the monitored
    // folders tab": headers carried ✎ and ＋ but nothing to delete one, and
    // the toolbar Remove button can only ever target a folder (SelectedWatchRow
    // refuses header rows). Removing a section drops the GROUPING only —
    // its folders keep every setting and land in the default group.

    [Fact]
    public void RemoveSectionKeepsItsFoldersAndMovesThemIntoTheDefault()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day"), ("C", "night")), _dialogs);

        var night = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.Header == "Night");
        vm.RemoveSection(night);

        // nothing deleted — "C" proves members are matched the same
        // case-insensitively as everywhere else, "B" that non-members are untouched
        Assert.Equal(new[] { "A", "B", "C" }, vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.Equal("", vm.WatchFolders.Single(w => w.Label == "A").Section);
        Assert.Equal("", vm.WatchFolders.Single(w => w.Label == "C").Section);
        Assert.Equal("Day", vm.WatchFolders.Single(w => w.Label == "B").Section);
        Assert.DoesNotContain(vm.WatchRows.OfType<WatchSectionVm>(), x => x.Header == "Night");
    }

    /// <summary>The half that made this "no way to remove": an emptied section
    /// is session-sticky, so InsertOrphanedStickyHeaders splices its header
    /// back in on EVERY rebuild. Removing it has to forget the sticky entry
    /// too, or the row simply reappears on the next keystroke.</summary>
    [Fact]
    public void RemovingAnAlreadyEmptiedSectionKeepsItGoneAcrossRebuilds()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night"), ("B", "")), _dialogs);

        // empty it the way a user does — drag its only folder into the
        // default group, which leaves the header behind as a sticky row
        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.DropWatch(vm.WatchFolders.Single(w => w.Label == "A"), def);
        var night = vm.WatchRows.OfType<WatchSectionVm>().Single(x => !x.IsDefault);

        vm.RemoveSection(night);
        vm.AddWatchCommand.Execute(null);   // any rebuild would resurrect a sticky header

        Assert.DoesNotContain(vm.WatchRows.OfType<WatchSectionVm>(), x => !x.IsDefault);
        Assert.DoesNotContain("Night", vm.SectionChoices);
    }

    [Fact]
    public void RemoveSectionDropsItFromTheSectionChoices()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day")), _dialogs);

        var night = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.Header == "Night");
        vm.RemoveSection(night);

        Assert.DoesNotContain("Night", vm.SectionChoices);
        Assert.Contains("Day", vm.SectionChoices);
    }

    /// <summary>The removal is real, not just visual: nothing in the saved
    /// config still carries the section name.</summary>
    [Fact]
    public async Task ARemovedSectionIsGoneFromTheSavedConfig()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night"), ("B", "Night")), _dialogs);

        var night = vm.WatchRows.OfType<WatchSectionVm>().Single(x => !x.IsDefault);
        vm.RemoveSection(night);

        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal(new[] { "A", "B" }, vm.Result!.WatchFolders.Select(w => w.Label).ToArray());
        Assert.All(vm.Result.WatchFolders, w => Assert.Equal("", w.Section));
    }

    /// <summary>Once removed, the name is genuinely free again — SectionKeyExists
    /// no longer sees it, so "Add section" reuses it instead of stepping to
    /// "New section 2" past a section that no longer exists.</summary>
    [Fact]
    public void RemoveSectionFreesItsNameForAddSectionToUseAgain()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs);

        var first = vm.AddSection();
        Assert.Equal("New section", first!.Header);
        vm.RemoveSection(first);

        var second = vm.AddSection();
        Assert.Equal("New section", second!.Header);
    }

    /// <summary>The default group is the implicit "no section" bucket — there
    /// is nowhere to move its folders to, so it has no remove at all (the ✕
    /// is collapsed on its row) and the call is inert if reached anyway.</summary>
    [Fact]
    public void RemovingTheDefaultHeaderIsANoOp()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", ""), ("B", "Night")), _dialogs);

        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.RemoveSection(def);

        Assert.Equal("Monitored folders", vm.MonitorTitle);
        Assert.Equal(new[] { "A", "B" }, vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.Equal("Night", vm.WatchFolders.Single(w => w.Label == "B").Section);
        Assert.Contains(vm.WatchRows.OfType<WatchSectionVm>(), x => x.IsDefault);
    }

    /// <summary>The ✕ tooltip names the group the folders will move to, so it
    /// has to follow a renamed default header — including through the header's
    /// own rename box, which is what edits MonitorTitle.</summary>
    [Fact]
    public void TheRemoveSectionHintFollowsTheDefaultGroupsName()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night")), _dialogs);
        Assert.Equal("Remove this section — its folders move to Monitored folders",
            vm.RemoveSectionHint);

        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.RemoveSectionHint)) raised++; };

        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.BeginSectionRename(def);
        def.EditText = "Work queues";
        vm.CommitSectionRename(def);

        Assert.Equal("Remove this section — its folders move to Work queues", vm.RemoveSectionHint);
        Assert.True(raised > 0, "renaming the default group must notify the ✕ tooltip's binding");
    }

    // ---- Path checks never run on the UI thread, and a stale one never lands.
    // Each test owns both clocks: a manual clock for the debounce and a
    // scheduler that holds background work until the test releases it.

    private static readonly TimeSpan PastTheDebounce = TimeSpan.FromSeconds(1);

    [Fact]
    public void TypingAPathLeavesTheFolderCheckToTheBackgroundScheduler()
    {
        // The check stands in for a stalled SMB round trip: it must never
        // run on the thread that set the path.
        var checks = 0;
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { checks++; return Directory.Exists(p); },
            scheduler: scheduler, time: time);

        vm.Inbox = @"\\unreachable\share\inbox";
        time.Advance(PastTheDebounce);
        Assert.Equal(0, checks);

        scheduler.ReleaseAll();
        Assert.True(checks > 0, "the check should have been waiting on the scheduler");
    }

    [Fact]
    public void TheNoteReflectsTheRealPathStateOnceTheTypingPauses()
    {
        var time = new ManualTimeProvider();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            scheduler: new InlineWorkScheduler(), time: time);

        vm.Inbox = _dir;   // real, existing folder
        time.Advance(PastTheDebounce);
        Assert.Equal("", vm.InboxNote);

        vm.Inbox = Path.Combine(_dir, "does-not-exist");
        time.Advance(PastTheDebounce);
        Assert.Contains("doesn't exist", vm.InboxNote);
    }

    [Fact]
    public void ARoutePathLeavesTheValidateRouteCheckToTheBackgroundScheduler()
    {
        // Config.ValidateRoute creates and deletes a real probe file in the
        // destination folder: the other slow check.
        var checks = 0;
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            validateRoute: r => { checks++; return Config.ValidateRoute(r, configPath: null); },
            scheduler: scheduler, time: time);
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];

        route.Path = _dir;
        time.Advance(PastTheDebounce);
        Assert.Equal(0, checks);

        scheduler.ReleaseAll();
        Assert.True(checks > 0, "the check should have been waiting on the scheduler");
    }

    /// <summary>The probe file is written once per pause in typing, not once
    /// per character typed into the destination box.</summary>
    [Fact]
    public void ValidateRouteProbeRunsOncePerPauseNotPerKeystroke()
    {
        var calls = 0;
        var time = new ManualTimeProvider();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            validateRoute: r => { calls++; return Config.ValidateRoute(r, configPath: null); },
            scheduler: new InlineWorkScheduler(), time: time);
        vm.AddRouteCommand.Execute(null);   // blank Path: checked at once, no I/O
        var route = vm.Routes[0];
        Assert.Equal(0, calls);

        // typing, 100 ms between keystrokes: well inside the 300 ms debounce
        var target = _dir;
        for (var i = 1; i <= target.Length; i++)
        {
            route.Path = target.Substring(0, i);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(0, calls);

        time.Advance(PastTheDebounce);   // the pause
        Assert.Equal(1, calls);
        Assert.Equal("", route.Problem);
    }

    [Fact]
    public void RouteProblemReflectsTheRealValidateRouteResultOnceTheTypingPauses()
    {
        var time = new ManualTimeProvider();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            scheduler: new InlineWorkScheduler(), time: time);
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];

        route.Path = _dir;   // exists and is writable
        time.Advance(PastTheDebounce);
        Assert.Equal("", route.Problem);

        route.Path = Path.Combine(_dir, "missing");
        time.Advance(PastTheDebounce);
        Assert.Contains("does not exist", route.Problem);
    }

    [Fact]
    public void AWatchFolderPathLeavesTheFolderCheckToTheBackgroundScheduler()
    {
        var checks = 0;
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { checks++; return Directory.Exists(p); },
            scheduler: scheduler, time: time);
        vm.AddWatchCommand.Execute(null);
        var w = vm.WatchFolders[0];
        scheduler.ReleaseAll();   // the new row's own first check
        checks = 0;

        w.Path = _dir;
        time.Advance(PastTheDebounce);
        Assert.Equal(0, checks);

        scheduler.ReleaseAll();
        Assert.True(checks > 0, "the check should have been waiting on the scheduler");
    }

    // ---- Clearing a path cancels its in-flight check: otherwise the stale
    // check lands later and overwrites the note for a path the user no
    // longer has.

    [Fact]
    public void ClearingInboxCancelsTheInFlightProbeInsteadOfLettingItOverwriteTheNote()
    {
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs, scheduler: scheduler, time: time);

        vm.Inbox = _dir;                  // a real folder: its check would clear the note
        time.Advance(PastTheDebounce);    // the check is now in flight
        vm.Inbox = "";                    // blank needs no I/O: answered at once
        Assert.Contains("no inbox folder set", vm.InboxNote);

        scheduler.ReleaseAll();           // the stale check finishes
        Assert.Contains("no inbox folder set", vm.InboxNote);
    }

    [Fact]
    public void ClearingARoutePathCancelsTheInFlightValidateRouteProbe()
    {
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs, scheduler: scheduler, time: time);
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];

        route.Path = _dir;
        time.Advance(PastTheDebounce);
        route.Path = "";
        Assert.Equal("no destination path configured", route.Problem);

        scheduler.ReleaseAll();
        Assert.Equal("no destination path configured", route.Problem);
    }

    [Fact]
    public void ClearingAWatchFolderPathCancelsTheInFlightProbe()
    {
        var time = new ManualTimeProvider();
        var scheduler = new ManualWorkScheduler();
        var vm = new SettingsViewModel(new Config(), _dialogs, scheduler: scheduler, time: time);
        vm.AddWatchCommand.Execute(null);
        var w = vm.WatchFolders[0];

        w.Path = _dir;
        time.Advance(PastTheDebounce);
        w.Path = "";
        Assert.Equal("no folder chosen yet", w.Problem);

        scheduler.ReleaseAll();
        Assert.Equal("no folder chosen yet", w.Problem);
    }

    /// <summary>DW-66: dropping a folder on empty space below the list had
    /// no test. It goes to the end, into the last folder's section, which is
    /// where it then shows.</summary>
    [Fact]
    public void DropOnEmptySpaceMovesTheFolderToTheEndOfTheLastSection()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "Day"), ("C", "Day")), _dialogs);
        var a = vm.WatchFolders[0];

        vm.DropWatch(a, over: null);

        Assert.Equal(new[] { "B", "C", "A" }, vm.WatchFolders.Select(w => w.Label).ToArray());
        Assert.Equal("Day", a.Section);
        Assert.Same(a, vm.SelectedWatch);
    }

    /// <summary>DW-66: the only folder dropped on empty space stays put.</summary>
    [Fact]
    public void DropOnEmptySpaceWithNothingElseInTheListLeavesTheFolderAlone()
    {
        var vm = new SettingsViewModel(WatchCfg(("A", "Night")), _dialogs);
        var a = vm.WatchFolders[0];

        vm.DropWatch(a, over: null);

        Assert.Same(a, Assert.Single(vm.WatchFolders));
        Assert.Equal("Night", a.Section);
    }

    /// <summary>DW-68: a section typed with the same name as the default
    /// heading showed as a second header with that same text, though the
    /// dashboard shows the two as one group. Settings now shows them as one
    /// group too.</summary>
    [Fact]
    public void ASectionNamedLikeTheDefaultHeadingIsShownAsTheDefaultGroup()
    {
        var cfg = WatchCfg(("A", ""), ("B", "monitored folders"));
        cfg.MonitorTitle = "Monitored folders";
        var vm = new SettingsViewModel(cfg, _dialogs);

        var header = Assert.Single(vm.WatchRows.OfType<WatchSectionVm>());
        Assert.True(header.IsDefault);
        Assert.Equal(new object[] { header, vm.WatchFolders[0], vm.WatchFolders[1] }, vm.WatchRows.ToArray());
    }

    /// <summary>DW-70: with the default heading renamed to "New section",
    /// Add section named its new section "New section" too: two headings
    /// with one name.</summary>
    [Fact]
    public void AddSectionNeverReusesTheDefaultHeadingsName()
    {
        var cfg = WatchCfg(("A", ""));
        cfg.MonitorTitle = "New section";
        var vm = new SettingsViewModel(cfg, _dialogs);

        var added = vm.AddSection();

        Assert.NotNull(added);
        Assert.NotEqual("New section", added.Header, StringComparer.CurrentCultureIgnoreCase);
        Assert.Equal(2, vm.WatchRows.OfType<WatchSectionVm>().Count());
    }
}

public class ApplySettingsTests
{
    [Fact]
    public async Task FreshConfigForSettingsRereadsTheSharedConfigFromDisk()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        // simulate an admin hand-editing the shared alerts while the app runs
        SetAlertTextsOnDisk(fx.CfgPath, "ADMIN-EDIT");

        var fresh = await fx.Shell.FreshConfigForSettingsAsync();
        Assert.Contains("ADMIN-EDIT", fresh.AlertTexts);
    }

    /// <summary>Q2-20: opening Settings read config.json and its side file
    /// several times and hashed three sections, all on the UI thread before
    /// the window appeared, even if the user then cancelled at once. With the
    /// config on a slow or dead share the dashboard froze on the click. The
    /// reads now run off the UI thread; the window opens once they're back.</summary>
    [Fact]
    public async Task OpeningSettingsReadsTheConfigOffTheUiThread()
    {
        var scheduler = new ControlledWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.Shell.SaveConfigNow();
        SetAlertTextsOnDisk(fx.CfgPath, "ADMIN-EDIT");
        var queuedBefore = scheduler.Queued;

        var opening = fx.Shell.FreshConfigForSettingsAsync();

        Assert.False(opening.IsCompleted);   // nothing read on the click itself
        Assert.Equal(queuedBefore + 1, scheduler.Queued);
        scheduler.ReleaseNewest();
        Assert.Contains("ADMIN-EDIT", (await opening).AlertTexts);
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
    public void HistorySwapFailureLeavesTheOldHistoryUsable()
    {
        // Regression: ApplySettingsAsync used to dispose the old History
        // BEFORE constructing the new one. If `new History(newDb)` throws,
        // _history was left pointing at a disposed connection — autocomplete,
        // CSV export and the History window would then fail silently for the
        // rest of the session, with no message to the user.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();

        // A plain file sits where the new db's directory needs to be
        // created, so History's constructor (Directory.CreateDirectory)
        // throws deterministically.
        var blocker = Path.Combine(fx.Dir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var newDb = Path.Combine(blocker, "sub", "audit.sqlite");

        var clone = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        clone.HistoryDb = newDb;
        fx.Shell.ApplySettings(clone);

        // the shell must still have a USABLE history — not a disposed handle
        var count = fx.Shell.History.ExportCsv(Path.Combine(fx.Dir, "export.csv"));
        Assert.Equal(0, count);

        // and the user must have been told the swap failed
        Assert.NotEmpty(fx.Dialogs.Warnings);
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
        // mapping) runs a full TrySave, which rewrites config.json — alerts,
        // destinations and monitored folders included — from _cfg. _cfg is
        // whatever this run started with, so without refreshing from disk
        // first, this save would silently revert an admin's intervening
        // hand-edit to the shared alerts.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        SetAlertTextsOnDisk(fx.CfgPath, "ADMIN-TERM");

        fx.Shell.SaveMergeHeaders(new Dictionary<string, string> { ["first"] = "First name" });

        var back = Config.Load(fx.CfgPath);
        Assert.Equal(new[] { "ADMIN-TERM" }, back.AlertTexts);
        Assert.Equal("First name", back.MergeHeaders["first"]); // the save itself still landed
    }

    [Fact]
    public void ToolStateSaveStillRefreshesWhileAPeerHoldsBoxLabelsLocked()
    {
        // Review finding: the refresh used to run the full Config.Load, which
        // also opens box-labels.json. A peer mid-print holds that file
        // exclusively, the load failed, the refresh was skipped, and the save
        // wrote this station's stale alerts over the peer's.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        SetAlertTextsOnDisk(fx.CfgPath, "ADMIN-TERM");

        var labels = Path.Combine(fx.Dir, "box-labels.json");
        using (new FileStream(labels, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            fx.Shell.SaveMergeHeaders(new Dictionary<string, string> { ["first"] = "First name" });

        var back = Config.Load(fx.CfgPath);
        Assert.Equal(new[] { "ADMIN-TERM" }, back.AlertTexts);
        Assert.Equal("First name", back.MergeHeaders["first"]);
    }

    [Fact]
    public void ToolStateSaveRefusesAndWarnsWhenTheSharedConfigCannotBeRead()
    {
        // No silent fallback: an unreadable config.json must not be
        // overwritten with this station's stale sections.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        const string broken = """{"inbox":"x","theme":"auto","theme":"dark"}""";
        File.WriteAllText(fx.CfgPath, broken);

        fx.Shell.SaveMergeHeaders(new Dictionary<string, string> { ["first"] = "First name" });

        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("wasn't saved", warning.Message);
        Assert.Equal(broken, File.ReadAllText(fx.CfgPath));   // untouched
    }

    [Fact]
    public async Task OpeningSettingsWithADuplicateKeyInConfigJsonDoesNotCrash()
    {
        // Config.Load accepts a key written twice (last wins); JsonNode
        // throws ArgumentException for it. The snapshot must not escape that.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        var text = File.ReadAllText(fx.CfgPath);
        File.WriteAllText(fx.CfgPath, text.Replace("\"theme\":", "\"theme\": \"auto\", \"theme\":"));

        var fresh = await fx.Shell.FreshConfigForSettingsAsync();

        Assert.NotNull(fresh);
    }

    /// <summary>Another station's edit to the shared alerts, landing in
    /// config.json while this station runs.</summary>
    private static void SetAlertTextsOnDisk(string cfgPath, string term)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        node["alert_texts"] = new System.Text.Json.Nodes.JsonArray(term);
        File.WriteAllText(cfgPath, node.ToJsonString());
    }
}

/// <summary>What a screen reader hears for each row of the monitored-folders
/// list (bound as AutomationProperties.Name in SettingsWindow.xaml).</summary>
public class WatchRowAccessibleNameTests
{
    [Fact]
    public void AFolderRowIsNamedByItsLabelAndFollowsAnEdit()
    {
        using var row = WatchEditVm.From(new WatchFolder { Label = "Failed transfers", Path = "" });
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Failed transfers", row.AccessibleName);
        row.Label = "Rejected";

        Assert.Equal("Rejected", row.AccessibleName);
        Assert.Contains(nameof(WatchEditVm.AccessibleName), raised);
    }

    [Fact]
    public void ASectionHeaderSaysItIsASection() =>
        Assert.Equal("Section: Failed queues",
            new WatchSectionVm { Header = "Failed queues" }.AccessibleName);
}
