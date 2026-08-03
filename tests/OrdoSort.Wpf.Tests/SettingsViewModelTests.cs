using System.Diagnostics;
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

    /// <summary>Live path notes now run their real Directory.Exists/
    /// File.Exists/Config.ReadDoc check debounced and off the UI thread (Task
    /// 2 — see DebouncedProbe), so a fresh construction or edit doesn't
    /// reflect the real answer the instant it returns. Poll for it instead of
    /// asserting immediately — this is what "eventually correct" means for an
    /// async check; the timeout is generous, but a local-disk probe should
    /// land in well under a second.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
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
    public void SavedPasswordsPassThroughUntouchedSettingsNoLongerOwnsThem()
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
        Assert.True(vm.TryBuildResult());
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
    public void FourFilingModesRoundTrip()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","naming_mode":"append","enter_commits":true}""");
        var vm = new SettingsViewModel(cfg, _dialogs);
        Assert.True(vm.ModeAppend);
        Assert.True(vm.EnterCommits);

        vm.ModePrefix = true;
        vm.EnterCommits = false;   // toggled live — must land in the built config too

        Assert.True(vm.TryBuildResult());
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
    public void WatchFolderSectionRoundTripsThroughSettings()
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

        Assert.True(vm.TryBuildResult());
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
        vm.AlertTerms.Clear();
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
    public void AlertChipsSeedAddDedupeRemoveAndRoundTrip()
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
        Assert.True(vm.TryBuildResult());
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

        // Config.ReadDoc is a real file read, debounced and off the UI
        // thread (Task 2) — even the just-loaded initial value needs a poll.
        WaitFor(() => vm.DestinationsFileNote == "0 entries",
            "the freshly-saved destinations.json should read back as 0 entries");

        // blank needs no I/O — resolved synchronously, no wait
        vm.DestinationsFile = "";
        Assert.Equal("blank = the default beside config.json", vm.DestinationsFileNote);

        vm.DestinationsFile = "missing-dests.json";
        WaitFor(() => vm.DestinationsFileNote.Contains("will be created on save"),
            "a missing destinations file should eventually report it'll be created");
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
    public void RenamingTheDefaultHeaderEditsMonitorTitle()
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
        Assert.True(vm.TryBuildResult());
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

    [Fact]
    public void DropOnAHeaderJoinsThatGroupAndTheDefaultHeaderClearsTheSection()
    {
        var vm = new SettingsViewModel(
            WatchCfg(("A", "Night"), ("B", "")), _dialogs);

        var def = vm.WatchRows.OfType<WatchSectionVm>().Single(x => x.IsDefault);
        vm.DropWatch(vm.WatchFolders[0], def);   // A into the default group

        Assert.Equal("", vm.WatchFolders.Single(w => w.Label == "A").Section);

        var night = vm.WatchRows.OfType<WatchSectionVm>().SingleOrDefault(x => !x.IsDefault);
        Assert.Null(night);   // Night emptied out, so its header is gone
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

    // ---- Task 2: path checks must never block the UI thread -------------

    [Fact]
    public void TypingAPathDoesNotBlockOnTheProbe()
    {
        // A deliberately slow stand-in for Directory.Exists — exactly the
        // shape of a stalled SMB round trip. Wired straight into the seam
        // (SettingsViewModel's directoryExists ctor param) so the check
        // below hits it on every keystroke, same as the real UI does.
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { Thread.Sleep(300); return Directory.Exists(p); });

        // A bound TextBlock reacts to InboxNote's PropertyChanged
        // SYNCHRONOUSLY, on whatever thread raised it — WPF bindings don't
        // defer unless IsAsync is set, which SettingsWindow.xaml doesn't.
        // Subscribing and re-reading the note here reproduces exactly what
        // the real window's binding does, without needing a live window.
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.InboxNote)) _ = vm.InboxNote; };

        var sw = Stopwatch.StartNew();
        vm.Inbox = @"\\unreachable\share\inbox";
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"setting Inbox blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
    }

    [Fact]
    public void TheNoteEventuallyReflectsTheRealPathState()
    {
        // Proves the other half of the fix: it isn't "never check" — the
        // note settles on the real answer once the debounced, off-thread
        // probe completes.
        var vm = new SettingsViewModel(new Config(), _dialogs);

        vm.Inbox = _dir;   // real, existing folder
        WaitFor(() => vm.InboxNote == "", "InboxNote should eventually clear for a real, existing folder");

        vm.Inbox = Path.Combine(_dir, "does-not-exist");
        WaitFor(() => vm.InboxNote.Contains("doesn't exist"),
            "InboxNote should eventually report a folder that doesn't exist");
    }

    [Fact]
    public void RoutePathDoesNotBlockOnTheValidateRouteProbe()
    {
        // RouteEditVm.Problem -> Config.ValidateRoute creates+deletes a real
        // probe file in the destination folder — the other named offender.
        var vm = new SettingsViewModel(new Config(), _dialogs,
            validateRoute: r => { Thread.Sleep(300); return Config.ValidateRoute(r); });
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];
        route.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(route.Problem)) _ = route.Problem; };

        var sw = Stopwatch.StartNew();
        route.Path = _dir;
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"setting a route's Path blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
    }

    [Fact]
    public void ValidateRouteProbeRunsOncePerPauseNotPerKeystroke()
    {
        var calls = 0;
        var vm = new SettingsViewModel(new Config(), _dialogs,
            validateRoute: r => { Interlocked.Increment(ref calls); return Config.ValidateRoute(r); });
        vm.AddRouteCommand.Execute(null);   // blank Path: the ctor's own priming check is synchronous, no I/O
        var route = vm.Routes[0];
        Assert.Equal(0, calls);

        // simulate typing a destination character by character, faster than
        // the debounce window — exactly the keystroke burst the audit flagged
        var target = _dir;
        for (var i = 1; i <= target.Length; i++)
            route.Path = target.Substring(0, i);

        WaitFor(() => route.Problem == "", "the route's Problem should eventually resolve for the real folder");
        Thread.Sleep(350);   // no more keystrokes coming; let the debounce fully settle

        Assert.Equal(1, calls);   // the probe file was created/deleted once, not once per character
    }

    [Fact]
    public void RouteProblemEventuallyReflectsTheRealValidateRouteResult()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs);
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];

        route.Path = _dir;   // exists and is writable
        WaitFor(() => route.Problem == "", "an existing, writable destination should eventually clear the problem");

        route.Path = Path.Combine(_dir, "missing");
        WaitFor(() => route.Problem.Contains("does not exist"),
            "a missing destination should eventually report it");
    }

    [Fact]
    public void WatchFolderPathDoesNotBlockOnTheProbe()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { Thread.Sleep(300); return Directory.Exists(p); });
        vm.AddWatchCommand.Execute(null);
        var w = vm.WatchFolders[0];
        w.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(w.Problem)) _ = w.Problem; };

        var sw = Stopwatch.StartNew();
        w.Path = _dir;
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"setting a watch folder's Path blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
    }

    // ---- Task 2 follow-up: clearing a path must cancel its in-flight probe,
    // not just skip past it — otherwise the stale probe resolves later and
    // silently overwrites the note/Problem for a path the user no longer has.

    [Fact]
    public void ClearingInboxCancelsTheInFlightProbeInsteadOfLettingItOverwriteTheNote()
    {
        // A slow directoryExists (the shape of a stalled SMB round trip)
        // that, if not cancelled, resolves LONG after the box is cleared.
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { Thread.Sleep(500); return Directory.Exists(p); });

        vm.Inbox = _dir;   // arms a slow probe for a real, existing folder
        vm.Inbox = "";     // cleared inside the debounce+I/O window — no I/O needed for blank

        // blank needs no I/O: this must be correct immediately
        Assert.Contains("no inbox folder set", vm.InboxNote);

        // Wait past BOTH the debounce (300ms) and the slow probe (500ms).
        // If the stale probe for _dir wasn't cancelled, it resolves here
        // and silently overwrites the note with "" (the real answer for a
        // folder the user no longer has configured).
        Thread.Sleep(1200);
        Assert.Contains("no inbox folder set", vm.InboxNote);
    }

    [Fact]
    public void ClearingARoutePathCancelsTheInFlightValidateRouteProbe()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs,
            validateRoute: r => { Thread.Sleep(500); return Config.ValidateRoute(r); });
        vm.AddRouteCommand.Execute(null);
        var route = vm.Routes[0];

        route.Path = _dir;   // arms a slow probe for a real, writable folder
        route.Path = "";     // cleared inside the debounce+I/O window

        Assert.Equal("no destination path configured", route.Problem);

        Thread.Sleep(1200);
        Assert.Equal("no destination path configured", route.Problem);
    }

    [Fact]
    public void ClearingAWatchFolderPathCancelsTheInFlightProbe()
    {
        var vm = new SettingsViewModel(new Config(), _dialogs,
            directoryExists: p => { Thread.Sleep(500); return Directory.Exists(p); });
        vm.AddWatchCommand.Execute(null);
        var w = vm.WatchFolders[0];

        w.Path = _dir;   // arms a slow probe for a real folder
        w.Path = "";     // cleared inside the debounce+I/O window

        Assert.Equal("no folder chosen yet", w.Problem);

        Thread.Sleep(1200);
        Assert.Equal("no folder chosen yet", w.Problem);
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
