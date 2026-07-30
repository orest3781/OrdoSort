# OrdoSort Rebrand & Repo Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the app as OrdoSort at `S:\OrdoSort` — full clean rebrand of the FileRouter/Sendu/Paper Trail codebase — verified by build + both test suites, then pushed to `https://github.com/orest3781/OrdoSort`.

**Architecture:** Mechanical in-place rebrand of a working .NET 8 WPF app: copy the extracted archive to the repo root, rename files/directories, run an ordered token-replacement sweep, then hand-fix the spots a sweep can't get right (assembly name, pack URIs, exe references, stale prose). Code logic never changes.

**Tech Stack:** C# / .NET 8, WPF, xUnit, Git Bash (commands below use Git Bash syntax from `S:/OrdoSort`), GitHub Actions.

## Global Constraints

- Repo root is `S:\OrdoSort`. Git is already initialized on `main` with two commits (the design spec). Never rewrite those commits.
- Source of truth (read-only input): `S:\OrdoSort\sendu-1.4.0\sendu-1.4.0\`. It is moved to `S:\tmp\sendu-1.4.0` only in Task 7, after verification.
- **Clean-history rule:** no code is committed until Task 7. Tasks 1–6 work entirely on untracked files. (The only committed files before Task 7 are the spec and this plan under `docs/superpowers/`.)
- **Do not copy from the archive:** `README.md` (rewritten in Task 5), `docs/` (old Paper Trail branding + Pages site + historical specs), `plans/` (historical design doc).
- **Naming (exact):** solution `OrdoSort.sln`; projects `OrdoSort.Core`, `OrdoSort.Wpf`, `OrdoSort.Core.Tests`, `OrdoSort.Wpf.Tests`, `OrdoSort.Smoke`; WPF app `<AssemblyName>OrdoSort</AssemblyName>` (binary `OrdoSort.exe`) with `<RootNamespace>OrdoSort.Wpf</RootNamespace>`; pack URIs use `pack://application:,,,/OrdoSort;component/...`; sound assets `ordosort-alert.wav`, `ordosort-send.wav`, `ordosort-aside.wav`.
- **Replacement map (apply in this order — longer tokens first):**
  1. `FileRouterNet` → `OrdoSort`
  2. `FileRouter` → `OrdoSort`
  3. `filerouter` → `ordosort`
  4. `PaperTrail` → `OrdoSort`
  5. `Papertrail` → `OrdoSort`
  6. `papertrail` → `ordosort`
  7. `Paper Trail` → `OrdoSort`
  8. `paper trail` → `OrdoSort`
  9. `Sendu` → `OrdoSort`
  10. `sendu` → `ordosort`
- Verification greps exclude: `.git/`, `docs/` (the spec/plan legitimately mention old names), `sendu-1.4.0/`, `bin/`, `obj/`, `demo-full/`, `publish/`, `out/`, and binary files (`grep -I`).
- No version tag is pushed. `v1.0.0` is tagged later when the first release is cut.

---

### Task 1: Copy the source tree to the repo root (baseline green)

**Files:**
- Create (copies): `src/`, `tests/`, `tools/`, `demo/`, `.github/`, `FileRouterNet.sln`, `.gitignore`, `.gitattributes`, `run.bat`, `reset.bat`, `publish.bat`, `demo-full.bat` — all at `S:/OrdoSort/`

**Interfaces:**
- Consumes: the archive at `sendu-1.4.0/sendu-1.4.0/`
- Produces: a buildable FileRouter-named tree at the repo root; baseline test counts for later comparison

- [ ] **Step 1: Copy the tree (excluding README.md, docs/, plans/)**

```bash
cd /s/OrdoSort
cp -r sendu-1.4.0/sendu-1.4.0/src sendu-1.4.0/sendu-1.4.0/tests \
      sendu-1.4.0/sendu-1.4.0/tools sendu-1.4.0/sendu-1.4.0/demo \
      sendu-1.4.0/sendu-1.4.0/.github .
cp sendu-1.4.0/sendu-1.4.0/FileRouterNet.sln \
   sendu-1.4.0/sendu-1.4.0/.gitignore \
   sendu-1.4.0/sendu-1.4.0/.gitattributes \
   sendu-1.4.0/sendu-1.4.0/run.bat \
   sendu-1.4.0/sendu-1.4.0/reset.bat \
   sendu-1.4.0/sendu-1.4.0/publish.bat \
   sendu-1.4.0/sendu-1.4.0/demo-full.bat .
```

- [ ] **Step 2: Verify the copy landed**

Run: `ls -A /s/OrdoSort`
Expected: `FileRouterNet.sln`, `src`, `tests`, `tools`, `demo`, `run.bat`, `reset.bat`, `publish.bat`, `demo-full.bat`, `.github`, `.gitignore`, `.gitattributes` — plus the pre-existing `.git`, `docs`, `sendu-1.4.0`, `ordosort-logo-concept.jpg`. No `README.md`, no `plans/`.

- [ ] **Step 3: Baseline build**

Run: `cd /s/OrdoSort && dotnet build FileRouterNet.sln`
Expected: `Build succeeded.` (0 errors; warnings acceptable — note any for comparison later)

- [ ] **Step 4: Baseline tests — record the counts**

Run: `dotnet test FileRouterNet.sln --verbosity minimal`
Expected: all tests pass in both suites (`FileRouter.Core.Tests`, `FileRouter.Wpf.Tests`). **Write down the passed-test totals** — Task 6 must reproduce exactly these numbers.

- [ ] **Step 5: No commit** — per the clean-history rule, nothing is staged or committed in this task.

---

### Task 2: Rename directories and files

**Files:**
- Rename: solution, five project folders + their `.csproj` files, three sound `.wav` assets

**Interfaces:**
- Consumes: the copied tree from Task 1
- Produces: the on-disk names Task 3's content sweep expects (`OrdoSort.sln`, `src/OrdoSort.Core/OrdoSort.Core.csproj`, `src/OrdoSort.Wpf/OrdoSort.Wpf.csproj`, `tests/OrdoSort.Core.Tests/OrdoSort.Core.Tests.csproj`, `tests/OrdoSort.Wpf.Tests/OrdoSort.Wpf.Tests.csproj`, `tools/OrdoSort.Smoke/OrdoSort.Smoke.csproj`, `src/OrdoSort.Wpf/Assets/sounds/ordosort-{alert,send,aside}.wav`)

- [ ] **Step 1: Rename everything (inner files first, then their folders)**

```bash
cd /s/OrdoSort
mv FileRouterNet.sln OrdoSort.sln

mv src/FileRouter.Core/FileRouter.Core.csproj src/FileRouter.Core/OrdoSort.Core.csproj
mv src/FileRouter.Core src/OrdoSort.Core

mv src/FileRouter.Wpf/Assets/sounds/papertrail-alert.wav src/FileRouter.Wpf/Assets/sounds/ordosort-alert.wav
mv src/FileRouter.Wpf/Assets/sounds/papertrail-send.wav  src/FileRouter.Wpf/Assets/sounds/ordosort-send.wav
mv src/FileRouter.Wpf/Assets/sounds/papertrail-aside.wav src/FileRouter.Wpf/Assets/sounds/ordosort-aside.wav
mv src/FileRouter.Wpf/FileRouter.Wpf.csproj src/FileRouter.Wpf/OrdoSort.Wpf.csproj
mv src/FileRouter.Wpf src/OrdoSort.Wpf

mv tests/FileRouter.Core.Tests/FileRouter.Core.Tests.csproj tests/FileRouter.Core.Tests/OrdoSort.Core.Tests.csproj
mv tests/FileRouter.Core.Tests tests/OrdoSort.Core.Tests

mv tests/FileRouter.Wpf.Tests/FileRouter.Wpf.Tests.csproj tests/FileRouter.Wpf.Tests/OrdoSort.Wpf.Tests.csproj
mv tests/FileRouter.Wpf.Tests tests/OrdoSort.Wpf.Tests

mv tools/FileRouter.Smoke/FileRouter.Smoke.csproj tools/FileRouter.Smoke/OrdoSort.Smoke.csproj
mv tools/FileRouter.Smoke tools/OrdoSort.Smoke
```

- [ ] **Step 2: Verify no old-named files or folders remain**

Run: `find . -iname "*filerouter*" -o -iname "*papertrail*" | grep -v "^./sendu-1.4.0" | grep -v "^./docs" | grep -v /bin/ | grep -v /obj/`
Expected: no output. (Build output under `bin/`/`obj/` still carries old names — harmless; it is regenerated and gitignored.)

Note: the build is **expected to be broken** between this task and the end of Task 3 (project references still point at old paths). Do not run `dotnet build` here.

---

### Task 3: Content replacement sweep

**Files:**
- Modify: every text file in the tree (`.cs`, `.xaml`, `.csproj`, `.sln`, `.yml`, `.bat`, `.md`, `.json`, `.txt`, `.gitignore`, `.gitattributes`) outside the excluded paths

**Interfaces:**
- Consumes: the renamed tree from Task 2
- Produces: a tree whose namespaces are `OrdoSort.Core` / `OrdoSort.Wpf`, whose project references resolve, and where `SoundService` sound names are `ordosort-alert` / `ordosort-send` / `ordosort-aside` (matching Task 2's renamed `.wav` files)

- [ ] **Step 1: Run the ordered sweep**

```bash
cd /s/OrdoSort
find . -type f \( -name "*.cs" -o -name "*.xaml" -o -name "*.csproj" -o -name "*.sln" \
    -o -name "*.yml" -o -name "*.bat" -o -name "*.md" -o -name "*.json" -o -name "*.txt" \
    -o -name ".gitignore" -o -name ".gitattributes" \) \
  -not -path "./.git/*" -not -path "./sendu-1.4.0/*" -not -path "./docs/*" \
  -not -path "*/bin/*" -not -path "*/obj/*" -not -path "./demo-full/*" \
  -not -path "./publish/*" -not -path "./out/*" \
  -print0 | xargs -0 sed -i \
  -e 's/FileRouterNet/OrdoSort/g' \
  -e 's/FileRouter/OrdoSort/g' \
  -e 's/filerouter/ordosort/g' \
  -e 's/PaperTrail/OrdoSort/g' \
  -e 's/Papertrail/OrdoSort/g' \
  -e 's/papertrail/ordosort/g' \
  -e 's/Paper Trail/OrdoSort/g' \
  -e 's/paper trail/OrdoSort/g' \
  -e 's/Sendu/OrdoSort/g' \
  -e 's/sendu/ordosort/g'
```

(sed edits bytes in place: UTF-8 BOMs, CRLF line endings, and em-dashes all survive untouched because every replacement token is plain ASCII within a single line.)

- [ ] **Step 2: Build and test — green again**

Run: `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln --verbosity minimal`
Expected: build succeeds; both suites pass with **exactly the Task 1 Step 4 totals**. At this point the assembly is still named `OrdoSort.Wpf` and the pack URIs say `OrdoSort.Wpf;component` — consistent with each other, so `SoundAssetTests` passes. Task 4 changes both together.

- [ ] **Step 3: Spot-check the sweep did what it should**

Run: `grep -rn "namespace OrdoSort" --include="*.cs" src | head -3 && grep -n "ordosort-alert" src/OrdoSort.Wpf/Services/SoundService.cs`
Expected: `namespace OrdoSort.Core;` / `namespace OrdoSort.Wpf...` hits, and `SoundEvent.NewAlert => "ordosort-alert",`.

---

### Task 4: Assembly name `OrdoSort`, pack URIs, exe references

**Files:**
- Modify: `src/OrdoSort.Wpf/OrdoSort.Wpf.csproj`, all `.cs`/`.xaml` files containing `;component`, `run.bat`, `publish.bat`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`

**Interfaces:**
- Consumes: the swept tree from Task 3 (assembly currently `OrdoSort.Wpf`)
- Produces: binary named `OrdoSort.exe`; pack URIs `pack://application:,,,/OrdoSort;component/...`; scripts and workflows referencing `OrdoSort.exe` / `ordosort-*.zip`

- [ ] **Step 1: Set the assembly name**

In `src/OrdoSort.Wpf/OrdoSort.Wpf.csproj` replace:

```xml
    <AssemblyName>OrdoSort.Wpf</AssemblyName>
```

with:

```xml
    <AssemblyName>OrdoSort</AssemblyName>
```

(`<RootNamespace>OrdoSort.Wpf</RootNamespace>` stays. The `InternalsVisibleTo` entries `OrdoSort.Wpf.Tests` and `OrdoSort.Smoke` stay — those are the *other* projects' assembly names, which default to their csproj file names and are unaffected by this change.)

- [ ] **Step 2: Fix the seven pack URIs to the new assembly name**

```bash
cd /s/OrdoSort
grep -rl "OrdoSort\.Wpf;component" --include="*.cs" --include="*.xaml" src \
  | xargs sed -i 's/OrdoSort\.Wpf;component/OrdoSort;component/g'
```

Run: `grep -rn ";component" --include="*.cs" --include="*.xaml" src | grep -v "/OrdoSort;component"`
Expected: no output (every pack URI now uses `/OrdoSort;component/`).

- [ ] **Step 3: run.bat — exe path and stale comment**

Replace this line:

```bat
set "EXE=%~dp0src\OrdoSort.Wpf\bin\Debug\net8.0-windows\OrdoSort.Wpf.exe"
```

with:

```bat
set "EXE=%~dp0src\OrdoSort.Wpf\bin\Debug\net8.0-windows\OrdoSort.exe"
```

and delete these two now-stale comment lines (post-sweep text shown):

```bat
rem The exe keeps its OrdoSort.Wpf name for compatibility - only the
rem product is called OrdoSort.
```

- [ ] **Step 4: publish.bat — exe name in comment and echoes**

Replace (post-sweep text shown):

```bat
rem Build the portable single-file exe locally: publish\OrdoSort.Wpf.exe
```
→
```bat
rem Build the portable single-file exe locally: publish\OrdoSort.exe
```

```bat
echo Portable exe: %~dp0publish\OrdoSort.Wpf.exe
```
→
```bat
echo Portable exe: %~dp0publish\OrdoSort.exe
```

```bat
echo or pass one:  OrdoSort.Wpf.exe --config C:\path\config.json
```
→
```bat
echo or pass one:  OrdoSort.exe --config C:\path\config.json
```

- [ ] **Step 5: ci.yml — artifact name and path**

Replace (post-sweep text shown):

```yaml
      - uses: actions/upload-artifact@v7
        with:
          name: OrdoSort-portable-win-x64
          path: out/portable/OrdoSort.Wpf.exe
```

with:

```yaml
      - uses: actions/upload-artifact@v7
        with:
          name: ordosort-portable-win-x64
          path: out/portable/OrdoSort.exe
```

- [ ] **Step 6: release.yml — zip step exe paths and comment**

Replace the zip step's comment + copies (post-sweep text shown):

```yaml
      # zips download cleaner than bare exes (browsers and SmartScreen are
      # kinder to archives), and they carry a proper inner filename
      # the product is OrdoSort; the assembly keeps its OrdoSort.Wpf name
      # compatibility, so the zip carries a friendly ordosort.exe inside
      - name: Zip the assets
        shell: pwsh
        run: |
          Copy-Item out/portable/OrdoSort.Wpf.exe ordosort.exe
          Compress-Archive -Path ordosort.exe -DestinationPath "ordosort-${{ steps.ver.outputs.tag }}-win-x64.zip"
          Remove-Item ordosort.exe
          Copy-Item out/selfcontained/OrdoSort.Wpf.exe ordosort.exe
          Compress-Archive -Path ordosort.exe -DestinationPath "ordosort-${{ steps.ver.outputs.tag }}-win-x64-selfcontained.zip"
          Remove-Item ordosort.exe
```

with:

```yaml
      # zips download cleaner than bare exes (browsers and SmartScreen are
      # kinder to archives), and they carry a proper inner filename
      - name: Zip the assets
        shell: pwsh
        run: |
          Copy-Item out/portable/OrdoSort.exe ordosort.exe
          Compress-Archive -Path ordosort.exe -DestinationPath "ordosort-${{ steps.ver.outputs.tag }}-win-x64.zip"
          Remove-Item ordosort.exe
          Copy-Item out/selfcontained/OrdoSort.exe ordosort.exe
          Compress-Archive -Path ordosort.exe -DestinationPath "ordosort-${{ steps.ver.outputs.tag }}-win-x64-selfcontained.zip"
          Remove-Item ordosort.exe
```

(The release title `OrdoSort ${{ steps.ver.outputs.tag }}`, the zip filenames, and the manual-run artifact `ordosort-win-x64` / `ordosort-*.zip` were already fixed by the Task 3 sweep — verify, don't re-edit.)

- [ ] **Step 7: Build, check the exe name, run tests**

Run: `dotnet build OrdoSort.sln && ls src/OrdoSort.Wpf/bin/Debug/net8.0-windows/OrdoSort.exe && dotnet test OrdoSort.sln --verbosity minimal`
Expected: build succeeds, `OrdoSort.exe` exists, both suites pass with the Task 1 totals — `SoundAssetTests` passing proves the pack URIs resolve under the renamed assembly.

---

### Task 5: README, logo, .gitignore cleanup

**Files:**
- Create: `README.md`, `docs/logo-concept.jpg` (moved from repo root)
- Modify: `.gitignore`

**Interfaces:**
- Consumes: the rebranded tree from Task 4
- Produces: the complete public face of the repo; after this task the grep sweep must come back clean

- [ ] **Step 1: Move the logo into docs/**

```bash
cd /s/OrdoSort
mv ordosort-logo-concept.jpg docs/logo-concept.jpg
```

- [ ] **Step 2: Write README.md with exactly this content**

````markdown
<p align="center">
  <img src="docs/logo-concept.jpg" alt="OrdoSort — every document, where it belongs" width="480">
</p>

# OrdoSort

**Every document, where it belongs.** OrdoSort watches an inbox folder. Each
arriving PDF opens in a built-in viewer; you type the name it should carry,
press one of your destination buttons (or its hotkey), and the file is renamed
and moved. A dashboard shows what's waiting, monitored folders light up when
they need attention, and every move is written to an audit log that is backed
up daily — so you can always answer *where did that go, and when?*

Built with C# / .NET 8 + WPF. PDFs render in **WebView2** (Edge's engine,
already on the machine), so no PDF library ships with the app.

## Download

Portable builds are attached to every [release](../../releases) (and every CI
run uploads one under the run's Artifacts):

- **`ordosort-vX-win-x64.zip`** (~3 MB) — a single exe; needs the .NET 8
  Desktop Runtime, which modern Windows 10/11 machines already have (Windows
  offers the download link if it's missing).
- **`…-selfcontained.zip`** (~70 MB) — carries the runtime; nothing to
  install.

Unzip anywhere and run — the app reads (or creates on first run) a
`config.json` beside the exe, or takes `--config <path>`. Locally,
`publish.bat` builds the same portable exe into `publish\`.

To cut a release: `git tag v1.0.0 && git push origin v1.0.0` — the Release
workflow tests, builds, zips, and publishes.

## Design goals

- **Small.** No bundled browser, no bundled PDF renderer, no MVVM framework —
  the app's own code is a few hundred KB.
- **Network-safe.** The audit database uses a rollback journal (never WAL,
  which corrupts over SMB) with a `busy_timeout`, so several workstations can
  file into one `history.sqlite` on a share. A poll (default 15s, configurable
  5–600) backstops folder watching where SMB drops change notifications.
- **Never loses a file.** Files are only ever *moved*, never deleted or
  overwritten; a taken name gets a Windows-style ` (2)` counter. Illegal
  filename characters are rejected up front — a colon would otherwise hide a
  document in an NTFS alternate data stream.
- **Looks after the eyes.** Follows Windows light/dark mode live (or force
  either); every text color pairing in the theme is enforced to WCAG AA 4.5:1
  **by a unit test**; app font and text size are configurable.

## Features

- **The routing loop** — Ready → Processing → Done. Live inbox monitoring
  (new arrivals join a running session), a live "will be filed as" preview
  that flags illegal names before you commit, name autocomplete ranked by
  recency then frequency (Tab completes a word at a time), uppercase and
  word-separator polishing, and a color-coded confirmation card after every
  routing. Commit, set-aside, and undo are reentrancy-guarded — a fast
  double-press can never mislabel a document.
- **Two naming modes** — *Insert at the `--`*: any filename containing `--`
  gets the typed name spliced at the first one (`REPORT--1042.pdf` + SMITH
  JOHN → `REPORT-SMITH JOHN-1042.pdf`); or *Full replace*, which takes
  **every** PDF in the inbox (insert sessions only pick up `--` files).
  Per-route overrides, filename suffixes, and real config-driven hotkeys.
- **Dashboard** — a compact window parked in the corner of your screen that
  sizes itself to its content: a big inbox count, plus a grid of monitored
  folder tiles. A header dropdown picks their visibility: *Active only*
  (tiles appear while a folder holds matching files), *All* (every tile
  stays, even at zero), or *Hidden* (no tiles — and the folder sweep is
  skipped entirely). Filename alert terms flash a tile (and the count) red,
  and alerts found in subfolders say which one.
- **Alerts that reach you** — a new alerting file chimes, slides a toast into
  the corner (click to open the folder), badges the taskbar, and flashes the
  taskbar button when the app isn't focused — once per newly-arrived file,
  never on a repeat scan. Sounds are the built-in synthesized *OrdoSort* set,
  a Windows chime, any `.wav` you point to, or silent — per moment (new
  alert, filed, set aside, error). The set-aside banner ages ("oldest 4
  days").
- **Viewer gestures** — Shift+scroll zooms at the cursor, left-drag pans.
- **History** — a network-safe SQLite audit log with daily point-in-time
  backups, an in-app viewer (filter, lazy load), and CSV export with a
  formula-injection guard.
- **Settings** — six sectioned pages with live previews everywhere: route
  buttons render exactly as they'll appear, dashboard tiles preview against
  the real folder, naming choices show a worked example, and validation
  happens as you type. Unknown hand-edited config keys always survive.
- **Tools** — *Unlock PDFs* (the unlocked file keeps its name and place, the
  locked original moves to a dated `locked_archive` folder beside it; saved
  passwords DPAPI-encrypted per Windows account), *Bulk rename*
  (find/replace, affixes, case, hand-editable preview, batch undo),
  *Match & merge* (pair PDFs against a roster (CSV or Excel) by name and
  merge each person's ID into the filename, with a side-by-side Review
  matches view for ambiguous and suggested matches), and *Box labels*
  (storage-box labels, ten 4×2" labels per letter sheet with cutting
  gutters — big client+number code, a Code 39 barcode for hand scanners,
  created and destruction dates on black bars,
  per-client retention offsets, and a resettable running number; a live
  card previews the exact label, a full-sheet print preview with printer
  picker prints in-app at guaranteed 100% scale, and PDF export remains as
  an alternative).

## Structure

```
src/OrdoSort.Core/       pure logic — no UI, unit-tested
  Naming.cs              filename construction + reserved-char guard
  BulkRename.cs          batch rename + the review-file name parser
  MatchMerge.cs          roster CSV matching + ID merge
  Config.cs  Scanner.cs  Commit.cs  Session.cs
  History.cs             network-safe SQLite audit log
src/OrdoSort.Wpf/        the app: MVVM view models (headless-tested) + XAML
  Theme/                 WCAG-enforced light/dark palette, live OS switching
  ViewModels/ Views/ Windows/
tests/OrdoSort.Core.Tests/   xUnit — the routing rules, adversarially
tests/OrdoSort.Wpf.Tests/    xUnit — the whole app logic, headless
tools/OrdoSort.Smoke/        UI proofs against the real WebView2 viewer
```

## Build & test

```
dotnet build
dotnet test
```

## Run the demo

Run `reset.bat` once to generate a demo workspace (sample PDFs, two routes,
a monitored folder), then launch with `run.bat`, or:

```
dotnet run --project src/OrdoSort.Wpf -- --config demo\config.json
```

## The workbench

`reset.bat` is a five-document sketch. `demo-full.bat` builds the thing you
actually test against, under `demo-full\`:

```
demo-full.bat            300 inbox documents
demo-full.bat 2000       a deeper inbox (~4s, ~9 MB)

dotnet run --project src/OrdoSort.Wpf -- --config demo-full\config.json
```

Ten destinations on Ctrl+1..Ctrl+0 (three with suffixes, one overriding the
naming mode), an inbox salted with unfilable files and alert terms, a
set-aside folder backdated to 94 days, three monitored folders with one alert
down a subfolder — and a folder per tool holding the cases that are awkward
on purpose:

| Folder | What it is for |
|---|---|
| `locked\` | Password-protected PDFs: twelve whose passwords are pre-saved in the config, six whose passwords you deliberately do **not** have, and two that aren't encrypted at all. |
| `rename\` | Review-stem names to rebuild, a junk token to find/replace, mixed case, and a pair that collides once renamed. |
| `merge\` | A roster and 22 PDFs engineered to hit every match status: 8 clean, **5 ambiguous** and **3 suggested** (these open Review matches), 2 already merged, 2 unmatched, 2 unnamed. |

The generator is deterministic — same seed, same workbench — and finishes by
running the app's own `Config`, `Scanner`, `MatchMerge` and `Unlock` logic
over what it just wrote, so the summary it prints is checked rather than
claimed. Everything lives under `demo-full\`, which is regenerated on each
run and never touches real documents.
````

- [ ] **Step 3: .gitignore — drop the Python-parity stanza**

Delete these two lines (post-sweep text shown; the referenced folder never
existed in this repo):

```
# The original Python app, kept beside the repo for parity reference
.OrdoSort_python/
```

- [ ] **Step 4: Grep sweep — must be clean**

Run:

```bash
cd /s/OrdoSort
grep -rniIE "filerouter|papertrail|paper ?trail|sendu" . \
  --exclude-dir=.git --exclude-dir=docs --exclude-dir=bin --exclude-dir=obj \
  --exclude-dir=demo-full --exclude-dir=publish --exclude-dir=out \
  --exclude-dir=sendu-1.4.0 && echo "DIRTY" || echo "CLEAN"
```

Expected: `CLEAN`. If `DIRTY`, fix every listed occurrence (rename residue) and re-run.

---

### Task 6: Full verification gate

**Files:** none modified — verification only.

**Interfaces:**
- Consumes: the finished tree from Task 5
- Produces: the green light Task 7 requires before committing

- [ ] **Step 1: Release build**

Run: `cd /s/OrdoSort && dotnet build OrdoSort.sln -c Release`
Expected: `Build succeeded.`

- [ ] **Step 2: Release tests — compare against baseline**

Run: `dotnet test OrdoSort.sln -c Release --verbosity minimal`
Expected: both suites pass with **exactly the totals recorded in Task 1 Step 4**. Any difference = investigate before proceeding.

- [ ] **Step 3: Demo generation exercises the app's own logic end-to-end**

Run: `dotnet run --project tools/OrdoSort.Smoke -- reset-demo`
Expected: exits 0, prints its checked summary, and `demo/config.json` + `demo/inbox/` exist afterward. (Invoke the Smoke tool directly, not `reset.bat` — the bat's `pause` can hang a non-interactive shell.)

- [ ] **Step 4: Re-run the grep sweep from Task 5 Step 4**

Expected: `CLEAN` (the demo generator must not have written any old-name content).

---

### Task 7: Archive out, single clean commit, push

**Files:**
- Move: `sendu-1.4.0/` → `S:\tmp\sendu-1.4.0`
- Commit: the entire rebranded tree
- Push: `main` → `https://github.com/orest3781/OrdoSort`

**Interfaces:**
- Consumes: the verified tree from Task 6
- Produces: the published OrdoSort repository

- [ ] **Step 1: Move the archive out of the repo**

```bash
mkdir -p /s/tmp && mv /s/OrdoSort/sendu-1.4.0 /s/tmp/sendu-1.4.0
```

Run: `ls /s/tmp/sendu-1.4.0 && ls /s/OrdoSort`
Expected: archive present under `/s/tmp`; gone from the repo root.

- [ ] **Step 2: Stage and review — the status must match this checklist**

Run: `cd /s/OrdoSort && git add -A && git status --short`
Expected staged set: `OrdoSort.sln`, `README.md`, `.gitignore`, `.gitattributes`, the four `.bat` files, `.github/`, `src/OrdoSort.Core/`, `src/OrdoSort.Wpf/`, `tests/OrdoSort.Core.Tests/`, `tests/OrdoSort.Wpf.Tests/`, `tools/OrdoSort.Smoke/`, `demo/names.txt`, `docs/logo-concept.jpg`.
Must **NOT** appear: anything under `sendu-1.4.0/`, `bin/`, `obj/`, `demo/config.json`, `demo/inbox/`, `demo-full/`, `publish/`. If any appear, unstage and fix `.gitignore` before continuing.

- [ ] **Step 3: Commit**

```bash
git commit -m "OrdoSort — initial import

.NET 8 WPF document router: inbox routing loop, dashboard, alerts,
network-safe SQLite audit history, and PDF tools (unlock, bulk rename,
match & merge, box labels). Core logic and app tested by two xUnit suites.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

- [ ] **Step 4: Add the remote and push**

```bash
git remote add origin https://github.com/orest3781/OrdoSort.git
git push -u origin main
```

Expected: push accepted (the repo is empty — no force needed, and never use force).

- [ ] **Step 5: Verify the push landed**

Run: `git ls-remote origin main`
Expected: one ref line whose SHA equals `git rev-parse main`. Do **not** push any tag — `v1.0.0` comes with the first release later.
