# Remove Custom-Template Naming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Excise the `template` naming mode completely — Core engine, config keys, both Settings surfaces, and tests — with existing configs quietly migrating `naming_mode: "template"` → `"replace"`.

**Architecture:** The feature came in through three seams and leaves through the same three: `Naming` (mode constant + token engine + the `template`/`today` parameters), `Config` (two `naming_template` keys + load validation), and the Settings VM/XAML (fifth radio + per-route override row). Task order is chosen so `main` builds green after every commit: Task 1 strips the WPF surface while the Core engine still exists (its parameters are optional — call sites simply stop passing them); Task 2 then deletes the engine, which nothing references anymore, and adds the load-time migration.

**Tech Stack:** C#/.NET 8, WPF, xUnit. Solution `OrdoSort.sln` at repo root; build with `dotnet build`, test with `dotnet test tests/OrdoSort.Core.Tests` and `dotnet test tests/OrdoSort.Wpf.Tests`.

## Global Constraints

- Delivery is directly on `main` (user-approved), one commit per task, push ONLY in Task 3 after the full gate.
- Commit messages end with the two trailers, exactly:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT`
- The tree is LF-only. Do not introduce CRLF; do not let an editor re-wrap untouched lines.
- Surviving modes are exactly `insert | replace | prefix | append`; their semantics, the pickup rule (`insert` needs `--`, every other mode takes any .pdf), and Enter behavior are UNTOUCHED.
- Migration rule, verbatim from the spec: `naming_mode == "template"` maps to `"replace"` at load (global AND per-route) BEFORE validation, so no existing config can fail to load. Orphaned `naming_template` values survive inertly via the Extras round-trip — no active stripping.
- Removal only: no layout redesign in XAML beyond deleting the template rows; adjacent controls keep their existing margins and styles.
- `SkipFile`, `Scanner.Eligible`, and demo/smoke tools are untouched (verified: `tools/` has zero template references).

---

### Task 1: Strip the custom-template surface from Settings (WPF)

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` (~line 1131)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Filing tab ~221-235, Destinations ~464-482)
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `Naming.BuildTarget`'s `routeTemplate`/`globalTemplate` parameters are OPTIONAL in the current Core — call sites may simply stop passing them, so this task compiles against unchanged Core.
- Produces: a `SettingsViewModel` with four filing modes (no `ModeTemplate`, `NamingTemplate`, or `TemplateNote` members anywhere in the Wpf project). Task 2 relies on zero Wpf references to `Naming.ModeTemplate` / `Naming.ValidateTemplate` / `Naming.ResolveTemplate` / `Config.NamingTemplate` / `Route.NamingTemplate` remaining.

- [ ] **Step 1: Rewrite the five-mode round-trip test as four-mode; delete the five template tests**

In `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`, replace `FiveFilingModesRoundTripWithTemplate` (whole method) with:

```csharp
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
```

Delete these five test methods entirely (each with its comment lines): `ABadTemplateBlocksSaveWithItsOwnMessage`, `RouteTemplateRoundTripsThroughTheRouteEditor`, `ABadRouteTemplateOverrideBlocksSaveWithARouteLabeledMessage`, `ARouteInTemplateModeWithABlankBoxFallsBackToTheGlobalTemplateAndSaves`, `WhitespaceOnlyRouteTemplateSavesAsNullNotAsWhitespace`. Leave `FilingExampleUsesAMarkerFreeSampleForPrefixAndAppend` in place — it stays valid.

- [ ] **Step 2: RouteEditVm — remove the per-route template member**

In `SettingsViewModel.cs` (RouteEditVm, top of file):
- Delete the field line `private string _namingTemplate = "";   // "" = inherit the global template`.
- Delete the property line `public string NamingTemplate { get => _namingTemplate; set => Set(ref _namingTemplate, value); }`.
- In `From(Route r)`: delete `NamingTemplate = r.NamingTemplate ?? "",`.
- In `ToRoute()`: delete `NamingTemplate = NamingTemplate.Trim().Length == 0 ? null : NamingTemplate.Trim(),`.

- [ ] **Step 3: SettingsViewModel — remove the global template members and validation**

All in `SettingsViewModel.cs`:

a) `ModeChoices`: delete the entry `new("template", "Custom template"),`.

b) Constructor: delete `NamingTemplate = current.NamingTemplate;` (the line after `FilingMode = current.NamingMode;`).

c) `HookRoute` — the second `if` currently reads:

```csharp
            if (e.PropertyName is nameof(RouteEditVm.NamingMode) or nameof(RouteEditVm.NamingTemplate)
                && ReferenceEquals(r, SelectedRoute))
                Raise(nameof(RouteFilingExample));
```

Replace with:

```csharp
            if (e.PropertyName is nameof(RouteEditVm.NamingMode)
                && ReferenceEquals(r, SelectedRoute))
                Raise(nameof(RouteFilingExample));
```

d) `RouteFilingExample` getter — the `BuildTarget` call currently ends:

```csharp
                var result = Naming.BuildTarget(
                    sample, "Smith John",
                    routeMode: routeMode, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false,
                    routeTemplate: r.NamingTemplate, globalTemplate: NamingTemplate);
```

Replace with:

```csharp
                var result = Naming.BuildTarget(
                    sample, "Smith John",
                    routeMode: routeMode, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false);
```

Also trim its doc comment's first sentence from "…EFFECTIVE naming mode + template (its own override, falling back to the Filing tab's global setting) does…" to "…EFFECTIVE naming mode (its own override, falling back to the Filing tab's global setting) does…".

e) `FilingMode` setter: delete the two lines `Raise(nameof(ModeTemplate));` and `Raise(nameof(TemplateNote));` (keep the other Raise lines, including `Raise(nameof(RouteFilingExample));`).

f) Delete the `ModeTemplate` bool wrapper line:
`public bool ModeTemplate { get => FilingMode == Naming.ModeTemplate; set { if (value) FilingMode = Naming.ModeTemplate; } }`

g) Delete the whole VM-level `NamingTemplate` property (backing field `_namingTemplate` + property with its three Raise calls) and the whole `TemplateNote` computed property including its `/// <summary>` block.

h) `FilingExample` getter: change the comment lines

```csharp
            // Insert/Replace both work on the classic "--" scanner name;
            // Prefix/Append/Template don't need (or want) a marker, and
```

to

```csharp
            // Insert/Replace both work on the classic "--" scanner name;
            // Prefix/Append don't need (or want) a marker, and
```

and the `BuildTarget` call currently ending

```csharp
                var result = Naming.BuildTarget(
                    sample, name,
                    routeMode: null, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false,
                    globalTemplate: NamingTemplate);
```

becomes

```csharp
                var result = Naming.BuildTarget(
                    sample, name,
                    routeMode: null, globalMode: FilingMode,
                    routeSuffix: "", appendSuffix: false, exists: _ => false);
```

i) `HardErrors()`: delete the global block

```csharp
        if (FilingMode == Naming.ModeTemplate)
        {
            var templateError = Naming.ValidateTemplate(NamingTemplate);
            if (templateError.Length > 0) errors.Add(templateError);
        }
```

and, inside the route loop, the per-route block INCLUDING its four comment lines:

```csharp
            // a route in template mode is validated here too — not just the
            // global template — using the same fallback ResolveTemplate uses
            // at commit time: the route's own template, or the Filing tab's
            // global one when the route's box is blank.
            if (r.NamingMode == Naming.ModeTemplate)
            {
                var routeTemplate = r.NamingTemplate.Trim();
                var effectiveTemplate = routeTemplate.Length == 0 ? NamingTemplate : routeTemplate;
                var templateError = Naming.ValidateTemplate(effectiveTemplate);
                if (templateError.Length > 0)
                    errors.Add($"{label}: template — {templateError}");
            }
```

j) The config-build method (~line 1285): delete `cfg.NamingTemplate = NamingTemplate.Trim();` (keep `cfg.NamingMode = FilingMode;`).

- [ ] **Step 4: ShellViewModel — revert the preview call site**

In `UpdatePreview()` (~line 1131), the `BuildTarget` call currently ends:

```csharp
            var result = Naming.BuildTarget(
                Path.GetFileName(current), TypedName,
                route?.NamingMode, _session.SessionMode,
                route?.Suffix ?? "", route?.AppendSuffix ?? false,
                _ => false,
                routeTemplate: route?.NamingTemplate, globalTemplate: _cfg.NamingTemplate);
```

Replace with:

```csharp
            var result = Naming.BuildTarget(
                Path.GetFileName(current), TypedName,
                route?.NamingMode, _session.SessionMode,
                route?.Suffix ?? "", route?.AppendSuffix ?? false,
                _ => false);
```

- [ ] **Step 5: SettingsWindow.xaml — delete both template rows**

a) Filing tab: delete this whole block (the fifth radio, the template-box grid, and the note — the `CaptionText` pickup note that follows stays, unchanged):

```xml
                        <RadioButton GroupName="NamingMode" IsChecked="{Binding ModeTemplate}"
                                     Content="Custom template" />
                        <Grid Margin="22,4,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="220" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <TextBox Text="{Binding NamingTemplate, UpdateSourceTrigger=PropertyChanged}"
                                     IsEnabled="{Binding ModeTemplate}" />
                            <TextBlock Grid.Column="1" Style="{StaticResource SubtleText}"
                                       VerticalAlignment="Center" Margin="8,0,0,0"
                                       Text="tokens: {name} · {original} · {date}" />
                        </Grid>
                        <TextBlock Text="{Binding TemplateNote}" Style="{StaticResource NoteText}"
                                   Margin="22,-6,0,10" />
```

b) Destinations detail form: delete the whole conditional template `Grid` (the one whose `Grid.Style` contains `<DataTrigger Binding="{Binding NamingMode}" Value="template">`), from its opening `<Grid>` through its closing `</Grid>` — i.e. the block between the "Naming mode:" combo grid and the "Button color:" grid.

- [ ] **Step 6: Build and run both suites**

Run: `dotnet build` then `dotnet test tests/OrdoSort.Wpf.Tests` and `dotnet test tests/OrdoSort.Core.Tests`
Expected: build clean; Wpf suite green (5 tests fewer than the 317 baseline: 312); Core suite green and unchanged (375).

- [ ] **Step 7: Commit**

```bash
git add -A src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests
git commit -m "refactor(wpf): drop the custom-template surface from Settings"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 2: Excise the template engine from Core; migrate old configs

**Files:**
- Modify: `src/OrdoSort.Core/Naming.cs`
- Modify: `src/OrdoSort.Core/Config.cs`
- Modify: `src/OrdoSort.Core/Commit.cs` (~lines 33-50)
- Modify: `src/OrdoSort.Core/Session.cs` (~lines 13-19, 91-92)
- Test: `tests/OrdoSort.Core.Tests/NamingConfigTests.cs`, `tests/OrdoSort.Core.Tests/NamingTests.cs`, `tests/OrdoSort.Core.Tests/PipelineTests.cs`

**Interfaces:**
- Consumes: Task 1's guarantee that no Wpf code references any template member.
- Produces: `Naming.Modes = { insert, replace, prefix, append }`; `Naming.ApplyName(string originalFilename, string typedName, string mode)`; `Naming.BuildTarget(string originalFilename, string typedName, string? routeMode, string globalMode, string routeSuffix, bool appendSuffix, Func<string, bool> exists)`; `Commit.CommitFile(string src, string typedName, Route route, string globalMode)`. `Config`/`Route` no longer have `NamingTemplate`.

- [ ] **Step 1: Write the three failing migration tests**

In `tests/OrdoSort.Core.Tests/NamingConfigTests.cs`, add:

```csharp
    [Fact]
    public void GlobalTemplateModeMigratesToReplaceAtLoad()
    {
        // the "template" naming mode was removed 2026-08 — a config that
        // still says it must load quietly as "replace" (the closest
        // surviving semantics), never brick startup with a validation error
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{date}-{name}"}""");
        Assert.Equal("replace", Config.Load(path).NamingMode);
    }

    [Fact]
    public void RouteTemplateModeMigratesToReplaceAtLoad()
    {
        // per-route overrides migrate too — including routes arriving via
        // the destinations.json side file, the live path since the split
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"inbox":"C:/in"}""");
        File.WriteAllText(Path.Combine(_dir, "destinations.json"), """
            {"routes":[{"label":"A","path":"C:/a","naming_mode":"template","naming_template":"{name}!"}]}
            """);
        var route = Config.Load(path).Routes.Single();
        Assert.Equal("replace", route.NamingMode);
        // the orphaned key is untyped now — it survives in Extras, not lost
        Assert.True(route.Extras.ContainsKey("naming_template"));
    }

    [Fact]
    public void OrphanedNamingTemplateKeysSurviveAsInertExtras()
    {
        // naming_template is no longer a typed key; a hand-edited leftover
        // rides the Extras round trip like any unknown key — not stripped
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{date}-{name}"}""");
        var back = Config.Load(path);
        Config.Save(back, path);
        Assert.Contains("naming_template", File.ReadAllText(path));
        Assert.Equal("replace", Config.Load(path).NamingMode);
    }
```

- [ ] **Step 2: Run the three new tests to verify they fail**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter "FullyQualifiedName~MigratesToReplace|FullyQualifiedName~OrphanedNamingTemplate"`
Expected: FAIL — current code loads the mode as `"template"`, not `"replace"` (all three assert `"replace"`).

- [ ] **Step 3: Naming.cs — delete the engine**

- Delete `public const string ModeTemplate = "template";` and change `Modes` to:

```csharp
    public static readonly string[] Modes =
        { ModeInsert, ModeReplace, ModePrefix, ModeAppend };
```

- Delete the `TemplateTokenRegex()` generated-regex declaration (both attribute lines and the partial method) and the `TemplateTokens` HashSet field.
- Delete the whole `ValidateTemplate` method with its `/// <summary>` block.
- Delete the whole `ResolveTemplate` method with its `/// <summary>` block.
- `ApplyName`: change the signature to

```csharp
    public static string ApplyName(string originalFilename, string typedName, string mode)
```

and delete the entire `case ModeTemplate:` block from its switch (the `ModeReplace`/`ModePrefix`/`ModeAppend` cases and the `default` insert case stay byte-identical).
- `BuildTarget`: change the signature to

```csharp
    public static NameResult BuildTarget(
        string originalFilename, string typedName,
        string? routeMode, string globalMode,
        string routeSuffix, bool appendSuffix,
        Func<string, bool> exists)
```

and replace its first three statements

```csharp
        var mode = ResolveMode(routeMode, globalMode);
        var template = routeMode == ModeTemplate
            ? ResolveTemplate(routeMode, routeTemplate, globalTemplate)
            : globalTemplate;
        var stem = ApplyName(originalFilename, typedName, mode, template, today);
```

with

```csharp
        var mode = ResolveMode(routeMode, globalMode);
        var stem = ApplyName(originalFilename, typedName, mode);
```

- [ ] **Step 4: Config.cs — drop the keys, add the migration**

- `Route`: delete the line `[JsonPropertyName("naming_template")] public string? NamingTemplate { get; set; }`.
- `Config`: delete the line `[JsonPropertyName("naming_template")] public string NamingTemplate { get; set; } = "";`.
- `Load`: delete the whole block

```csharp
        if (cfg.NamingMode == Naming.ModeTemplate)
        {
            var templateError = Naming.ValidateTemplate(cfg.NamingTemplate);
            if (templateError.Length > 0)
                throw new ConfigException($"naming_template: {templateError}");
        }
```

(the `naming_mode must be one of …` check above it stays — its message auto-shrinks to four modes via `string.Join`).
- `Normalize()`: replace the line `NamingTemplate ??= "";` with

```csharp
        // The "template" naming mode was removed (2026-08). A config that
        // still carries it loads as "replace" — the closest surviving
        // semantics — so an old file never fails validation over a mode
        // that no longer exists. (Its naming_template value, if any, rides
        // along untyped in Extras.)
        if (NamingMode == "template") NamingMode = "replace";
```

- `NormalizeSectionItems()`: inside the `foreach (var r in Routes)` loop, extend the existing null-hardening with the per-route migration so routes from destinations.json migrate too:

```csharp
        foreach (var r in Routes)
        {
            r.Label ??= ""; r.Path ??= ""; r.Hotkey ??= ""; r.Suffix ??= "";
            r.Extras ??= new();
            // removed-mode migration — see the note in Normalize()
            if (r.NamingMode == "template") r.NamingMode = "replace";
        }
```

- [ ] **Step 5: Commit.cs and Session.cs — revert the threading**

`Commit.cs` — signature and `Build()`:

```csharp
    public static CommitOutcome CommitFile(
        string src, string typedName, Route route, string globalMode)
```

```csharp
        Naming.NameResult Build() => Naming.BuildTarget(
            Path.GetFileName(src), typedName, route.NamingMode, globalMode,
            route.Suffix, route.AppendSuffix,
            name => File.Exists(Path.Combine(destDir, name)));
```

`Session.cs` — the commit call becomes:

```csharp
        var outcome = Commit.CommitFile(src, typedName, route, SessionMode);
```

and the ctor-area comment (which explains template threading that no longer exists) shrinks to:

```csharp
    // Session is rebuilt from scratch by ShellViewModel.ApplySettings whenever
    // settings change, so both _cfg and the ctor-cached SessionMode are
    // effectively frozen for the life of one session.
```

- [ ] **Step 6: Prune the template tests; keep the coverage they carried incidentally**

a) `NamingConfigTests.cs`:
- In `PickupRequiresTheMarkerOnlyInInsertMode`, replace the two rows
  `[InlineData("template", "plain.pdf", true)]` and `[InlineData("template", "not-a-pdf.txt", false)]` with the single row
  `[InlineData("append", "not-a-pdf.txt", false)]` — the non-PDF-rejected coverage must not be lost with the template rows.
- Delete these four test methods entirely: `NamingTemplateKeysRoundTrip`, `GlobalTemplateModeWithBadTemplateFailsLoadReadably`, `RouteTemplatesAreNotValidatedAtLoad`, `OmittedRouteTemplateStaysNullThroughLoadAndSave`.
- Update the class doc comment to `/// <summary>Naming modes: pickup rule, config keys, removed-mode migration.</summary>`.

b) `NamingTests.cs`:
- `BlankNamePreservesTheOriginalStemInEveryMode` — the loop body becomes:

```csharp
        foreach (var mode in Naming.Modes)
            Assert.Equal("20240115--1042",
                Naming.ApplyName("20240115--1042.pdf", "  ", mode));
```

- Delete these seven test methods plus the `// ---- template validation`, `// ---- template resolution`, and (only if it has no remaining tests under it — it does: keep it) `// ---- BuildTarget end to end` section comment lines as applicable: `TemplateRendersAllThreeTokens`, `TemplateKeepsLiteralText`, `BadTemplatesFailValidationReadably`, `GoodTemplatesPassValidation`, `RouteTemplateWinsAndFallsBackToGlobal`, `BuildTargetRendersATemplateWithSuffixAndCollision`, `TemplateOutputStillRejectsIllegalCharacters`. (After the deletions the `// ---- BuildTarget end to end` header would sit above nothing — delete that header too; the class then ends after `BlankNamePreservesTheOriginalStemInEveryMode`.)

c) `PipelineTests.cs`: delete the `CommitRendersARouteTemplate` test method.

- [ ] **Step 7: Run the full Core suite, then build the whole solution and run the Wpf suite**

Run: `dotnet test tests/OrdoSort.Core.Tests`
Expected: green — the three migration tests now pass; expected total ≈ 359 (375 − 13 template-engine cases − 1 net theory row − 4 config tests − 1 pipeline test + 3 migration tests). Record the exact number.

Run: `dotnet build` then `dotnet test tests/OrdoSort.Wpf.Tests`
Expected: clean build (nothing in Wpf references the removed members after Task 1) and green (312).

- [ ] **Step 8: Commit**

```bash
git add -A src/OrdoSort.Core tests/OrdoSort.Core.Tests
git commit -m "refactor(core): remove the template naming engine; old configs migrate to replace"
```

(with the two Global Constraints trailers appended to the message body).

---

### Task 3: Full gate, residue sweep, push

**Files:**
- No source changes expected. This task is the delivery gate.

**Interfaces:**
- Consumes: Tasks 1-2 committed on `main`.
- Produces: `origin/main` updated; recorded final test totals.

- [ ] **Step 1: Residue sweep**

Run: `git grep -iE "ModeTemplate|naming_template|ValidateTemplate|ResolveTemplate|TemplateNote|TemplateToken" -- src tools`
Expected: no output. Then:
Run: `git grep -iE "ModeTemplate|ValidateTemplate|ResolveTemplate" -- tests`
Expected: no output (the literal string `"template"` remains ONLY inside the three migration tests and nowhere else — verify with `git grep -n "\"template\"" -- tests`, which must list only `NamingConfigTests.cs` migration-test lines).

- [ ] **Step 2: Release build + both suites**

Run: `dotnet build -c Release` then `dotnet test tests/OrdoSort.Core.Tests -c Release` and `dotnet test tests/OrdoSort.Wpf.Tests -c Release`
Expected: clean, green, green. Record exact totals (expected ≈ Core 359 + Wpf 312 = 671).

- [ ] **Step 3: Smoke: dialogs, demo-full, launch sanity**

Run the established smoke gate from the repo root (same commands as every prior delivery):
- `dotnet run --project tools/OrdoSort.Smoke -c Release -- dialogs` → exit code 0
- `dotnet run --project tools/OrdoSort.Smoke -c Release -- demo-full` → prints "All checks passed"
- Launch sanity: start the app against a scratch config, confirm the main window comes up and the Filing tab shows exactly four radios, then close it.

- [ ] **Step 4: Push (ancestry-checked, never force)**

```bash
git fetch origin
git merge-base --is-ancestor origin/main HEAD && git push origin main
```

Expected: push accepted (the ancestry check guarantees a fast-forward without force).
