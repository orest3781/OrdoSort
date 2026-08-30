# Merging Word, Excel and CSV files with PDFs — Design

**Status:** approved 2026-08-30. Supersedes nothing.

**Goal:** the Merge PDFs window accepts `.docx`, `.xlsx` and `.csv` alongside PDFs and zips, converts them to PDF pages, and merges them into the same output — loose in the list, and inside archives.

**Reported by the owner:** "is there a way to add an option to also handle excel, csv, docx files to also be able to merge with the pdfs".

---

## The crux

None of these formats *are* PDFs. Something has to render them to pages first, and that is the whole of the design's difficulty. Nothing in the app does it today.

What the repo already has, and this design builds on:

| Existing | Where | Used for |
|---|---|---|
| `Csv.ReadTable(path)` → `List<List<string>>`, dispatching `.xlsx` to `XlsxTable.Read` and anything else to a delimited-text parse with BOM/UTF-8/Latin-1 detection | `src/OrdoSort.Core/Csv.cs`, `XlsxTable.cs` | roster loading |
| PDF drawing from scratch — `XGraphics.FromPdfPage`, `DrawString`, `XFont`, explicit page sizing | `src/OrdoSort.Core/BoxLabels.cs` (`RenderPdf`) | box labels |
| The compose/draw seam: `ComposeDrawing` returns a pure `LabelDrawing`, `RenderPdf` draws it | `BoxLabels.cs` | testability |
| Password contract: `Passwords.Resolve(candidates, ask, item, inside, tryWith)`, `PasswordRequest`, unit-scoped candidates | `Passwords.cs`, `ZipListViewModel` | zips and PDFs |
| Probe-on-add, marking rows before a run | `ZipListViewModel` | archives needing a password |
| Fail-whole per unit, status vocabulary `ok`/`no_pdfs`/`needs_password`/`error` | `PdfMerge.cs` | merging |

Word has nothing to build on; Excel and CSV have readers but only for *values*.

## Decisions taken (owner, 2026-08-30)

1. **Office first, in-repo fallback.** Word and Excel do the conversion when Office is installed (it is, on this machine: Office 16, `Word.Application.16` / `Excel.Application.16` registered; LibreOffice is not present). Where Office is absent, CSV and XLSX still convert in-repo as clean data tables and `.docx` reports that it cannot be converted.
2. **Loose files and inside zips.** Both units accept the new types.
3. **Protected documents ask, like zips and PDFs do** — saved passwords tried automatically, prompt for the rest, same window and same skip behaviour.
4. **Spreadsheets: all sheets, columns fit to page width.** Nothing silently dropped; a wide sheet stays readable rather than being sliced across continuation pages. This is the *Office* behaviour. The in-repo fallback can only reach the first worksheet — that is all `XlsxTable.Read` returns — so on a PC without Excel a multi-sheet workbook converts its first sheet and the row's note says so. The two paths differ here, deliberately and visibly.
5. **Structure: convert during the merge** (rejected alternatives in "Approaches not taken").
6. **Late-bound COM**, not the typed Office interop packages.

## Non-goals

- `.doc`, `.xls`, `.rtf`, `.txt` — the old binary formats only work on the Office path, and the repo already directs people to `.xlsx`/`.csv` elsewhere (`MatchMerge.cs`'s roster error). Easy to add later; deliberately out of v1.
- A standalone "convert to PDF" action that writes a PDF beside the source without merging.
- Any change to the Zip and unzip window, PDF page counts, or Triage.
- Fidelity guarantees. No automated test can assert a document "looks right"; that is eyes-on acceptance.

---

## Architecture

`OrdoSort.Core` targets `net8.0` (platform-neutral); `OrdoSort.Wpf` targets `net8.0-windows`. COM therefore cannot live in Core without dragging a platform target through the pure-logic library. The split follows that line.

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
// Status: "ok" | "needs_password" | "unsupported" | "error"
```

`unsupported` is a converter-internal signal meaning "not mine", not a user-facing outcome: it is what lets the chain fall through from one implementation to the next. If **no** implementation handles a type, the merge reports `error` with a message naming why (see Error handling). An implementer must not map `unsupported` straight onto a `MergeResult` status.

**Bytes in, bytes out, deliberately.** `PdfMerge` already buffers every source in memory (its own doc comment accepts this and names `Unlock.LargeFileThresholdBytes` as the precedent for changing it), and its ZipSlip immunity rests on the rule that *a zip entry's own name never reaches a filesystem API*. A byte-oriented contract keeps that rule intact even though Office can only open a real file: the Office adapter writes a temp file under a **generated** name, converts, reads the result back, and deletes it. The archive never chooses a path.

The status vocabulary is deliberately the one `MergeResult` already speaks, so conversion failures flow through the existing fail-whole and colour rules without adding a concept to the UI grammar.

### The two implementations

**`TableToPdf` (Core).** CSV and XLSX through the existing readers, drawn with PdfSharp. Split along the seam `BoxLabels` already establishes:

- a **pure** `Paginate(table, pageSize, font) → IReadOnlyList<TablePage>` — no PdfSharp types, exhaustively testable;
- a thin drawing shell that renders what it returns.

Refuses `docx` with `unsupported`. Reads only the first worksheet of a workbook (that is what `XlsxTable` provides) — a limitation recorded in the row's own note, not hidden.

**`OfficeConverter` (Wpf).** Late-bound COM via `Type.GetTypeFromProgID`: no new NuGet package, nothing required at build time, and no version coupling to an Office generation. `Handles` returns false when the ProgIDs are not registered.

### Composition

`MergePdfsViewModel` builds the chain: **Office when available, the Core renderer only when it is not.** If Office is present and fails on a particular file, that failure stands — no silent downgrade to a lesser rendering of a document the user believes converted properly.

### Intake

`MergePdfsViewModel.Extensions` grows from `{pdf, zip}` to `{pdf, zip, docx, xlsx, csv}`; `KindOf` gains the corresponding kinds for the Kind column. `Intake.Expand`/`Add` need no change — they already take the extension set.

---

## Data flow

A non-PDF is converted to PDF bytes and then goes through **exactly the same `AddPdf` call a real PDF does**, so page import, the "output is always plain and unencrypted" rule, and fail-whole all apply unchanged.

- **Loose group** (`MergeFiles`): natural sort as today; anything that is not a PDF is read and handed to the converter first.
- **Inside a zip** (`MergeZipCore`): the entry filter widens from `.pdf` to the convertible set. Entries are still read into memory by `Zipper.ReadEntry`, still never by name.

Ordering is unchanged — mixed types take their place in the same sort.

**One wording consequence.** A zip holding only a `.docx` reports "had no PDFs" today. The internal status key stays `no_pdfs` (it is wired into XAML triggers and the tally clauses); the user-facing clause becomes **"had nothing to merge"**, which is now the true statement.

### Passwords

Three layers can now require one: the zip, a PDF inside it or loose, and a protected Office document. All three go through `Passwords.Resolve` with the same candidate list and the same prompt, including the unit-scoped behaviour already shipped — a password typed for one document is tried automatically on the next.

**The hang, and the specific thing that prevents it.** `Documents.Open` and `Workbooks.Open` each take a password argument. Pass nothing for a protected file and Office raises a modal dialog on a hidden window: the COM call never returns and the run is wedged. The adapter therefore **always passes a password** — a deliberate garbage sentinel when no candidate is available — so Office throws a catchable error instead of prompting. That error becomes `needs_password`, which drives the app's own prompt. Alongside it: `Visible = false`, `DisplayAlerts = false`, `AutomationSecurity = ForceDisable` so a macro-bearing document cannot raise its own dialog either.

### Office hazards the design handles by construction

- **Orphaned processes.** Every `Application` is quit and released in a `finally`, with a kill-by-PID safety net. **One instance per run, not per file** — cold start dominates the cost, so per-file would make a ten-file merge crawl.
- **Temp files.** Generated names under `%TEMP%`, deleted deterministically in a `finally`. This matters more here than in most applications: these are clients' documents, and this repo has a PHI history. A converted temp file left behind is exactly the residue that caused trouble before.
- **Sheet layout** (decision 4): per worksheet `Zoom = false`, `FitToPagesWide = 1`, `FitToPagesTall = false`, then one `ExportAsFixedFormat` for the whole workbook. A CSV rides the same path as a one-sheet workbook.

### What the user sees

The row note reads "converting…" while it runs, then the usual result. Cancel takes effect **between** files — a COM call in flight cannot be interrupted — and quits Office on the way out.

**Unconvertible files are caught at drop time, not after a long run.** The probe that already runs when a row is added performs the check: on a PC without Word, a dropped `.docx` is marked immediately with the reason, the same way an archive is marked `NeedsPassword` today.

---

## Error handling

| Outcome | Status | Row | Unit |
|---|---|---|---|
| Converted | `ok` | normal | pages merged |
| Protected, prompt skipped | `needs_password` | amber, still runnable, names the document | fail-whole, names the culprit |
| No converter for this type on this PC | `error` | red, names the reason (e.g. "Word isn't installed") | fail-whole |
| Corrupt, or conversion threw | `error` | red, names the document | fail-whole |

Fail-whole is unchanged and deliberate: a merged file that quietly dropped a document looks identical to a complete one until somebody notices it is missing.

---

## Testing

**Tier 1 — pure, hermetic (Core).** `Paginate` gets exhaustive facts with no PDF involved: quoted fields with embedded commas and newlines, a sheet wider than the page, an empty file, thousands of rows spilling across pages, non-UTF8 encodings. The drawing shell gets "valid PDF, expected page count" checks.

**Tier 2 — merge rules with a fake converter (Core).** Fail-whole on conversion failure, `needs_password` threading through all three layers, mixed-type ordering, the widened zip-entry filter. Injected, deterministic, no Office. Most new facts live here.

**Tier 3 — the Office adapter, which cannot be hermetic.** Tests skip when Word and Excel are not registered, and cover exactly three things:

1. a real `.docx` converts to a document with pages;
2. a **protected** file returns `needs_password` **under a hard timeout**, so a regression fails the test rather than wedging the suite the way this repo's known `HeaderLayoutTests` stall already does;
3. no `WINWORD.EXE`/`EXCEL.EXE` survives the run, asserted by comparing process IDs before and after.

**E2E.** Scenarios on the existing `zipmerge` surface — a converted document among loose files, and a protected one driving the prompt — using the deterministic Core converter so they stay reproducible.

**Not testable, stated plainly:** whether the output *looks* right. Fidelity is eyes-on acceptance on the owner's own documents.

---

## Task 1 is a spike, before anything is built

The design rests on two unverified assumptions:

1. late-bound COM conversion works from this app's process;
2. the garbage-password sentinel makes Office fail fast instead of showing its dialog.

Task 1 converts one real `.docx` and one protected `.docx`, measures cold-start cost, and confirms no process is left behind. If the sentinel does not behave, the design changes **before** anything is built on it. The spike's code is throwaway; its output is an answer.

## Rollback

Purely additive. Narrowing `Extensions` back to `{pdf, zip}` disables the feature in one line.

## Approaches not taken

- **Convert at intake** rather than during the merge. `PdfMerge` would never learn non-PDF types exist and would stay exactly as reviewed. Rejected because zip-internal documents still need converting during the merge, so both paths end up existing anyway — and because it does work for files the user may then remove.
- **Typed Office interop packages.** Compile-checked calls, at the price of two new dependencies and coupling to an Office generation. The one-version rule in the project standards argues against it; late binding costs a little `dynamic` code and buys independence.
- **LibreOffice headless.** One free converter for all three formats with good fidelity, but a ~400MB prerequisite on every PC, and it is not installed here.
- **In-repo only.** No external dependency, fully testable — but a Word document reduced to plain text loses tables, images and layout, which makes it useless in a filing packet.
