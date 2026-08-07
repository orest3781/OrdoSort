using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>TEMPORARY diagnostic-only reproduction for the user-reported
/// "Section dropdown doesn't move folders between sections" bug
/// (2026-08-07 diagnosis session). Drives the REAL, compiled SettingsWindow's
/// Section ComboBox (SettingsWindow.xaml:848-852) exactly as a user would —
/// via its PART_EditableTextBox and via a real opened drop-down — rather than
/// asserting against the view model directly, because the leading hypothesis
/// is a WPF binding re-entrancy issue in the control itself
/// (SettingsViewModel.HookWatch, :765-775, synchronously rebuilds WatchRows
/// AND reassigns the combo's own ItemsSource on every keystroke). Every
/// assertion below encodes the CORRECT expected behaviour (typed/picked text
/// sticks, WatchRows regroups to match) — not the buggy one — so this file
/// stays a valid regression test if left in the tree; delete it once a
/// permanent regression test lands elsewhere, or keep it, per the fix
/// author's judgement.</summary>
[Collection(HighlightContrastTests.Name)]
public class SectionDropdownReproTests
{
    private readonly HighlightContrastFixture _fx;
    public SectionDropdownReproTests(HighlightContrastFixture fx) => _fx = fx;

    private const string FolderA = "Alpha folder";
    private const string SectionA = "Alpha section";
    private const string FolderB = "Beta folder";

    // 2026-08-07 fix session (Task 1: every section listed, an emptied one
    // stays for the session) — the user's literal setup: "3 sections with
    // 1 folder each", none of them the blank/default group.
    private const string SectionOne = "Section one";
    private const string SectionTwo = "Section two";
    private const string SectionThree = "Section three";

    private static Config ThreeSectionsOneFolderEachConfig() => new()
    {
        WatchFolders =
        {
            new WatchFolder { Label = "Folder one", Path = @"C:\queues\one", Section = SectionOne, Color = "#C0392B" },
            new WatchFolder { Label = "Folder two", Path = @"C:\queues\two", Section = SectionTwo, Color = "#2980B9" },
            new WatchFolder { Label = "Folder three", Path = @"C:\queues\three", Section = SectionThree, Color = "#27AE60" },
        },
    };

    private sealed class NoDialogs : IDialogService
    {
        public void Warn(string message, string title) { }
        public void Info(string message, string title) { }
        public bool Confirm(string message, string title) => true;
        public string? AskSaveFile(string filter, string suggestedName) => null;
        public string? AskOpenFile(string filter) => null;
        public string? AskFilePath(string filter, string suggestedName) => null;
        public string? BrowseFolder(string? startAt) => null;
    }

    private (SettingsViewModel Vm, SettingsWindow Window, ComboBox Combo) OpenDashboardOnFolderB() =>
        OpenDashboardSelecting(FolderB, extraFolder: null);

    /// <summary>As above, but the caller picks which folder starts selected
    /// (and can add a third folder in its own section) — used for the
    /// "already has a section, change it to a DIFFERENT one" scenario, which
    /// is the literal reading of the user's report ("moving folders to
    /// different sections") and is NOT covered by the blank-to-named case
    /// OpenDashboardOnFolderB sets up.</summary>
    private (SettingsViewModel Vm, SettingsWindow Window, ComboBox Combo) OpenDashboardSelecting(
        string selectLabel, (string Label, string Section)? extraFolder)
    {
        var cfg = new Config();
        cfg.WatchFolders.Add(new WatchFolder
        {
            Label = FolderA, Path = @"C:\queues\alpha", Section = SectionA, Color = "#C0392B",
        });
        cfg.WatchFolders.Add(new WatchFolder
        {
            Label = FolderB, Path = @"C:\queues\beta", Section = "", Color = "#2980B9",
        });
        if (extraFolder is { } ex)
            cfg.WatchFolders.Add(new WatchFolder
            {
                Label = ex.Label, Path = @"C:\queues\" + ex.Label, Section = ex.Section, Color = "#27AE60",
            });
        return OpenDashboardFor(cfg, selectLabel);
    }

    /// <summary>2026-08-07 fix session (Step 1/2 extension): the shared
    /// window-building tail of <see cref="OpenDashboardSelecting"/>, pulled
    /// out so tests that need a config shape it doesn't offer (the user's
    /// literal report — THREE named sections, one folder each, none of them
    /// blank) don't have to duplicate the real-window/real-ComboBox
    /// plumbing. Behaviour of the two original callers is unchanged; this
    /// is a pure extraction.</summary>
    private (SettingsViewModel Vm, SettingsWindow Window, ComboBox Combo) OpenDashboardFor(
        Config cfg, string selectLabel)
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var cfgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ordo_test_section_repro_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(), () => ThemePalette.Light, cfgPath,
            uiContext: SynchronizationContext.Current);
        vm.SelectedWatch = vm.WatchFolders.First(w => w.Label == selectLabel);

        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();
        PumpRender();

        var tabControl = FindDescendant<TabControl>(window)
            ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
        var monitoredFolders = tabControl.Items.Cast<TabItem>()
                .FirstOrDefault(ti => (ti.Header?.ToString() ?? "").Replace("_", "") == "Monitored folders")
            ?? throw new InvalidOperationException("no \"Monitored folders\" TabItem found");
        tabControl.SelectedItem = monitoredFolders;
        window.UpdateLayout();
        PumpRender();
        window.UpdateLayout();

        var combo = FindAllDescendants<ComboBox>(window)
                .FirstOrDefault(c => (c.ToolTip as string) ==
                    "Group this folder's tile appears under \u2014 or manage sections directly in the list")
            ?? throw new InvalidOperationException("no Section ComboBox found under SettingsWindow");
        combo.ApplyTemplate();
        combo.UpdateLayout();
        PumpRender();
        return (vm, window, combo);
    }

    private static TextBox EditableTextBoxOf(ComboBox combo) =>
        FindDescendant<TextBox>(combo)
            ?? throw new InvalidOperationException("no PART_EditableTextBox realized under the Section ComboBox");

    /// <summary>Case 4 (first keystroke vs. later) + the core of the
    /// hypothesis: type a brand-new section name one character at a time
    /// through the REAL PART_EditableTextBox (exactly how ComboBox wires an
    /// editable TextBox's Text to its own Text DP, TwoWay/PropertyChanged),
    /// and check after EVERY keystroke that both the control's own Text and
    /// WatchEditVm.Section still hold what was typed so far.</summary>
    [Fact]
    public void TypingANewSectionNameSurvivesEveryKeystroke() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardOnFolderB();
        try
        {
            var box = EditableTextBoxOf(combo);
            var beta = vm.WatchFolders.First(w => w.Label == FolderB);
            const string typed = "Incoming 2";
            var soFar = "";
            foreach (var ch in typed)
            {
                soFar += ch;
                box.Text = soFar;
                box.CaretIndex = box.Text.Length;
                PumpRender();

                Assert.True(soFar == box.Text,
                    $"after typing \"{soFar}\": PART_EditableTextBox.Text reads \"{box.Text}\" " +
                    "(the control itself lost what was just typed)");
                Assert.True(soFar == beta.Section,
                    $"after typing \"{soFar}\": WatchEditVm.Section reads \"{beta.Section}\" " +
                    "(the two-way binding did not deliver, or was overwritten after delivery — " +
                    "this is the reported bug)");
            }
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>End-to-end version of the above: type a new section name and
    /// commit it by moving focus elsewhere (Tab/blur), the way a real user
    /// finishes editing. The folder must end up filed under a NEW section
    /// header with that name in WatchRows — the actual dashboard-grouping
    /// effect the user says isn't happening.</summary>
    [Fact]
    public void TypingANewSectionNameMovesTheFolderOnCommit() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardOnFolderB();
        try
        {
            var box = EditableTextBoxOf(combo);
            var beta = vm.WatchFolders.First(w => w.Label == FolderB);
            const string typed = "Incoming 2";
            box.Focus();
            foreach (var ch in typed)
            {
                box.Text += ch;
                box.CaretIndex = box.Text.Length;
                PumpRender();
            }

            // commit: move focus off the combo, as Tab/click-away would
            var other = FindDescendant<TabControl>(window)!;
            System.Windows.Input.Keyboard.Focus(other);
            PumpRender();
            window.UpdateLayout();

            Assert.True(typed == beta.Section,
                $"after commit: WatchEditVm.Section reads \"{beta.Section}\", expected \"{typed}\"");

            var header = vm.WatchRows.OfType<WatchSectionVm>()
                .FirstOrDefault(h => h.Header == typed);
            Assert.True(header is not null,
                "no \"" + typed + "\" section header appeared in WatchRows after commit");
            var idx = vm.WatchRows.ToList().IndexOf(header!);
            Assert.True(vm.WatchRows.Skip(idx + 1).OfType<WatchEditVm>()
                    .TakeWhile(w => true).Any(w => ReferenceEquals(w, beta))
                || vm.WatchRows.Contains(beta) && vm.WatchRows.IndexOf(beta) > idx,
                $"{FolderB} did not land under its new \"{typed}\" section header in WatchRows");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Case 1: picking an EXISTING section from the open drop-down
    /// (a discrete click, not a keystroke stream) rather than typing.</summary>
    [Fact]
    public void PickingAnExistingSectionFromTheDropDownMovesTheFolder() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardOnFolderB();
        try
        {
            var beta = vm.WatchFolders.First(w => w.Label == FolderB);
            Assert.Contains(SectionA, combo.ItemsSource!.Cast<string>());

            combo.SelectedItem = SectionA;
            PumpRender();
            window.UpdateLayout();

            Assert.True(SectionA == beta.Section,
                $"after picking \"{SectionA}\" from the drop-down: WatchEditVm.Section reads \"{beta.Section}\"");
            Assert.True(SectionA == combo.Text,
                $"after picking \"{SectionA}\" from the drop-down: ComboBox.Text reads \"{combo.Text}\"");

            var header = vm.WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h => h.Header == SectionA);
            Assert.True(header is not null, $"no \"{SectionA}\" section header in WatchRows");
            var idx = vm.WatchRows.ToList().IndexOf(header!);
            Assert.True(vm.WatchRows.Contains(beta) && vm.WatchRows.IndexOf(beta) > idx,
                $"{FolderB} did not land under the \"{SectionA}\" header in WatchRows after picking it");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Case 3: the section being changed belongs to a folder that is
    /// NOT the selected/focused row (here: via the same path DropWatch and
    /// CommitSectionRename use — a direct Section assignment) while Folder
    /// B's combo has an in-progress, uncommitted keystroke. This isolates
    /// whether the ItemsSource swap alone — regardless of which control
    /// triggered it — disturbs a focused, mid-edit combo that is NOT the
    /// source of the change.</summary>
    [Fact]
    public void AnotherFoldersSectionChangeDoesNotDisturbTheFocusedCombo() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardOnFolderB();
        try
        {
            var alpha = vm.WatchFolders.First(w => w.Label == FolderA);
            var beta = vm.WatchFolders.First(w => w.Label == FolderB);
            var box = EditableTextBoxOf(combo);
            box.Focus();
            box.Text = "Partial";
            box.CaretIndex = box.Text.Length;
            PumpRender();
            Assert.True("Partial" == beta.Section, "setup: typed prefix did not reach Section");

            // a foreign folder's Section changes via the same programmatic
            // path drag-and-drop (DropWatch) and header-rename
            // (CommitSectionRename) use — NOT through Folder B's own combo
            alpha.Section = "Alpha section renamed";
            PumpRender();
            window.UpdateLayout();

            Assert.True("Partial" == box.Text,
                $"PART_EditableTextBox.Text changed to \"{box.Text}\" after an UNRELATED folder's " +
                "Section changed (ItemsSource swap alone disturbed the focused, mid-edit combo)");
            Assert.True("Partial" == beta.Section,
                $"WatchEditVm.Section changed to \"{beta.Section}\" after an UNRELATED folder's Section changed");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>The literal reading of the report: a folder that ALREADY
    /// belongs to a named section (SectionA, and is that section's ONLY
    /// member) gets moved to a DIFFERENT one by select-all-and-retype — the
    /// realistic way a user edits an already-filled combo, which passes
    /// through an intermediate Section="" (default-section) state that the
    /// blank-to-named tests above never exercise. SectionA's header must
    /// disappear (no members left) and a new one must appear with the folder
    /// under it.</summary>
    [Fact]
    public void RetypingAnExistingSectionMovesTheFolderToTheNewOne() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardSelecting(FolderA, extraFolder: null);
        try
        {
            var box = EditableTextBoxOf(combo);
            var alpha = vm.WatchFolders.First(w => w.Label == FolderA);
            Assert.True(SectionA == alpha.Section, "setup: folder A did not start in SectionA");

            // select-all + retype, exactly like a user clearing an existing value
            box.Focus();
            box.SelectAll();
            box.Text = "";
            box.CaretIndex = 0;
            PumpRender();

            const string typed = "Different section";
            var soFar = "";
            foreach (var ch in typed)
            {
                soFar += ch;
                box.Text = soFar;
                box.CaretIndex = box.Text.Length;
                PumpRender();

                Assert.True(soFar == box.Text,
                    $"after retyping \"{soFar}\": PART_EditableTextBox.Text reads \"{box.Text}\"");
                Assert.True(soFar == alpha.Section,
                    $"after retyping \"{soFar}\": WatchEditVm.Section reads \"{alpha.Section}\"");
            }

            // FIX 2026-08-07 (session-sticky sections, Task 1): this used to
            // assert the OLD header was gone entirely — that WAS the bug
            // ("moving the first folder into the second section [caused]
            // the first section [to be] removed"). An emptied section now
            // stays for the rest of the Settings session so it can be moved
            // back into without retyping the name; assert it survives with
            // NO members instead of asserting it vanished.
            var oldHeader = vm.WatchRows.OfType<WatchSectionVm>().SingleOrDefault(h => h.Header == SectionA);
            Assert.True(oldHeader is not null,
                $"the old \"{SectionA}\" header should stay in WatchRows (session-sticky) even with no members left");
            Assert.DoesNotContain(vm.WatchFolders, w => w.Section == SectionA);
            Assert.Contains(SectionA, vm.SectionChoices);
            var newHeader = vm.WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h => h.Header == typed);
            Assert.True(newHeader is not null, $"no \"{typed}\" section header appeared in WatchRows");
            var idx = vm.WatchRows.ToList().IndexOf(newHeader!);
            Assert.True(vm.WatchRows.Contains(alpha) && vm.WatchRows.IndexOf(alpha) > idx,
                $"{FolderA} did not land under its new \"{typed}\" section header in WatchRows");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Same literal scenario but via the drop-down: a folder that
    /// already has SectionA gets pointed at a DIFFERENT existing section
    /// (Gamma section, held by a third folder) by picking it from the
    /// drop-down rather than typing.</summary>
    [Fact]
    public void PickingADifferentExistingSectionMovesAnAlreadySectionedFolder() => _fx.Invoke(() =>
    {
        const string gammaSection = "Gamma section";
        var (vm, window, combo) = OpenDashboardSelecting(FolderA, extraFolder: ("Gamma folder", gammaSection));
        try
        {
            var alpha = vm.WatchFolders.First(w => w.Label == FolderA);
            Assert.True(SectionA == alpha.Section, "setup: folder A did not start in SectionA");
            Assert.Contains(gammaSection, combo.ItemsSource!.Cast<string>());

            combo.SelectedItem = gammaSection;
            PumpRender();
            window.UpdateLayout();

            Assert.True(gammaSection == alpha.Section,
                $"after picking \"{gammaSection}\": WatchEditVm.Section reads \"{alpha.Section}\"");

            // FIX 2026-08-07 (session-sticky sections, Task 1) — see the
            // matching comment in RetypingAnExistingSectionMovesTheFolder-
            // ToTheNewOne: the old header now SURVIVES the folder leaving,
            // empty, instead of disappearing.
            var oldHeader = vm.WatchRows.OfType<WatchSectionVm>().SingleOrDefault(h => h.Header == SectionA);
            Assert.True(oldHeader is not null,
                $"the old \"{SectionA}\" header should stay in WatchRows (session-sticky) even with no members left");
            Assert.DoesNotContain(vm.WatchFolders, w => w.Section == SectionA);
            Assert.Contains(SectionA, vm.SectionChoices);
            var newHeader = vm.WatchRows.OfType<WatchSectionVm>().First(h => h.Header == gammaSection);
            var idx = vm.WatchRows.ToList().IndexOf(newHeader);
            Assert.True(vm.WatchRows.Contains(alpha) && vm.WatchRows.IndexOf(alpha) > idx,
                $"{FolderA} did not land under the \"{gammaSection}\" header in WatchRows after picking it");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    // ============================================================
    // 2026-08-07 fix session, Task 1: "every section listed, an emptied
    // one survives the session". Steps 1/2 below are written from the
    // user's own words FIRST, before the production fix, and must fail
    // for the reason the user gave — not for some other, assumed-fragile
    // reason (three prior hypotheses about this bug were wrong, and all
    // six tests above passed against every one of them).
    // ============================================================

    /// <summary>Step 1 — the literal user report: "in the monitored
    /// folders list there are 3 sections with 1 folder each. when i select
    /// the first folder and use the Section dropdown, only the section
    /// below the first section is listed in the dropdown." Root cause:
    /// SectionChoices filtered with `!ReferenceEquals(w, SelectedWatch)`,
    /// meant to list "the OTHER folders'" sections — with one folder per
    /// section, the selected folder is the ONLY thing keeping its own
    /// section alive, so that filter dropped exactly its own section from
    /// its own drop-down. Must fail before the fix, for that reason.</summary>
    [Fact]
    public void EveryFoldersOwnSectionIsListedWithOneFolderPerSection() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardFor(ThreeSectionsOneFolderEachConfig(), "Folder one");
        try
        {
            // vm.SectionChoices: what the bug report is actually about
            var choices = vm.SectionChoices.ToList();
            Assert.Contains(SectionOne, choices);     // the SELECTED folder's OWN section — this is what the bug dropped
            Assert.Contains(SectionTwo, choices);
            Assert.Contains(SectionThree, choices);

            // and the same thing through the REAL drop-down the user used
            var comboItems = combo.ItemsSource!.Cast<string>().ToList();
            Assert.Contains(SectionOne, comboItems);
            Assert.Contains(SectionTwo, comboItems);
            Assert.Contains(SectionThree, comboItems);
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Step 2 — the rest of the report: "when i move the first
    /// folder into the second section, the first section is removed."
    /// Moves Folder one into Section two through the REAL drop-down (the
    /// literal action reported) and requires the product owner's decision:
    /// Section one survives — still a header row, still offered, in its
    /// ORIGINAL slot rather than shoved to the end — so Folder one can be
    /// moved straight back in without retyping the name. Also proves the
    /// negative the brief calls out explicitly: sticky means "existed this
    /// session", not "anything ever typed" — a name nobody ever used stays
    /// absent.</summary>
    [Fact]
    public void AnEmptiedSectionStaysStickyForTheRestOfTheSession() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardFor(ThreeSectionsOneFolderEachConfig(), "Folder one");
        try
        {
            var folderOne = vm.WatchFolders.First(w => w.Label == "Folder one");
            const string neverTouched = "Never touched section";

            combo.SelectedItem = SectionTwo;   // "move the first folder into the second section"
            PumpRender();
            window.UpdateLayout();
            Assert.Equal(SectionTwo, folderOne.Section);

            // Section one is NOT removed — it stays, empty, in its
            // ORIGINAL position among the named headers (index 0), not
            // shoved to the end
            var namedHeaders = vm.WatchRows.OfType<WatchSectionVm>().Where(h => !h.IsDefault).ToList();
            var sectionOneHeader = namedHeaders.FirstOrDefault(h => h.Header == SectionOne);
            Assert.True(sectionOneHeader is not null,
                $"\"{SectionOne}\" should still have a header row after its only folder left it");
            Assert.Equal(0, namedHeaders.IndexOf(sectionOneHeader!));
            Assert.DoesNotContain(vm.WatchFolders, w => w.Section == SectionOne);

            // ...and still offered — in the VM and in the real drop-down
            Assert.Contains(SectionOne, vm.SectionChoices);
            Assert.Contains(SectionOne, combo.ItemsSource!.Cast<string>());

            // the negative: a name nobody ever used this session, and that
            // was never created via "Add section" either, must not appear
            Assert.DoesNotContain(neverTouched, vm.SectionChoices);
            Assert.DoesNotContain(vm.WatchRows.OfType<WatchSectionVm>(), h => h.Header == neverTouched);

            // and Folder one can be moved straight back into it
            combo.SelectedItem = SectionOne;
            PumpRender();
            window.UpdateLayout();
            Assert.Equal(SectionOne, folderOne.Section);
            var header = vm.WatchRows.OfType<WatchSectionVm>().First(h => h.Header == SectionOne);
            var idx = vm.WatchRows.ToList().IndexOf(header);
            Assert.True(vm.WatchRows.Contains(folderOne) && vm.WatchRows.IndexOf(folderOne) > idx,
                "Folder one did not land back under Section one after being moved back into it");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Step 6: an emptied, session-sticky section must never leak
    /// a phantom row into the built config — sections stay DERIVED at
    /// rest, nothing phantom is written to monitored-folders.json. Checked
    /// on Result (what TryBuildResult actually produces, and what gets
    /// JSON-written on save), not just on WatchRows/SectionChoices.</summary>
    [Fact]
    public void AnEmptySectionIsNotPersistedByTryBuildResult() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardFor(ThreeSectionsOneFolderEachConfig(), "Folder one");
        try
        {
            combo.SelectedItem = SectionTwo;   // empties Section one — still sticky, still shown
            PumpRender();
            window.UpdateLayout();
            Assert.Contains(SectionOne, vm.SectionChoices);   // setup check: it really is sticky right now

            Assert.True(vm.TryBuildResult());
            var result = vm.Result!;

            // exactly the 3 REAL folders — no 4th, phantom row for the
            // empty sticky section
            Assert.Equal(3, result.WatchFolders.Count);
            Assert.DoesNotContain(result.WatchFolders, w => w.Section == SectionOne);
            Assert.All(result.WatchFolders, w => Assert.False(string.IsNullOrWhiteSpace(w.Label)));
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>Step 7 (neighbours) + Step 5's third deliberate decision
    /// ("what a header rename does to a sticky entry"): renaming a header
    /// that's ALREADY EMPTIED — a state that could not exist before this
    /// fix. CommitSectionRename's own member-rewrite loop finds nothing to
    /// touch (there are no members), so retitling the STICKY entry itself
    /// is the only thing that can keep the new name from reverting on the
    /// next rebuild, and the OLD name must not linger behind as a second,
    /// stranded empty header next to the renamed one.</summary>
    [Fact]
    public void RenamingAnEmptiedStickyHeaderRetitlesItWithoutLeavingTheOldNameBehind() => _fx.Invoke(() =>
    {
        var (vm, window, combo) = OpenDashboardFor(ThreeSectionsOneFolderEachConfig(), "Folder one");
        try
        {
            combo.SelectedItem = SectionTwo;   // empties Section one
            PumpRender();
            window.UpdateLayout();
            var emptyHeader = vm.WatchRows.OfType<WatchSectionVm>().Single(h => h.Header == SectionOne);

            vm.BeginSectionRename(emptyHeader);
            emptyHeader.EditText = "Renamed empty section";
            vm.CommitSectionRename(emptyHeader);

            Assert.DoesNotContain(SectionOne, vm.SectionChoices);
            Assert.DoesNotContain(vm.WatchRows.OfType<WatchSectionVm>(), h => h.Header == SectionOne);
            Assert.Contains("Renamed empty section", vm.SectionChoices);
            var renamed = vm.WatchRows.OfType<WatchSectionVm>()
                .SingleOrDefault(h => h.Header == "Renamed empty section");
            Assert.True(renamed is not null, "the renamed sticky header should still be in WatchRows, still empty");
            Assert.DoesNotContain(vm.WatchFolders, w => w.Section == "Renamed empty section");
        }
        finally { window.Close(); vm.Dispose(); }
    });

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
