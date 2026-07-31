# Filing & Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Five naming modes (insert / replace / prefix / append / custom template with `{name}` `{original}` `{date}` tokens) available globally and per-route with live previews, plus an Enter key that always files (last-used or first destination, user's choice).

**Architecture:** The engine work is additive at three seams: `Naming.ApplyName` (mode semantics + template rendering), `Scanner.Eligible` (pickup rule), `Naming.ResolveMode`/new `ResolveTemplate` (override resolution). `BuildTarget`/`CommitFile` grow optional parameters so existing callers compile unchanged. The Settings VM swaps its `InsertMode` bool for a five-value `FilingMode` string with radio wrappers; Enter behavior changes are two small methods in `ShellViewModel`.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main` (established delivery mode: commits per task, push only in the final task's gate).

## Global Constraints

- Mode config values, verbatim: `insert | replace | prefix | append | template`. New config keys: `naming_template` (Config, default `""`), `naming_template` (Route, nullable).
- Template tokens exactly `{name}`, `{original}`, `{date}`; `{date}` renders `yyyyMMdd`; unknown tokens / empty template / no-token template / unmatched braces are validation errors with readable messages.
- Blank typed name preserves the original stem in EVERY mode.
- Prefix = `{name}-{original stem}`; Append = `{original stem}-{name}` (literal `-` joint).
- Pickup: `insert` requires the `--` marker; every other mode picks up every `.pdf`. Pickup uses the global session mode only.
- Template resolution: a route with `naming_mode: "template"` uses its own `naming_template`, falling back to the global template only when its own is null/empty.
- Validation placement: global mode+template at load AND settings time; per-route templates at settings time and commit time only — never at load.
- Enter: `enter_commits` bool keeps its name (`true` = last-used, `false` = first destination); Enter always files when routes exist and Screen == Processing; last-used mode uses route 0 until a route has been used; the buttons' Enter-target marker follows `EnterCommits ? (_lastRoute ?? 0) : 0`.
- Baseline: Core 321 + Wpf 268 = 589 tests green; grow only by additions (plus the sanctioned Enter-behavior test updates in Task 4 and Settings-VM updates in Task 5).

---

### Task 1: Naming engine — modes, templates, validation

**Files:**
- Modify: `src/OrdoSort.Core/Naming.cs`
- Test: `tests/OrdoSort.Core.Tests/NamingTests.cs` (append)

**Interfaces:**
- Produces (exact, later tasks rely on these):
  - `Naming.ModePrefix = "prefix"`, `Naming.ModeAppend = "append"`, `Naming.ModeTemplate = "template"`; `Naming.Modes` contains all five.
  - `public static string ValidateTemplate(string template)` — `""` when valid, else a readable error.
  - `public static string ResolveTemplate(string? routeMode, string? routeTemplate, string globalTemplate)`.
  - `public static string ApplyName(string originalFilename, string typedName, string mode, string template = "", DateTime? today = null)`.
  - `public static NameResult BuildTarget(string originalFilename, string typedName, string? routeMode, string globalMode, string routeSuffix, bool appendSuffix, Func<string, bool> exists, string? routeTemplate = null, string globalTemplate = "", DateTime? today = null)`.

- [ ] **Step 1: Write the failing tests** — append to `NamingTests.cs` (match its existing style):

```csharp
    // ---- new modes ----------------------------------------------------

    [Fact]
    public void PrefixPutsTheTypedNameFirst() =>
        Assert.Equal("SMITH JOHN-20240115--1042",
            Naming.ApplyName("20240115--1042.pdf", "SMITH JOHN", Naming.ModePrefix));

    [Fact]
    public void AppendPutsTheTypedNameLast() =>
        Assert.Equal("20240115--1042-SMITH JOHN",
            Naming.ApplyName("20240115--1042.pdf", "SMITH JOHN", Naming.ModeAppend));

    [Fact]
    public void BlankNamePreservesTheOriginalStemInEveryMode()
    {
        foreach (var mode in Naming.Modes)
            Assert.Equal("20240115--1042",
                Naming.ApplyName("20240115--1042.pdf", "  ", mode,
                    template: "{date}-{name}", today: new DateTime(2026, 7, 31)));
    }

    [Fact]
    public void TemplateRendersAllThreeTokens() =>
        Assert.Equal("20260731-SMITH JOHN-scan001",
            Naming.ApplyName("scan001.pdf", "SMITH JOHN", Naming.ModeTemplate,
                template: "{date}-{name}-{original}", today: new DateTime(2026, 7, 31)));

    [Fact]
    public void TemplateKeepsLiteralText() =>
        Assert.Equal("FAX SMITH JOHN (copy)",
            Naming.ApplyName("scan001.pdf", "SMITH JOHN", Naming.ModeTemplate,
                template: "FAX {name} (copy)", today: new DateTime(2026, 7, 31)));

    // ---- template validation ------------------------------------------

    [Theory]
    [InlineData("", "template is empty")]
    [InlineData("no tokens here", "at least one")]
    [InlineData("{name}-{bogus}", "bogus")]
    [InlineData("{name}-{", "unmatched")]
    [InlineData("}{name}", "unmatched")]
    public void BadTemplatesFailValidationReadably(string template, string mustMention)
    {
        var error = Naming.ValidateTemplate(template);
        Assert.NotEqual("", error);
        Assert.Contains(mustMention, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{name}")]
    [InlineData("{date}-{name}-{original}")]
    [InlineData("FAX {name} (copy)")]
    public void GoodTemplatesPassValidation(string template) =>
        Assert.Equal("", Naming.ValidateTemplate(template));

    // ---- template resolution ------------------------------------------

    [Fact]
    public void RouteTemplateWinsAndFallsBackToGlobal()
    {
        Assert.Equal("{name}!", Naming.ResolveTemplate(
            Naming.ModeTemplate, "{name}!", "{date}"));
        Assert.Equal("{date}", Naming.ResolveTemplate(
            Naming.ModeTemplate, "", "{date}"));
        Assert.Equal("{date}", Naming.ResolveTemplate(
            Naming.ModeTemplate, null, "{date}"));
    }

    // ---- BuildTarget end to end ---------------------------------------

    [Fact]
    public void BuildTargetRendersATemplateWithSuffixAndCollision()
    {
        var taken = new HashSet<string> { "20260731-SMITH JOHN_TAX.pdf" };
        var r = Naming.BuildTarget("scan001.pdf", "SMITH JOHN",
            routeMode: Naming.ModeTemplate, globalMode: Naming.ModeInsert,
            routeSuffix: "_TAX", appendSuffix: true,
            exists: taken.Contains,
            routeTemplate: "{date}-{name}", globalTemplate: "",
            today: new DateTime(2026, 7, 31));
        Assert.Equal("20260731-SMITH JOHN_TAX (2).pdf", r.Filename);
        Assert.Equal(Naming.ModeTemplate, r.ModeUsed);
    }

    [Fact]
    public void TemplateOutputStillRejectsIllegalCharacters() =>
        Assert.Throws<ArgumentException>(() =>
            Naming.BuildTarget("scan001.pdf", "SMITH: JOHN",
                routeMode: null, globalMode: Naming.ModeTemplate,
                routeSuffix: "", appendSuffix: false, exists: _ => false,
                globalTemplate: "{name}", today: new DateTime(2026, 7, 31)));
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter NamingTests -v minimal`
Expected: compile FAILURE (new mode constants / parameters don't exist) — the red step.

- [ ] **Step 3: Implement in `Naming.cs`:**

Replace the constants block:

```csharp
    public const string ModeInsert = "insert";
    public const string ModeReplace = "replace";
    public const string ModePrefix = "prefix";
    public const string ModeAppend = "append";
    public const string ModeTemplate = "template";
    public static readonly string[] Modes =
        { ModeInsert, ModeReplace, ModePrefix, ModeAppend, ModeTemplate };
```

Add the token regex beside the existing generated regexes:

```csharp
    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex TemplateTokenRegex();

    private static readonly HashSet<string> TemplateTokens = new() { "name", "original", "date" };
```

Add validation and resolution:

```csharp
    /// <summary>"" when the template is usable; else a readable error. A
    /// template must contain at least one known token, no unknown tokens,
    /// and no stray braces.</summary>
    public static string ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "The template is empty — use tokens like {name}, {original}, {date}.";
        var known = 0;
        foreach (Match m in TemplateTokenRegex().Matches(template))
        {
            if (!TemplateTokens.Contains(m.Groups[1].Value))
                return $"Unknown token {{{m.Groups[1].Value}}} — the tokens are " +
                       "{name}, {original} and {date}.";
            known++;
        }
        if (known == 0)
            return "The template needs at least one token: {name}, {original} or {date}.";
        var leftover = TemplateTokenRegex().Replace(template, "");
        if (leftover.Contains('{') || leftover.Contains('}'))
            return "The template has an unmatched { or } — brace tokens must be " +
                   "{name}, {original} or {date}.";
        return "";
    }

    /// <summary>The template that goes with the EFFECTIVE mode: a route in
    /// template mode uses its own template, falling back to the global one
    /// when its own is absent.</summary>
    public static string ResolveTemplate(string? routeMode, string? routeTemplate, string globalTemplate) =>
        routeMode == ModeTemplate
            ? (string.IsNullOrEmpty(routeTemplate) ? globalTemplate : routeTemplate)
            : globalTemplate;
```

Extend `ApplyName` (new signature; the blank-name rule stays first so it covers every mode):

```csharp
    public static string ApplyName(string originalFilename, string typedName,
        string mode, string template = "", DateTime? today = null)
    {
        if (Array.IndexOf(Modes, mode) < 0)
            throw new ArgumentException($"Unknown naming mode: '{mode}'");
        var name = StripPdfExt(typedName);
        var stem = StripPdfExt(originalFilename);
        if (string.IsNullOrWhiteSpace(name))
            return stem;
        switch (mode)
        {
            case ModeReplace: return name;
            case ModePrefix: return $"{name}-{stem}";
            case ModeAppend: return $"{stem}-{name}";
            case ModeTemplate:
            {
                var error = ValidateTemplate(template);
                if (error.Length > 0) throw new ArgumentException(error);
                var date = (today ?? DateTime.Now).ToString("yyyyMMdd");
                return TemplateTokenRegex().Replace(template, m => m.Groups[1].Value switch
                {
                    "name" => name,
                    "original" => stem,
                    _ => date,
                });
            }
            default:  // insert: the typed name replaces the FIRST "--"
            {
                var split = stem.IndexOf("--", StringComparison.Ordinal);
                if (split <= 0 || split + 2 >= stem.Length)
                    throw new ArgumentException(
                        $"Insert mode needs '--' in the filename, got '{originalFilename}'");
                return $"{stem[..split]}-{name}-{stem[(split + 2)..]}";
            }
        }
    }
```

Extend `BuildTarget` — new optional parameters, threading template + date into `ApplyName`:

```csharp
    public static NameResult BuildTarget(
        string originalFilename, string typedName,
        string? routeMode, string globalMode,
        string routeSuffix, bool appendSuffix,
        Func<string, bool> exists,
        string? routeTemplate = null, string globalTemplate = "",
        DateTime? today = null)
    {
        var mode = ResolveMode(routeMode, globalMode);
        var template = routeMode == ModeTemplate
            ? ResolveTemplate(routeMode, routeTemplate, globalTemplate)
            : globalTemplate;
        var stem = ApplyName(originalFilename, typedName, mode, template, today);
        ...
```

(The rest of `BuildTarget` — suffix, `RejectIllegal`, collision counter — is unchanged.)

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter NamingTests -v minimal`
Expected: all pass (existing NamingTests + the ~10 new ones).

- [ ] **Step 5: Full Core suite** — `dotnet test tests/OrdoSort.Core.Tests -v minimal` — expect 321 + new, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Core/Naming.cs tests/OrdoSort.Core.Tests/NamingTests.cs
git commit -m "feat(core): prefix, append and template naming modes

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 2: Pickup rule + config keys

**Files:**
- Modify: `src/OrdoSort.Core/Scanner.cs` (`Eligible`), `src/OrdoSort.Core/Config.cs`
- Test: `tests/OrdoSort.Core.Tests/ConfigSplitTests.cs` sibling — create `tests/OrdoSort.Core.Tests/NamingConfigTests.cs`

**Interfaces:**
- Consumes: Task 1's mode constants and `ValidateTemplate`.
- Produces: `Config.NamingTemplate` (`naming_template`, default `""`), `Route.NamingTemplate` (`naming_template`, nullable); load-time validation of the GLOBAL template.

- [ ] **Step 1: Write the failing tests** — create `NamingConfigTests.cs`:

```csharp
namespace OrdoSort.Core.Tests;

/// <summary>New naming modes: pickup rule, config keys, load validation.</summary>
public class NamingConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordonamecfg_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("insert", "plain.pdf", false)]
    [InlineData("insert", "a--b.pdf", true)]
    [InlineData("replace", "plain.pdf", true)]
    [InlineData("prefix", "plain.pdf", true)]
    [InlineData("append", "plain.pdf", true)]
    [InlineData("template", "plain.pdf", true)]
    [InlineData("template", "not-a-pdf.txt", false)]
    public void PickupRequiresTheMarkerOnlyInInsertMode(string mode, string file, bool eligible) =>
        Assert.Equal(eligible, Scanner.Eligible(file, mode));

    [Fact]
    public void NamingTemplateKeysRoundTrip()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = new Config { NamingMode = "template", NamingTemplate = "{date}-{name}" };
        cfg.Routes.Add(new Route { Label = "A", Path = "C:/a",
            NamingMode = "template", NamingTemplate = "{name}!" });
        Config.Save(cfg, path);
        var back = Config.Load(path);
        Assert.Equal("{date}-{name}", back.NamingTemplate);
        Assert.Equal("{name}!", back.Routes.Single().NamingTemplate);
    }

    [Fact]
    public void NewModesPassLoadValidation()
    {
        foreach (var mode in new[] { "prefix", "append" })
        {
            var path = Path.Combine(_dir, $"{mode}.json");
            File.WriteAllText(path, $$"""{"inbox":"C:/in","naming_mode":"{{mode}}"}""");
            Assert.Equal(mode, Config.Load(path).NamingMode);
        }
    }

    [Fact]
    public void GlobalTemplateModeWithBadTemplateFailsLoadReadably()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path,
            """{"inbox":"C:/in","naming_mode":"template","naming_template":"{bogus}"}""");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(path));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void RouteTemplatesAreNotValidatedAtLoad()
    {
        var path = Path.Combine(_dir, "route.json");
        File.WriteAllText(path, """
            {"inbox":"C:/in","routes":[
              {"label":"A","path":"C:/a","naming_mode":"template","naming_template":"{bogus}"}]}
            """);
        Assert.Equal("{bogus}", Config.Load(path).Routes.Single().NamingTemplate);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/OrdoSort.Core.Tests --filter NamingConfigTests -v minimal` — compile failure (properties missing).

- [ ] **Step 3: Implement.**

`Scanner.Eligible` becomes:

```csharp
    /// <summary>Which files the inbox picks up: insert mode needs the "--"
    /// marker to splice into; every other mode works on ANY pdf.</summary>
    public static bool Eligible(string filename, string mode) =>
        mode == Naming.ModeInsert
            ? Naming.InboxRegex().IsMatch(filename)
            : filename.EndsWith(Naming.PdfExt, StringComparison.OrdinalIgnoreCase);
```

`Config.cs`: add after `naming_mode`:

```csharp
    [JsonPropertyName("naming_template")] public string NamingTemplate { get; set; } = "";
```

`Route`: add after its `naming_mode`:

```csharp
    [JsonPropertyName("naming_template")] public string? NamingTemplate { get; set; }
```

`Normalize()`: add `NamingTemplate ??= "";` with the other string defaults. `Load` validation, right after the existing `naming_mode` check:

```csharp
        if (cfg.NamingMode == Naming.ModeTemplate)
        {
            var templateError = Naming.ValidateTemplate(cfg.NamingTemplate);
            if (templateError.Length > 0)
                throw new ConfigException($"naming_template: {templateError}");
        }
```

- [ ] **Step 4: Run tests** — filter pass, then full Core suite green.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/Scanner.cs src/OrdoSort.Core/Config.cs tests/OrdoSort.Core.Tests/NamingConfigTests.cs
git commit -m "feat(core): pickup rule for new modes + naming_template config keys

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 3: Thread templates through Commit and the live preview

**Files:**
- Modify: `src/OrdoSort.Core/Commit.cs` (`CommitFile`), `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` (the `BuildTarget` preview call ~line 1092 and the `CommitFile` call — find it: `grep -n "CommitFile" src tests -r`)
- Test: the file where `CommitFile` tests live (find: `grep -rln "CommitFile" tests/`) — add one template end-to-end test

**Interfaces:**
- Consumes: Task 1's `BuildTarget` optional params; Task 2's config keys.
- Produces: `Commit.CommitFile(string src, string typedName, Route route, string globalMode, string globalTemplate = "", DateTime? today = null)`.

- [ ] **Step 1: Extend `CommitFile`** — new optional parameters, threaded into its `Build()` local:

```csharp
    public static CommitOutcome CommitFile(
        string src, string typedName, Route route, string globalMode,
        string globalTemplate = "", DateTime? today = null)
    {
        ...
        Naming.NameResult Build() => Naming.BuildTarget(
            Path.GetFileName(src), typedName, route.NamingMode, globalMode,
            route.Suffix, route.AppendSuffix,
            name => File.Exists(Path.Combine(destDir, name)),
            routeTemplate: route.NamingTemplate, globalTemplate: globalTemplate,
            today: today);
        ...
```

(`SkipFile` is untouched — blank names bypass mode logic.)

- [ ] **Step 2: Thread the Wpf call sites.** In `ShellViewModel`: the commit call gains `_cfg.NamingTemplate` as `globalTemplate`; the live-preview `BuildTarget` call (~line 1092) gains `routeTemplate: <route>.NamingTemplate, globalTemplate: _cfg.NamingTemplate` using whatever route variable is in scope there (read the surrounding method — it resolves the pending route the same way the commit does).

- [ ] **Step 3: Add the end-to-end test** in the file where `CommitFile` tests live, following its temp-dir conventions:

```csharp
    [Fact]
    public void CommitRendersARouteTemplate()
    {
        // arrange per the file's conventions: temp src dir with "scan001.pdf",
        // temp dest dir; route with NamingMode "template", NamingTemplate "{date}-{name}"
        var outcome = Commit.CommitFile(src, "SMITH JOHN", route, Naming.ModeInsert,
            globalTemplate: "", today: new DateTime(2026, 7, 31));
        Assert.EndsWith("20260731-SMITH JOHN.pdf", outcome.FiledPath);
    }
```

(Adapt arrangement and the outcome-property name to the file's real shape; the assertion is the requirement.)

- [ ] **Step 4: Run the full solution** — `dotnet test OrdoSort.sln -v minimal` — everything green.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/Commit.cs src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs tests/
git commit -m "feat: thread naming templates through commit and live preview

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 4: Enter always files

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` (`OnEnterAsync`, `MarkRouteState`)
- Test: `tests/OrdoSort.Wpf.Tests/` — the file covering `OnEnterAsync` (find: `grep -rln "OnEnterAsync" tests/`)

**Interfaces:**
- Consumes: nothing new.
- Produces: the Enter contract Task 5's UI copy relies on.

- [ ] **Step 1: Update the existing Enter tests + add the new cases.** The current tests assert the old behavior (unchecked = inert; no-last-used = hint). Rewrite per the new contract and add:

```csharp
    [Fact]
    public void EnterFilesToTheFirstRouteBeforeAnyRouteWasUsed()
    { /* fixture in last-used mode (EnterCommits=true), fresh session, press Enter
         → the file lands in route 0's folder */ }

    [Fact]
    public void EnterFilesToTheFirstRouteInFirstDestinationMode()
    { /* EnterCommits=false, use route 2 first, then Enter → still route 0 */ }

    [Fact]
    public void EnterRefilesToTheLastUsedRoute()
    { /* EnterCommits=true, file one to route 2, then Enter → route 2 */ }

    [Fact]
    public void EnterTargetMarkerTracksTheMode()
    { /* EnterCommits=false → Routes[0].IsEnterTarget; EnterCommits=true after
         using route 2 → Routes[2].IsEnterTarget */ }
```

Write them as real tests against the headless shell fixture, following the file's existing arrangement helpers (it already files documents through `OnRouteAsync` and asserts `IsEnterTarget`).

- [ ] **Step 2: Run to verify the new tests fail** (old behavior still in place).

- [ ] **Step 3: Implement.** `OnEnterAsync` becomes:

```csharp
    internal Task OnEnterAsync()
    {
        if (Screen != Screen.Processing || _cfg.Routes.Count == 0) return Task.CompletedTask;
        var target = _cfg.EnterCommits ? (_lastRoute ?? 0) : 0;
        if (target >= _cfg.Routes.Count) target = 0;
        return OnRouteAsync(target);
    }
```

`MarkRouteState`'s target line becomes:

```csharp
        var enterTarget = Routes.Count == 0
            ? (int?)null
            : (_cfg.EnterCommits ? (_lastRoute ?? 0) : 0);
```

Update the `OnEnter` doc comment to the new contract ("Enter always files: the last-used route — starting at the first — or always the first, per enter_commits."). Delete the retired status-line hint.

- [ ] **Step 4: Run the Wpf suite** — all green.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs tests/OrdoSort.Wpf.Tests
git commit -m "feat(shell): Enter always files — last-used or first destination

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 5: Settings UI — five modes, template boxes, Enter radios

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`, `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml`
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2 (constants, `ValidateTemplate`, config keys).
- Produces: VM members `FilingMode` (string), radio wrappers `ModeInsert/ModeReplace/ModePrefix/ModeAppend/ModeTemplate` (bool), `NamingTemplate` (string), `TemplateNote` (string), extended `ModeChoices`, `RouteVm.NamingTemplate` (string, `""`↔null), `RouteFilingExample` (string).

- [ ] **Step 1: View model.**
  - Replace the `InsertMode` bool with `string FilingMode` (one of the five mode values) plus five bool radio wrappers, each `get => FilingMode == Naming.ModeX; set { if (value) FilingMode = Naming.ModeX; }`; setting `FilingMode` raises all five wrappers + `FilingExample` + `TemplateNote`.
  - `NamingTemplate` string property (raises `FilingExample` + `TemplateNote`). `TemplateNote` = `Naming.ValidateTemplate(NamingTemplate)` when `FilingMode == template` else `""` — bind it under the template box like the page's other note texts.
  - `FilingExample` switches on `FilingMode`, calling `Naming.BuildTarget` as it does today with `globalMode: FilingMode, globalTemplate: NamingTemplate` (wrap in try/catch `ArgumentException` → show the message as the example text, mirroring how invalid names surface today).
  - From-config (~line 403): `FilingMode = current.NamingMode; NamingTemplate = current.NamingTemplate;`. Build (~line 1185): `cfg.NamingMode = FilingMode; cfg.NamingTemplate = NamingTemplate.Trim();` — and template validation joins the existing errors flow: when `FilingMode == template` and `ValidateTemplate` fails, add its message to the same errors list that blocks save today (find the errors mechanism in `TryBuildResult`).
  - `ModeChoices` (the route-override combo source): extend with `prefix` → "Prefix", `append` → "Append", `template` → "Custom template" entries (keep the existing blank-inherit entry first and the labels for insert/replace as they are).
  - `RouteVm`: add `NamingTemplate` string property mapping `""` ↔ null exactly as its `NamingMode` does (lines ~94/~106).
  - `RouteFilingExample`: computed on the settings VM from the selected route's effective mode+template over the global ones — `Naming.BuildTarget("20240115--12345.pdf", "SMITH JOHN", route.NamingMode…, …)` in a try/catch, raised when the selected route, its mode/template, or the global mode/template change.

- [ ] **Step 2: XAML — Filing tab.** Replace the two naming radios with five, keeping the worked-example style:
  - `Insert at the "--"   ·   20240115--12345.pdf  →  20240115-Smith John-12345.pdf` → `IsChecked="{Binding ModeInsert}"`
  - `Full replace   ·   20240115--12345.pdf  →  Smith John.pdf` → `ModeReplace`
  - `Prefix   ·   scan001.pdf  →  Smith John-scan001.pdf` → `ModePrefix`
  - `Append   ·   scan001.pdf  →  scan001-Smith John.pdf` → `ModeAppend`
  - `Custom template` → `ModeTemplate`, followed by an indented row: a TextBox bound `NamingTemplate` (`UpdateSourceTrigger=PropertyChanged`, `IsEnabled="{Binding ModeTemplate}"`), a caption `tokens: {name} · {original} · {date}` in `SubtleText`, and a `NoteText` TextBlock bound `TemplateNote`.
  - Update the pickup caption text to: `Insert mode picks up only PDFs with "--" in the name; every other mode picks up every PDF. Anything else shows as "ignored" on the Ready screen.`
  - Replace the Enter checkbox with the radio pair (the page already uses the `InvertBool` converter for the naming pair today — reuse it):
    ```xaml
    <TextBlock Text="Enter files to:" Margin="0,4,0,2" />
    <RadioButton Content="the last-used destination (starts at the first)"
                 IsChecked="{Binding EnterCommits}" Margin="12,0,0,2" />
    <RadioButton Content="always the first destination"
                 IsChecked="{Binding EnterCommits, Converter={StaticResource InvertBool}}"
                 Margin="12,0,0,0" />
    ```

- [ ] **Step 3: XAML — Destinations tab.** Under the existing `Naming mode:` override row, add a template row visible only when the override is Custom template (a `Style.Triggers` `DataTrigger` on `{Binding NamingMode}` value `template` toggling `Visibility`, defaulting `Collapsed`): label `Template:`, TextBox bound `NamingTemplate` (`UpdateSourceTrigger=PropertyChanged`). Under the route Preview, add a `SubtleText` TextBlock bound to `RouteFilingExample` prefixed `files as: ` (bind via `StringFormat`).

- [ ] **Step 4: Tests** — update the Settings tests that set/assert `InsertMode` to use `FilingMode`/wrappers (preserve each test's intent), and add:

```csharp
    [Fact]
    public void FiveFilingModesRoundTripWithTemplate()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","naming_mode":"append"}""");
        var vm = /* construct per file conventions */;
        Assert.True(vm.ModeAppend);
        vm.ModeTemplate = true;
        vm.NamingTemplate = "{date}-{name}";
        var built = /* build */;
        Assert.Equal("template", built.NamingMode);
        Assert.Equal("{date}-{name}", built.NamingTemplate);
    }

    [Fact]
    public void ABadTemplateBlocksSaveWithItsOwnMessage()
    {
        /* vm in template mode, NamingTemplate "{bogus}" → the build/validation
           path reports an error mentioning "bogus" and does not produce a config */
    }

    [Fact]
    public void RouteTemplateRoundTripsThroughTheRouteEditor()
    {
        /* route with override mode "template" + template "{name}!" survives
           from-config → VM → build-config */
    }
```

(Adapt helper names to the file; assertions are the requirement.)

- [ ] **Step 5: Run the Wpf suite** — all green.

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests
git commit -m "feat(settings): five naming modes with templates + Enter radio pair

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 6: Full gate and push

**Files:** none — verification and delivery.

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean build, everything green (record exact totals; baseline 589 + additions).
- [ ] **Step 2:** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — "All checks passed" (the workbench config uses insert + per-route overrides; its checks must still hold).
- [ ] **Step 3:** Launch sanity: build Debug, `Start-Process` the exe with `--config demo-full/config.json`, confirm the process appears with its window, open nothing else, `Stop-Process`. 
- [ ] **Step 4:** `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, no tags.
