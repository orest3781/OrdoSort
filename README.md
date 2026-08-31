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
- **Four naming modes** — *Insert at the `--`*: any filename containing `--`
  gets the typed name spliced at the first one (`REPORT--1042.pdf` + SMITH
  JOHN → `REPORT-SMITH JOHN-1042.pdf`); *Full replace*, which takes
  **every** PDF in the inbox (insert sessions only pick up `--` files); or
  *Prefix* and *Append*, which put the typed name before or after the
  existing filename. Per-route overrides, filename suffixes, and real
  config-driven hotkeys.
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
- **Tools** — nine utilities, each in its own window:
  - *Unlock PDFs* — the unlocked file keeps its name and place; the locked
    original moves to a dated `locked_archive` folder beside it. Saved
    passwords are stored as plain text in the shared `config.json` so they
    work from every station — the folder's own permissions are the security
    boundary.
  - *Bulk rename* — find/replace, affixes, case, a hand-editable preview, and
    batch undo.
  - *Match & merge* — pair PDFs against a roster (CSV or Excel) by name and
    merge each person's ID into the filename, with a side-by-side *Review
    matches* view for the ambiguous and suggested ones.
  - *Box labels* — ten 4×2" storage-box labels per letter sheet with cutting
    gutters: a big client+number code, a Code 39 barcode for hand scanners,
    created and destruction dates on black bars, per-client retention
    offsets, and a resettable running number. A live card previews the exact
    label, and a full-sheet print preview with printer picker prints in-app
    at a guaranteed 100% scale — PDF export remains as an alternative.
  - *Filename list* — drop files or folders and get their names as a list you
    curate: remove rows and they stay removed across a rebuild, add size,
    modified date, folder or full path as columns, filter by name, and copy
    or export exactly the columns you can see.
  - *PDF page counts* — page counts for a pile of PDFs or a whole folder,
    copied to the clipboard or saved as text; an unreadable file reports its
    error in place rather than stopping the run.
  - *List reformatter* — paste a spreadsheet column and get it back as a
    comma-delimited line, one item per line, or joined by any delimiter you
    like, with the empty rows closed up and counted — including the cells
    that only look empty because they hold a zero-width character.
  - *Zip and unzip* — files and folders into one archive, or each archive
    into its own folder beside it. A password-protected zip asks for its
    password instead of failing, after the passwords the app already knows
    have been tried; a skipped one stays runnable.
  - *Merge PDFs* — loose PDFs into one document named after their folder,
    and every PDF inside a zip into one document beside the zip. Word,
    Excel, PowerPoint, image and text files merge alongside them too —
    loose or inside a zip — converted to PDF on the way in, through Office
    itself when it's installed and an in-house converter when it isn't. A
    row of per-type toggles (PDF, Zip, Word, Excel, PowerPoint, Images,
    Text) chooses what merges, remembered between sessions. A locked PDF
    or archive asks for its password; one unopenable document merges
    nothing from its group rather than a document with pages quietly
    missing.

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

## The end-to-end suite

```
e2e.bat
```

(or `dotnet run --project tools\OrdoSort.Smoke -- e2e` — the bat wraps that
command, forwards any arguments, and opens the report when the run ends)
drives all 12 user-facing surfaces — every Tools-menu utility (the zip
window contributes three, one per job), History, and the routing loop — as real windows against real files
in a throwaway temp folder, then writes `evidence\<timestamp>\report.html`:
one row per scenario, with the assertions it ran and a screenshot of the
window.

Run a single surface with `e2e.bat zip` (or `unzip`, `zipmerge`, `unlock`, …
— the filter is exact-match-first, so `zip` selects Zip alone rather than
dragging in Zip merge), and add `--keep` to leave that run's fixtures on
disk for inspection. It drives real WPF windows and WebView2, so it needs a
desktop session — that's why it runs as its own CI job (`e2e.yml`) rather
than alongside the headless unit tests in `ci.yml`.

## Run the demo

Run `demo-full.bat` once to generate the workbench (300 inbox documents,
ten routes, three monitored folders), then launch with `run.bat`, or:

```
dotnet run --project src/OrdoSort.Wpf -- --config demo-full\config.json
```

## The workbench

`demo-full.bat` builds the thing you actually test against, under `demo-full\`:

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
| `merge\` | A roster and 23 PDFs engineered to hit every match status: 8 clean, **5 ambiguous** and **3 suggested** (these open Review matches), 3 already merged, 2 unmatched, 2 unnamed. |

The generator is deterministic — same seed, same workbench — and finishes by
running the app's own `Config`, `Scanner`, `MatchMerge` and `Unlock` logic
over what it just wrote, so the summary it prints is checked rather than
claimed. Everything lives under `demo-full\`, which is regenerated on each
run and never touches real documents.

## License

MIT — see [LICENSE](LICENSE). Third-party packages and their licenses are
listed in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
