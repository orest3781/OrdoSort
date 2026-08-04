# Audit Remediation — Finishing Plan (Tasks 10–12)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the last three packages of `2026-08-02-audit-remediation.md` — visual consistency, the verify-then-decide items and minors, and the release gate — then push the 21+ accumulated commits to `origin/main`.

**Architecture:** The original Task 10 is split into three independently reviewable packages (caption sizing, shared field rows, primary-button/rhythm) because a reviewer can meaningfully reject any one of them while approving its neighbours, and because Task 3 below is the only one that can move pixels in four windows at once. Original Tasks 11 and 12 keep their shape. Every package ends green with its own tests and its own commit; nothing is pushed until the final gate.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`.

## Global Constraints

Inherited verbatim from `docs/superpowers/plans/2026-08-02-audit-remediation.md` — all of them still bind:

- **Decisions already taken (do not re-open):** invariant dates are **forward-only**; High Contrast is **detect-and-step-aside**.
- **Proof standard, non-negotiable:** demonstrate the failing state BEFORE the fix; **confirm the compiled assembly under test lacks the fix before trusting any "before" measurement**; find glyphs by scanning for max contrast, never a hand-picked coordinate; render both palettes where appearance is involved.
- **Harness rules:** replicate `SmokeUi.Boot`; re-apply `ShutdownMode` AFTER `InitializeComponent` or windows render 0x0; drain the dispatcher at `DispatcherPriority.Render` before reading or capturing; popups/drop-downs are separate HWNDs — render the popup's `Child`. Delete harnesses when done.
- **The precedence trap (bitten 5×, both directions):** a style Setter outranks INHERITANCE (bare/auto-wrapped `TextBlock` labels need a LOCAL `Foreground` bound to the container ancestor); a LOCAL value outranks a NAMED STYLE's Setter (never blanket-apply that remedy to a label already carrying `Style="{StaticResource SubtleText}"` — use `Style BasedOn` + a `DataTrigger` instead). `ControlTemplate.Resources` is measured non-functional for this trap.
- Do NOT run the smoke `screenshots` mode as a gate (always exits 1); it is fine as a rendering tool.
- Config keys, internal type names and the `routes` schema are NOT renamed by the copy work — user-facing text only.
- Commit per package; push only in the final task.

Constraints specific to this plan:

- **Baseline: 865 tests green (Core 367 + Wpf 498), build clean with 0 warnings** — measured 2026-08-03 against the working tree as it stands (Task 1's staged changes included). Suites must stay green and grow.
- **The gate command is not the obvious one.** Smart App Control blocks the test assembly by hash, so `dotnet test` alone silently skips the entire WPF suite with `Application Control policy has blocked this file (0x800711C7)` and still exits 0. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  `-t:Rebuild -p:Deterministic=false` gives the assembly a fresh MVID so the block does not apply. **A test step that reports fewer than ~498 WPF tests did not run the suite** — treat that as a failed step, not a pass.
- Tests live beside the existing suites in `tests/OrdoSort.Wpf.Tests/`. Off-screen window tests follow the established pattern: `[Collection(HighlightContrastTests.Name)]`, inject `HighlightContrastFixture`, and do UI work inside `_fx.Invoke(() => { … })`. See `HistoryWindowXamlTests.cs:23-45` for a complete worked example.

## File Structure

| File | Responsibility | Touched by |
|---|---|---|
| `src/OrdoSort.Wpf/Theme/Styles.xaml` | central styles; gains `FieldRow`/`FieldLabel`, keeps `CaptionText` | Tasks 2, 3, 4 |
| `src/OrdoSort.Wpf/Theme/ThemeManager.cs` | publishes theme brushes; gains the fixed `Light.*`/`Dark.*` keys | Task 6 |
| `src/OrdoSort.Wpf/Views/RgbToBrushConverter.cs` | `Rgb` → `Brush`; gains a cache | Task 7 |
| `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` | loses its private `FieldRow`/`FieldLabel`; preview cards re-pointed at the palette | Tasks 2, 3, 6 |
| `src/OrdoSort.Wpf/Windows/{LabelMaker,MatchMerge,BulkRename,Unlock,ManageSaved}Window.xaml` | caption sizing, shared field rows, one primary button | Tasks 2, 3, 4 |
| `src/OrdoSort.Wpf/Views/{ProcessingView,ReadyView}.xaml` | caption sizing | Task 2 |
| `tests/OrdoSort.Wpf.Tests/CaptionSizingTests.cs` | NEW — the type ramp resolves as intended | Task 2 |
| `tests/OrdoSort.Wpf.Tests/SharedFieldRowTests.cs` | NEW — the four windows' field rows match Settings' metrics | Task 3 |
| `tests/OrdoSort.Wpf.Tests/AppearancePreviewTests.cs` | NEW — preview cards cannot drift from `ThemePalette` | Task 6 |

---

### Task 1: Commit the verified in-flight work and repair the ledger

The working tree already carries the tail of the original Task 9 (copy and terminology), and it has been QC'd this session: **build clean, 0 warnings, Core 367 + Wpf 498 = 865 tests, 0 failed.** Nothing about it needs re-deriving — it needs committing. The old plan's checkboxes were also never ticked past its Task 1, so the ledger claims eight packages are outstanding that are in fact committed.

**Files:**
- Commit as-is: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`, `tests/OrdoSort.Wpf.Tests/UnexpectedErrorTests.cs`, `tests/OrdoSort.Wpf.Tests/CopyAndTerminologyTests.cs`
- Modify: `docs/superpowers/plans/2026-08-02-audit-remediation.md` (checkboxes only)

**Interfaces:**
- Produces: `ReportUnexpected(Exception ex, string action, string whereabouts = "either where it started or where it was going")` — the third parameter is optional, so no other caller changes.

- [ ] **Step 1: Re-confirm green before committing.** Do not trust this plan's baseline; measure it.

```bash
cd /s/OrdoSort
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: `0 Warning(s) 0 Error(s)`, then two `Passed!` lines — Core **367**, Wpf **498**. If the WPF line is missing or reports a skip, the Smart App Control block is back: re-run the rebuild command and try again.

- [ ] **Step 2: Commit the fix and its tests.**

```bash
git add src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs \
        tests/OrdoSort.Wpf.Tests/UnexpectedErrorTests.cs \
        tests/OrdoSort.Wpf.Tests/CopyAndTerminologyTests.cs
git commit -m "fix(ui): Undo names the two places the right way round"
```

The commit body should record the reasoning already written into the code comments: the reassurance sentence "the document is either where it started or where it was going" is true of filing and of setting aside — both begin in the inbox — but Undo runs the move in reverse, so its "where it started" is the destination folder. A user who has just pressed Undo would read that sentence and search the one place it was not describing.

- [ ] **Step 3: Tick the old ledger.** In `docs/superpowers/plans/2026-08-02-audit-remediation.md`, change `- [ ]` to `- [x]` for every step of Tasks 2–9 and append `— DONE, <sha>` to each task heading, using these commits:

| Task | Heading suffix to append |
|---|---|
| 2 Settings path checks off the UI thread | `— DONE, 536eecd (+ 3820408)` |
| 3 Enter through the command; safe history swap | `— DONE, 051d2ff` |
| 4 Invariant dates for written values | `— DONE, aa2a9f0` |
| 5 Viewer lifetime, init reporting, IME guard | `— DONE, 3e5c731 (+ b7b34ac)` |
| 6 DPI manifest + High Contrast step-aside | `— DONE, cedbfa2` |
| 7 History filtering, empty state, trimming | `— DONE, 10d5ac3` |
| 8 Keyboard and accessibility | `— DONE, ad99128 (+ dddac41, cb50988)` |
| 9 Copy and terminology | `— DONE, 8bdab38 (+ da84d2e, b837b84, and this task's commit)` |

Then add a line under the header pointing forward: `> Tasks 10–12 continue in 2026-08-03-audit-remediation-finish.md.`

- [ ] **Step 4: Commit the ledger.**

```bash
git add docs/superpowers/plans/2026-08-02-audit-remediation.md
git commit -m "docs: tick the audit-remediation ledger through Task 9"
```

---

### Task 2: Caption sizing — retire the 20 hand-written `FontSize="11"` sites

`CaptionText` already exists (`Styles.xaml:1614-1617`: `Foreground=Theme.SubtleText`, `FontSize=11`) and is barely used, while 20 call sites hand-write the size. **The sites are not interchangeable** — sweeping them uniformly would silently recolour six labels, so they are handled in three groups.

**Files:**
- Modify: `src/OrdoSort.Wpf/Views/ProcessingView.xaml:45,175`, `src/OrdoSort.Wpf/Views/ReadyView.xaml:56`, `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml:97-98`, `src/OrdoSort.Wpf/Windows/LabelMakerWindow.xaml:81,143,158,212`, `src/OrdoSort.Wpf/Windows/ManageSavedWindow.xaml:72,74`, `src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml:44,102,108`, `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml:433,930,998,1006,1279`, `src/OrdoSort.Wpf/Windows/UnlockWindow.xaml:88,110`
- Create: `tests/OrdoSort.Wpf.Tests/CaptionSizingTests.cs`
- **Do NOT touch** `Styles.xaml:1251` — that `FontWeight="SemiBold" FontSize="11"` is the Calendar `DayTitleTemplate`'s day-of-week header (Mo/Tu/We), theme infrastructure rather than app copy. Record the exemption in the commit body.

**Interfaces:**
- Consumes: `CaptionText` (existing, `Styles.xaml:1614`).
- Produces: `CaptionTextOnSurface` — a new keyed `TextBlock` style, `BasedOn` the implicit `TextBlock` style, `FontSize=11`, **no `Foreground` setter** (so it inherits `Theme.Text`). Used by Group B below.

- [ ] **Step 1: Write the failing test.** Create `tests/OrdoSort.Wpf.Tests/CaptionSizingTests.cs`. It asserts the two caption styles resolve to the intended values — `CaptionTextOnSurface` does not exist yet, so the second fact fails.

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The caption rung of the type ramp. Two styles, deliberately:
/// CaptionText is small AND de-emphasised (the overwhelmingly common case —
/// hints, notes, counts beside a control); CaptionTextOnSurface is small at
/// full text weight, for the handful of captions that carry real content the
/// user is meant to read, not skim. Before this task both were spelled
/// FontSize="11" by hand at 20 call sites, so the difference between them was
/// invisible in the XAML and drifted.
///
/// Asserted through a real Application resource lookup and a real applied
/// style, not by reading the style object's setters — a setter can be present
/// and still lose to something with higher precedence.</summary>
[Collection(HighlightContrastTests.Name)]
public class CaptionSizingTests
{
    private readonly HighlightContrastFixture _fx;
    public CaptionSizingTests(HighlightContrastFixture fx) => _fx = fx;

    private (double size, Color fore) Resolve(string styleKey)
    {
        var block = new TextBlock { Text = "sample" };
        block.Style = (Style)_fx.App.Resources[styleKey];
        var host = new Border { Child = block };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        return (block.FontSize, ((SolidColorBrush)block.Foreground).Color);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptionTextIsSmallAndDeEmphasised(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        var (size, fore) = Resolve("CaptionText");
        Assert.Equal(11d, size);
        Assert.Equal(Color.FromRgb(p.SubtleText.R, p.SubtleText.G, p.SubtleText.B), fore);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptionTextOnSurfaceIsSmallAtFullTextWeight(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        var (size, fore) = Resolve("CaptionTextOnSurface");
        Assert.Equal(11d, size);
        // the whole point of the second style: NOT SubtleText
        Assert.Equal(Color.FromRgb(p.Text.R, p.Text.G, p.Text.B), fore);
    });
}
```

- [ ] **Step 2: Run it — the second theory MUST FAIL** with a `ResourceReferenceKeyNotFoundException` or a null style for `CaptionTextOnSurface`. Paste the output.

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build \
  --filter "FullyQualifiedName~CaptionSizingTests" -v minimal
```

- [ ] **Step 3: Add the second style** in `Theme/Styles.xaml`, immediately after `CaptionText` (line 1617), inside the same type-ramp comment block:

```xml
    <!-- The caption rung at full text weight. Most captions are hints and
         should be de-emphasised (CaptionText above); these few carry content
         the user actually reads — the last action's detail line, a subfolder
         path, a live preview note — and are small only to fit, not to recede. -->
    <Style x:Key="CaptionTextOnSurface" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="FontSize" Value="11" />
    </Style>
```

- [ ] **Step 4: Group A — the 14 exact-equivalents.** Each of these already reads `Style="{StaticResource SubtleText}" FontSize="11"`, which is byte-for-byte what `CaptionText` resolves to. Replace both attributes with `Style="{StaticResource CaptionText}"`. **Zero visual change** — this is pure de-duplication.

| File | Line |
|---|---|
| `Views/ProcessingView.xaml` | 45 |
| `Windows/BulkRenameWindow.xaml` | 97-98 |
| `Windows/LabelMakerWindow.xaml` | 143, 158, 212 |
| `Windows/ManageSavedWindow.xaml` | 72, 74 |
| `Windows/MatchMergeWindow.xaml` | 102, 108 |
| `Windows/SettingsWindow.xaml` | 930, 1006, 1279 |
| `Windows/UnlockWindow.xaml` | 88, 110 |

Example, `UnlockWindow.xaml:87-90` before:

```xml
                <TextBlock Text="{Binding AddNote}" Style="{StaticResource SubtleText}"
                           FontSize="11" VerticalAlignment="Center"
                           TextTrimming="CharacterEllipsis" />
```

after:

```xml
                <TextBlock Text="{Binding AddNote}" Style="{StaticResource CaptionText}"
                           VerticalAlignment="Center"
                           TextTrimming="CharacterEllipsis" />
```

- [ ] **Step 5: Group B — the 4 bare sites.** These carry `FontSize="11"` with **no** `Style`, so they render `Theme.Text` today. Point them at `CaptionTextOnSurface`, which preserves that exactly:

| File | Line | Content |
|---|---|---|
| `Views/ProcessingView.xaml` | 175 | `{Binding LastActionDetail}` |
| `Views/ReadyView.xaml` | 56 | `{Binding SubfolderNote}` |
| `Windows/MatchMergeWindow.xaml` | 44 | the "no roster loaded yet…" placeholder |
| `Windows/SettingsWindow.xaml` | 998 | `{Binding TilePreviewNote}` |

`SettingsWindow.xaml:998` sits inside the dashboard tile preview and its sibling on line 997 has an explicit `Foreground` binding — leave that sibling alone; only the `TilePreviewNote` line changes.

**Watch `MatchMergeWindow.xaml:44`:** the element opens with `>` and carries an inline `<TextBlock.Style>` child for its visibility trigger. Adding a `Style="…"` attribute alongside an inline `TextBlock.Style` is an XAML compile error. Handle it the Group C way instead.

- [ ] **Step 6: Group C — the 2 sites with an inline `<TextBlock.Style>`.** `LabelMakerWindow.xaml:81` (`{Binding NextNumberText}`) and `SettingsWindow.xaml:433` (`{Binding GestureText}`) each declare an inline style with triggers. An attribute `Style=` cannot coexist with it. Instead, delete the `FontSize="11"` attribute and add `BasedOn` to the inline style:

```xml
                                    <TextBlock DockPanel.Dock="Right" Text="{Binding NextNumberText}"
                                               VerticalAlignment="Center" Margin="8,0,0,0">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock"
                                                   BasedOn="{StaticResource CaptionTextOnSurface}">
                                                <!-- existing setters and triggers unchanged -->
```

If the inline style already has a `BasedOn` pointing at the implicit `TextBlock` style (`{StaticResource {x:Type TextBlock}}`), replace that value — `CaptionTextOnSurface` is itself `BasedOn` the implicit style, so nothing is lost.

- [ ] **Step 7: Prove no site changed colour or size unintentionally.** Render the five affected windows in both palettes and compare against the current gallery:

```bash
dotnet run --project tools/OrdoSort.Smoke -- screenshots "$SCRATCH/caption-after" both
```

(Exit code 1 is expected and meaningless — see Global Constraints.) Open `ProcessingView`, `ReadyView`, `BulkRename`, `LabelMaker`, `MatchMerge`, `ManageSaved`, `Settings` and `Unlock` in both palettes. Every one of the 20 labels must look **identical** to before. If any label changed weight or colour, it was mis-grouped — fix the grouping, not the style.

- [ ] **Step 8: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core 367, Wpf 502 (498 + 4 new theories).

- [ ] **Step 9: Commit.**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests/CaptionSizingTests.cs
git commit -m "refactor(ui): caption sizing goes through the type ramp"
```

Record in the body: 14 sites were exact equivalents, 4 needed `CaptionTextOnSurface` to keep `Theme.Text`, 2 took `BasedOn` because of an inline style, and `Styles.xaml:1251` is exempt as Calendar infrastructure.

---

### Task 3: Extract the shared field row

`FieldLabel` and `FieldRow` are declared privately in `SettingsWindow.xaml:15-21` and used at 19 sites there. BulkRename, LabelMaker, MatchMerge and Unlock hand-roll the same shape with their own margins, which is why their label columns and row gaps have drifted apart. Move both styles into `Theme/Styles.xaml` and point the four windows at them — **with each window's rendered result unchanged**, proven by measurement.

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (add both styles near the named-styles block, after `SubtleText` at line 1717)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml:15-21` (delete the private copies; the 19 `{StaticResource …}` references resolve to the app-level ones unchanged)
- Modify: `src/OrdoSort.Wpf/Windows/{BulkRename,LabelMaker,MatchMerge,Unlock}Window.xaml` (point their field rows at the shared styles)
- Create: `tests/OrdoSort.Wpf.Tests/SharedFieldRowTests.cs`

**Interfaces:**
- Produces: `FieldLabel` (`TextBlock`, `VerticalAlignment=Center`, `Margin=0,0,10,0`) and `FieldRow` (`Grid`, `Margin=0,0,0,10`) as **app-level** resources. Both keep their exact current setter values — this task moves them, it does not retune them.

- [ ] **Step 1: Capture the before-state.** Render the four windows in both palettes and keep the PNGs; they are the acceptance evidence for Step 6.

```bash
dotnet run --project tools/OrdoSort.Smoke -- screenshots "$SCRATCH/fieldrow-before" both
```

- [ ] **Step 2: Write the failing test.** Create `tests/OrdoSort.Wpf.Tests/SharedFieldRowTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace OrdoSort.Wpf.Tests;

/// <summary>FieldRow/FieldLabel used to live privately inside
/// SettingsWindow.xaml while four other windows hand-rolled the same shape,
/// so the label gap and row rhythm drifted per window. These assert the
/// styles are resolvable app-wide and carry the metrics Settings established
/// — the values are pinned deliberately: a later "tidy-up" that nudges them
/// silently re-lays-out five windows at once.</summary>
[Collection(HighlightContrastTests.Name)]
public class SharedFieldRowTests
{
    private readonly HighlightContrastFixture _fx;
    public SharedFieldRowTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void FieldLabelIsAppLevelAndKeepsSettingsMetrics() => _fx.Invoke(() =>
    {
        var style = _fx.App.Resources["FieldLabel"] as Style;
        Assert.NotNull(style);
        var label = new TextBlock { Text = "Inbox:", Style = style };
        var host = new Border { Child = label };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
        Assert.Equal(new Thickness(0, 0, 10, 0), label.Margin);
    });

    [Fact]
    public void FieldRowIsAppLevelAndKeepsSettingsMetrics() => _fx.Invoke(() =>
    {
        var style = _fx.App.Resources["FieldRow"] as Style;
        Assert.NotNull(style);
        var row = new Grid { Style = style };
        var host = new Border { Child = row };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        Assert.Equal(new Thickness(0, 0, 0, 10), row.Margin);
    });
}
```

- [ ] **Step 3: Run — both MUST FAIL** (the keys are window-private today, so `Application.Current.Resources["FieldLabel"]` is null). Paste the output.

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build \
  --filter "FullyQualifiedName~SharedFieldRowTests" -v minimal
```

- [ ] **Step 4: Move the styles.** Cut lines 15-21 out of `SettingsWindow.xaml` and paste them into `Theme/Styles.xaml` after `SubtleText`, carrying the explanatory comment and adding why they are app-level:

```xml
    <!-- The label+control row shape. Lived privately in SettingsWindow until
         2026-08-03; BulkRename, LabelMaker, MatchMerge and Unlock hand-rolled
         it with their own margins and drifted apart. Settings' values win
         because it has 19 rows to the others' handful. -->
    <Style x:Key="FieldLabel" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Margin" Value="0,0,10,0" />
    </Style>
    <Style x:Key="FieldRow" TargetType="Grid">
        <Setter Property="Margin" Value="0,0,0,10" />
    </Style>
```

Leave every `Style="{StaticResource FieldRow}"` / `FieldLabel` reference in `SettingsWindow.xaml` untouched — `StaticResource` walks up to the app dictionary and finds them there. Sites that override the row margin locally (`:318` `Margin="0,14,0,10"`, `:342` `Margin="0,8,0,10"`) keep their local value, which correctly still wins.

- [ ] **Step 5: Re-point the four windows.** In each of BulkRename, LabelMaker, MatchMerge and Unlock, find the label/control grids and apply `Style="{StaticResource FieldRow}"` to the `Grid` and `Style="{StaticResource FieldLabel}"` to the label `TextBlock`, **deleting the local `Margin`/`VerticalAlignment` those elements set by hand**. LabelMaker's five `<ColumnDefinition Width="130" />` label columns (`:134,149,164,184,219`) stay as they are — column width is a per-window layout decision, not part of the row style.

Where a window's existing margin genuinely differs from Settings' and the difference is intentional (e.g. a tighter final row above a button bar), keep it as an explicit local `Margin` on that one element and say so in the commit body. Do not quietly re-space a window to match Settings.

- [ ] **Step 6: Prove the pixels did not move.** Re-render and diff against Step 1:

```bash
dotnet run --project tools/OrdoSort.Smoke -- screenshots "$SCRATCH/fieldrow-after" both
```

Compare `BulkRename`, `LabelMaker`, `MatchMerge` and `Unlock` before vs after in both palettes. Any row that shifted is a metric that differed and was silently normalised — either revert that element to a local margin or state the change explicitly. **"Looks fine" is not the standard here; name every row that moved.**

- [ ] **Step 7: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core 367, Wpf 504.

- [ ] **Step 8: Commit.**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests/SharedFieldRowTests.cs
git commit -m "refactor(ui): one shared field-row style for all five windows"
```

---

### Task 4: One primary per window, and an honest spacing rhythm

**Files:**
- Modify: `src/OrdoSort.Wpf/Windows/LabelMakerWindow.xaml:11-28`
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (the rhythm comment)

- [ ] **Step 1: Audit which windows have more than one primary.** The audit named LabelMaker; confirm it is the only one before editing:

```bash
grep -rn 'StaticResource PrimaryButton' src/OrdoSort.Wpf --include=*.xaml
```

Count per file. Any file with two or more in the same button bar is in scope; record the list in the commit body even if LabelMaker is the only one.

- [ ] **Step 2: Demote Save PDF in LabelMaker.** Today `Print…` (`:20-28`) carries `PrimaryButton` **and** `IsDefault="True"`, while `Save PDF…` (`:11-17`) is a plain `Button` — so LabelMaker's button bar is already correct and the audit's finding may be stale. **Verify before changing anything:** if `Save PDF…` is genuinely unweighted, mark this step done-by-inspection and say so. If a `PrimaryButton` style is present on both, remove it from `Save PDF…` and delete the two `Foreground="{DynamicResource Theme.AccentText}"` locals on its icon and label (they exist only to survive the auto-wrap trap on an accent background; on a plain button they would fight the theme).

Reasoning to record either way: Print… is the in-app path — it opens the preview, which Esc backs out of — whereas Save PDF… hands off to the file system. One weighted action per window, and it is the one that keeps the user inside the app.

- [ ] **Step 3: Correct the documented rhythm.** The refresh documented a 6/10/16 rhythm; the codebase actually practises 8 more than 10. Measure first, then write down what is true:

```bash
grep -rho 'Margin="[0-9, ]*"' src/OrdoSort.Wpf --include=*.xaml \
  | grep -o '[0-9]\+' | sort -n | uniq -c | sort -rn | head -12
```

Update the rhythm comment in `Theme/Styles.xaml` to the measured set with the counts inline, e.g. `<!-- Spacing rhythm as practised: 6/8/10/16 (8 occurs 80×, 10 occurs 62×) -->`. **Do not re-space anything to match the doc** — this step makes the documentation honest, it does not start a re-layout.

- [ ] **Step 4: Full suites green** (no test is expected to change; this is a comment and at most one style attribute).

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

- [ ] **Step 5: Commit.**

```bash
git add src/OrdoSort.Wpf
git commit -m "refactor(ui): one primary per window; the spacing rhythm says what we do"
```

---

### Task 5: Settle the two open questions by measurement

Both are "is this real?" questions the audit could not answer from a screenshot. Neither may be a defect. **Answering with evidence is the deliverable; a fix is only required if the answer says so.**

**Files:** as determined by the measurements.

- [ ] **Step 1: The fifth delete-segment checkbox.** `BulkRenameWindow.xaml:78` renders `<CheckBox Content="last" IsChecked="{Binding DeleteSegLast}" />` — the audit saw no visible label, while its four numbered siblings (`Content="1"`…`"4"`) show theirs. The siblings differ only by `Margin="0,0,8,0"`, which cannot hide text.

Build BulkRenameWindow off-screen on the shared fixture, walk to that fifth `CheckBox`, and compute the WCAG contrast of its resolved label foreground against the surface behind it — the way `HighlightContrastTests` does. Per the proof standard: find the glyphs by scanning for maximum contrast, never a hand-picked coordinate, and remember the `FindDescendant<TextBlock>` trap — a `CheckBox`'s string `Content` is auto-wrapped, and `AccessText` hides a private always-empty decoy `TextBlock` child that will hand you a convincing false reading.

- If contrast is below 4.5:1 → it is the auto-wrap precedence trap. Fix it the proven way: an explicit `ContentTemplate` on the `CheckBox` style whose `TextBlock` carries a **local** `Foreground` bound to `{RelativeSource AncestorType=CheckBox}`. `ControlTemplate.Resources` is measured non-functional for this; do not try it again.
- If contrast is fine (expected: 11–13:1, matching its siblings) → record it as a screenshot artifact, add the measurement to the commit body, and change nothing.

- [ ] **Step 2: Triage's double-selected rows.** `TriageWindow.xaml:41-42` sets `SelectionMode="Single"` on the `Candidates` `DataGrid`, yet the audit render showed both rows in the selected treatment. Build TriageWindow off-screen with two candidates, `UpdateLayout()`, and read `DataGridRow.IsSelected` on both containers plus each row's resolved `Background`.

- If exactly one row reports `IsSelected` but both render the selected background → the row background is being resolved from something other than selection (alternation, a hover trigger left latched, or a style that sets `Background` unconditionally). Fix the resolution and add a test asserting the unselected row's background equals `Theme.Surface`.
- If both report `IsSelected` → `SelectionMode` is being overridden or the selection is being set programmatically twice; fix the source.
- If exactly one reports selected and only that one renders selected → demo state, not a bug. Record and move on.

- [ ] **Step 3: Write the answers down.** Append a short "Verify-then-decide outcomes" section to `docs/superpowers/audits/2026-08-02-ui-audit-pass2.md` recording, for each question: what was measured, the number, and the verdict. A future reader must not have to re-run this.

- [ ] **Step 4: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

- [ ] **Step 5: Commit.**

```bash
git add -A
git commit -m "fix(ui): settle the two verify-then-decide questions with measurements"
```

If both turned out to be non-defects, use `docs: record the verify-then-decide measurements` instead.

---

### Task 6: The Appearance preview cards have already drifted — repair, then pin

**This is not a hypothetical risk; the drift has happened.** `SettingsWindow.xaml:1113-1170` paints three theme preview cards (Auto as a split light/dark pair at `:1113-1135`, Light at `:1142-1155`, Dark at `:1158-1170`) from ~22 hand-picked hex literals. They must be literals — the cards show both palettes at once, so `Theme.*` dynamic resources cannot serve them. But they were written against the **pre-refresh** palette and never updated by the 2026-08-01 "crisp workbench" refresh:

| Card element | XAML today | `ThemePalette` today | |
|---|---|---|---|
| light accent bar | `#1565C0` | `#2D323A` (Light.Accent) | Material blue vs graphite |
| dark accent bar | `#4C8FD6` | `#CDD2DA` (Dark.Accent) | blue vs pale steel |
| light card bg | `#F4F4F7` | `#F7F8F9` (Light.WindowBg) | |
| light border | `#D8D8DE`, `#E2E2E8` | `#BAC0C8` (Light.Border) | |
| light rule | `#9A9AA3` | `#545A63` (Light.SubtleText) | |
| dark card bg | `#1B1B1F` | `#1A1C1F` (Dark.WindowBg) | |
| dark surface | `#26262B` | `#26292D` (Dark.Surface) | |
| dark border | `#3A3A41` | `#4C525A` (Dark.Border) | |
| dark rule | `#7F7F88` | `#A8ADB4` (Dark.SubtleText) | |
| light surface | `#FFFFFF` | `#FFFFFF` (Light.Surface) | ✓ the only match |

So the theme picker currently advertises a blue accent the app has not had since 2026-08-01. Fixing the hexes and adding a test would work, but the drift would simply restart. Instead, publish both palettes as fixed resources and let the XAML reference them — then drift is impossible by construction, and the test guards the publication.

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/ThemeManager.cs` (`Apply`, alongside the `Theme.*` writes at `:62-92`)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml:1113-1170`
- Create: `tests/OrdoSort.Wpf.Tests/AppearancePreviewTests.cs`

**Interfaces:**
- Produces: frozen `SolidColorBrush` resources under the keys `Light.WindowBg`, `Light.Surface`, `Light.Border`, `Light.SubtleText`, `Light.Accent` and the same five under `Dark.*`. These are **palette-fixed** — unlike `Theme.*`, they do not change when the active theme changes.

- [ ] **Step 1: Write the failing test.** Create `tests/OrdoSort.Wpf.Tests/AppearancePreviewTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Appearance tab's three theme preview cards must show both
/// palettes at once, so they cannot use Theme.* (which follows the ACTIVE
/// theme). They used to hand-write ~22 hex literals instead — and by
/// 2026-08-03 they had drifted a full refresh behind, still advertising the
/// pre-2026-08-01 Material blue accent the app no longer has.
///
/// The fix is structural: ThemeManager publishes both palettes under fixed
/// Light.*/Dark.* keys and the XAML references those. These tests pin the
/// publication — including that it does NOT follow the active theme, which is
/// the whole reason the keys exist.</summary>
[Collection(HighlightContrastTests.Name)]
public class AppearancePreviewTests
{
    private readonly HighlightContrastFixture _fx;
    public AppearancePreviewTests(HighlightContrastFixture fx) => _fx = fx;

    private Color Brush(string key) =>
        ((SolidColorBrush)_fx.App.Resources[key]).Color;

    private static Color Expect(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothPalettesArePublishedRegardlessOfTheActiveTheme(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

        var l = ThemePalette.Light;
        Assert.Equal(Expect(l.WindowBg), Brush("Light.WindowBg"));
        Assert.Equal(Expect(l.Surface), Brush("Light.Surface"));
        Assert.Equal(Expect(l.Border), Brush("Light.Border"));
        Assert.Equal(Expect(l.SubtleText), Brush("Light.SubtleText"));
        Assert.Equal(Expect(l.Accent), Brush("Light.Accent"));

        var d = ThemePalette.Dark;
        Assert.Equal(Expect(d.WindowBg), Brush("Dark.WindowBg"));
        Assert.Equal(Expect(d.Surface), Brush("Dark.Surface"));
        Assert.Equal(Expect(d.Border), Brush("Dark.Border"));
        Assert.Equal(Expect(d.SubtleText), Brush("Dark.SubtleText"));
        Assert.Equal(Expect(d.Accent), Brush("Dark.Accent"));
    });
}
```

- [ ] **Step 2: Run — MUST FAIL** with the `Light.*` keys unresolved. Paste the output.

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj --no-build \
  --filter "FullyQualifiedName~AppearancePreviewTests" -v minimal
```

- [ ] **Step 3: Publish both palettes** in `ThemeManager.Apply`, after the `Theme.*` block (`ThemeManager.cs:92`):

```csharp
        // The Appearance tab's preview cards show BOTH palettes side by side,
        // so they cannot use Theme.* — those follow the active theme. These
        // keys are palette-fixed and identical in every theme. Published here
        // rather than as XAML literals because the literals drifted a whole
        // refresh behind (2026-08-03: the cards still showed the pre-refresh
        // Material blue accent).
        foreach (var (prefix, pal) in new[] { ("Light", ThemePalette.Light), ("Dark", ThemePalette.Dark) })
        {
            r[$"{prefix}.WindowBg"] = Brush(pal.WindowBg);
            r[$"{prefix}.Surface"] = Brush(pal.Surface);
            r[$"{prefix}.Border"] = Brush(pal.Border);
            r[$"{prefix}.SubtleText"] = Brush(pal.SubtleText);
            r[$"{prefix}.Accent"] = Brush(pal.Accent);
        }
```

- [ ] **Step 4: Run the test again — MUST PASS.**

- [ ] **Step 5: Re-point the three preview cards.** In `SettingsWindow.xaml:1113-1170`, replace each hex literal per the mapping table at the head of this task, using `{DynamicResource Light.…}` / `{DynamicResource Dark.…}`. For example `:1121`:

```xml
                                                Background="{DynamicResource Light.Accent}" HorizontalAlignment="Left"
```

The light card's inner surface (`#FFFFFF` at `:1117,1145`) becomes `{DynamicResource Light.Surface}` — same colour today, but it must go through the key or the next palette change re-opens the drift. Both light border shades (`#D8D8DE`, `#E2E2E8`) collapse to `Light.Border`; the two-shade distinction was itself part of the drift.

**After this step no hex literal may remain in `:1113-1170`.** Verify:

```bash
sed -n '1108,1175p' src/OrdoSort.Wpf/Windows/SettingsWindow.xaml | grep -n '#[0-9A-Fa-f]'
```

Expected: no output.

- [ ] **Step 6: Look at the result.** Render Settings in both palettes and open the Appearance tab. The three cards must now show graphite/steel accents matching the app, not blue.

```bash
dotnet run --project tools/OrdoSort.Smoke -- screenshots "$SCRATCH/appearance-after" both
```

- [ ] **Step 7: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core 367, Wpf 506.

- [ ] **Step 8: Commit.**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests/AppearancePreviewTests.cs
git commit -m "fix(settings): the theme preview cards show the theme the app actually has"
```

---

### Task 7: The remaining minors

Five small items, one commit. Each is independently revertable; if any turns out to be bigger than it looks, stop and say so rather than growing the task.

**Files:**
- Modify: `src/OrdoSort.Wpf/Views/RgbToBrushConverter.cs`
- Modify: `src/OrdoSort.Wpf/Windows/UnlockWindow.xaml:144-145`
- Modify: `src/OrdoSort.Wpf/Views/ReadyView.xaml` (banner), `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (General tab) — only if the decision says so

- [ ] **Step 1: Cache the converter's brushes.** `RgbToBrushConverter.Convert` calls `ThemeManager.Brush(c)`, which allocates and freezes a **new** `SolidColorBrush` on every binding evaluation (`ThemeManager.cs:137-142`). The dashboard re-evaluates these per tile per refresh. Cache by `Rgb`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Views;

/// <summary>Bridges the view models' WPF-free Rgb colors into brushes.
/// Cached: ThemeManager.Brush allocates and freezes a new brush per call, and
/// the dashboard re-evaluates these bindings per tile per refresh. The brushes
/// are frozen, so sharing one instance across every binding is safe, and the
/// key space is bounded by the palette plus whatever route colours the user
/// has configured.</summary>
public sealed class RgbToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<Rgb, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Rgb c ? Cache.GetOrAdd(c, ThemeManager.Brush) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

Add a test to an existing suite asserting two conversions of the same `Rgb` return the **same instance** and that the returned brush is frozen.

- [ ] **Step 2: Unlock's results list.** `UnlockWindow.xaml:144-145` binds `ResultLines` to a plain `ItemsControl`, which does not virtualise — a bulk unlock over hundreds of files realises every row. Give it a virtualising panel:

```xml
                    <ItemsControl ItemsSource="{Binding ResultLines}"
                                  AutomationProperties.Name="Results"
                                  VirtualizingPanel.IsVirtualizing="True"
                                  VirtualizingPanel.VirtualizationMode="Recycling">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <VirtualizingStackPanel />
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
```

**`ItemsControl` does not virtualise from the attached properties alone** — the `ItemsPanelTemplate` is required, and the control must be inside a `ScrollViewer` with `CanContentScroll="True"`. Check the surrounding markup; if the `ItemsControl` is not in a scroller, virtualisation cannot engage — say so and leave it alone rather than shipping markup that does nothing.

- [ ] **Step 3: Decide the two layout questions, and record the decision.**
  (a) **Ready-screen banner wrapping mid-phrase** — render `ReadyView` at the minimum window width in both palettes, read the actual wrap points, and either insert a non-breaking space / `TextWrapping` adjustment at the phrase that breaks badly, or record that the break is acceptable. Name the phrase either way.
  (b) **Settings' General-tab dead space** — the tab is sized for the tallest tab's content. Decide: size-to-tab (a visible resize when switching tabs) or accept the dead space (a stable dialog). **Recommendation: accept**, and record it — a dialog that changes height when you click a tab is worse than empty space. Write the decision into the audit doc, not just the commit message.

- [ ] **Step 4: The 130px label columns vs the configurable 6–72pt text size.** At large text sizes the fixed `<ColumnDefinition Width="130" />` (19 sites across Settings, LabelMaker) clips labels. Render Settings and LabelMaker at the maximum configured text size and look. If labels clip, change those columns to `Width="Auto"` with `SharedSizeGroup` so each window's labels stay aligned while growing with the font; if they do not clip, record the measurement and leave it. **Do not change 19 column definitions on suspicion** — measure first.

- [ ] **Step 5: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

- [ ] **Step 6: Commit.**

```bash
git add -A
git commit -m "fix(ui): cache converted brushes, virtualise the unlock results, settle the layout minors"
```

---

### Task 8: Full gate and push

Nothing here is optional and nothing here is a formality — this is the first push in 25+ commits.

- [ ] **Step 1: Release build and full suites.**

```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

Record both totals. Expected floor: Core 367, Wpf 506+. **A WPF line reporting a skip or a few dozen tests means the Smart App Control block hit the Release assembly — re-run the rebuild.**

- [ ] **Step 2: Smoke.**

```bash
dotnet run --project tools/OrdoSort.Smoke -- demo-full
```

Expected: ends `All checks passed`, exit 0.

- [ ] **Step 3: Gallery comparison.** Regenerate and compare against the pre-remediation renders:

```bash
dotnet run --project tools/OrdoSort.Smoke -- screenshots "$SCRATCH/gate-gallery" both
```

Ignore the exit code. Walk every window in both palettes and confirm: no window regressed; the Task 1/7/8/9 work from the previous plan still looks right; the caption sizing (Task 2) changed nothing visible; the field rows (Task 3) did not shift; and the Appearance cards (Task 6) show graphite/steel, not blue.

- [ ] **Step 4: Launch sanity.**

```powershell
$p = Start-Process -FilePath "src\OrdoSort.Wpf\bin\Debug\net8.0-windows\OrdoSort.exe" `
                   -ArgumentList "--config","demo-full\config.json" -PassThru
Start-Sleep -Seconds 5
$p.Refresh(); $p.MainWindowHandle
Stop-Process -Id $p.Id
Get-Process OrdoSort -ErrorAction SilentlyContinue
```

Expected: a non-zero `MainWindowHandle`, and nothing left running afterwards.

- [ ] **Step 5: Push.** This is outward-facing and irreversible in practice — **confirm with the user before running it**, and never force.

```bash
git log --oneline origin/main..main
git push origin main
git ls-remote origin main
```

Expected: a fast-forward, and `git ls-remote` reporting the same SHA as local `main`.

- [ ] **Step 6: Close the ledgers.** Tick every box in this plan, append `— DONE, <sha>` to each task heading, and update the memory file `ordosort-rebrand-state.md` with: the new HEAD, the new test totals, and the fact that the audit-remediation program is complete. The remaining program after this is the user's visual acceptance pass, then distribution/first-run and the `v1.0.0` tag — **which is the user's shipping call, always confirmed before tagging.**

---

## Model assignments

Per the standing frugality policy (`model-frugality`): haiku implementers for transcription-grade tasks, sonnet reviewers, sonnet finals for small mechanical diffs, fable finals only for large or risky diffs.

| Task | Implementer | Final review |
|---|---|---|
| 1 Commit + ledger | haiku | sonnet |
| 2 Caption sizing | haiku (the three groups are fully enumerated) | sonnet |
| 3 Shared field row | sonnet (Step 5 needs judgement per window) | sonnet |
| 4 Primary + rhythm | haiku | sonnet |
| 5 Verify-then-decide | sonnet (measurement design, precedence traps) | sonnet |
| 6 Preview cards | sonnet | sonnet |
| 7 Minors | sonnet (four independent decisions) | sonnet |
| 8 Gate and push | sonnet | — (user confirms the push) |
