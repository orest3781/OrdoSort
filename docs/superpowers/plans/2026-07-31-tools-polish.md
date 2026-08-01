# Tools Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Unlock window redesign (one box, auto-try saved, save-on-success banner, Manage saved dialog; Settings tab becomes "Data files"), Bulk-rename segment deletion, the box-label date-bar style toggle, and the four accepted Box-labels follow-ups.

**Architecture:** Core work first (segment-delete step in `BulkRename.TransformStem`; `date_style` on `BoxLabelsDoc` + a style parameter through the `BoxLabels` layout; HResult classification in `BoxLabelStore`), then the two window rewires (LabelMaker: merge-Persist, ceiling check, offloaded claim, style radios; Unlock: auto-try + banner + dialog), then Settings tab slimming, then gate+push.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main` (established: commits per task, push only in the final task). Suites baseline: Core 352 + Wpf 287 = 639.

## Global Constraints

- Segment rules, verbatim from spec: stem split on `-` WITHOUT removing empties (lossless round trip — `a--b` → `[a,"",b]`); positions 1-indexed plus `last`; short stems unaffected by out-of-range positions; `last` never removes a one-segment stem's only segment; rejoin with `-`; pipeline order review → segment delete → find/replace → affixes → case.
- `date_style`: `"bars" | "plain"`, top-level key in box-labels.json beside `label_clients`, default `"bars"`, unknown values read as `"bars"`, written only through `BoxLabelStore`.
- Unlock auto-try order: typed password first (if non-blank), then every saved password, per file; per-file outcomes unchanged. Banner only when the TYPED password unlocked ≥1 file AND is not already saved (compare by unprotected value); banner clears on next run and on window close. `saved_passwords` stays in config.json (DPAPI, per-machine).
- Persist merge: only clients the window actually touched (added/edited/removed, tracked by id) are applied to the fresh doc; untouched clients keep their on-disk state entirely; zero-edit close writes nothing.
- Contention = IOException with HResult 0x80070020 (ERROR_SHARING_VIOLATION) or 0x80070021 (ERROR_LOCK_VIOLATION) ONLY; everything else fails fast with its own message.
- Ceiling: inside the claim mutation, `start + count - 1 > BoxLabels.MaxNumber` refuses WITHOUT consuming numbers, message: `this batch would pass label 99 999 999 — reset or renumber the client`.
- Reviewer process rule (carry into every review dispatch): inspect history via `git show COMMIT:path`, never checkout over the tree.
- Sanctioned existing-test updates: Unlock VM tests touching `SelectedSaved`/remember-as, Settings tests touching the removed passwords section/tab name. Everything else grows by addition only.

---

### Task 1: Core — segment deletion in BulkRename

**Files:**
- Modify: `src/OrdoSort.Core/BulkRename.cs` (`RenameOp` record ~line 36, `TransformStem` ~line 66)
- Test: `tests/OrdoSort.Core.Tests/` — the file holding BulkRename tests (find: `grep -rln "TransformStem\|RenameOp" tests/`)

**Interfaces:**
- Produces: `RenameOp` gains `IReadOnlyCollection<int> DeleteSegments` (default empty; positive 1-indexed positions) and `bool DeleteLastSegment` (default false). `internal static string DeleteSegmentsFromStem(string stem, IReadOnlyCollection<int> positions, bool deleteLast)` — pure, unit-testable.

- [ ] **Step 1: Write the failing tests** (append to the BulkRename test file, matching its style):

```csharp
    [Theory]
    [InlineData("20240115-SCANRUN7-SMITH JOHN-12345", new[] { 2 }, false, "20240115-SMITH JOHN-12345")]
    [InlineData("a-b-c", new[] { 1, 3 }, false, "b")]
    [InlineData("a-b", new[] { 5 }, false, "a-b")]              // out of range: untouched
    [InlineData("a--b", new[] { 2 }, false, "a-b")]             // empty segment is a segment
    [InlineData("a--b", new int[0], false, "a--b")]             // nothing checked: lossless
    [InlineData("a-b-c", new int[0], true, "a-b")]              // last
    [InlineData("solo", new int[0], true, "solo")]              // last never empties a 1-segment stem
    [InlineData("a-b-c", new[] { 1 }, true, "b")]               // positions + last combine
    [InlineData("a-b", new[] { 1 }, true, "a-b")]               // everything would go: untouched
    public void SegmentDeletionFollowsTheRules(string stem, int[] positions, bool last, string expected) =>
        Assert.Equal(expected, BulkRename.DeleteSegmentsFromStem(stem, positions, last));

    [Fact]
    public void SegmentDeleteRunsAfterReviewRenameAndBeforeFindReplace()
    {
        var op = new BulkRename.RenameOp(
            Find: "SMITH", Replace: "X", Prefix: "", Suffix: "", Case: "keep",
            ReviewRename: false, ReviewDate: default,
            DeleteSegments: new[] { 1 }, DeleteLastSegment: false);
        // stem "JUNK-SMITH JOHN": delete seg 1 -> "SMITH JOHN", then find/replace -> "X JOHN"
        Assert.Equal("X JOHN", BulkRename.TransformStem("JUNK-SMITH JOHN", op));
    }
```

(Adapt the `RenameOp` construction to its REAL member list — read the record first; add the two new members preserving existing order/defaults so existing constructions compile unchanged. The pipeline-order assertion is the requirement.)

- [ ] **Step 2: red** — filter run fails to compile.
- [ ] **Step 3: Implement.**

```csharp
    /// <summary>Remove 1-indexed segments (stem split on '-', empties kept —
    /// "a--b" is three segments) plus optionally the last segment. Out-of-range
    /// positions are ignored; the last segment of a one-segment stem stays.</summary>
    internal static string DeleteSegmentsFromStem(
        string stem, IReadOnlyCollection<int> positions, bool deleteLast)
    {
        if (positions.Count == 0 && !deleteLast) return stem;
        var parts = stem.Split('-');
        if (parts.Length <= 1) return stem;
        var drop = new HashSet<int>(positions.Where(p => p >= 1 && p <= parts.Length));
        if (deleteLast) drop.Add(parts.Length);
        if (drop.Count >= parts.Length) return stem;   // deleting every segment is never meaningful
        var kept = parts.Where((_, i) => !drop.Contains(i + 1)).ToArray();
        return string.Join('-', kept);
    }
```

Add to `RenameOp` (keeping all existing members and defaults):
`IReadOnlyCollection<int>? DeleteSegments = null, bool DeleteLastSegment = false` — normalize null→empty inside `TransformStem`. In `TransformStem`, insert between the review-rename block and find/replace:

```csharp
        outp = DeleteSegmentsFromStem(outp, op.DeleteSegments ?? Array.Empty<int>(), op.DeleteLastSegment);
```

- [ ] **Step 4: green** — filter + full Core suite (352 + new).
- [ ] **Step 5: Commit** — `feat(core): position-based segment deletion in bulk rename` (+ session trailers as in every prior commit).

---

### Task 2: Core — date_style + HResult classification

**Files:**
- Modify: `src/OrdoSort.Core/ConfigDocs.cs` (`BoxLabelsDoc`), `src/OrdoSort.Core/BoxLabels.cs` (layout entry), `src/OrdoSort.Core/BoxLabelStore.cs`
- Test: `tests/OrdoSort.Core.Tests/BoxLabelStoreTests.cs` + the BoxLabels layout test file (find: `grep -rln "BoxLabels" tests/OrdoSort.Core.Tests/`)

**Interfaces:**
- Produces: `BoxLabelsDoc.DateStyle` (`[JsonPropertyName("date_style")]`, string, default `"bars"`); `BoxLabels.DateStyleBars = "bars"` / `DateStylePlain = "plain"` consts + `public static string NormalizeDateStyle(string?)` (unknown/null → `"bars"`); the label layout entry point gains a `string dateStyle` parameter (default `DateStyleBars`) — bars mode unchanged; plain mode: the two date bars are NOT emitted and their texts render black (`White = false`), same layout boxes. `internal static bool BoxLabelStore.IsContention(IOException)`.

- [ ] **Step 1: tests first.**

```csharp
    // BoxLabelStoreTests:
    [Fact]
    public void DateStyleRoundTripsAndDefaultsToBars()
    {
        var p = PathOf("box-labels.json");
        BoxLabelStore.Mutate(p, d => { d.DateStyle = "plain"; return 0; });
        Assert.Equal("plain", BoxLabelStore.Read(p).DateStyle);
        Assert.Contains("\"date_style\"", File.ReadAllText(p));
        Assert.Equal("bars", new BoxLabelsDoc().DateStyle);
        Assert.Equal("bars", BoxLabels.NormalizeDateStyle("neon"));
        Assert.Equal("plain", BoxLabels.NormalizeDateStyle("plain"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]   // sharing violation
    [InlineData(unchecked((int)0x80070021), true)]   // lock violation
    [InlineData(unchecked((int)0x80070070), false)]  // disk full
    [InlineData(unchecked((int)0x80070035), false)]  // bad network path
    public void ContentionClassificationIsHResultBased(int hresult, bool contention) =>
        Assert.Equal(contention, BoxLabelStore.IsContention(new IOException("x", hresult)));

    [Fact]
    public void NonContentionIOExceptionFailsFastWithItsOwnMessage()
    {
        // a directory where the box-labels PATH is itself an existing DIRECTORY
        // -> FileStream open throws a non-sharing IOException immediately
        var p = PathOf("box-labels.json");
        Directory.CreateDirectory(p);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigException>(() => BoxLabelStore.Mutate(p, d => 0));
        Assert.True(sw.ElapsedMilliseconds < 1000, "must not burn the retry budget");
        Assert.DoesNotContain("another station", ex.Message);
    }
```

For the layout: find the existing layout tests and add — bars mode: the date texts have `White == true` and date-bar rects exist; plain mode (same inputs + `dateStyle: BoxLabels.DateStylePlain`): those rects absent, those texts `White == false`, all other elements identical (compare counts). Read the layout output type (`Bars`/`Texts` collections seen in LabelPreview) to write exact assertions.

- [ ] **Step 2: red.**
- [ ] **Step 3: Implement.** `BoxLabelsDoc`: add `[JsonPropertyName("date_style")] public string DateStyle { get; set; } = "bars";` (+ `DateStyle ??= "bars"` wherever the store normalizes doc fields). `BoxLabels`: consts + `NormalizeDateStyle` + thread `dateStyle` (defaulted) into the layout, guarding ONLY the two date-bar emissions and their texts' `White` flag — identify them by reading the layout code (they render the created/destruction dates; the barcode bars are untouched). `BoxLabelStore`:

```csharp
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    internal static bool IsContention(IOException ex) =>
        ex.HResult is ErrorSharingViolation or ErrorLockViolation;
```

In BOTH `Read`'s and `Mutate`'s retry `catch (IOException) when (...)` filters, add `IsContention(ex)` to the condition (name the exception in the catch), and change each FINAL IOException catch: contention → the "another station…" ConfigException as today; non-contention → `throw new ConfigException($"box-labels file error: {ex.Message} ({fullPath})");` immediately (no retry).

- [ ] **Step 4: green** — BoxLabelStore filter + full Core.
- [ ] **Step 5: Commit** — `feat(core): label date_style + contention-only retry classification`.

---

### Task 3: LabelMaker — merge-Persist, ceiling, offloaded claim, style radios

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs`, `src/OrdoSort.Wpf/Windows/LabelMakerWindow.xaml`(+`.xaml.cs` if the Persist-on-close hook changes), `src/OrdoSort.Wpf/Views/LabelPreview.cs` if the style needs threading there
- Test: `tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2's `DateStyle`, `NormalizeDateStyle`, layout `dateStyle` param, `IsContention` behavior.
- Produces: `LabelMakerViewModel.DateStyleBars`/`DateStylePlain` bool radio pair (backed by one string, seeded from the store doc at open, persisted through the store); dirty-id tracking for merge-Persist.

- [ ] **Step 1: Merge-Persist.** Track touched clients: a `HashSet<string> _dirtyIds` — add the client's id on any VM edit (hook the existing property-change plumbing on `LabelClientVm`), on Add (new id once typed), and record removed ids in a `HashSet<string> _removedIds`. Rewrite `Persist()`:

```csharp
    internal void Persist()
    {
        if (_dirtyIds.Count == 0 && _removedIds.Count == 0) return;   // zero-edit close writes nothing
        try
        {
            BoxLabelStore.Mutate(_boxLabelsPath, doc =>
            {
                doc.LabelClients.RemoveAll(c => _removedIds.Contains(c.Id));
                foreach (var vm in Clients)
                {
                    if (!_dirtyIds.Contains(vm.Id)) continue;        // untouched: disk wins
                    var fresh = doc.LabelClients.FirstOrDefault(c => c.Id == vm.Id);
                    if (fresh is null) doc.LabelClients.Add(vm.ToClient());
                    else
                    {
                        var edited = vm.ToClient();
                        fresh.DestroyDays = edited.DestroyDays;
                        fresh.NextNumber = edited.NextNumber;
                        fresh.Extras = edited.Extras;
                    }
                }
                return 0;
            });
            _dirtyIds.Clear(); _removedIds.Clear();
        }
        catch (ConfigException ex) { _dialogs.Warn(ex.Message, "OrdoSort — label maker"); }
    }
```

Note: `ClaimNumbers` must NOT mark its client dirty (the store already holds the advanced number; marking dirty would re-write the on-screen value over a later station's advance). After a claim, the VM's NextNumberText update is display-only.

- [ ] **Step 2: Ceiling check.** Inside `ClaimNumbers`' mutation, before advancing: `if (s + count - 1 > BoxLabels.MaxNumber) throw new ConfigException("this batch would pass label 99 999 999 — reset or renumber the client");` — the exception aborts the Mutate write (callback throws before truncation — proven safe in the store's tests), so nothing is consumed; the existing catch → warn → null path handles it.
- [ ] **Step 3: Offload the Save-PDF claim.** In `SavePdfAsync`, move the `ClaimNumbers` call inside the existing `_scheduler.Run(...)` block that wraps the render (claim first inside it, abort the scheduled work on null, marshal the VM `NextNumberText` update back per the file's existing dispatcher conventions — follow how status updates already marshal).
- [ ] **Step 4: Style radios.** VM: string `_dateStyle` seeded `BoxLabels.NormalizeDateStyle(BoxLabelStore.Read(path).DateStyle)` at ctor (inside the existing try/catch); bool wrappers `DateStyleBars`/`DateStylePlain` (the two-radio pattern from the Filing tab, incl. `GroupName="DateStyle"` in XAML — the radio-group lesson is learned); changing style persists immediately via `BoxLabelStore.Mutate(p, d => { d.DateStyle = value; return 0; })` (style is config-ish, not counter-ish — immediate write is correct and cheap) and refreshes the live preview. Thread the style into every layout call (preview, print, PDF) — find them via the layout function's call sites.
- [ ] **Step 5: XAML.** `Date bars:` label + two radios (`black bars (white text)` / `plain (black text)`) in the window near the labels-count row, `GroupName="DateStyle"`.
- [ ] **Step 6: Tests** (LabelMakerViewModelTests, existing conventions):
  - Merge: seed store with clients A(7) B(10); open VM; edit A's days; externally advance B to 50 via the store; close-Persist; assert disk has A edited AND B still 50. Zero-edit close: externally advance then Persist → file byte-unchanged (or B still advanced + no write — assert via timestamps or by the store content).
  - Ceiling: client at MaxNumber-1, claim 3 → warn, null, store counter unchanged.
  - Style: seed doc `date_style: "plain"` → VM DateStylePlain true; flip to bars → store doc updated.
- [ ] **Step 7: green** — full Wpf suite; **Commit** — `feat(labels): merge-persist, ceiling guard, offloaded claim, date-bar style`.

---

### Task 4: Unlock redesign

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs`, `src/OrdoSort.Wpf/Windows/UnlockWindow.xaml`(+`.xaml.cs`)
- Create: `src/OrdoSort.Wpf/Windows/ManageSavedWindow.xaml`(+`.xaml.cs`)
- Test: `tests/OrdoSort.Wpf.Tests/` — the Unlock VM tests (find: `grep -rln "UnlockViewModel" tests/`)

**Interfaces:**
- Consumes: existing `PasswordVault.Protect/Unprotect`, `SavedPassword`, the VM's `Saved` collection + save-config callback (line ~299 area).
- Produces: `SaveBannerVisible`/`SaveBannerText`/`SaveBannerName`/`SaveBannerCommand` on the VM; `ManageSavedWindow` modal.

- [ ] **Step 1: Auto-try.** Read the unlock loop first. Change the per-file password candidates to: typed `Password` first (if non-blank), then each `Saved` entry's unprotected value, skipping duplicates of the typed value; stop at first success per file, outcomes unchanged. Remove `SelectedSaved` and its box-filling setter; remove the standing remember-as properties/row.
- [ ] **Step 2: Banner.** After a run: if the typed password unlocked ≥1 file AND no `Saved` entry unprotects to the same value → `SaveBannerVisible = true`, `SaveBannerText = $"✓ {n} unlocked with a new password — save it as:"`. `SaveBannerCommand` (needs non-blank `SaveBannerName`): reuse the existing save flow (~line 299: Protect + add to `_cfg.SavedPasswords` + `Saved` + the save-config callback), then hide the banner. Banner resets on the next run start and on window close.
- [ ] **Step 3: Window XAML.** Remove the `Saved:` picker row and remember-as row; add the banner (a themed Border, `Visibility` bound, name TextBox + Save button) between the password row and the buttons; add a `Manage saved…` button beside Close opening the new modal (`Owner = this`).
- [ ] **Step 4: ManageSavedWindow.** Move the Settings page's saved-passwords markup (name+password boxes, Add/Remove, list, DPAPI note — read the current Tools & data tab for the exact block) into the new window bound to the Unlock VM's `Saved` + the same add/remove handlers Settings used (relocate that logic onto UnlockViewModel or a small shared VM — mirror how Settings did it; persistence via the same save-config callback).
- [ ] **Step 5: Settings slimming.** Remove the saved-passwords section + Unlock explainer from the sixth tab; rename `Header="Tools &amp; data"` → `Header="Data files"`; remove the now-orphaned password add/remove members from SettingsViewModel (grep their usages — tests included). The Data files section stays exactly as is.
- [ ] **Step 6: Tests.** Port existing Unlock tests off `SelectedSaved`/remember-as to the new surface preserving intent; add: auto-try order (typed beats saved; saved works with blank box), banner appears only for a new working typed password, Save adds + hides, banner clears next run; Settings tests referencing the removed section updated. Every ported test listed in the report.
- [ ] **Step 7: green** — full Wpf + Core; the Smoke `dialogs` check must still construct every window (`dotnet run --project tools/OrdoSort.Smoke -- dialogs` exits 0 — ManageSavedWindow joins it if the check enumerates windows; read DialogCheck.cs and add it if so). **Commit** — `feat(unlock): one-box auto-try redesign with save-on-success and Manage saved dialog`.

---

### Task 5: Bulk rename UI — segment checkboxes

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs`, `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml`
- Test: the BulkRename VM test area in `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` (or wherever `BulkRenameViewModel` tests live — grep)

**Interfaces:**
- Consumes: Task 1's `RenameOp.DeleteSegments`/`DeleteLastSegment`.

- [ ] **Step 1: VM.** Five bools `DeleteSeg1..DeleteSeg4`, `DeleteSegLast`, each recomputing the preview on set (mirror how Find/Replace/Case properties trigger it); the op construction passes `DeleteSegments = [checked positions]`, `DeleteLastSegment = DeleteSegLast`.
- [ ] **Step 2: XAML.** In the transform grid, a new row: `Delete segment:` label + five CheckBoxes `1 2 3 4 last` horizontally, matching the row styles around it.
- [ ] **Step 3: Test.** VM test: files `A-B-C.pdf`; check seg 2 → preview `A-C.pdf`; check last too → `A.pdf`; uncheck all → original. Plus: hand-edited target still overrides.
- [ ] **Step 4: green** — full Wpf; **Commit** — `feat(bulk-rename): delete-segment position checkboxes`.

---

### Task 6: Full gate and push

- [ ] `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — record exact totals (expect Core ~356+, Wpf ~295+).
- [ ] `dotnet run --project tools/OrdoSort.Smoke -- demo-full` → "All checks passed" (its locked/ checks exercise saved-password unlocking — they must hold under auto-try).
- [ ] `dotnet run --project tools/OrdoSort.Smoke -- dialogs` → exit 0.
- [ ] Launch sanity (Start-Process with demo-full config, window check, clean stop).
- [ ] `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, no tags.
