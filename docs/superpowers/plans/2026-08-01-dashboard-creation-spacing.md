# Dashboard Contextual Creation + Spacing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make section/folder creation contextual on the Settings → Dashboard tab (per-header ＋, section-inheriting Add folder, an Add section button that opens the new header for naming) and land the four approved spacing fixes.

**Architecture:** All creation semantics are `SettingsViewModel` methods (`AddFolderToSection`, the reworked `AddWatchCommand`, `AddSection`) so they're unit-testable; the XAML adds two small buttons and one code-behind focus hook reusing the existing ✎ pattern. The spacing fixes are four attribute-level XAML edits in the same tab.

**Tech Stack:** C#/.NET 8 WPF, xUnit. Build `dotnet build`; test `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`.

## Global Constraints

- Delivery directly on `main` (user-approved), one commit per task. The PUSH happens only after the final whole-branch review, by the controller — no task pushes.
- Commit messages end with the two trailers, exactly:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT`
- The tree is LF-only.
- Sections keep existing only through folders — no phantom empty sections, NO config schema changes, no dashboard (Ready) changes, no changes to rename/drag/grouping rules or Remove/↑/↓ semantics.
- Spacing values, verbatim from the spec: Section combo column 180 → 140; "Section:" label left margin 12 → 8 (right margin stays 10); chips ScrollViewer MaxHeight 64 → 60 plus `Padding="0,0,6,0"`; section-header template root `Margin="0,6,0,2"`; the five file-type checkbox margins 14 → 10.
- Known harness quirk: the smoke `screenshots` command always exits 1; success = PNGs produced.

---

### Task 1: VM — contextual creation methods (with tests)

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes (existing): `WatchFolders`, `SelectedWatch`, `WatchRows`, `WatchSectionVm { Header, IsDefault, IsEditing, EditText }`, `BeginSectionRename(WatchSectionVm)`, `WatchEditVm`, the `WatchCfg(params (string Label, string Section)[])` test helper added by the rework, `AddWatchCommand` (RelayCommand).
- Produces (Task 2 calls these exact names):
  - `public void AddFolderToSection(WatchSectionVm h)`
  - `public WatchSectionVm? AddSection()`
  - the reworked `AddWatchCommand` body (same property, new behavior).

- [ ] **Step 1: Write the failing tests**

Append inside the test class in `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter "FullyQualifiedName~PerHeaderAdd|FullyQualifiedName~AddFolder|FullyQualifiedName~AddSectionGenerates"`
Expected: compile FAILURE (`AddFolderToSection` / `AddSection` don't exist). That is this task's red state.

- [ ] **Step 3: Add the two creation methods**

In `SettingsViewModel`, directly after `CancelSectionRename`, add:

```csharp
    /// <summary>Per-header ＋: a new folder born INTO that section, landing
    /// right after the group's last member so it appears where you clicked
    /// (an empty group's folder lands at the end of the flat list).</summary>
    public void AddFolderToSection(WatchSectionVm h)
    {
        var vm = new WatchEditVm
        {
            Label = "New folder",
            Section = h.IsDefault ? "" : h.Header,
        };
        var last = WatchFolders.LastOrDefault(w => h.IsDefault
            ? w.Section.Trim().Length == 0
            : string.Equals(w.Section.Trim(), h.Header, StringComparison.CurrentCultureIgnoreCase));
        var at = last is null ? WatchFolders.Count : WatchFolders.IndexOf(last) + 1;
        WatchFolders.Insert(at, vm);
        SelectedWatch = vm;
    }

    /// <summary>"Add section": a uniquely named section born with one folder
    /// inside (sections only exist through folders), its header opened
    /// straight into rename mode so the next keystrokes name it. Returns the
    /// header so the window can focus its edit box.</summary>
    public WatchSectionVm? AddSection()
    {
        var name = "New section";
        for (var n = 2; SectionKeyExists(name); n++)
            name = $"New section {n}";
        var vm = new WatchEditVm { Label = "New folder", Section = name };
        WatchFolders.Add(vm);
        SelectedWatch = vm;
        var header = WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h =>
            !h.IsDefault && string.Equals(h.Header, name, StringComparison.CurrentCultureIgnoreCase));
        if (header is not null) BeginSectionRename(header);
        return header;
    }

    private bool SectionKeyExists(string name) =>
        WatchFolders.Any(w => string.Equals(w.Section.Trim(), name, StringComparison.CurrentCultureIgnoreCase));
```

- [ ] **Step 4: Rework AddWatchCommand**

In the ctor, the current body

```csharp
        AddWatchCommand = new RelayCommand(() =>
        {
            var vm = new WatchEditVm { Label = "New folder" };
            WatchFolders.Add(vm);
            SelectedWatch = vm;
        });
```

becomes

```csharp
        AddWatchCommand = new RelayCommand(() =>
        {
            // "Add folder": born into the SELECTED folder's section, right
            // after it — not teleported to the default group at the far end
            var vm = new WatchEditVm
            {
                Label = "New folder",
                Section = SelectedWatch?.Section ?? "",
            };
            var at = SelectedWatch is { } sel ? WatchFolders.IndexOf(sel) + 1 : WatchFolders.Count;
            WatchFolders.Insert(at, vm);
            SelectedWatch = vm;
        });
```

- [ ] **Step 5: Run the new tests, then both full suites**

Run: the Step 2 filter command → all five PASS.
Run: `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`
Expected: green (Wpf 322 + 5 = 327; Core 359).

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs
git commit -m "feat(wpf): contextual folder/section creation methods for the dashboard list"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 2: XAML + code-behind — creation buttons and the four spacing fixes

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Dashboard TabItem, ~lines 513-845)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes (Task 1, exact): `_vm.AddFolderToSection(WatchSectionVm)`, `_vm.AddSection()` returning `WatchSectionVm?`, reworked `AddWatchCommand`.
- Produces: final UI. No new public surface.

- [ ] **Step 1: Buttons row**

In the Dashboard tab's `WrapPanel` (under the folder list), replace

```xml
                                    <Button Content="Add" Command="{Binding AddWatchCommand}" MinWidth="48" Margin="0,0,4,4" />
```

with

```xml
                                    <Button Content="Add folder" Command="{Binding AddWatchCommand}" Margin="0,0,4,4" />
                                    <Button Content="Add section" Click="OnAddSectionClick" Margin="0,0,4,4" />
```

(Remove/↑/↓ stay untouched.)

- [ ] **Step 2: Per-header ＋ button**

In the `WatchSectionVm` DataTemplate's horizontal `StackPanel`, directly after the ✎ Button, add:

```xml
                                                    <Button Content="＋" Margin="2,0,0,0" Padding="3,0"
                                                            Background="Transparent" BorderThickness="0"
                                                            Click="OnSectionAddFolderClick"
                                                            ToolTip="Add a folder to this section"
                                                            AutomationProperties.Name="{Binding Header, StringFormat=Add folder to section {0}}" />
```

- [ ] **Step 3: The four spacing fixes**

a) Label/Section row: the fourth `ColumnDefinition` `Width="180"` → `Width="140"`; the "Section:" TextBlock's `Margin="12,0,10,0"` → `Margin="8,0,10,0"`.

b) Alerts chips: the footer `ScrollViewer` `MaxHeight="64"` → `MaxHeight="60"`, and add `Padding="0,0,6,0"` to it.

c) Section-header template: the `WatchSectionVm` DataTemplate's root `<Grid>` → `<Grid Margin="0,6,0,2">`.

d) File types: all five type CheckBoxes ("Any file", "PDF", "TIFF", "JPEG", "PNG") change `Margin="0,2,14,2"` → `Margin="0,2,10,2"`.

- [ ] **Step 4: Code-behind handlers**

In `SettingsWindow.xaml.cs`, beside `OnSectionRenameClick`, add:

```csharp
    private void OnSectionAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WatchSectionVm h }) _vm.AddFolderToSection(h);
    }

    private void OnAddSectionClick(object sender, RoutedEventArgs e)
    {
        if (_vm.AddSection() is not { } header) return;
        // container generation is async — focus the header's edit box after
        // the rebuilt list has generated it
        Dispatcher.BeginInvoke(new Action(() =>
        {
            WatchList.ScrollIntoView(header);
            WatchList.UpdateLayout();
            if (WatchList.ItemContainerGenerator.ContainerFromItem(header) is ListBoxItem item
                && FindDescendant<TextBox>(item) is { IsVisible: true } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }
```

- [ ] **Step 5: Build, both suites, dialogs smoke**

Run: `dotnet build`, `dotnet test tests/OrdoSort.Wpf.Tests`, `dotnet test tests/OrdoSort.Core.Tests`
Expected: clean; Wpf 327; Core 359.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs`
Expected: exit 0 ("DIALOGS OK").

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf/Windows/SettingsWindow.xaml src/OrdoSort.Wpf/Windows/SettingsWindow.xaml.cs
git commit -m "feat(wpf): contextual creation buttons + dashboard spacing fixes"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 3: Gate + fresh captures (NO push)

**Files:**
- No source changes. If a gate step fails, STOP and report; do not fix.

**Interfaces:**
- Consumes: Tasks 1-2 committed on `main`.
- Produces: recorded totals + fresh Dashboard-tab PNGs for the user's acceptance pass. The push is the controller's, after the final review.

- [ ] **Step 1: Release build + both suites**

Run: `dotnet build -c Release`, `dotnet test tests/OrdoSort.Wpf.Tests -c Release`, `dotnet test tests/OrdoSort.Core.Tests -c Release`
Expected: clean; record exact totals (expected Wpf 327 + Core 359 = 686).

- [ ] **Step 2: Smokes**

Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs` → exit 0.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- demo-full` → "All checks passed".

- [ ] **Step 3: Fresh Dashboard-tab captures**

Run (PowerShell, from `C:\Users\stoic\.claude\jobs\ae22c4bb\tmp\DashShot`):
`dotnet run -c Release -- "C:\Users\stoic\.claude\jobs\ae22c4bb\tmp\dashshots-after"`
Expected: prints "DASHSHOT OK" (exit 0) and writes `SettingsDashboard-light.png` + `SettingsDashboard-dark.png` to that folder. This scratch harness renders the Settings window with the Dashboard tab selected against a stress config — it is how the four spacing defects were found, so the after-shots are the verification.
