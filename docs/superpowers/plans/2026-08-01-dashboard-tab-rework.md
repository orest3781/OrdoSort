# Dashboard Tab Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make everything on the Settings → Dashboard tab visible at the default window size, with the folder list doubling as a real section manager (grouped headers, one-action rename, drag between sections).

**Architecture:** The left folder list becomes a composite flat projection (`WatchRows`: section-header rows interleaved with folder rows) built in `SettingsViewModel` by the same grouping rules the dashboard's `TileGroups` uses; all management semantics (rename/merge/blank/default-heading/drop) are VM methods, so they're unit-testable and the XAML/code-behind stays thin. The tab's XAML compacts: top heading row deleted (the default group's header edits `MonitorTitle` now), detail form loses two rows, and the Alerts/polling footer becomes two columns at half height with the chips capped.

**Tech Stack:** C#/.NET 8 WPF, xUnit. Build `dotnet build`; test `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`.

## Global Constraints

- Delivery directly on `main` (user-approved), one commit per task, push ONLY in Task 3 after the full gate.
- Commit messages end with the two trailers, exactly:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT`
- The tree is LF-only.
- NO config schema changes: `section` stays a per-folder string, `monitor_title` unchanged, monitored-folders.json format untouched. No dashboard (Ready screen) changes.
- Grouping rules, verbatim from the spec: "first-seen over flat folder order, trimmed + case-insensitive keys, first-seen casing wins — the same rules `TileGroups` uses on Ready." The default (blank-section) group is ALWAYS shown in the Settings list, even empty (pinned first when empty), and its header edits `MonitorTitle`.
- The Section ComboBox's `IsTextSearchEnabled="False"` and its explanatory comment block are load-bearing and MUST be preserved verbatim wherever that ComboBox moves.
- Alert behavior (comma/newline splitting, case-insensitive dedupe, removal) and polling behavior are UNCHANGED — layout only.
- Known harness quirk: the smoke `screenshots` command always exits 1 (its unconditional WebView2 NOTE is counted as a failure). Success for screenshots = PNGs produced, not exit code.

---

### Task 1: VM — grouped projection + section management (with tests)

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: existing members — `WatchFolders` (ObservableCollection<WatchEditVm>), `SelectedWatch`, `MonitorTitle`, `HookWatch`, the ctor's `WatchFolders.CollectionChanged` handler, `RecomputeTilePreview()`, `SectionChoices`.
- Produces (Task 2 binds/calls these exact names):
  - `public sealed class WatchSectionVm : ObservableObject` with `string Header {get;init;}`, `bool IsDefault {get;init;}`, `bool IsEditing {get;set;}`, `string EditText {get;set;}` — in the `OrdoSort.Wpf.ViewModels` namespace (same file, beside `WatchEditVm`).
  - `public ObservableCollection<object> WatchRows { get; }`
  - `public object? SelectedWatchRow { get; set; }`
  - `public void BeginSectionRename(WatchSectionVm h)`
  - `public void CommitSectionRename(WatchSectionVm h)`
  - `public void CancelSectionRename(WatchSectionVm h)`
  - `public void DropWatch(WatchEditVm dragged, object? over)`

- [ ] **Step 1: Write the failing tests**

Append to `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs` (inside the existing class; `_dir`/`_dialogs` are the class's existing fixtures):

```csharp
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
        Assert.Equal("A", (vm.SelectedWatch)!.Label);
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
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~WatchRows|FullyQualifiedName~Rename|FullyQualifiedName~DropOn|FullyQualifiedName~TypingANewSection|FullyQualifiedName~SelectingAHeaderRow"`
Expected: compile FAILURE (`WatchSectionVm`, `WatchRows`, `DropWatch` … don't exist yet). That is the red state for this task.

- [ ] **Step 3: Add `WatchSectionVm`**

In `SettingsViewModel.cs`, directly after the `WatchEditVm` class ends, add:

```csharp
/// <summary>A section-header row in the composite folder list (Dashboard
/// tab). The header is the section's display name — first-seen casing for
/// named groups, MonitorTitle (or "(untitled)") for the default group.</summary>
public sealed class WatchSectionVm : ObservableObject
{
    private bool _isEditing;
    private string _editText = "";

    public string Header { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
}
```

- [ ] **Step 4: Add the projection, the selection adapter, and the management methods**

All in `SettingsViewModel`:

a) Beside the other collections (near `WatchFolders`), add:

```csharp
    /// <summary>The Dashboard tab's left list: section headers interleaved
    /// with their member folders, grouped by the same rules TileGroups uses
    /// on Ready (first-seen over flat order, trimmed case-insensitive keys,
    /// first-seen casing wins). The default (blank-section) group always
    /// exists here — it is the edit surface for MonitorTitle and the drop
    /// target for clearing a folder's section — and pins first when empty.</summary>
    public ObservableCollection<object> WatchRows { get; } = new();

    /// <summary>ListBox adapter over WatchRows: only folder rows are
    /// selectable. Selecting a header (or the null WPF pushes during a
    /// rebuild) is rejected and the visual selection snaps back.</summary>
    public object? SelectedWatchRow
    {
        get => SelectedWatch;
        set
        {
            if (value is WatchEditVm w) SelectedWatch = w;
            else Raise(nameof(SelectedWatchRow));
        }
    }
```

b) The rebuild, placed next to `RecomputeTilePreview()`:

```csharp
    private void RebuildWatchRows()
    {
        if (WatchFolders is null) return;   // ctor assigns MonitorTitle before the list exists

        var editing = WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h => h.IsEditing);

        var order = new List<WatchSectionVm>();
        var members = new Dictionary<WatchSectionVm, List<WatchEditVm>>();
        var byKey = new Dictionary<string, WatchSectionVm>(StringComparer.CurrentCultureIgnoreCase);
        WatchSectionVm? def = null;

        foreach (var w in WatchFolders)
        {
            var key = w.Section.Trim();
            WatchSectionVm h;
            if (key.Length == 0)
            {
                if (def is null)
                {
                    def = new WatchSectionVm { Header = DefaultSectionHeader, IsDefault = true };
                    order.Add(def);
                    members[def] = new List<WatchEditVm>();
                }
                h = def;
            }
            else if (!byKey.TryGetValue(key, out h!))
            {
                h = new WatchSectionVm { Header = key };
                byKey[key] = h;
                order.Add(h);
                members[h] = new List<WatchEditVm>();
            }
            members[h].Add(w);
        }
        if (def is null)
        {
            def = new WatchSectionVm { Header = DefaultSectionHeader, IsDefault = true };
            order.Insert(0, def);
            members[def] = new List<WatchEditVm>();
        }

        WatchRows.Clear();
        foreach (var h in order)
        {
            WatchRows.Add(h);
            foreach (var w in members[h]) WatchRows.Add(w);
        }

        // a header mid-rename survives the rebuild its own edits trigger
        if (editing is not null)
        {
            var again = WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h =>
                h.IsDefault == editing.IsDefault
                && (h.IsDefault || string.Equals(h.Header, editing.Header,
                        StringComparison.CurrentCultureIgnoreCase)));
            if (again is not null)
            {
                again.EditText = editing.EditText;
                again.IsEditing = true;
            }
        }

        Raise(nameof(SelectedWatchRow));   // re-point the ListBox at the kept selection
    }

    private string DefaultSectionHeader =>
        MonitorTitle.Trim().Length == 0 ? "(untitled)" : MonitorTitle;
```

c) The management methods, directly after `RebuildWatchRows`:

```csharp
    public void BeginSectionRename(WatchSectionVm h)
    {
        h.EditText = h.IsDefault ? MonitorTitle : h.Header;
        h.IsEditing = true;
    }

    /// <summary>Apply a header edit: the default group's header IS
    /// MonitorTitle; a named group's rename rewrites the Section of every
    /// member (trimmed, case-insensitive match) — so renaming onto another
    /// section merges, and renaming to blank moves members to the default.</summary>
    public void CommitSectionRename(WatchSectionVm h)
    {
        if (!h.IsEditing) return;
        h.IsEditing = false;
        var t = h.EditText.Trim();
        if (h.IsDefault)
        {
            MonitorTitle = t;
            return;
        }
        foreach (var w in WatchFolders)
            if (string.Equals(w.Section.Trim(), h.Header, StringComparison.CurrentCultureIgnoreCase))
                w.Section = t;
    }

    public void CancelSectionRename(WatchSectionVm h) => h.IsEditing = false;

    /// <summary>Drop semantics for the grouped list: the drop position
    /// implies both the new section and the new flat position.</summary>
    public void DropWatch(WatchEditVm dragged, object? over)
    {
        if (ReferenceEquals(dragged, over)) return;
        switch (over)
        {
            case WatchEditVm target:
            {
                dragged.Section = target.Section;
                var from = WatchFolders.IndexOf(dragged);
                var to = WatchFolders.IndexOf(target);
                if (from >= 0 && to >= 0 && from != to) WatchFolders.Move(from, to);
                break;
            }
            case WatchSectionVm h:
            {
                dragged.Section = h.IsDefault ? "" : h.Header;
                var first = WatchFolders.FirstOrDefault(w =>
                    !ReferenceEquals(w, dragged)
                    && (h.IsDefault
                        ? w.Section.Trim().Length == 0
                        : string.Equals(w.Section.Trim(), h.Header,
                            StringComparison.CurrentCultureIgnoreCase)));
                var from = WatchFolders.IndexOf(dragged);
                var to = first is null ? WatchFolders.Count - 1 : WatchFolders.IndexOf(first);
                if (from >= 0 && to >= 0 && from != to) WatchFolders.Move(from, to);
                break;
            }
            case null:
            {
                var last = WatchFolders.LastOrDefault(w => !ReferenceEquals(w, dragged));
                if (last is not null)
                {
                    dragged.Section = last.Section;
                    var from = WatchFolders.IndexOf(dragged);
                    if (from >= 0) WatchFolders.Move(from, WatchFolders.Count - 1);
                }
                break;
            }
        }
        SelectedWatch = dragged;
    }
```

- [ ] **Step 5: Wire the triggers**

a) `MonitorTitle` (currently `public string MonitorTitle { get => _monitorTitle; set => Set(ref _monitorTitle, value); }`) becomes:

```csharp
    public string MonitorTitle
    {
        get => _monitorTitle;
        set { if (Set(ref _monitorTitle, value)) RebuildWatchRows(); }
    }
```

b) `HookWatch` (currently one line calling only `RecomputeTilePreview`) becomes:

```csharp
    private void HookWatch(WatchEditVm w) =>
        w.PropertyChanged += (_, e) =>
        {
            RecomputeTilePreview();
            if (e.PropertyName is nameof(WatchEditVm.Section))
            {
                RebuildWatchRows();
                Raise(nameof(SectionChoices));
            }
        };
```

c) In the ctor's `WatchFolders.CollectionChanged` handler, add `RebuildWatchRows();` immediately after the existing `RecomputeTilePreview();` line, and add a `RebuildWatchRows();` call on its own line right after the ctor's final `RecomputeTilePreview();`.

d) In the `SelectedWatch` setter's `if (Set(...))` block, add `Raise(nameof(SelectedWatchRow));` after the existing `Raise(nameof(SectionChoices));`.

- [ ] **Step 6: Run the new tests, then both full suites**

Run: the Step 2 filter command → all new tests PASS.
Run: `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`
Expected: green (Wpf 312 + 10 new = 322; Core 359 untouched).

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs
git commit -m "feat(wpf): grouped section projection + management for the dashboard folder list"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 2: XAML + code-behind — grouped list UI, compacted form, two-column footer

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Dashboard TabItem, lines ~512-845)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs` (drag handlers ~97-142; new section-edit handlers)

**Interfaces:**
- Consumes (from Task 1, exact): `WatchRows` (ObservableCollection<object>), `SelectedWatchRow` (object?), `WatchSectionVm { Header, IsDefault, IsEditing, EditText }`, `_vm.BeginSectionRename(h)`, `_vm.CommitSectionRename(h)`, `_vm.CancelSectionRename(h)`, `_vm.DropWatch(dragged, over)`.
- Produces: the final tab layout. No new public surface.

- [ ] **Step 1: Delete the top heading band and renumber the rows**

In the Dashboard `TabItem`'s root `Grid`: delete one `<RowDefinition Height="Auto" />` (four become three) and delete the whole first `<StackPanel>` (the "Section heading:" `FieldRow` grid plus its `NoteText` line). Then: the `"Monitored folders"` `SectionText` TextBlock loses `Grid.Row="1"` and its margin becomes `Margin="0,0,0,10"`; the folder-editor `Grid` changes `Grid.Row="2"` → `Grid.Row="1"`; the footer `StackPanel` (replaced in Step 4) is `Grid.Row="2"`.

- [ ] **Step 2: Replace the flat list with the grouped composite list**

Replace the whole `<ListBox x:Name="WatchList" …>…</ListBox>` element with:

```xml
                                <ListBox x:Name="WatchList" ItemsSource="{Binding WatchRows}"
                                         SelectedItem="{Binding SelectedWatchRow}" AllowDrop="True"
                                         PreviewMouseLeftButtonDown="List_DragArm"
                                         PreviewMouseMove="List_DragMove"
                                         Drop="List_Drop" DragOver="List_DragOver">
                                    <ListBox.Resources>
                                        <DataTemplate DataType="{x:Type vm:WatchSectionVm}">
                                            <Grid>
                                                <StackPanel Orientation="Horizontal">
                                                    <StackPanel.Style>
                                                        <Style TargetType="StackPanel">
                                                            <Style.Triggers>
                                                                <DataTrigger Binding="{Binding IsEditing}" Value="True">
                                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                                </DataTrigger>
                                                            </Style.Triggers>
                                                        </Style>
                                                    </StackPanel.Style>
                                                    <TextBlock Text="{Binding Header}" FontWeight="SemiBold"
                                                               TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                                                    <Button Content="✎" Margin="6,0,0,0" Padding="3,0"
                                                            Background="Transparent" BorderThickness="0"
                                                            Click="OnSectionRenameClick"
                                                            ToolTip="Rename this section — updates every folder in it"
                                                            AutomationProperties.Name="{Binding Header, StringFormat=Rename section {0}}" />
                                                </StackPanel>
                                                <TextBox Text="{Binding EditText, UpdateSourceTrigger=PropertyChanged}"
                                                         LostFocus="OnSectionEditLostFocus"
                                                         PreviewKeyDown="OnSectionEditKeyDown"
                                                         AutomationProperties.Name="{Binding Header, StringFormat=Section name {0}}">
                                                    <TextBox.Style>
                                                        <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                                                            <Setter Property="Visibility" Value="Collapsed" />
                                                            <Style.Triggers>
                                                                <DataTrigger Binding="{Binding IsEditing}" Value="True">
                                                                    <Setter Property="Visibility" Value="Visible" />
                                                                </DataTrigger>
                                                            </Style.Triggers>
                                                        </Style>
                                                    </TextBox.Style>
                                                </TextBox>
                                            </Grid>
                                        </DataTemplate>
                                        <DataTemplate DataType="{x:Type vm:WatchEditVm}">
                                            <StackPanel Orientation="Horizontal" Margin="12,0,0,0">
                                                <Rectangle Width="12" Height="12" RadiusX="2" RadiusY="2"
                                                           VerticalAlignment="Center" Margin="0,0,7,0"
                                                           Fill="{Binding Color, Converter={StaticResource ColorStringToBrush}}"
                                                           Stroke="{DynamicResource Theme.Border}" StrokeThickness="1" />
                                                <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" />
                                            </StackPanel>
                                        </DataTemplate>
                                    </ListBox.Resources>
                                </ListBox>
```

- [ ] **Step 3: Compact the detail form**

a) Replace the two separate `FieldRow` grids for "Label:" and "Section:" with ONE grid. The `IsTextSearchEnabled` comment block moves with the ComboBox VERBATIM (copy it exactly from the current file):

```xml
                                <Grid Style="{StaticResource FieldRow}">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="110" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="Auto" />
                                        <ColumnDefinition Width="180" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="Label:" Style="{StaticResource FieldLabel}" />
                                    <TextBox Grid.Column="1" Text="{Binding Label, UpdateSourceTrigger=PropertyChanged}" />
                                    <TextBlock Grid.Column="2" Text="Section:" Style="{StaticResource FieldLabel}" Margin="12,0,10,0" />
                                    <!-- IsTextSearchEnabled=False: [FULL ORIGINAL COMMENT BLOCK, VERBATIM] -->
                                    <ComboBox Grid.Column="3" IsEditable="True" IsTextSearchEnabled="False"
                                              ItemsSource="{Binding DataContext.SectionChoices,
                                                            RelativeSource={RelativeSource AncestorType=Window}}"
                                              Text="{Binding Section, UpdateSourceTrigger=PropertyChanged}"
                                              ToolTip="Dashboard group this folder appears under — or manage sections directly in the list" />
                                </Grid>
```

b) Replace the "Tile color:" `FieldRow` grid AND the swatches `ItemsControl` that follows it with one merged grid — the swatch `ItemsControl` content (ItemsPanel + ItemTemplate with `OnWatchSwatch`, `SwatchCheck`, `SelectedWatch.Color` MultiBinding) is kept VERBATIM, only re-parented:

```xml
                                <Grid Style="{StaticResource FieldRow}">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="110" />
                                        <ColumnDefinition Width="120" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="Tile color:" Style="{StaticResource FieldLabel}"
                                               VerticalAlignment="Top" Margin="0,4,10,0" />
                                    <TextBox Grid.Column="1" VerticalAlignment="Top"
                                             Text="{Binding Color, UpdateSourceTrigger=PropertyChanged}" />
                                    <ItemsControl Grid.Column="2" Margin="8,0,0,0"
                                                  ItemsSource="{x:Static vm:SettingsViewModel.SwatchColors}">
                                        [ORIGINAL ItemsPanel + ItemTemplate, VERBATIM]
                                    </ItemsControl>
                                </Grid>
```

Everything else in the detail pane (Folder row, Problem/"Create it", file types, "include subfolders", tile preview) stays byte-identical.

- [ ] **Step 4: Replace the footer with the two-column layout**

Replace the entire footer `<StackPanel Grid.Row="3">…</StackPanel>` (Alerts header through the poll caption) with — the chip `ItemsControl`'s ItemsPanel/ItemTemplate, the add-box `DockPanel`, and the four preset chip `Button`s are kept VERBATIM from the current file, only re-parented:

```xml
                    <Grid Grid.Row="2" Margin="0,14,0,0">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="300" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Text="Alerts" Style="{StaticResource SectionText}" Margin="0,0,0,6" />
                            <TextBlock Style="{StaticResource SubtleText}" TextWrapping="Wrap"
                                       Text="Filenames containing these terms flash the tile and the inbox count red (ignores case):" />
                            <ScrollViewer MaxHeight="64" VerticalScrollBarVisibility="Auto"
                                          HorizontalScrollBarVisibility="Disabled" Focusable="False"
                                          Margin="0,4,0,4">
                                <ItemsControl ItemsSource="{Binding AlertTerms}" Focusable="False">
                                    [ORIGINAL chip ItemsPanel + ItemTemplate, VERBATIM]
                                </ItemsControl>
                            </ScrollViewer>
                            [ORIGINAL add-box DockPanel, VERBATIM]
                            <CheckBox Content="Flash alerts (uncheck for a steady highlight)"
                                      IsChecked="{Binding FlashAlerts}" />
                        </StackPanel>
                        <StackPanel Grid.Column="1" Margin="16,0,0,0">
                            <TextBlock Text="Polling" Style="{StaticResource SectionText}" Margin="0,0,0,6" />
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="Check folders every" VerticalAlignment="Center" Margin="0,0,8,0" />
                                <TextBox Width="52"
                                         Text="{Binding PollSecondsText, UpdateSourceTrigger=PropertyChanged}"
                                         AutomationProperties.Name="Check folders every seconds" />
                                <TextBlock Text="sec" VerticalAlignment="Center" Margin="6,0,0,0" />
                            </StackPanel>
                            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                [ORIGINAL four preset chip Buttons, VERBATIM]
                            </StackPanel>
                            <TextBlock Style="{StaticResource CaptionText}" TextWrapping="Wrap" Margin="0,6,0,0"
                                       Text="Lower = alerts appear sooner; higher = gentler on a network share." />
                        </StackPanel>
                    </Grid>
```

- [ ] **Step 5: Code-behind — route drops through DropWatch; add the header-edit handlers; guard header drags**

In `SettingsWindow.xaml.cs`:

a) In `List_Drop`, replace the `WatchList` branch body:

```csharp
        else if (list == WatchList && e.Data.GetData(typeof(WatchEditVm)) is WatchEditVm watch)
        {
            _vm.DropWatch(watch, over);
        }
```

b) In `List_DragMove`, before the `DragDrop.DoDragDrop(...)` line, add:

```csharp
        if (item is not (RouteEditVm or WatchEditVm)) return;   // headers don't drag
```

c) Add three handlers beside `OnWatchSwatch`:

```csharp
    private void OnSectionRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WatchSectionVm h } btn) return;
        _vm.BeginSectionRename(h);
        // focus the edit box once its Visible state has applied
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (btn.Parent is StackPanel { Parent: Grid g })
                foreach (var child in g.Children)
                    if (child is TextBox tb) { tb.Focus(); tb.SelectAll(); }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSectionEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: WatchSectionVm h }) _vm.CommitSectionRename(h);
    }

    private void OnSectionEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WatchSectionVm h }) return;
        if (e.Key == Key.Enter) { _vm.CommitSectionRename(h); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelSectionRename(h); e.Handled = true; }
    }
```

- [ ] **Step 6: Build, run both suites, run the dialogs smoke**

Run: `dotnet build`, `dotnet test tests/OrdoSort.Wpf.Tests`, `dotnet test tests/OrdoSort.Core.Tests`
Expected: clean build; Wpf 322 green; Core 359 green.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs`
Expected: exit 0 ("DIALOGS OK") — this constructs SettingsWindow against the new XAML, catching template/binding wiring errors headlessly.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/SettingsWindow.xaml src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs
git commit -m "feat(wpf): dashboard tab rework — grouped list UI, compacted form, two-column footer"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 3: Full gate, acceptance screenshots, push

**Files:**
- No source changes expected. Delivery gate only — if a gate step fails, STOP and report; do not fix.

**Interfaces:**
- Consumes: Tasks 1-2 committed on `main`.
- Produces: `origin/main` updated; light+dark screenshots for the user's visual acceptance pass.

- [ ] **Step 1: Release build + both suites**

Run: `dotnet build -c Release`, `dotnet test tests/OrdoSort.Wpf.Tests -c Release`, `dotnet test tests/OrdoSort.Core.Tests -c Release`
Expected: clean; record exact totals (expected Wpf 322 + Core 359 = 681).

- [ ] **Step 2: Smokes**

Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs` → exit 0.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- demo-full` → prints "All checks passed".

- [ ] **Step 3: Acceptance screenshots**

Run (PowerShell): `dotnet run --project tools/OrdoSort.Smoke -c Release -- screenshots "$env:TEMP\ordo-dashboard-rework" both`
Expected: PNGs produced for both themes. Per Global Constraints the command ALWAYS exits 1 (unconditional WebView2 note) — success = the PNG set exists. Report the folder path and the Settings PNG filenames.

- [ ] **Step 4: Push (ancestry-checked, never force)**

```bash
git fetch origin
git merge-base --is-ancestor origin/main HEAD && git push origin main
```

Expected: fast-forward push accepted.
