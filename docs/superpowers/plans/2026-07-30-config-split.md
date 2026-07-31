# Config Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `config.json`'s four list sections into per-section JSON files (`destinations.json`, `monitored-folders.json`, `alerts.json`, `box-labels.json`) with configurable paths, self-completing migration, and multi-machine-safe box-label counters.

**Architecture:** Four small "doc" types serialize each side file; `Config.Load` gains side-file-wins/inline-fallback resolution and `Config.Save` writes the main file without the split sections plus the three Settings-owned docs (box-labels is bootstrap-only). A new `BoxLabelStore` does exclusive-open read-modify-write with retries for every label mutation. The Settings UI gains a Data files section; demo generators emit the split layout.

**Tech Stack:** C# / .NET 8, System.Text.Json (incl. `JsonNode` for section-stripped saves), xUnit. Repo `S:\OrdoSort`, branch `main` (user-approved; normal commits per task, push only in the final task).

## Global Constraints

- Config keys and defaults, verbatim from the spec: `destinations_file` → `destinations.json`, `monitored_folders_file` → `monitored-folders.json`, `alerts_file` → `alerts.json`, `box_labels_file` → `box-labels.json`. Relative paths resolve against `config.json`'s directory.
- Side-file contents: `{"routes": [...]}`, `{"watch_folders": [...]}`, `{"alert_texts": [...]}`, `{"label_clients": [...]}`; unknown top-level keys round-trip per file; serialization matches `config.json` (indented, relaxed escaping, trailing `\n`).
- Load: side file wins → inline fallback → empty. Unreadable/invalid side file ⇒ `ConfigException` naming that file.
- Save: main file omits the four sections; writes destinations/monitored-folders/alerts docs; **never overwrites** `box-labels.json` (creates it only if missing). First run creates all five files.
- Box-label mutations: exclusive open (`FileShare.None`), retry every **150 ms up to 5 s**, then readable failure ("another station is using the box-labels file — try again").
- Existing 557 tests stay green (modulo the two format-sensitive updates specified in Task 2); new tests only add.
- Tests must not reduce coverage of the inline-legacy path — the smoke tools' inline temp configs are intentionally left unsplit as living fallback coverage.

---

### Task 1: Section doc types + split-aware Load

**Files:**
- Create: `src/OrdoSort.Core/ConfigDocs.cs`
- Modify: `src/OrdoSort.Core/Config.cs` (new properties + `Load` + helpers)
- Test: `tests/OrdoSort.Core.Tests/ConfigSplitTests.cs` (new)

**Interfaces:**
- Produces (later tasks rely on these exact names):
  - `DestinationsDoc { List<Route> Routes; Dictionary<string, JsonElement> Extras }` and analogous `MonitoredFoldersDoc { WatchFolders }`, `AlertsDoc { AlertTexts (List<string>) }`, `BoxLabelsDoc { LabelClients }` — all in namespace `OrdoSort.Core`.
  - `Config` properties: `string DestinationsFile / MonitoredFoldersFile / AlertsFile / BoxLabelsFile` and `[JsonIgnore] Dictionary<string, JsonElement> DestinationsFileExtras / MonitoredFoldersFileExtras / AlertsFileExtras / BoxLabelsFileExtras`.
  - `public static string Config.ResolveBeside(string configPath, string sectionPath)`.

- [ ] **Step 1: Write the failing tests** — create `tests/OrdoSort.Core.Tests/ConfigSplitTests.cs`:

```csharp
namespace OrdoSort.Core.Tests;

/// <summary>Split config: side files win, inline is the legacy fallback,
/// and a broken side file fails naming that file.</summary>
public class ConfigSplitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordosplit_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string json)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void SideFileWinsOverInline()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","routes":[{"label":"INLINE","path":"C:/x"}]}""");
        Write("destinations.json",
            """{"routes":[{"label":"SIDE","path":"C:/y"}],"custom_top":"kept"}""");
        var c = Config.Load(cfg);
        var r = Assert.Single(c.Routes);
        Assert.Equal("SIDE", r.Label);
        Assert.True(c.DestinationsFileExtras.ContainsKey("custom_top"));
    }

    [Fact]
    public void InlineIsUsedWhenSideFileMissing()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","alert_texts":["URGENT"],"watch_folders":[{"label":"W","path":"C:/w"}]}""");
        var c = Config.Load(cfg);
        Assert.Equal(new[] { "URGENT" }, c.AlertTexts);
        Assert.Equal("W", Assert.Single(c.WatchFolders).Label);
    }

    [Fact]
    public void MissingEverythingIsEmpty()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        var c = Config.Load(cfg);
        Assert.Empty(c.Routes);
        Assert.Empty(c.WatchFolders);
        Assert.Empty(c.AlertTexts);
        Assert.Empty(c.LabelClients);
    }

    [Fact]
    public void BrokenSideFileNamesTheFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("alerts.json", "{ not json");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(cfg));
        Assert.Contains("alerts.json", ex.Message);
    }

    [Fact]
    public void SideFileNullsNormalizeLikeInline()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("destinations.json",
            """{"routes":[null,{"label":null,"path":"C:/a"}]}""");
        var c = Config.Load(cfg);
        var r = Assert.Single(c.Routes);          // null entry dropped
        Assert.Equal("", r.Label);                // null field defaulted
        Assert.NotNull(r.Extras);
    }

    [Fact]
    public void RelativeSectionPathResolvesBesideConfig()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        File.WriteAllText(Path.Combine(sub, "team-dests.json"),
            """{"routes":[{"label":"TEAM","path":"C:/t"}]}""");
        var cfg = Write("config.json",
            """{"inbox":"C:/in","destinations_file":"shared/team-dests.json"}""");
        var c = Config.Load(cfg);
        Assert.Equal("TEAM", Assert.Single(c.Routes).Label);
        Assert.Equal(Path.Combine(_dir, "x.json"),
            Config.ResolveBeside(cfg, "x.json"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter ConfigSplitTests -v minimal`
Expected: compile FAILURE (`DestinationsFileExtras`, `ResolveBeside` don't exist yet) — that counts as the red step.

- [ ] **Step 3: Implement** — create `src/OrdoSort.Core/ConfigDocs.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrdoSort.Core;

/// <summary>The four per-section config files. Each is a JSON object with a
/// single list key; unknown top-level keys round-trip (same contract as
/// config.json itself).</summary>
public sealed class DestinationsDoc
{
    [JsonPropertyName("routes")] public List<Route> Routes { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class MonitoredFoldersDoc
{
    [JsonPropertyName("watch_folders")] public List<WatchFolder> WatchFolders { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class AlertsDoc
{
    [JsonPropertyName("alert_texts")] public List<string> AlertTexts { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

public sealed class BoxLabelsDoc
{
    [JsonPropertyName("label_clients")] public List<LabelClient> LabelClients { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Extras { get; set; } = new();
}
```

In `src/OrdoSort.Core/Config.cs`, add after the `label_clients` property (`LabelClients`):

```csharp
    // ---- split config: where each section lives (relative = beside config.json)
    [JsonPropertyName("destinations_file")] public string DestinationsFile { get; set; } = "destinations.json";
    [JsonPropertyName("monitored_folders_file")] public string MonitoredFoldersFile { get; set; } = "monitored-folders.json";
    [JsonPropertyName("alerts_file")] public string AlertsFile { get; set; } = "alerts.json";
    [JsonPropertyName("box_labels_file")] public string BoxLabelsFile { get; set; } = "box-labels.json";

    // Unknown top-level keys of each side file, carried for round-trip
    [JsonIgnore] public Dictionary<string, JsonElement> DestinationsFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> MonitoredFoldersFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> AlertsFileExtras { get; set; } = new();
    [JsonIgnore] public Dictionary<string, JsonElement> BoxLabelsFileExtras { get; set; } = new();
```

Add to `Normalize()` (with the other string defaults):

```csharp
        DestinationsFile ??= "destinations.json";
        MonitoredFoldersFile ??= "monitored-folders.json";
        AlertsFile ??= "alerts.json";
        BoxLabelsFile ??= "box-labels.json";
```

Add these members to `Config`:

```csharp
    /// <summary>Resolve a section-file path: absolute stays; relative lands
    /// beside config.json (the names_file / history_db rule).</summary>
    public static string ResolveBeside(string configPath, string sectionPath) =>
        Path.IsPathRooted(sectionPath)
            ? sectionPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, sectionPath);

    public static T? ReadDoc<T>(string configPath, string sectionPath) where T : class
    {
        var full = ResolveBeside(configPath, sectionPath);
        if (!File.Exists(full)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(full), Opts)
                   ?? throw new ConfigException($"Config file {full} is empty");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Config file {full} is not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Config file {full} could not be read: {ex.Message}");
        }
    }
```

At the end of `Load(path)` (after the existing validation, before `return cfg;`) add:

```csharp
        // ---- split sections: a side file wins; inline (legacy) is the fallback
        if (ReadDoc<DestinationsDoc>(path, cfg.DestinationsFile) is { } dd)
        {
            cfg.Routes = Clean(dd.Routes);
            cfg.DestinationsFileExtras = dd.Extras ?? new();
        }
        if (ReadDoc<MonitoredFoldersDoc>(path, cfg.MonitoredFoldersFile) is { } md)
        {
            cfg.WatchFolders = Clean(md.WatchFolders);
            cfg.MonitoredFoldersFileExtras = md.Extras ?? new();
        }
        if (ReadDoc<AlertsDoc>(path, cfg.AlertsFile) is { } ad)
        {
            cfg.AlertTexts = Clean(ad.AlertTexts);
            cfg.AlertsFileExtras = ad.Extras ?? new();
        }
        if (ReadDoc<BoxLabelsDoc>(path, cfg.BoxLabelsFile) is { } bd)
        {
            cfg.LabelClients = Clean(bd.LabelClients);
            cfg.BoxLabelsFileExtras = bd.Extras ?? new();
        }
        cfg.NormalizeSectionItems();
        return cfg;
```

and change the existing `return cfg;` above it into falling through to this block (there must be exactly one return). Extract the per-item loops at the bottom of `Normalize()` (the `foreach (var r in Routes) …`, `foreach (var w in WatchFolders) …`, `foreach (var c in LabelClients) …` blocks) into a new `internal void NormalizeSectionItems()` that `Normalize()` also calls — so side-file content gets identical null-hardening to inline content. `AlertTexts = Clean(AlertTexts);` stays in `Normalize()` and is re-applied above via `Clean`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter ConfigSplitTests -v minimal`
Expected: all 6 PASS.

- [ ] **Step 5: Full Core suite still green**

Run: `dotnet test tests/OrdoSort.Core.Tests -v minimal`
Expected: 301 + 6 = 307 passed (Save is untouched so far; nothing else changes).

- [ ] **Step 6: Commit**

```bash
git add src/OrdoSort.Core/ConfigDocs.cs src/OrdoSort.Core/Config.cs tests/OrdoSort.Core.Tests/ConfigSplitTests.cs
git commit -m "feat(core): section doc types and split-aware config load

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 2: Split-aware Save/TrySave + migration

**Files:**
- Modify: `src/OrdoSort.Core/Config.cs` (`Save`, `TrySave`)
- Modify: `tests/OrdoSort.Core.Tests/QcTests.cs` (`RouteUnknownKeysSurviveRoundTrip` — format-sensitive)
- Test: `tests/OrdoSort.Core.Tests/ConfigSplitTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1's doc types, `ResolveBeside`, `XxxFileExtras`.
- Produces: `Config.Save(cfg, path)` writes main + 3 docs + bootstraps `box-labels.json` only if missing; `Config.TrySave(cfg, path, out string error)` reports per-file failures joined with `"; "`.

- [ ] **Step 1: Write the failing tests** — append to `ConfigSplitTests`:

```csharp
    [Fact]
    public void SaveWritesSideFilesAndStripsInlineSections()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","routes":[{"label":"A","path":"C:/a"}],
             "watch_folders":[{"label":"W","path":"C:/w"}],
             "alert_texts":["URGENT"],
             "label_clients":[{"id":"ACME","destroy_days":30,"next_number":7}]}
            """);
        var c = Config.Load(cfg);          // inline fallback path
        Config.Save(c, cfg);               // migration completes here

        var main = File.ReadAllText(cfg);
        Assert.DoesNotContain("\"routes\"", main);
        Assert.DoesNotContain("\"watch_folders\"", main);
        Assert.DoesNotContain("\"alert_texts\"", main);
        Assert.DoesNotContain("\"label_clients\"", main);
        Assert.Contains("\"destinations_file\"", main);

        Assert.Contains("\"A\"", File.ReadAllText(Path.Combine(_dir, "destinations.json")));
        Assert.Contains("\"W\"", File.ReadAllText(Path.Combine(_dir, "monitored-folders.json")));
        Assert.Contains("URGENT", File.ReadAllText(Path.Combine(_dir, "alerts.json")));
        Assert.Contains("\"ACME\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));

        // and the split files load back identically
        var back = Config.Load(cfg);
        Assert.Equal("A", Assert.Single(back.Routes).Label);
        Assert.Equal(7, Assert.Single(back.LabelClients).NextNumber);
    }

    [Fact]
    public void SaveNeverOverwritesExistingBoxLabels()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("box-labels.json",
            """{"label_clients":[{"id":"REAL","destroy_days":30,"next_number":99}]}""");
        var c = Config.Load(cfg);
        c.LabelClients = new() { new LabelClient { Id = "STALE", NextNumber = 1 } };
        Config.Save(c, cfg);
        Assert.Contains("\"REAL\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
        Assert.DoesNotContain("STALE", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
    }

    [Fact]
    public void SideFileExtrasSurviveSaveRoundTrip()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("alerts.json", """{"alert_texts":["A"],"admin_note":"keep me"}""");
        var c = Config.Load(cfg);
        Config.Save(c, cfg);
        Assert.Contains("keep me", File.ReadAllText(Path.Combine(_dir, "alerts.json")));
    }

    [Fact]
    public void FirstRunCreatesAllFiveFiles()
    {
        var cfg = Path.Combine(_dir, "fresh", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        Config.Load(cfg);   // first-run: creates defaults
        foreach (var f in new[] { "config.json", "destinations.json",
                 "monitored-folders.json", "alerts.json", "box-labels.json" })
            Assert.True(File.Exists(Path.Combine(_dir, "fresh", f)), f);
    }

    [Fact]
    public void TrySaveNamesTheFailingSideFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        var dests = Path.Combine(_dir, "destinations.json");
        File.WriteAllText(dests, "{}");
        using var hold = new FileStream(dests, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var c = new Config();
        var ok = Config.TrySave(c, cfg, out var error);
        Assert.False(ok);
        Assert.Contains("destinations.json", error);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter ConfigSplitTests -v minimal`
Expected: the five new tests FAIL (Save still writes inline sections and no side files).

- [ ] **Step 3: Implement** — in `Config.cs`, add `using System.Text.Json.Nodes;` and replace `Save` and `TrySave` with:

```csharp
    /// <summary>Write the main config (without the split sections) and the
    /// Settings-owned side files. box-labels.json is bootstrap-only: created
    /// when missing, never overwritten — its counters belong to the Box
    /// labels tool's exclusive writer (BoxLabelStore).</summary>
    public static void Save(Config cfg, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        SaveMain(cfg, path);
        WriteDoc(path, cfg.DestinationsFile,
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras });
        WriteDoc(path, cfg.MonitoredFoldersFile,
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras });
        WriteDoc(path, cfg.AlertsFile,
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras });
        var labels = ResolveBeside(path, cfg.BoxLabelsFile);
        if (!File.Exists(labels))
            WriteJson(labels,
                new BoxLabelsDoc { LabelClients = cfg.LabelClients, Extras = cfg.BoxLabelsFileExtras });
    }

    private static void SaveMain(Config cfg, string path)
    {
        var node = JsonSerializer.SerializeToNode(cfg, Opts)!.AsObject();
        node.Remove("routes");
        node.Remove("watch_folders");
        node.Remove("alert_texts");
        node.Remove("label_clients");
        File.WriteAllText(path, node.ToJsonString(Opts) + "\n");
    }

    private static void WriteDoc<T>(string configPath, string sectionPath, T doc) =>
        WriteJson(ResolveBeside(configPath, sectionPath), doc);

    internal static void WriteJson<T>(string fullPath, T doc) =>
        File.WriteAllText(fullPath, JsonSerializer.Serialize(doc, Opts) + "\n");

    /// <summary>Save that reports failure instead of crashing — each file is
    /// attempted independently and every failure is named.</summary>
    public static bool TrySave(Config cfg, string path, out string error)
    {
        var errors = new List<string>();
        Attempt(() => SaveMain(cfg, path), path);
        Attempt(() => WriteDoc(path, cfg.DestinationsFile,
            new DestinationsDoc { Routes = cfg.Routes, Extras = cfg.DestinationsFileExtras }),
            ResolveBeside(path, cfg.DestinationsFile));
        Attempt(() => WriteDoc(path, cfg.MonitoredFoldersFile,
            new MonitoredFoldersDoc { WatchFolders = cfg.WatchFolders, Extras = cfg.MonitoredFoldersFileExtras }),
            ResolveBeside(path, cfg.MonitoredFoldersFile));
        Attempt(() => WriteDoc(path, cfg.AlertsFile,
            new AlertsDoc { AlertTexts = cfg.AlertTexts, Extras = cfg.AlertsFileExtras }),
            ResolveBeside(path, cfg.AlertsFile));
        Attempt(() =>
        {
            var labels = ResolveBeside(path, cfg.BoxLabelsFile);
            if (!File.Exists(labels))
                WriteJson(labels, new BoxLabelsDoc { LabelClients = cfg.LabelClients, Extras = cfg.BoxLabelsFileExtras });
        }, ResolveBeside(path, cfg.BoxLabelsFile));

        error = string.Join("; ", errors);
        return errors.Count == 0;

        void Attempt(Action write, string file)
        {
            try { write(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or DirectoryNotFoundException)
            {
                errors.Add($"Couldn't save settings to {file}: {ex.Message}");
            }
        }
    }
```

(The first-run branch in `Load` already calls `Save(fresh, path)` — with the new `Save` it now creates all five files, which is exactly the spec's first-run invariant.)

- [ ] **Step 4: Update the one format-sensitive existing test** — in `tests/OrdoSort.Core.Tests/QcTests.cs`, `RouteUnknownKeysSurviveRoundTrip` asserts the custom route key survives in `config.json`; post-split it survives in `destinations.json`. Replace the two assertion lines:

```csharp
        Assert.Contains("my_custom_key", File.ReadAllText(path));
        Assert.Contains("must survive", File.ReadAllText(path));
```

with:

```csharp
        var dests = Path.Combine(_dir, "destinations.json");
        Assert.Contains("my_custom_key", File.ReadAllText(dests));
        Assert.Contains("must survive", File.ReadAllText(dests));
```

If any OTHER existing test fails on the new save format, apply the same pattern — the data now lives in the section's side file; assert there. Record every such change in your report.

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/OrdoSort.Core.Tests -v minimal`
Expected: all pass (307 + 5 = 312, plus any Step 4-style updates — count unchanged, content updated).

- [ ] **Step 6: Full solution test** (WPF suite consumes Load/TrySave via ShellViewModel; it must stay green untouched)

Run: `dotnet test OrdoSort.sln -v minimal`
Expected: Wpf 256 still pass.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Core/Config.cs tests/OrdoSort.Core.Tests/ConfigSplitTests.cs tests/OrdoSort.Core.Tests/QcTests.cs
git commit -m "feat(core): split-aware save with self-completing migration

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 3: BoxLabelStore — exclusive, retrying label mutations

**Files:**
- Create: `src/OrdoSort.Core/BoxLabelStore.cs`
- Test: `tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs` (new)

**Interfaces:**
- Consumes: `BoxLabelsDoc`, `Config.WriteJson` serialization style (same `Opts`).
- Produces (Task 4 relies on these exact signatures):
  - `public static BoxLabelsDoc BoxLabelStore.Read(string fullPath)` — shared read; missing file ⇒ empty doc.
  - `public static T BoxLabelStore.Mutate<T>(string fullPath, Func<BoxLabelsDoc, T> mutate)` — exclusive read-modify-write; retries sharing violations every 150 ms up to 5 s, then throws `ConfigException("another station is using the box-labels file — try again (<path>)")`.

- [ ] **Step 1: Write the failing tests** — create `tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs`:

```csharp
namespace OrdoSort.Core.Tests;

/// <summary>The exclusive box-labels writer: fresh reads, atomic increments,
/// readable failure when another station holds the file.</summary>
public class BoxLabelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordolabels_").FullName;
    private string PathOf(string n) => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ReadOfMissingFileIsEmptyDoc()
    {
        var doc = BoxLabelStore.Read(PathOf("box-labels.json"));
        Assert.Empty(doc.LabelClients);
    }

    [Fact]
    public void MutateCreatesPersistsAndReturns()
    {
        var p = PathOf("box-labels.json");
        var start = BoxLabelStore.Mutate(p, doc =>
        {
            var c = new LabelClient { Id = "ACME", NextNumber = 5 };
            doc.LabelClients.Add(c);
            var s = c.NextNumber;
            c.NextNumber += 3;
            return s;
        });
        Assert.Equal(5, start);
        Assert.Equal(8, BoxLabelStore.Read(p).LabelClients.Single().NextNumber);
    }

    [Fact]
    public void ConcurrentIncrementsNeverCollide()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.LabelClients.Add(
            new LabelClient { Id = "ACME", NextNumber = 1 }); return 0; });

        var starts = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 8, _ =>
            starts.Add(BoxLabelStore.Mutate(p, d =>
            {
                var c = d.LabelClients.Single(x => x.Id == "ACME");
                var s = c.NextNumber;
                c.NextNumber += 1;
                return s;
            })));

        Assert.Equal(8, starts.Distinct().Count());          // no duplicates
        Assert.Equal(9, BoxLabelStore.Read(p).LabelClients[0].NextNumber); // gapless
    }

    [Fact]
    public void HeldFileFailsReadablyAfterRetries()
    {
        var p = PathOf("box-labels.json");
        File.WriteAllText(p, """{"label_clients":[]}""");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() =>
            BoxLabelStore.Mutate(p, d => 0, maxWaitMs: 700));
        Assert.Contains("another station", ex.Message);
        Assert.True(sw.ElapsedMilliseconds >= 600, "should have retried before failing");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter BoxLabelStoreTests -v minimal`
Expected: compile FAILURE (`BoxLabelStore` doesn't exist).

- [ ] **Step 3: Implement** — create `src/OrdoSort.Core/BoxLabelStore.cs`:

```csharp
using System.Text.Json;

namespace OrdoSort.Core;

/// <summary>All mutations of box-labels.json go through here. The file can
/// live on a share with several stations printing: an exclusive open with
/// retries (the busy_timeout philosophy) makes counter advances atomic.</summary>
public static class BoxLabelStore
{
    private const int RetryDelayMs = 150;
    private const int DefaultMaxWaitMs = 5000;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Non-exclusive read for display. Missing file = no clients yet.</summary>
    public static BoxLabelsDoc Read(string fullPath)
    {
        if (!File.Exists(fullPath)) return new BoxLabelsDoc();
        try
        {
            return JsonSerializer.Deserialize<BoxLabelsDoc>(File.ReadAllText(fullPath), Opts)
                   ?? new BoxLabelsDoc();
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Config file {fullPath} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Exclusive read-modify-write. The callback sees the FRESH
    /// on-disk doc (never a stale in-memory copy), mutates it, and its
    /// return value is handed back after the write lands.</summary>
    public static T Mutate<T>(string fullPath, Func<BoxLabelsDoc, T> mutate,
        int maxWaitMs = DefaultMaxWaitMs)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var fs = new FileStream(fullPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(fs, leaveOpen: true);
                var text = reader.ReadToEnd();
                var doc = text.Trim().Length == 0
                    ? new BoxLabelsDoc()
                    : JsonSerializer.Deserialize<BoxLabelsDoc>(text, Opts) ?? new BoxLabelsDoc();
                doc.LabelClients ??= new();
                doc.Extras ??= new();

                var result = mutate(doc);

                fs.Seek(0, SeekOrigin.Begin);
                fs.SetLength(0);
                using var writer = new StreamWriter(fs);
                writer.Write(JsonSerializer.Serialize(doc, Opts) + "\n");
                writer.Flush();
                return result;
            }
            catch (JsonException ex)
            {
                throw new ConfigException($"Config file {fullPath} is not valid JSON: {ex.Message}");
            }
            catch (IOException) when (sw.ElapsedMilliseconds + RetryDelayMs <= maxWaitMs)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                throw new ConfigException(
                    $"another station is using the box-labels file — try again ({fullPath})");
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/OrdoSort.Core.Tests --filter BoxLabelStoreTests -v minimal`
Expected: 4 PASS (the concurrency test may take a few seconds — retries under contention are the point).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Core/BoxLabelStore.cs tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs
git commit -m "feat(core): exclusive retrying box-label store

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 4: Rewire the Box labels tool to the store

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs` (ctor, `Persist`, `Advance`, print/save flows)
- Modify: `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` (expose the resolved path)
- Modify: the `LabelMakerViewModel` construction site (find it: `grep -rn "new LabelMakerViewModel" src/`)
- Test: `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` (the label-maker tests there adapt; add fresh-counter test)

**Interfaces:**
- Consumes: `BoxLabelStore.Read/Mutate`, `Config.ResolveBeside`.
- Produces: `LabelMakerViewModel(Config cfg, string boxLabelsPath, IDialogService dialogs, …)` — the `Action saveConfig` parameter is REPLACED by `string boxLabelsPath`. `ShellViewModel` gains `internal string BoxLabelsPath => ResolvePath(_cfg.BoxLabelsFile, _cfgPath);` (using its existing `ResolvePath` helper).

- [ ] **Step 1: Adapt the view model.** In `LabelMakerViewModel`:
  - Replace field `private readonly Action _saveConfig;` with `private readonly string _boxLabelsPath;`; change the ctor parameter `Action saveConfig` → `string boxLabelsPath` and assign it. Delete the `_cfg.LabelClients` seeding line and seed from the store instead:

```csharp
        foreach (var c in BoxLabelStore.Read(boxLabelsPath).LabelClients)
            Hook(Clients.AddReturn(LabelClientVm.From(c)));
```

  - Replace `Persist()` (whole-list editing save — add/remove/edit semantics) with:

```csharp
    /// <summary>Write the edited client list back to the box-labels file.
    /// Editing is whole-list (this window IS the editor); counters advance
    /// through AdvanceFresh so a concurrent printer is never clobbered.</summary>
    internal void Persist()
    {
        try
        {
            BoxLabelStore.Mutate(_boxLabelsPath, doc =>
            {
                doc.LabelClients = Clients.Select(c => c.ToClient()).ToList();
                return 0;
            });
        }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
        }
    }
```

  - Replace `Advance(client, start, count, status)` with a fresh-counter advance used by BOTH print and Save PDF flows. The batch's start number must come from the FRESH file, not the on-screen value:

```csharp
    /// <summary>Claim `count` numbers for `client` from the FRESH on-disk
    /// counter (several stations may be printing). Returns the claimed start,
    /// or null when the file is busy past the retry window.</summary>
    private long? ClaimNumbers(LabelClientVm client, int count)
    {
        try
        {
            var start = BoxLabelStore.Mutate(_boxLabelsPath, doc =>
            {
                var c = doc.LabelClients.FirstOrDefault(x => x.Id == client.Id);
                if (c is null)
                {
                    c = client.ToClient();
                    doc.LabelClients.Add(c);
                }
                var s = c.NextNumber;
                c.NextNumber = s + count;
                return s;
            });
            client.NextNumberText = (start + count).ToString();
            return start;
        }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message, "OrdoSort — label maker");
            return null;
        }
    }
```

  - In `Print()` and the Save PDF flow, where the batch is built and then `Advance(...)` was called: call `ClaimNumbers(b.Client, b.Count)` FIRST; abort quietly on `null`; use the returned start as the batch's first number (rebuild the batch numbers from it if `BuildBatch` embedded the stale start); keep the existing status-line messages. The status-setting line from old `Advance` moves inline after a successful claim.

- [ ] **Step 2: Wire the path through.** In `ShellViewModel`, next to the other path helpers, add:

```csharp
    /// <summary>box-labels.json resolved beside the config (or absolute).</summary>
    internal string BoxLabelsPath => ResolvePath(_cfg.BoxLabelsFile, _cfgPath);
```

Find the construction site (`grep -rn "new LabelMakerViewModel" src/`) and change the second argument from the save-config callback to the path (e.g. `_shell.BoxLabelsPath` / `vm.BoxLabelsPath` — match the variable in scope there).

- [ ] **Step 3: Fix and extend the tests.** In `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs`, the label-maker tests construct the VM with a save callback — update them to pass a temp-file path (each test its own `Path.Combine(dir, "box-labels.json")`). Add:

```csharp
    [Fact]
    public void PrintClaimsNumbersFromTheFreshFileNotTheScreen()
    {
        var dir = Directory.CreateTempSubdirectory("ordomm_").FullName;
        try
        {
            var path = Path.Combine(dir, "box-labels.json");
            BoxLabelStore.Mutate(path, d => { d.LabelClients.Add(
                new LabelClient { Id = "ACME", DestroyDays = 30, NextNumber = 10 }); return 0; });

            var vm = MakeLabelVm(path);          // the test-file's existing factory, now path-based
            // another station advances the counter AFTER our window opened:
            BoxLabelStore.Mutate(path, d =>
                { d.LabelClients.Single(c => c.Id == "ACME").NextNumber = 50; return 0; });

            var start = vm.ClaimNumbers(vm.Clients.Single(c => c.Id == "ACME"), 3);
            Assert.Equal(50, start);                 // fresh, not the stale 10
            Assert.Equal(53, BoxLabelStore.Read(path).LabelClients.Single().NextNumber);
        }
        finally { Directory.Delete(dir, true); }
    }
```

Declare `ClaimNumbers` as `internal` (not `private`) — `InternalsVisibleTo` already exposes internals to this suite; that's the established pattern (`OnEnterAsync` etc.). Adapt `MakeLabelVm` to the file's existing label-maker VM factory, changed to take the box-labels path instead of a save callback.

- [ ] **Step 4: Run the Wpf suite**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -v minimal`
Expected: all pass (256 + new, minus none).

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf src/OrdoSort.Core tests/OrdoSort.Wpf.Tests
git commit -m "feat(labels): route all box-label mutations through the exclusive store

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 5: Settings — Data files section

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs` (four path properties + notes + load/save)
- Modify: `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml` (Data files section on the Tools & data tab)
- Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `Config` path keys (Task 1), `Config.ResolveBeside`, doc types.
- Produces: `SettingsViewModel` string properties `DestinationsFile`, `MonitoredFoldersFile`, `AlertsFile`, `BoxLabelsFile` with matching note properties `DestinationsFileNote` etc.

- [ ] **Step 1: View model.** Following the file's existing property pattern (`Set(ref …)` + a computed note, as `Inbox`/`InboxNote` do):
  - Add the four string properties; setting one recomputes its note.
  - Copy values from `current` in the from-config block (near line 401, where `EnterCommits = current.EnterCommits;` is) and write them into `cfg` in the build block (near line 1087): `cfg.DestinationsFile = DestinationsFile.Trim();` etc., defaulting a blank box back to the key's default filename (`"destinations.json"` etc.).
  - Note computation (one helper, four call sites):

```csharp
    private string DataFileNote(string sectionPath, Func<string, int> countEntries)
    {
        var p = sectionPath.Trim();
        if (p.Length == 0) return "blank = the default beside config.json";
        string full;
        try { full = Config.ResolveBeside(_cfgPath, p); }
        catch (Exception) { return "not a usable path"; }
        if (!File.Exists(full))
            return "will be created on save — the current list is written there";
        try { return $"{countEntries(p)} entries"; }   // counters take the SECTION path
        catch (ConfigException ex) { return ex.Message; }
    }
```

    The four counters read through the doc types with the raw section path (`ReadDoc` resolves internally and is `public` per Task 1), e.g. for destinations: `DataFileNote(DestinationsFile, sp => (Config.ReadDoc<DestinationsDoc>(_cfgPath, sp) ?? new DestinationsDoc()).Routes.Count)`.
  - `_cfgPath` must be available in `SettingsViewModel`; it already receives the current config — pass the config path in via the existing construction chain if absent (check the ctor; `ShellViewModel` holds `_cfgPath` and constructs the settings VM — add the parameter and pass it through).
  - Four Browse commands mirroring `BrowseNamesFileCommand` exactly (same dialog-service call, `.json` filter), each writing the picked path back into its property.

- [ ] **Step 1b: Fresh read when Settings opens (spec: "re-read when Settings opens").** At the Settings-open site (`MainWindow.xaml.cs`, `OnSettings` — it builds the settings VM from the shell's in-memory config): re-load from disk first so an admin's edits to shared side files appear without an app restart. In `ShellViewModel` add:

```csharp
    /// <summary>Fresh config for the Settings window: shared side files may
    /// have changed on disk. A load failure (e.g. a half-edited side file)
    /// warns and falls back to the in-memory config rather than blocking.</summary>
    internal Config FreshConfigForSettings()
    {
        try { return Config.Load(_cfgPath); }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message + "\n\nShowing the settings the app is currently running with.",
                "OrdoSort — settings");
            return _cfg;
        }
    }
```

and have `OnSettings` pass `FreshConfigForSettings()` where it currently passes the live config. Add a headless test: write a config dir, load the shell fixture, mutate `alerts.json` on disk, call `FreshConfigForSettings()`, assert the new alert term is present.

- [ ] **Step 2: XAML.** On the `Tools &amp; data` tab in `SettingsWindow.xaml`, above the "Unlock PDFs" heading, add a "Data files" block using the page's existing `FieldRow`/`FieldLabel`/`NoteText` styles — four rows, exactly like the General tab's `Names list:` row (130px label column, TextBox bound with `UpdateSourceTrigger=PropertyChanged`, Browse… button, note TextBlock underneath):
  - `Destinations:` → `DestinationsFile` / `DestinationsFileNote` / `BrowseDestinationsFileCommand`
  - `Monitored folders:` → `MonitoredFoldersFile` / `MonitoredFoldersFileNote` / `BrowseMonitoredFoldersFileCommand`
  - `Alerts:` → `AlertsFile` / `AlertsFileNote` / `BrowseAlertsFileCommand`
  - `Box labels:` → `BoxLabelsFile` / `BoxLabelsFileNote` / `BrowseBoxLabelsFileCommand`
  - Above the rows, one `SubtleText` block: `Changing a path re-points the app at that file — it does not move the current contents. A missing file is created on save from the current list.`

- [ ] **Step 3: Tests** — add to `SettingsViewModelTests.cs` (follow the file's existing load-build round-trip conventions):

```csharp
    [Fact]
    public void DataFilePathsRoundTripThroughSettings()
    {
        var cfg = LoadFromJson("""{"inbox":"C:/in","destinations_file":"shared/dests.json"}""");
        var vm = MakeVm(cfg);                      // the file's existing factory
        Assert.Equal("shared/dests.json", vm.DestinationsFile);
        vm.AlertsFile = "team-alerts.json";
        var built = BuildConfig(vm);
        Assert.Equal("shared/dests.json", built.DestinationsFile);
        Assert.Equal("team-alerts.json", built.AlertsFile);
        Assert.Equal("monitored-folders.json", built.MonitoredFoldersFile); // untouched default
    }

    [Fact]
    public void SettingsSaveNeverRewritesBoxLabels()
    {
        // Arrange a real temp config dir with a box-labels file holding a counter
        var dir = Directory.CreateTempSubdirectory("ordoset_").FullName;
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            Config.Save(new Config(), cfgPath);
            BoxLabelStore.Mutate(Path.Combine(dir, "box-labels.json"), d =>
                { d.LabelClients.Add(new LabelClient { Id = "ACME", NextNumber = 42 }); return 0; });

            var cfg = Config.Load(cfgPath);
            cfg.LabelClients = new();              // settings-era stale view
            Config.Save(cfg, cfgPath);             // what ApplySettings does

            Assert.Equal(42, BoxLabelStore.Read(Path.Combine(dir, "box-labels.json"))
                .LabelClients.Single().NextNumber);
        }
        finally { Directory.Delete(dir, true); }
    }
```

(Adjust `MakeVm`/`BuildConfig`/`LoadFromJson` to the helper names that actually exist in the file — it has this factory pattern already for `UnknownKeysAndToolStateSurviveOkByConstruction`.)

- [ ] **Step 4: Run the Wpf suite**

Run: `dotnet test tests/OrdoSort.Wpf.Tests -v minimal`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OrdoSort.Wpf tests/OrdoSort.Wpf.Tests
git commit -m "feat(settings): Data files section — configurable section-file paths

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 6: Demo generators emit the split layout

**Files:**
- Modify: `tools/OrdoSort.Smoke/DemoReset.cs` (the `config.json` write, ~line 36)
- Modify: `tools/OrdoSort.Smoke/DemoWorkbench.cs` (the `config.json` write, ~line 539)

**Interfaces:**
- Consumes: `Config.Save` (Task 2), doc types (Task 1).

- [ ] **Step 1: DemoReset.** Replace the single anonymous-object `File.WriteAllText(... "config.json" ...)` block with a main config + three docs (box-labels bootstrap comes free via `Config.Save`, but DemoReset builds raw JSON — switch it to typed `Config` + `Config.Save`, which produces the split automatically):

```csharp
        var cfg = new OrdoSort.Core.Config
        {
            Inbox = inbox.Replace('\\', '/'),
            Deferred = deferred.Replace('\\', '/'),
            HistoryDb = "history.sqlite",
            NamingMode = "insert",
            Sort = "size_desc",
            UppercaseNames = true,
            EnterCommits = true,
            MonitorTitle = "Needs attention",
            FlashAlerts = true,
            AlertTexts = { "URGENT" },
            WatchFolders =
            {
                new OrdoSort.Core.WatchFolder { Label = "Failed transfers",
                    Path = failed.Replace('\\', '/'), Recursive = false,
                    Filetypes = "pdf", Color = "#c0392b" },
            },
            Routes =
            {
                new OrdoSort.Core.Route { Label = "Invoices",
                    Path = invoices.Replace('\\', '/'), Hotkey = "Ctrl+1",
                    AppendSuffix = true, Suffix = "_INVOICE", Color = "#2e7d32" },
                new OrdoSort.Core.Route { Label = "Statements",
                    Path = statements.Replace('\\', '/'), Hotkey = "Ctrl+2",
                    AppendSuffix = false, Suffix = "", Color = "#1565c0" },
            },
        };
        OrdoSort.Core.Config.Save(cfg, Path.Combine(demo, "config.json"));
```

(Names file: the demo config reads `names.txt` via the default — the current anonymous object didn't set `names_file` either; the typed default `"names.txt"` preserves behavior. Keep the surrounding `Console.WriteLine` lines.)

- [ ] **Step 2: DemoWorkbench.** At ~line 539 it already serializes a real `Config` instance by hand — replace the `File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(cfg, …))` call with:

```csharp
        OrdoSort.Core.Config.Save(cfg, Path.Combine(root, "config.json"));
```

and remove the now-unused local `JsonSerializerOptions` if nothing else uses it. The workbench's own self-check (`Config.Load` + "config.json loads and validates") now exercises the split round-trip end to end.

- [ ] **Step 3: Run both generators and their checks**

Run: `dotnet run --project tools/OrdoSort.Smoke -- reset-demo`
Expected: exit 0; `demo/` now contains `config.json`, `destinations.json`, `monitored-folders.json`, `alerts.json`, `box-labels.json`; summary unchanged.
Run: `dotnet run --project tools/OrdoSort.Smoke -- demo-full`
Expected: "All checks passed", including the routes count check reading through the split files.

- [ ] **Step 4: Commit**

```bash
git add tools/OrdoSort.Smoke
git commit -m "feat(demo): generators emit the split config layout

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 7: Full gate and push

**Files:** none — verification and delivery.

- [ ] **Step 1: Clean build + full suites**

Run: `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal`
Expected: build clean; every test passes; total ≥ 557 + the new tests from Tasks 1–5 (record exact totals).

- [ ] **Step 2: Demo self-checks (Debug path)**

Run: `dotnet run --project tools/OrdoSort.Smoke -- demo-full`
Expected: "All checks passed".

- [ ] **Step 3: Launch sanity** — `dotnet build src/OrdoSort.Wpf/OrdoSort.Wpf.csproj && start src/OrdoSort.Wpf/bin/Debug/net8.0-windows/OrdoSort.exe --config demo-full/config.json` (PowerShell: `Start-Process`), confirm the app starts, routes and tiles appear (split files feeding them), then close it.

- [ ] **Step 4: Push**

```bash
git push origin main && git ls-remote origin main
```
Expected: fast-forward push; remote SHA equals `git rev-parse main`. No tags.

- [ ] **Step 5: Update the walkthrough ledger** — mark sub-project 1 delivered in the session notes (the controller handles the program-level tracking).
