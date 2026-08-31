# Merging documents, spreadsheets, slides, images and text with PDFs — Design

**Status:** approved 2026-08-30. Scope expanded the same day (file types, and per-type toggles) — see "Amendment" at the end for what changed and why.

**Goal:** the Merge PDFs window accepts documents, spreadsheets, slide decks, images, text files and CSVs beside PDFs and zips, converts each to PDF pages, and merges them into the same output — loose in the list and inside archives — with a row of toggles saying which types take part.

**Reported by the owner:** "is there a way to add an option to also handle excel, csv, docx files to also be able to merge with the pdfs", then "i want there to be a toggle for all file types that should merge", then a request to cover every type worth covering.

---

## The crux

None of these formats *are* PDFs. Something has to render each to pages first, and that is the whole of the design's difficulty. Nothing in the app does it today.

What the repo already has, and this design builds on:

| Existing | Where | Used for |
|---|---|---|
| `Csv.ReadTable(path)` → `List<List<string>>`, dispatching `.xlsx` to `XlsxTable.Read` and anything else to a delimited-text parse with BOM/UTF-8/Latin-1 detection | `Csv.cs`, `XlsxTable.cs` | roster loading |
| PDF drawing from scratch — `XGraphics.FromPdfPage`, `DrawString`, `XFont`, explicit page sizing | `BoxLabels.cs` (`RenderPdf`) | box labels |
| The compose/draw seam: `ComposeDrawing` returns a pure `LabelDrawing`, `RenderPdf` draws it | `BoxLabels.cs` | testability |
| Password contract: `Passwords.Resolve(candidates, ask, item, inside, tryWith)`, unit-scoped candidates | `Passwords.cs`, `ZipListViewModel` | zips and PDFs |
| Probe-on-add, marking rows before a run | `ZipListViewModel` | archives needing a password |
| Fail-whole per unit; statuses `ok`/`no_pdfs`/`needs_password`/`error` | `PdfMerge.cs` | merging |
| `filetypes` as a stored comma list (`"pdf"`, `"pdf,tif"`) | `Config.cs` (monitored folders) | the precedent for persisting the toggles |
| WPF's own image decoders — `BitmapDecoder`, multi-frame TIFF included | framework, already referenced | nothing yet |

Word has nothing to build on. Excel and CSV have readers, but only for *values*. Images have decoders sitting unused in a framework the app already references.

## Installed on the owner's machine (measured 2026-08-30)

`Word.Application.16`, `Excel.Application.16`, `PowerPoint.Application.16`, `Outlook.Application.16`, `Publisher.Application.16`, `Access.Application.16`. Visio is **not** registered. LibreOffice is **not** installed.

## Decisions taken (owner, 2026-08-30)

1. **Office first, in-repo fallback.** Word, Excel and PowerPoint do the conversion where Office is installed. Where it is absent, CSV/TSV/XLSX still convert in-repo as data tables, text and images still convert in-repo, and the Office-only formats report that they cannot be converted.
2. **Loose files and inside zips.** Both units accept the new types.
3. **Protected documents ask, like zips and PDFs do** — saved passwords tried automatically, prompt for the rest, same window and same skip behaviour. *Office path only; see decision 8.*
4. **Spreadsheets: all sheets, columns fit to page width.** This is the *Office* behaviour. The in-repo fallback reaches the first worksheet only — that is all `XlsxTable.Read` returns — so on a PC without Excel a multi-sheet workbook converts its first sheet and the row's note says so. The two paths differ here, deliberately and visibly.
5. **Convert during the merge**, not at intake (rejected alternatives at the end).
6. **Late-bound COM**, not the typed Office interop packages.
7. **Per-type toggles in the window, remembered.** A row of checkboxes in the Merge PDFs window itself, persisted to config so the choice survives a restart.
8. **A switched-off type is listed but excluded**, greyed with a "not included" note, and joins in the moment its type is switched back on — no re-dropping. Inside a zip, entries of an excluded type are skipped and counted as skipped.

## The types v1 handles

Toggles are per **group**, not per extension — nobody wants `.jpg` and `.png` on separate switches.

| Group | Extensions | Converted by | Without Office |
|---|---|---|---|
| PDF | `pdf` | — (native) | native |
| Zip | `zip` | — (container) | native |
| Word | `docx`, `doc`, `docm`, `rtf`, `odt` | Word | **not available** |
| Excel | `xlsx`, `xls`, `xlsm`, `ods`, `csv`, `tsv` | Excel | `csv`/`tsv`/`xlsx` as data tables |
| PowerPoint | `pptx`, `ppt` | PowerPoint | **not available** |
| Images | `jpg`, `jpeg`, `png`, `tif`, `tiff`, `bmp`, `gif` | WPF decoders + PdfSharp | **same** — no Office involved |
| Text | `txt`, `log`, `md`, `json` | in-repo paginator | **same** — no Office involved |

Images and Text never touch Office at all: no hang risk, no orphaned processes, no fidelity gap between the two paths.

### Image page sizing

One image per page. The page is sized from the image's own DPI when that yields a **sane physical size — every side between 1 and 30 inches** — which is exactly the case for a scan (1700×2200 at 200 DPI is a letter page). Otherwise, when the DPI is meaningless (a phone photo reporting 72), the image is scaled to fit US Letter preserving aspect ratio, portrait or landscape chosen by which fits better. This makes scans come out at their true size and photos come out sensible, without a setting.

## Non-goals

- **`.msg` / `.eml`** — an email as pages is worth doing, but it belongs with the Outlook attachment-downloader the owner also asked about: same COM channel, same workflow, one design conversation.
- `.pub` (Publisher), Access reports, `.vsd` (Visio is not installed).
- A standalone "convert to PDF" action that writes a PDF beside the source without merging.
- Any change to the Zip and unzip window, PDF page counts, or Triage.
- Fidelity guarantees. No automated test can assert a document "looks right"; that is eyes-on acceptance.

---

## Architecture

`OrdoSort.Core` targets `net8.0` (platform-neutral); `OrdoSort.Wpf` targets `net8.0-windows`. COM and the WPF image decoders cannot live in Core without dragging a platform target through the pure-logic library. The split follows that line.

### The contract (Core)

```csharp
public interface IDocumentConverter
{
    /// <param name="extension">dot-less, lowercase, as Intake produces.</param>
    bool Handles(string extension);

    ConversionResult ToPdf(byte[] source, string displayName,
                           IReadOnlyList<string> candidates,
                           Func<PasswordRequest, string?>? ask);
}

public sealed record ConversionResult(string Status, byte[]? Pdf,
                                      string Message = "", string? Item = null);
// "ok" | "needs_password" | "unsupported" | "error"
```

`unsupported` is a converter-internal signal meaning "not mine" — what lets a chain fall through to the next implementation. It is never a user-facing outcome: when *nothing* handles a type, the merge reports `error` naming why.

**Bytes in, bytes out, deliberately.** `PdfMerge` already buffers every source in memory, and its ZipSlip immunity rests on the rule that *a zip entry's own name never reaches a filesystem API*. A byte-oriented contract keeps that rule intact even though Office can only open a real file: the Office adapter writes a temp file under a **generated** name, converts, reads the result back, deletes. The archive never chooses a path.

The status vocabulary is deliberately the one `MergeResult` already speaks, so conversion failures flow through the existing fail-whole and colour rules without adding a concept to the UI grammar.

### Four implementations

| Class | Layer | Handles | Notes |
|---|---|---|---|
| `TableToPdf` | Core | `csv`, `tsv`, `xlsx` | Existing readers; first worksheet only for xlsx; **never prompts** (decision 8 below) |
| `TextToPdf` | Core | `txt`, `log`, `md`, `json` | The same paginator, one column |
| `ImageToPdf` | Wpf | `jpg`…`gif` | `BitmapDecoder`, multi-frame TIFF → one page per frame |
| `OfficeConverter` | Wpf | Word, Excel, PowerPoint groups | Late-bound COM |

Both Core converters share one pure paginator (`TablePages.Paginate`), split from drawing along the seam `BoxLabels` already uses: pagination is arithmetic — checkable with a calculator — and the PdfSharp shell only draws what it returns.

**The fallback never prompts.** Only the Office path participates in the password contract. `TableToPdf` has no decryptor, so a password could not be used even if typed; a protected file there reports `error` naming the reason. Prompting for a password that cannot help is worse than saying so. *(Refinement of decision 3.)*

### Composition

`ConverterChain` asks the first link that `Handles` the extension and **returns its result, whatever it is** — only when a link does not handle the type at all does it try the next. So Office is preferred where present, and **an Office failure never silently downgrades** to a lesser in-repo rendering of a document the user believes converted properly.

Order: `OfficeConverter` → `ImageToPdf` → `TableToPdf` → `TextToPdf`.

### The toggles

**Intake stays permissive; the toggles act on inclusion, not acceptance.** Every mergeable type is always accepted into the list; the toggles decide what actually merges. That is what makes "switch it back on and they join in" work, and it is why exclusion cannot be a row *status* — a status is the result of a run, and this must change the instant a checkbox flips.

- A row gains a computed `IsIncluded`, driven by the live toggle set, feeding the greying, the "not included" note, `IsRunnable`, and the button's count. Flipping a toggle re-raises it on every row.
- `PdfMerge` takes the enabled set too, because a zip's **entries** must be filtered identically: a Word file inside an archive is skipped when Word is off, and counts toward `SkippedEntries` so "an empty zip" is still distinguishable from "a zip filtered down to nothing".
- Persisted as a comma list on the existing `filetypes` precedent, e.g. `"pdf,zip,word,excel,images,text"`. Group names, not extensions, so adding an extension to a group later does not need a config migration.
- PDF and Zip get toggles too, for uniformity. Turning everything off simply disables the Merge button, which the existing runnable-count logic already handles.
- The old "that isn't a type this window merges" refusal stays for genuinely foreign types (`.exe`, `.mp4`); the toggles only govern types the window *can* merge.

---

## Data flow

A non-PDF is converted to PDF bytes and then goes through **exactly the same `AddPdf` call a real PDF does**, so page import, the "output is always plain and unencrypted" rule, and fail-whole all apply unchanged.

- **Loose group:** natural sort as today; anything not a PDF is read and handed to the converter first; anything excluded by the toggles is not selected into the unit at all.
- **Inside a zip:** the entry filter widens from `.pdf` to "PDF, or something the converter handles **and** the toggles allow". Entries are still read into memory by `Zipper.ReadEntry`, still never by name.

Ordering is unchanged — mixed types take their place in the same sort.

**One wording consequence.** A zip holding only a `.docx` reports "had no PDFs" today. The internal status key stays `no_pdfs` (it is wired into XAML triggers and the tally clauses); the user-facing clause becomes **"had nothing to merge"**, which is now the true statement.

### Passwords

Three layers can require one: the zip, a PDF inside it or loose, and a protected Office document. All go through `Passwords.Resolve` with the same candidate list and prompt, including the unit-scoped behaviour already shipped — a password typed for one document is tried automatically on the next.

**The hang, and the specific thing that prevents it.** `Documents.Open`, `Workbooks.Open` and `Presentations.Open` each take a password. Pass nothing for a protected file and Office raises a modal dialog on a hidden window: the COM call never returns and the run is wedged. The adapter therefore **always passes a password** — a deliberate garbage sentinel when no candidate is available — so Office throws a catchable error instead of prompting. That error becomes `needs_password`, which drives the app's own prompt. Alongside it: `Visible = false`, `DisplayAlerts = false`, `AutomationSecurity = ForceDisable` so a macro-bearing document cannot raise its own dialog either.

### Office hazards handled by construction

- **Orphaned processes.** Every `Application` is quit and released in a `finally`, with a kill-by-PID safety net. **One instance per run, not per file** — cold start dominates the cost.
- **Temp files.** Generated names under `%TEMP%`, deleted deterministically in a `finally`. This matters more here than in most applications: these are clients' documents, and this repo has a PHI history. A converted temp file left behind is exactly the residue that caused trouble before.
- **Sheet layout** (decision 4): per worksheet `Zoom = false`, `FitToPagesWide = 1`, `FitToPagesTall = false`, then one `ExportAsFixedFormat` for the whole workbook.

### What the user sees

The row note reads "converting…" while it runs, then the usual result. Cancel takes effect **between** files — a COM call in flight cannot be interrupted — and quits Office on the way out.

**Unconvertible files are caught at drop time.** The probe that already runs when a row is added performs the check: on a PC without Word, a dropped `.docx` is marked immediately with the reason, the same way an archive is marked `NeedsPassword` today.

---

## Error handling

| Outcome | Status | Row | Unit |
|---|---|---|---|
| Converted | `ok` | normal | pages merged |
| Type switched off | — | greyed, "not included" | not selected into the unit |
| Protected, prompt skipped | `needs_password` | amber, still runnable, names the document | fail-whole, names the culprit |
| No converter for this type on this PC | `error` | red, names the reason ("Word isn't installed") | fail-whole |
| Corrupt, or conversion threw | `error` | red, names the document | fail-whole |

Fail-whole is unchanged and deliberate: a merged file that quietly dropped a document looks identical to a complete one until somebody notices it is missing.

---

## Testing

**Tier 1 — pure, hermetic (Core).** `Paginate` gets exhaustive facts with no PDF involved: quoted fields with embedded commas and newlines, a sheet wider than the page, an empty file, thousands of rows spilling across pages, non-UTF8 encodings, ragged rows. The drawing shells get "valid PDF, expected page count" checks.

**Tier 2 — merge rules with a fake converter (Core).** Fail-whole on conversion failure, `needs_password` threading, mixed-type ordering, the widened zip-entry filter, and the toggle filter. Injected, deterministic, no Office. Most new facts live here.

**Tier 3 — images (Wpf, hermetic).** Images need no Office, so these are ordinary tests: a generated PNG becomes one page; a **multi-frame TIFF becomes one page per frame**; a 200-DPI letter-sized scan comes out letter-sized; a 72-DPI photo is fitted rather than producing an absurd page; a corrupt file is an error, not a throw.

**Tier 4 — the Office adapter, which cannot be hermetic.** Skips when the apps are not registered, and covers: a real `.docx` and `.pptx` convert to documents with pages; a **protected** file returns `needs_password` **under a hard timeout**, so a regression fails the test rather than wedging the suite the way this repo's known `HeaderLayoutTests` stall already does; and no `WINWORD.EXE`/`EXCEL.EXE`/`POWERPNT.EXE` survives the run, asserted by comparing process IDs before and after.

**Tier 5 — the toggles (Wpf).** A switched-off type's rows are excluded and not counted by the button; switching it back on includes them **without re-adding**; the choice survives a round trip through config.

**E2E.** Scenarios on the existing `zipmerge` surface — a converted document among loose files, an image, and a type switched off — using the deterministic in-repo converters so they stay reproducible.

**Not testable, stated plainly:** whether the output *looks* right. Fidelity is eyes-on acceptance on the owner's own documents.

---

## Task 1 is a spike, before anything is built

The design rests on two unverified assumptions:

1. late-bound COM conversion works from this app's process (Word, Excel **and PowerPoint**);
2. the garbage-password sentinel makes Office fail fast instead of showing its dialog.

Task 1 converts one real `.docx`, one protected `.docx` and one `.pptx`, measures cold-start cost, and confirms no process is left behind. If the sentinel does not behave, the design changes **before** anything is built on it. The spike's code is throwaway; its output is an answer.

## Rollback

Purely additive. Every group is a toggle, so any type that misbehaves can be switched off by the user; narrowing the accepted set back to `{pdf, zip}` disables the feature entirely in one line.

## Approaches not taken

- **Convert at intake** rather than during the merge. `PdfMerge` would never learn non-PDF types exist. Rejected because zip-internal documents still need converting during the merge, so both paths end up existing anyway — and because it does work for files the user may then remove.
- **Typed Office interop packages.** Compile-checked calls, at the price of new dependencies and coupling to an Office generation. The one-version rule in the project standards argues against it.
- **LibreOffice headless.** One free converter for every Office format, but a ~400MB prerequisite on every PC, and it is not installed here.
- **In-repo only.** No external dependency, fully testable — but a Word document reduced to plain text loses tables, images and layout, which makes it useless in a filing packet.
- **Exclusion as a row status.** Rejected: a status is the outcome of a run, and exclusion has to change the instant a checkbox flips, with no run involved.

---

## Amendment (2026-08-30, after the first approval)

The original design covered `.docx`, `.xlsx` and `.csv` with no toggles. Two owner requests expanded it, and both changed the shape rather than just the list:

1. **"I want there to be a toggle for all file types that should merge."** This is why intake became permissive and exclusion became a live computed property rather than a status — see "The toggles". It also pushed the enabled set down into `PdfMerge`, because archive entries have to be filtered by the same rule.
2. **"What other files would be able to be converted and merged?"** — answered by measuring what is installed, then adding four groups: images, text, the Word/Excel sibling formats, and PowerPoint. Images are the notable one: they need no Office at all (WPF ships the decoders), they carry none of the COM hazards, and for an app that files scanned documents they are likely the most-used addition of the lot.

`.msg`/`.eml` were deliberately deferred to the Outlook conversation rather than dropped.

### Amendment (2026-08-30 review — `.htm`/`.html` dropped from the Word group)

This document originally listed `htm`, `html` under the Word group's extensions (the table above and the "types v1 handles" table both said so). The implementation never actually shipped them: `MergeTypes.ByGroup`'s Word entry is `["docx", "doc", "docm", "rtf", "odt"]`, and `MergeTypesTests` pins `htm`/`html` as extensions with no group at all. This document is now corrected to match, rather than left claiming a wider Word group than the code has ever had.

The reason, carried in the code's own comment (`DocumentConverter.cs`): opening a web document in Word fetches remote resources — both a hang surface (nothing in this class's password-sentinel or timeout machinery guards a network fetch the way it guards a modal dialog) and a beaconing surface, in a repo with a PHI history where an unexpected outbound request is exactly the kind of thing worth refusing by construction rather than trusting a setting to catch. `AutomationSecurity = ForceDisable` covers macros; it does not cover a `<link>` or an `<img src>` in an HTML file Word is asked to render. `rtf`, `odt` and `docm` stay in the Word group — a `docm`'s macros are exactly what AutomationSecurity is for.
