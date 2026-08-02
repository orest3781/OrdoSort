# Editable ComboBox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `IsEditable="True"` work in the themed ComboBox so the Dashboard's Section field is genuinely pick-or-type, not pick-only.

**Architecture:** One `ControlTemplate` change in `Theme/Styles.xaml`: add a `PART_EditableTextBox` swapped in by an `IsEditable` trigger, keeping the implicit TextBox style (for the themed editor context menu, caret and selection brushes) and neutralizing only its chrome with local values. Proven by an off-screen WPF harness, since headless view-model tests cannot reach template parts.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`.

## Global Constraints

- Template part name exactly `PART_EditableTextBox`; the existing `ContentPresenter` gains `x:Name="ContentSite"`; an `IsEditable = True` trigger shows the text box and collapses `ContentSite`.
- The text box must NOT use `Style="{x:Null}"` — the implicit `TextBox` style supplies `EditorContextMenu`, `CaretBrush`, `SelectionBrush`. Neutralize chrome with LOCAL values only: `Background="Transparent"`, `BorderThickness="0"`, `Padding="0"`, `MinHeight="0"`.
- Text box margin `8,0,28,0` (clear of the arrow so the drop-down stays clickable); `VerticalAlignment="Center"`; `IsReadOnly="{TemplateBinding IsReadOnly}"`; ToggleButton stays `Focusable="False"`.
- `IsTextSearchEnabled="False"` at `SettingsWindow.xaml:636` STAYS (auto-complete would clobber a typed new section name); add a one-line comment there recording that rationale.
- Baseline **686** tests green (Core 359 + Wpf 327) — must stay green; this fix adds no unit tests (behavior is template-level, proven by harness).
- Only `src/OrdoSort.Wpf/Theme/Styles.xaml` and `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` change.

---

### Task 1: Template part + harness proof

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (ComboBox template, lines ~556-596), `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml:636` (comment only)
- Harness: scratch WPF app under the session scratchpad, deleted after use

- [ ] **Step 1: Prove the defect first (red).** Build the harness before changing anything: a WPF app that boots the app's `Theme/Styles.xaml` resources (follow the established pattern — replicate `SmokeUi.Boot`, and re-apply `ShutdownMode` AFTER `InitializeComponent` or windows render 0x0), hosts a `ComboBox IsEditable="True"` with a bound text property and 2 items, renders it off-screen, then reports:

```csharp
var tb = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
Console.WriteLine($"PART present={tb is not null}");
```

Expected NOW: `PART present=False` — the defect, reproduced.

- [ ] **Step 2: Apply the template change.** In `Styles.xaml`, give the existing `ContentPresenter` `x:Name="ContentSite"`, and add immediately after it:

```xaml
                        <!-- Editable mode: WPF binds editing to a part of exactly
                             this name. The implicit TextBox style is deliberately
                             kept (themed context menu, caret, selection); only its
                             chrome is neutralized by these local values, which
                             outrank style setters. The right margin keeps the
                             drop-down arrow clickable. -->
                        <TextBox x:Name="PART_EditableTextBox"
                                 Visibility="Collapsed"
                                 Margin="8,0,28,0" Padding="0" MinHeight="0"
                                 VerticalAlignment="Center"
                                 Background="Transparent" BorderThickness="0"
                                 IsReadOnly="{TemplateBinding IsReadOnly}" />
```

and add to `ControlTemplate.Triggers`:

```xaml
                        <Trigger Property="IsEditable" Value="True">
                            <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible" />
                            <Setter TargetName="ContentSite" Property="Visibility" Value="Collapsed" />
                        </Trigger>
```

- [ ] **Step 3: Comment the call site.** At `SettingsWindow.xaml:636`, above the Section `ComboBox`, add:

```xaml
                                    <!-- pick-or-type: text search stays OFF so typing a
                                         new section ("Incoming 2") isn't auto-completed
                                         onto an existing one -->
```

- [ ] **Step 4: Harness proof (green).** Re-run the harness and extend it to assert all five spec checks:
  1. `PART present=True` and it is a `TextBox`.
  2. Typing updates the binding — set `tb.Text = "Typed section"`, pump, read the bound property → equals `"Typed section"`.
  3. Arrow still opens: set `combo.IsDropDownOpen = true`, pump, assert it stayed open; then select an item and assert `tb.Text` shows it.
  4. A NON-editable ComboBox still renders via `ContentSite` (assert its `PART_EditableTextBox` is collapsed/unused and the selection shows).
  5. Render both palettes off-screen; save two PNGs and confirm the editable box shows text (no blank/invisible field).

Record every result in the report; then delete the harness directory.

- [ ] **Step 5: Suites** — `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln -v minimal` → 686 green (Core 359 + Wpf 327), 0 failed.

- [ ] **Step 6: Commit** (do NOT push — the gate task pushes)

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml src/OrdoSort.Wpf/Windows/SettingsWindow.xaml
git commit -m "fix(theme): editable ComboBox gains PART_EditableTextBox

The Dashboard Section field declared IsEditable but the themed template
had no text-entry part, so it was pick-only — a new section could not be
typed. The implicit TextBox style is kept for the themed editor context
menu, caret and selection; only its chrome is neutralized locally.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 2: Gate and push

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean, 686 green (record totals).
- [ ] **Step 2:** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — ends "All checks passed". (Note: the smoke `screenshots` mode always exits 1 by a known harness quirk — do NOT run it as a gate.)
- [ ] **Step 3:** Launch sanity — build Debug, `Start-Process` the exe with `--config demo-full/config.json`, wait ~5s, confirm the process has a main window, `Stop-Process`, confirm none remains.
- [ ] **Step 4:** `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, never force.
