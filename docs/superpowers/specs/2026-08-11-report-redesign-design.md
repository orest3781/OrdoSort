# Report redesign: summary-first Turn-around and Production windows

Date: 2026-08-11
Status: approved design, ready for planning

## Problem

The Turn-around Time and Production windows load real data correctly but answer
no question. Loading the live sample set produces grids of tens of thousands of
rows, a one-line status bar, and nothing else. The person who needs these
numbers reports them upward monthly in three forms over time: read off the
screen, sent as a spreadsheet, and shown as a screenshot. A raw grid serves none
of them.

Four problems, each made concrete by the live data:

1. **No insight.** The reported metric — share of documents turned around in
   0–1 / 2 / 3+ days — is computed by hand in Excel, outside the app.
2. **Overwhelming grids.** Production shows over a thousand groups because
   `SOURCE-FOLDER` carries per-batch paths; there are only five real categories.
3. **Dated presentation.** Neither window matches the app's visual standard, and
   neither is presentable as a screenshot.
4. **Real-data friction.** Duplicate documents, two incompatible date formats,
   two incompatible filename conventions, empty files, retries, and an entire
   category missing from one log family all silently distort the numbers today.

## The data

Two independent log families, produced by two systems that were never designed
to be used together. This design joins them anyway, carefully, and reports what
it could not join.

**Upload logs** — `UPLOAD LOGS/<YYYYMMDD>/<YYYYMMDD-HHMM> PECF Report.xlsx`.
143 files in the sample, 6–7 per working day, 23,552 rows, covering
2026-07-01 to 2026-08-10. Sixteen columns; the ones that matter are `FileName`,
`Controlid`, and `SourceType`. A document's upload time is the timestamp of the
**earliest report that lists it**. Multiple reports per day give this
hour-level resolution.

**File move logs** — `FILE MOVE LOGS/<CATEGORY>/<YYYYMMDD>-<CATEGORY>-MOVE-LOG.csv`.
1,730 files across CD, EMAILS-APPEALS, EMAILS-MR, FAX and PORTAL, 163,282 rows,
covering 2025-08-28 to 2026-08-11. `DATE-TIME` is when the document was moved to
the upload staging area, i.e. when work on it finished.

The `old.logs` and `old.production` folders are superseded and are not a source.

### Three timestamps, two legs

Each document carries three times, not two. Measured across 17,878 joined
documents in the overlapping window:

| Leg | Meaning | Median | p90 |
|---|---|---|---|
| Scan date → move | document waits after scanning | 1 day (45.4% same-day) | 1 day |
| Move → upload | the upload itself | 1.71 hours | 4.46 hours |
| Scan date → upload | end-to-end | 1 day | 3 days |

The scan date is the `YYYYMMDD` prefix of the document's own filename. The
end-to-end leg is the metric already reported to leadership; computed from this
data it gives **0–1: 89.5%, 2: 0.2%, 3+: 10.3%**.

The decisive finding: **the upload leg is fast and the delay accumulates before
it.** Reporting only the upload leg would show a median of 1.7 hours and hide
the 10.3% of documents taking three or more days end-to-end.

### The join

Documents are matched between the two families on exact filename,
case-insensitively. In the overlapping window this matches 17,878 of 19,709
move-log documents (**90.7%**) with **zero negative gaps** — every matched
upload occurs after its move, which is strong evidence the join is semantically
sound rather than coincidental.

The 9.3% unmatched are not evenly spread, and the design must say so rather than
quietly shrink the denominator:

| Move-log category | In window | Unmatched |
|---|---|---|
| EMAILS-APPEALS | 7,471 | 4.5% |
| EMAILS-MR | 5,737 | 4.8% |
| CD | 2,288 | 13.9% |
| FAX | 3,994 | 17.1% |
| PORTAL | 219 | **100%** |

## Decisions

### 1. Report both legs, end-to-end as the headline

The Summary tab leads with the end-to-end metric in the existing 0–1 / 2 / 3+
day buckets — continuity with what leadership already receives. Beneath it, the
same population is split into its two legs (scan→move in days, move→upload in
hours) so a slow document shows *where* it waited. This is what makes the 10.3%
actionable instead of merely visible.

The upload leg uses hour buckets: ≤1h / ≤4h / ≤24h / >24h, which on live data
gives 19.1% / 66.4% / 13.1% / 1.4%.

No holiday calendar, and no business-day adjustment, in v1. An earlier draft
adopted business days; the newer data made it moot, because at this resolution
the weekend question is subsumed by the leg split.

### 2. Turn-around Summary tab: hero number with side matrix

A new tab, opened by default. Left: the end-to-end hero percentage (share in
0–1 days), its month-over-month delta, and the 2 / 3+ / excluded counts. Right:
the per-category matrix and a weekly sparkline. Below: the two-leg breakdown.
The existing Documents / Daily / Weekly / By category tabs remain as drill-down.

### 3. Production Summary tab: category cards with staff table

A new tab, opened by default. A strip of five category cards (documents per
category), then a staff table (Staff, Docs, Pages, busiest category). No trend
chart. The existing grid remains as drill-down.

### 4. Two sources in the Turn-around window

Turn-around now requires both log families. The window gains two labelled source
rows — "Upload logs" and "File move logs" — each with its own Browse button and
recursive load. When the user picks a folder containing both as subfolders, both
are populated automatically; otherwise each is picked separately. Neither
source alone can produce the end-to-end metric, and the window must say which
one is missing rather than showing an empty grid.

### 5. Data-integrity rules

Each is applied consistently and surfaced as a visible count next to the figure
it affects, so the denominator can be defended when questioned.

- **Parse three filename conventions.** `YYYYMMDD-…` covers 90.4% of upload
  rows (Email, FAX, Paper, CD). ECAA uses `MMDDYYYY …` with a space (8.1%) or
  carries no leading date at all (1.4%). A parser that understands only the
  first convention silently drops all 2,247 ECAA documents. Documents with no
  recoverable date are excluded and counted, never guessed.
- **Parse two `DATE-TIME` formats.** Move logs mix ISO (`2025-08-28 07:45:16`)
  and US (`8/28/2025 8:45`) within every category — not per-category, per-row.
- **Handle quoted filenames containing commas.** PORTAL names such as
  `"20260630-ALLEN,AMANDA [048962880].PDF"` are CSV-quoted and contain commas
  and brackets.
- **De-duplicate by document**, keeping the earliest move and the earliest
  upload. Retry rows and repeated filenames must not inflate volume.
- **Exclude PORTAL from turn-around**, with a stated reason on screen. The
  upload logs' `SourceType` values are Email, FAX, Paper, ECAA and CD — there is
  no Portal, and zero of 219 PORTAL documents matched even after stripping all
  punctuation. PORTAL remains in production volume, where its data is real.
- **Report unmatched documents per category**, never silently. A category
  drifting from 4% to 40% unmatched is itself the finding.
- **Report empty source files as no-activity days**, not errors — 581 of 1,730
  move-log files (34%) are empty, corresponding to weekends and days a category
  saw no work.
- **Normalize `SOURCE-FOLDER` to its first path segment** in Production,
  collapsing over a thousand values to five categories.

### 6. Export and copy

Export produces one `.xlsx`: sheet 1 the summary figures including every
exclusion count, sheet 2 the underlying detail rows. A **Copy summary** button
places the headline figures on the clipboard as plain text for pasting into
email.

### 7. Visual polish

Both windows adopt the app's established typography, spacing and status
vocabulary, and gain designed empty states. Summary tabs must be
screenshot-presentable without cropping at the windows' minimum width.

## Architecture

Computation stays in `OrdoSort.Core`, testable without a UI. View models expose
computed values; views bind and lay out. This follows the existing split.

### Core

- **New `DocumentKey`** — filename normalization and the three date-prefix
  conventions, in one place, used by both log families.
- **New `UploadLog`** — reads the PECF workbooks, yielding one record per
  document with its earliest report timestamp and `SourceType`.
- **New `MoveLog`** — reads the move CSVs, yielding one record per document with
  its earliest move timestamp, category, owner and page count; owns the
  dual-format `DATE-TIME` parsing.
- **New `TurnaroundJoin`** — joins the two, producing matched documents with all
  three timestamps plus per-category unmatched counts. This is the seam the
  90.7%/zero-negative evidence lives behind, and it is tested independently of
  any bucket maths.
- **`TurnaroundTime`** — bucket classifiers for both legs and the summary
  aggregate (totals, per-bucket counts and percentages, per-category breakdown,
  weekly series, exclusion counts).
- **`ProductionReport`** — first-path-segment normalization and the production
  summary aggregate.
- **New export builder** producing the two-sheet workbook, and a
  summary-to-text formatter shared with the Copy button.

### View models

Both gain a `Summary` property and a `SelectedTab` defaulting to Summary. The
existing `DebouncedProbe` path already loads off the UI thread; the join and
summary are computed in that same off-thread step.

### Views

One Summary tab per window. Tiles, matrix, cards and tables are plain bound
controls; the sparkline is a small drawn element, not a charting dependency.

## Testing

Core is tested against hand-built fixtures, one per rule: a document appearing
in two reports, a retry row, an ISO and a US `DATE-TIME` in one file, a
comma-bearing quoted filename, an ECAA `MMDDYYYY ` name, a name with no date, a
`PAPER/MEDR…` category, an empty file, and a PORTAL document absent from the
upload side. The join is tested for the property that matters: no matched pair
may have an upload earlier than its move.

The live figures quoted in this document become a miniature regression fixture —
same shapes, small enough to verify by hand.

**Live sample data is never copied into the repo or into fixtures.** It contains
real patient and staff names. `.realSamples/` is gitignored.

## Out of scope

- Holiday calendars and business-day adjustment.
- Charting libraries.
- Merging the two windows into a single dashboard.
- Changing how the source logs are produced.
- Reconciling the two systems' category vocabularies (move logs use
  CD/EMAILS-APPEALS/EMAILS-MR/FAX/PORTAL; upload logs use
  Email/FAX/Paper/ECAA/CD). Categories are reported per source, not merged.

## Sequencing

Four phases, each independently shippable:

1. `DocumentKey`, `UploadLog`, `MoveLog` — readers and parsing rules, with
   tests. No UI change.
2. `TurnaroundJoin` and the turn-around aggregates, with tests. No UI change.
3. Turn-around Summary tab, its two source pickers, export and copy.
4. Production Summary tab, its export and copy, and the shared polish pass.
