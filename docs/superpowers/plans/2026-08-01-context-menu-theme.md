# Context-Menu Theming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Theme every right-click menu in the app by adding the one missing implicit style — `ContextMenu` popup chrome — beside the existing menu templates.

**Architecture:** The MenuItem roles and menu-key Separator are already retemplated; only the ContextMenu's own chrome shows stock light styling. One implicit style with `OverridesDefaultStyle` and a flat themed Border template completes the set for every current and future right-click menu (all of which are WPF's built-in editor menus today).

**Tech Stack:** C#/.NET 8 WPF, xUnit. Build `dotnet build`; test `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`.

## Global Constraints

- Delivery directly on `main` (user-approved), one commit per task. The PUSH happens only after the final whole-branch review, by the controller — no task pushes.
- Commit messages end with the two trailers, exactly:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT`
- The tree is LF-only.
- Chrome values, verbatim from the spec: `Theme.Surface` background, 1px `Theme.Border`, 4px CornerRadius, 4px Padding, flat, `Theme.Text` foreground, `OverridesDefaultStyle`, and NO popup-edge margin.
- No changes to the menu bar, MenuItem templates, the Separator style, or any other style. The WebView2 PDF pane's native menu is out of scope.

---

### Task 1: The implicit ContextMenu style

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (insert after the `MenuItem.SeparatorStyleKey` Separator style's closing `</Style>`, ~line 460)

**Interfaces:**
- Consumes: existing theme brushes `Theme.Surface`, `Theme.Border`, `Theme.Text`; the existing implicit MenuItem style (untouched) supplies item visuals.
- Produces: an implicit `ContextMenu` style; no new keys, no code changes.

- [ ] **Step 1: Add the style**

Insert directly after the Separator style's closing `</Style>`:

```xml
    <!-- ContextMenu: the right-click popup chrome. The stock template is a
         hardcoded LIGHT plate (the same trap the MenuItem note above
         describes) — and every right-click menu in this app is WPF's
         built-in editor menu (Cut/Copy/Paste), so this one implicit style
         themes them all. Items already resolve the retemplated MenuItem
         roles. No outer margin: unlike the Menu templates, which own their
         Popup and its transparency, ContextMenu creates its own popup — a
         margin here can render as an opaque dead strip. -->
    <Style TargetType="ContextMenu">
        <Setter Property="Foreground" Value="{DynamicResource Theme.Text}" />
        <Setter Property="Background" Value="{DynamicResource Theme.Surface}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Theme.Border}" />
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ContextMenu">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="4" Padding="4">
                        <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle" />
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 2: Build, both suites, dialogs smoke**

Run: `dotnet build`, `dotnet test tests/OrdoSort.Wpf.Tests`, `dotnet test tests/OrdoSort.Core.Tests`
Expected: clean; Wpf 327 green, Core 359 green (no test changes — a style-only diff).
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs`
Expected: exit 0 ("DIALOGS OK").

- [ ] **Step 3: Commit**

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml
git commit -m "fix(theme): themed chrome for right-click context menus"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 2: Gate (NO push)

**Files:**
- No source changes. If a gate step fails, STOP and report; do not fix.

**Interfaces:**
- Consumes: Task 1 committed on `main`.
- Produces: recorded totals. The pixel verification of an open themed menu is the CONTROLLER's (scratch harness outside the repo); the push is the controller's, after the final review.

- [ ] **Step 1: Release build + both suites**

Run: `dotnet build -c Release`, `dotnet test tests/OrdoSort.Wpf.Tests -c Release`, `dotnet test tests/OrdoSort.Core.Tests -c Release`
Expected: clean; record exact totals (expected Wpf 327 + Core 359 = 686).

- [ ] **Step 2: Smokes**

Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs` → exit 0.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- demo-full` → "All checks passed".
