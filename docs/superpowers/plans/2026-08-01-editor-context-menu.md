# Editor Context Menus Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace WPF's unstylable built-in editor context menus with explicit themed menus on every TextBox and PasswordBox.

**Architecture:** Two `x:Shared="False"` ContextMenu resources of real MenuItems bound to ApplicationCommands (routing gives stock-identical enable/disable and gesture text), attached via one setter each in the existing implicit TextBox and PasswordBox styles. The already-shipped ContextMenu chrome style and MenuItem role templates then apply, because the types are real.

**Tech Stack:** C#/.NET 8 WPF, xUnit. Build `dotnet build`; test `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`.

## Global Constraints

- Delivery directly on `main` (user-approved), one commit per task. The PUSH happens only after the final whole-branch review, by the controller — no task pushes.
- Commit messages end with the two trailers, exactly:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT`
- The tree is LF-only.
- Only `src/OrdoSort.Wpf/Theme/Styles.xaml` may change. No changes to the Menu bar, MenuItem templates, the ContextMenu chrome style, or the TextBox/PasswordBox templates — only the two new resources and the two new setters.
- Menu contents, verbatim from the spec: EditorContextMenu = Cut / Copy / Paste / separator / "Select all" (sentence case); PasswordContextMenu = Paste only. Both `x:Shared="False"`.

---

### Task 1: The explicit editor menus

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (resources before the implicit TextBox style at ~line 131; one setter in each of the TextBox and PasswordBox styles)

**Interfaces:**
- Consumes: the implicit MenuItem style (role templates), the implicit ContextMenu chrome style, `ApplicationCommands`.
- Produces: keyed resources `EditorContextMenu` and `PasswordContextMenu`; no code changes.

- [ ] **Step 1: Add the two menu resources**

Insert directly BEFORE the line `<Style TargetType="TextBox">` (~line 131):

```xml
    <!-- Explicit editor menus: WPF's built-in TextBox/PasswordBox context
         menu is made of PRIVATE MenuItem/ContextMenu subclasses, which
         implicit styles never match (exact-type lookup) — so the themed
         menu styles in this file can't reach it. Handing every text box a
         menu of REAL types is the reliable route; ApplicationCommands
         routing keeps enable/disable and the gesture text identical to
         stock. x:Shared=False: each control gets its own instance, so one
         menu is never torn between two placement targets. -->
    <ContextMenu x:Key="EditorContextMenu" x:Shared="False">
        <MenuItem Header="Cut" Command="Cut" />
        <MenuItem Header="Copy" Command="Copy" />
        <MenuItem Header="Paste" Command="Paste" />
        <Separator />
        <MenuItem Header="Select all" Command="SelectAll" />
    </ContextMenu>
    <ContextMenu x:Key="PasswordContextMenu" x:Shared="False">
        <MenuItem Header="Paste" Command="Paste" />
    </ContextMenu>
```

- [ ] **Step 2: Attach them**

In the implicit TextBox style, directly after `<Setter Property="SelectionBrush" Value="{DynamicResource Theme.Accent}" />`, add:

```xml
        <Setter Property="ContextMenu" Value="{StaticResource EditorContextMenu}" />
```

In the implicit PasswordBox style, directly after its `<Setter Property="SelectionBrush" Value="{DynamicResource Theme.Accent}" />`, add:

```xml
        <Setter Property="ContextMenu" Value="{StaticResource PasswordContextMenu}" />
```

- [ ] **Step 3: Build, both suites, dialogs smoke**

Run: `dotnet build`, `dotnet test tests/OrdoSort.Wpf.Tests`, `dotnet test tests/OrdoSort.Core.Tests`
Expected: clean; Wpf 327 green, Core 359 green (style-only diff).
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs`
Expected: exit 0 ("DIALOGS OK").

- [ ] **Step 4: Commit**

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml
git commit -m "fix(theme): explicit themed context menus for text boxes (stock editor menu is unstylable)"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 2: Gate (NO push)

**Files:**
- No source changes. If a gate step fails, STOP and report; do not fix.

**Interfaces:**
- Consumes: Task 1 committed on `main`.
- Produces: recorded totals. The real-menu pixel verification is the CONTROLLER's (scratch harness outside the repo); the push is the controller's, after the final review.

- [ ] **Step 1: Release build + both suites**

Run: `dotnet build -c Release`, `dotnet test tests/OrdoSort.Wpf.Tests -c Release`, `dotnet test tests/OrdoSort.Core.Tests -c Release`
Expected: clean; record exact totals (expected Wpf 327 + Core 359 = 686).

- [ ] **Step 2: Smokes**

Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs` → exit 0.
Run: `dotnet run --project tools/OrdoSort.Smoke -c Release -- demo-full` → "All checks passed".
