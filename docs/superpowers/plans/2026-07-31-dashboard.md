# Dashboard Refinement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Monitored folders grouped into named dashboard sections (per-folder `section` field, first-appearance order, blank → the existing Section heading) and an alert-term chip editor — with zero change to matching, flashing, or the global visibility dropdown.

**Architecture:** `WatchFolder` gains a `section` key that rides through `FolderMonitor.FolderStatus` into the tile rebuild. `ShellViewModel.Tiles` REMAINS the flat store (MainWindow's self-sizing hook and DashboardTests depend on it); a new `TileGroups` collection is a grouped projection over the same `TileViewModel` instances, and `ReadyView` renders groups. The Settings Dashboard tab gains a Section pick-or-type ComboBox per folder and swaps the alerts multiline for a chip editor.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main` (established delivery mode: commits per task, push only in the final task).

## Global Constraints

- Spec-implementation refinement, recorded deliberately: the spec says `TileGroups` "replaces" the flat `Tiles`; this plan AUGMENTS instead — `Tiles` stays the flat rebuild target (its `CollectionChanged` drives `MainWindow` self-sizing at `MainWindow.xaml.cs:91`, and `DashboardTests` asserts on it), while `TileGroups` holds the same `TileViewModel` instances grouped. User-visible behavior matches the spec exactly.
- Effective section = folder's `section`, or `MonitorTitle` when blank. Groups in first-appearance order of the STATUS list. A section with no statuses simply produces no group ("Active only" filtering already happens upstream in the sweep — statuses only exist for folders holding files or in error).
- The tile rebuild signature must include the section so a section rename rebuilds tiles.
- Chips: trim, ignore blank, case-insensitive dedupe (a duplicate add is a no-op that still clears the box), order preserved; `cfg.AlertTexts` list format unchanged.
- Sanctioned existing-test updates ONLY where tests touch `AlertTextsText` or rendering internals; `DashboardTests`' flat-`Tiles` assertions must keep passing UNTOUCHED (that's the point of augment-don't-replace).
- Baseline: Core 350 + Wpf 279 = 629 green; grow only by additions plus the sanctioned updates.

---

### Task 1: Core — `section` on WatchFolder and FolderStatus

**Files:**
- Modify: `src/OrdoSort.Core/Config.cs` (WatchFolder), `src/OrdoSort.Core/FolderMonitor.cs` (FolderStatus + sweep)
- Test: `tests/OrdoSort.Core.Tests/NamingConfigTests.cs` sibling — create `tests/OrdoSort.Core.Tests/SectionTests.cs`

**Interfaces:**
- Produces: `WatchFolder.Section` (`[JsonPropertyName("section")]`, string, default `""`); `FolderMonitor.FolderStatus.Section` (string) populated from the folder each status was swept from.

- [ ] **Step 1: Write the failing tests** — create `SectionTests.cs`:

```csharp
namespace OrdoSort.Core.Tests;

/// <summary>Dashboard sections: the watch-folder `section` key and its ride
/// through the sweep.</summary>
public class SectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordosection_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SectionRoundTripsThroughMonitoredFoldersJson()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = new Config();
        cfg.WatchFolders.Add(new WatchFolder { Label = "A", Path = "C:/a", Section = "Failed queues" });
        cfg.WatchFolders.Add(new WatchFolder { Label = "B", Path = "C:/b" });
        Config.Save(cfg, path);
        Assert.Contains("Failed queues",
            File.ReadAllText(Path.Combine(_dir, "monitored-folders.json")));
        var back = Config.Load(path);
        Assert.Equal("Failed queues", back.WatchFolders[0].Section);
        Assert.Equal("", back.WatchFolders[1].Section);   // omitted -> ""
    }

    [Fact]
    public void SweepCarriesTheSectionIntoTheStatus()
    {
        var folder = Path.Combine(_dir, "watch");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "doc.pdf"), "x");
        var w = new WatchFolder { Label = "W", Path = folder, Section = "Incoming" };
        var status = Assert.Single(FolderMonitor.Sweep(new() { w }, new List<string>()));
        Assert.Equal("Incoming", status.Section);
    }
}
```

(Adapt the `FolderMonitor.Sweep` call to the real sweep entry point — find it: `grep -n "FolderStatus" src/OrdoSort.Core/FolderMonitor.cs`; the assertion — status carries the folder's section — is the requirement. If the sweep method takes different parameters (alert terms, filetypes), fill them with the simplest values that make one status appear.)

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/OrdoSort.Core.Tests --filter SectionTests -v minimal` — compile failure (properties missing).

- [ ] **Step 3: Implement.** `WatchFolder` (Config.cs), after its `color` property:

```csharp
    [JsonPropertyName("section")] public string Section { get; set; } = "";
```

Add `w.Section ??= "";` to the WatchFolders loop in `NormalizeSectionItems()` beside the other watch-folder null-hardening. In `FolderMonitor.cs`: add a `Section` member to `FolderStatus` (match its existing style — record positional or property, whichever it is) and populate it from the `WatchFolder` at the point each status is built (find every construction site in the file).

- [ ] **Step 4: Run tests** — filter passes, then full Core suite: `dotnet test tests/OrdoSort.Core.Tests -v minimal` — 350 + 2 = 352, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core tests/OrdoSort.Core.Tests/SectionTests.cs
git commit -m "feat(core): watch folders carry a dashboard section

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 2: Grouped tiles — ShellViewModel + ReadyView

**Files:**
- Create: `src/OrdoSort.Wpf/ViewModels/TileGroupViewModel.cs`
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` (`RefreshDashboard` ~line 404, signature list ~409), `src/OrdoSort.Wpf/Views/ReadyView.xaml` (heading + tile ItemsControl, lines 7-62)
- Test: `tests/OrdoSort.Wpf.Tests/DashboardTests.cs` (append — existing assertions stay untouched)

**Interfaces:**
- Consumes: `FolderStatus.Section` (Task 1).
- Produces: `ShellViewModel.TileGroups: ObservableCollection<TileGroupViewModel>`; `TileGroupViewModel { string Title; ObservableCollection<TileViewModel> Tiles }`.

- [ ] **Step 1: Write the failing tests** — append to `DashboardTests.cs`, following its `fx` fixture conventions (it builds real watch folders on disk and pumps the refresh):

```csharp
    [Fact]
    public void TilesGroupBySectionInFirstAppearanceOrder()
    {
        // three folders holding files: sections "Incoming", "" (default), "Incoming"
        // arrange per the fixture's existing multi-folder pattern, setting
        // Section on the WatchFolder configs; MonitorTitle stays default.
        // after refresh:
        Assert.Equal(2, fx.Shell.TileGroups.Count);
        Assert.Equal("Incoming", fx.Shell.TileGroups[0].Title);
        Assert.Equal("Monitored folders", fx.Shell.TileGroups[1].Title);   // default heading
        Assert.Equal(2, fx.Shell.TileGroups[0].Tiles.Count);
        Assert.Single(fx.Shell.TileGroups[1].Tiles);
        // the same instances live in the flat list (flash + sizing rely on it)
        Assert.Equal(fx.Shell.Tiles.Count,
            fx.Shell.TileGroups.Sum(g => g.Tiles.Count));
    }

    [Fact]
    public void AnEmptySectionProducesNoGroup()
    {
        // two folders, sections "Busy" (holding a file) and "Quiet" (empty folder)
        // active-only default: the sweep yields one status
        Assert.Single(fx.Shell.TileGroups);
        Assert.Equal("Busy", fx.Shell.TileGroups[0].Title);
    }

    [Fact]
    public void FlashReachesTilesInEveryGroup()
    {
        // two sections, both alerting; advance the flash tick per the file's
        // existing flash-test pattern; assert tiles in BOTH groups changed Back
    }
```

(Arrangement adapts to the fixture; the assertions are the requirements.)

- [ ] **Step 2: Run to verify failure** — compile failure (`TileGroups` missing).

- [ ] **Step 3: Implement.** New `TileGroupViewModel.cs`:

```csharp
namespace OrdoSort.Wpf.ViewModels;

/// <summary>One named section of monitored-folder tiles on the Ready
/// dashboard. Groups are projections: the TileViewModel instances are the
/// same objects held in ShellViewModel.Tiles (the flat list that drives
/// flashing and window sizing).</summary>
public sealed class TileGroupViewModel
{
    public TileGroupViewModel(string title) => Title = title;
    public string Title { get; }
    public System.Collections.ObjectModel.ObservableCollection<TileViewModel> Tiles { get; } = new();
}
```

`ShellViewModel`: add beside `Tiles`:

```csharp
    public ObservableCollection<TileGroupViewModel> TileGroups { get; } = new();
```

In `RefreshDashboard`: extend the signature line to include the section
(`+ $"|{s.Section}"` inside the existing signature `Select`), and replace the
rebuild block:

```csharp
        if (!ReferenceEquals(p, _tilePalette) || !signature.SequenceEqual(_tileSignature))
        {
            _tileSignature = signature;
            _tilePalette = p;
            Tiles.Clear();
            TileGroups.Clear();
            var defaultTitle = string.IsNullOrWhiteSpace(_cfg.MonitorTitle)
                ? "Monitored folders" : _cfg.MonitorTitle;
            var byTitle = new Dictionary<string, TileGroupViewModel>(StringComparer.CurrentCulture);
            foreach (var s in statuses)
            {
                var tile = new TileViewModel(s, p);
                Tiles.Add(tile);
                var title = string.IsNullOrWhiteSpace(s.Section) ? defaultTitle : s.Section;
                if (!byTitle.TryGetValue(title, out var group))
                {
                    group = new TileGroupViewModel(title);
                    byTitle[title] = group;
                    TileGroups.Add(group);
                }
                group.Tiles.Add(tile);
            }
        }
```

- [ ] **Step 4: ReadyView.xaml.** Replace the single heading TextBlock (line 7,
`Text="{Binding MonitorTitle}"`) and the `ItemsControl ItemsSource="{Binding Tiles}"`
wrapper with an outer group control, MOVING the existing tile `ItemsPanel` +
`ItemTemplate` (lines 9-61) inside unchanged:

```xaml
            <ItemsControl ItemsSource="{Binding TileGroups}" Focusable="False">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" FontWeight="SemiBold" Margin="0,0,0,6" />
                            <ItemsControl ItemsSource="{Binding Tiles}" Focusable="False">
                                <!-- the existing ItemsPanel + ItemTemplate move here VERBATIM -->
                            </ItemsControl>
                        </StackPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
```

Note: the inner tile template's bindings are unchanged (they bind TileViewModel
properties). `MonitorTitle` as a ShellViewModel property STAYS (it feeds the
default group title); only the XAML heading moves into groups.

- [ ] **Step 5: Run the Wpf suite** — new tests pass AND every pre-existing
`DashboardTests` assertion on the flat `Tiles` passes untouched.

Run: `dotnet test tests/OrdoSort.Wpf.Tests -v minimal`
Expected: 279 + 3 = 282, 0 failed, no existing test modified.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests/DashboardTests.cs
git commit -m "feat(dashboard): tiles grouped into named sections

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 3: Settings — per-folder Section field

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs` (`WatchEditVm` ~line 117 + its From/To mapping; a `SectionChoices` source), `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Dashboard tab folder editor + Section-heading note)
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `WatchFolder.Section` (Task 1).
- Produces: `WatchEditVm.Section` (string, `""` = default); `SettingsViewModel.SectionChoices` (IEnumerable<string> — distinct, non-blank sections of the OTHER folders, recomputed when the selected folder changes).

- [ ] **Step 1: View model.** `WatchEditVm`: add a `Section` string property
(`Set(ref …)` pattern), map it in its from-`WatchFolder` factory and its
to-`WatchFolder` build exactly like `Label`/`Filetypes` (plain string copy —
`""` stays `""`, no null mapping; the config default is `""`).
`SettingsViewModel`: add

```csharp
    /// <summary>Sections already in use by the OTHER monitored folders —
    /// the pick-or-type dropdown for the selected folder's Section box.</summary>
    public IEnumerable<string> SectionChoices =>
        Watches.Where(w => !ReferenceEquals(w, SelectedWatch))
               .Select(w => w.Section.Trim())
               .Where(s => s.Length > 0)
               .Distinct(StringComparer.CurrentCultureIgnoreCase)
               .ToList();
```

(adapt `Watches`/`SelectedWatch` to the actual collection/selection property
names around `WatchEditVm`; raise `SectionChoices` when the selection
changes, alongside the existing selected-watch change notifications).

- [ ] **Step 2: XAML.** In the Dashboard tab's folder editor (below the
`Label:` row), add a Section row following the page's FieldRow pattern:

```xaml
                                <Grid Style="{StaticResource FieldRow}">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="110" />
                                        <ColumnDefinition Width="220" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="Section:" Style="{StaticResource FieldLabel}" />
                                    <ComboBox Grid.Column="1" IsEditable="True"
                                              ItemsSource="{Binding DataContext.SectionChoices,
                                                            RelativeSource={RelativeSource AncestorType=Window}}"
                                              Text="{Binding Section, UpdateSourceTrigger=PropertyChanged}"
                                              ToolTip="Dashboard group this folder appears under — blank uses the Section heading above" />
                                </Grid>
```

Update the Section-heading field's note text to:
`Folders without a section land under this heading.`

- [ ] **Step 3: Tests** — add to `SettingsViewModelTests.cs` (adapt helpers):

```csharp
    [Fact]
    public void WatchFolderSectionRoundTripsThroughSettings()
    {
        // config with two watch folders, sections "Failed queues" and ""
        // → VM shows them; set the blank one to "Incoming"; build;
        // assert built.WatchFolders sections are "Failed queues" and "Incoming"
    }

    [Fact]
    public void SectionChoicesListsTheOtherFoldersDistinctSections()
    {
        // three folders: "Incoming", "incoming" (case-dup), "" — select the third;
        // assert SectionChoices is a single entry "Incoming"
    }
```

- [ ] **Step 4: Run the Wpf suite** — all green (282 + 2 = 284).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests
git commit -m "feat(settings): per-folder dashboard section with pick-or-type choices

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 4: Settings — alert chips

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs` (replace `AlertTextsText` ~line 931 + `ParseAlertTerms` + their from/build sites ~line 418), `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Dashboard tab Alerts block)
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `AlertTerms: ObservableCollection<string>`, `NewAlertText: string`, `AddAlertCommand`, `RemoveAlertCommand` (parameterized by the term string).

- [ ] **Step 1: View model.** Remove `AlertTextsText` + `ParseAlertTerms`. Add:

```csharp
    public ObservableCollection<string> AlertTerms { get; } = new();

    private string _newAlertText = "";
    public string NewAlertText { get => _newAlertText; set => Set(ref _newAlertText, value); }

    public RelayCommand AddAlertCommand { get; }
    public RelayCommand<string> RemoveAlertCommand { get; }
```

Wire in the constructor (mirroring the file's command style; if there is no
generic `RelayCommand<T>`, use the file's established parameterized-command
pattern — check how per-item commands are done elsewhere, e.g. route/password
removal, and match it):

```csharp
        AddAlertCommand = new RelayCommand(() =>
        {
            var term = NewAlertText.Trim();
            NewAlertText = "";
            if (term.Length == 0) return;
            if (!AlertTerms.Any(t => string.Equals(t, term, StringComparison.CurrentCultureIgnoreCase)))
                AlertTerms.Add(term);
        });
        RemoveAlertCommand = new RelayCommand<string>(t => AlertTerms.Remove(t));
        AlertTerms.CollectionChanged += (_, _) => RecomputeTilePreview();
```

From-config: `foreach (var t in current.AlertTexts) AlertTerms.Add(t);`
Build: `cfg.AlertTexts = AlertTerms.ToList();`

- [ ] **Step 2: XAML.** Replace the alerts multiline TextBox with:

```xaml
                        <ItemsControl ItemsSource="{Binding AlertTerms}" Focusable="False" Margin="0,4,0,4">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Border Background="{DynamicResource Theme.Surface}"
                                            BorderBrush="{DynamicResource Theme.Border}" BorderThickness="1"
                                            CornerRadius="10" Padding="8,2" Margin="0,0,6,6">
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock Text="{Binding}" VerticalAlignment="Center" />
                                            <Button Content="✕" Margin="6,0,0,0" Padding="2,0"
                                                    Background="Transparent" BorderThickness="0"
                                                    Command="{Binding DataContext.RemoveAlertCommand,
                                                              RelativeSource={RelativeSource AncestorType=Window}}"
                                                    CommandParameter="{Binding}" />
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <DockPanel Margin="0,0,0,4">
                            <Button DockPanel.Dock="Right" Content="Add" MinWidth="48" Margin="6,0,0,0"
                                    Command="{Binding AddAlertCommand}" />
                            <TextBox Text="{Binding NewAlertText, UpdateSourceTrigger=PropertyChanged}">
                                <TextBox.InputBindings>
                                    <KeyBinding Key="Return" Command="{Binding DataContext.AddAlertCommand,
                                                RelativeSource={RelativeSource AncestorType=Window}}" />
                                </TextBox.InputBindings>
                            </TextBox>
                        </DockPanel>
```

Update the caption above: drop "(one per line)" wording, keep the rest.

- [ ] **Step 3: Tests** — add (and update any existing test that set
`AlertTextsText` — find them: `grep -rn "AlertTextsText" tests/ --include="*.cs"` —
port each to the chips API preserving its intent):

```csharp
    [Fact]
    public void AlertChipsSeedAddDedupeRemoveAndRoundTrip()
    {
        // config with alert_texts ["URGENT","FAX"] → AlertTerms has both in order
        vm.NewAlertText = "  legal  ";
        vm.AddAlertCommand.Execute(null);
        Assert.Equal("", vm.NewAlertText);
        Assert.Equal(new[] { "URGENT", "FAX", "legal" }, vm.AlertTerms);
        vm.NewAlertText = "urgent";               // case-dup
        vm.AddAlertCommand.Execute(null);
        Assert.Equal(3, vm.AlertTerms.Count);     // no-op, box cleared
        Assert.Equal("", vm.NewAlertText);
        vm.RemoveAlertCommand.Execute("FAX");
        var built = /* build per file conventions */;
        Assert.Equal(new[] { "URGENT", "legal" }, built.AlertTexts);
    }
```

- [ ] **Step 4: Run the Wpf suite** — all green.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests
git commit -m "feat(settings): alert terms edited as chips

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 5: Demo sections + full gate + push

**Files:**
- Modify: `tools/OrdoSort.Smoke/DemoWorkbench.cs` (the `watches` definitions feeding line ~504)

- [ ] **Step 1: Demo sections.** Find the `watches` local (the tuple/record list
whose members `w.Label/w.Path/w.Recursive/w.Filetypes/w.Color` feed the
`WatchFolders` projection at ~line 504). Add a `Section` member: the first two
watch folders get `"Incoming"`, the third gets `"Failed queues"` (adapt member
syntax to the local's actual shape), and add `Section = w.Section` to the
`WatchFolder` projection. reset.bat's `DemoReset` is untouched (its single
folder proves the default-section fallback).

- [ ] **Step 2: Generator checks** — `dotnet run --project tools/OrdoSort.Smoke -- demo-full` → "All checks passed"; open `demo-full/monitored-folders.json` and confirm the section values landed.

- [ ] **Step 3: Full gate** — `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean, everything green (record totals; expect Core 352 + Wpf ~285).

- [ ] **Step 4: Launch sanity** — build Debug, Start-Process with `--config demo-full/config.json`, ~5s, confirm the OrdoSort process + window (the Ready dashboard should now show two headed groups — if a screenshotless check is all that's possible, the process/window check suffices; the grouped rendering is covered by Task 2's tests), Stop-Process, confirm none remains.

- [ ] **Step 5: Commit + push**

```bash
git add tools/OrdoSort.Smoke
git commit -m "feat(demo): sectioned monitored folders in the full workbench

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
git push origin main && git ls-remote origin main
```

Expected: fast-forward; SHAs match; no tags.
