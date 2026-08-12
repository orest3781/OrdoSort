# Report redesign: summary-first Turn-around and Production windows

Date: 2026-08-11
Status: approved design, ready for planning

## Problem

The Turn-around Time and Production windows load real data correctly but answer
no question. The live sample set produces grids of tens of thousands of rows, a
one-line status bar, and nothing else. The person who needs these numbers reports
them upward monthly in three forms over time: read off the screen, sent as a
spreadsheet, and shown as a screenshot. A raw grid serves none of them.

Four problems, each made concrete by the live data:

1. **No insight.** The reported metric — share of documents turned around in
   0–1 / 2 / 3+ days — is computed by hand in Excel, outside the app.
2. **Overwhelming grids.** Both windows present raw rows with no aggregate.
3. **Dated presentation.** Neither window matches the app's visual standard, and
   neither is presentable as a screenshot.
4. **Real-data friction.** Duplicate documents, two incompatible date formats,
   two incompatible filename conventions, legacy category aliases, empty files
   and ambiguous ownership all silently distort the numbers today.

## The data: two independent reports

The two log families are produced by different systems and are **not** joined.
Each drives one window. An earlier draft of this spec joined them on filename;
that is explicitly rejected — the logs were never designed to work together, and
keeping them separate matches how the numbers are actually produced and
defended.

### Upload logs → Turn-around report

`UPLOAD LOGS/<YYYYMMDD>/<YYYYMMDD-HHMM> PECF Report.xlsx`. 143 files in the
sample, 6–7 per working day, 23,552 rows, covering 2026-07-01 to 2026-08-10.

Turn-around is the distance between two dates, both recoverable from names
alone — the method already used in Excel:

- **Document date** — the date prefix of the row's `FileName`, i.e. when the
  document was worked.
- **Upload date** — the date in the report's own filename, i.e. when the report
  that first lists the document was generated.

De-duplicated to 23,306 distinct documents (246 repeats removed). ECAA is not
part of this process and is ignored (see "Ignoring sources" below), removing
2,247 documents and leaving 21,059, of which 20,952 are measurable. Calendar
days, the existing method:

| Bucket | Documents | Share |
|---|---|---|
| 0–1 days | 18,508 | **88.3%** |
| 2 days | 86 | 0.4% |
| 3+ days | 2,358 | **11.3%** |

By source, calendar days — the spread is the point, and it survives:

| SourceType | Documents | 0–1 | 2 | 3+ |
|---|---|---|---|---|
| Email | 12,748 | 91.0% | 0.2% | 8.8% |
| FAX | 3,315 | 87.3% | 0.0% | 12.7% |
| CD | 1,990 | 83.4% | 1.3% | 15.4% |
| Paper | 2,899 | 81.4% | 1.0% | 17.6% |

Counted in business days the same population reads 96.6% / 3.1% / 0.3%, which
flattens the category differences that make the report useful. **Calendar days
is the headline**, matching what leadership already receives; the business-day
figure appears as a single secondary line so the weekend's contribution stays
visible without displacing the primary metric.

### File move logs → Production report

`FILE MOVE LOGS/<CATEGORY>/<YYYYMMDD>-<CATEGORY>-MOVE-LOG.csv`. 1,730 files
across five categories, 163,282 rows, covering 2025-08-28 to 2026-08-11 —
240 days with activity, averaging 680 documents per day, peaking at 1,222.

| Category | Documents | Pages |
|---|---|---|
| EMAILS-APPEALS | 60,237 | 7,078,286 |
| EMAILS-MR | 47,064 | 34,159,631 |
| FAX | 34,106 | 1,570,603 |
| CD | 19,451 | 20,249,130 |
| PORTAL | 2,424 | 697,222 |

The `old.logs` and `old.production` folders are superseded and are not sources.

## Decisions

### 1. Turn-around Summary tab: hero number with side matrix

A new tab, opened by default. Left: the hero percentage in 0–1 calendar days,
its month-over-month delta, the 2 / 3+ counts, the business-day secondary line,
and the exclusion counts. Right: the per-`SourceType` matrix and a weekly
sparkline. The existing Documents / Daily / Weekly / By category tabs remain as
drill-down.

### 2. Production Summary tab: category cards with staff table

A new tab, opened by default. A strip of five category cards (documents per
category), then a staff table (Staff, Docs, Pages, busiest category). No trend
chart. The existing grid remains as drill-down.

### 3. Ignoring sources

Some values in the source data belong to processes this report does not cover.
ECAA is one today; there will be others. Rather than hard-coding a rule per
value, both windows carry an **ignore list**: the set of values discovered in
the loaded data is presented as a checklist, and unchecking one removes it from
every figure. The choice persists in config (`tat_ignored_sources`,
`production_ignored_categories`) so it survives a restart and does not have to
be re-applied each month.

Ignored values are never silently dropped. The summary always states what was
set aside and how much of it there was — "ignored: ECAA (2,247 documents)" —
so a reader can tell the difference between data that was absent and data that
was excluded on purpose.

This deployment ignores ECAA on the turn-around side. Nothing is ignored by
default in a fresh install; the list ships empty.

### 4. Turn-around data rules

- **Parse the document date from the filename.** With ECAA ignored, every
  remaining document uses `YYYYMMDD-…` and **all 20,952 parse cleanly** — the
  333 unparseable names in the raw data were all ECAA. `DocumentDate` also
  understands the `MMDDYYYY ` (space-separated) form ECAA uses, so re-including
  it later yields real dates rather than a wall of exclusions.
- **Count, never guess, unparseable names.** A name with no recoverable date is
  excluded and reported, never inferred from position or neighbours.
- **De-duplicate by filename, earliest report wins.** A document listed in
  several reports is one document, uploaded once.
- **Exclude documents whose upload date precedes their document date** — 107 in
  the sample, from future-dated filenames. Never render a negative turn-around.

### 5. Production data rules

- **Take the category from the containing folder**, which yields exactly the
  five real categories. `SOURCE-FOLDER` is unreliable for this: it now encodes
  `CATEGORY@EMPLOYEE` (`FAX@OHUMINILOWITSH`) and carries legacy aliases for the
  same category — `APPEALS` and `EMAILS_APPEAL` are one category, as are `MR`
  and `EMAILS_MR`. Deriving from `SOURCE-FOLDER` produces seven categories where
  there are five.
- **Attribute work by `FILE-OWNER`**, not by the name embedded in
  `SOURCE-FOLDER`. The two disagree on 30,282 rows (18.5%): the embedded name is
  whose queue the document came from, `FILE-OWNER` is who moved it. The window
  states which one it counts.
- **Surface unknown and near-duplicate owners.** `FILE-OWNER` is literally
  `Unknown` on 13,304 rows (8.1%), and `nguevara` (16,271) and `nguevera` (72)
  are one person spelled two ways. Both are reported, neither is silently
  merged — merging identities is the user's call, not the app's.
- **Parse two `DATE-TIME` formats.** Move logs mix ISO
  (`2025-08-28 07:45:16`) and US (`8/28/2025 8:45`) within every category, row
  by row rather than file by file.
- **Handle quoted filenames containing commas**, e.g.
  `"20260630-ALLEN,AMANDA [048962880].PDF"`.
- **Report empty files as no-activity days**, not errors — 581 of 1,730 (34%),
  corresponding to weekends and days a category saw no work.
- **De-duplicate repeated filenames** — 1,816 names repeat, 1,863 extra rows.

Every excluded or ambiguous document is counted and displayed next to the figure
it affects, so the denominator can be defended when questioned.

### 6. Export and copy

Each window's Export produces one `.xlsx`: sheet 1 the summary figures including
every exclusion count, sheet 2 the underlying detail rows. A **Copy summary**
button places the headline figures on the clipboard as plain text for pasting
into email.

### 7. Visual polish

Both windows adopt the app's established typography, spacing and status
vocabulary, and gain designed empty states. Summary tabs must be
screenshot-presentable without cropping at the windows' minimum width.

## Architecture

Computation stays in `OrdoSort.Core`, testable without a UI. View models expose
computed values; views bind and lay out. This follows the existing split.

Each window keeps its single folder source. The Turn-around window points at the
upload logs, the Production window at the move logs; both already load
recursively, which covers the dated and per-category subfolder layouts.

### Core

- **New `DocumentDate`** — the two filename date conventions in one tested
  place.
- **New `IgnoreList`** — the shared set-membership rule behind both windows'
  ignore lists, so "which values exist in this data" and "which are excluded"
  are answered the same way on both sides and each ignored value carries its
  own count for display.
- **`TurnaroundTime`** — de-duplication, calendar and business-day bucket
  classifiers, and the summary aggregate: totals, per-bucket counts and
  percentages, per-`SourceType` breakdown, weekly series, ignored and excluded
  counts.
- **`ProductionReport`** — folder-derived categories, owner attribution, and the
  production summary aggregate: per-category documents and pages, per-staff
  totals with busiest category, day coverage, no-activity days, and the unknown
  and near-duplicate owner counts.
- **New export builder** producing the two-sheet workbook, and a
  summary-to-text formatter shared with the Copy button.

### View models

Both gain a `Summary` property and a `SelectedTab` defaulting to Summary. The
existing `DebouncedProbe` path already loads off the UI thread; the summary is
computed in that same off-thread step.

### Views

One Summary tab per window. Tiles, matrix, cards and tables are plain bound
controls; the sparkline is a small drawn element, not a charting dependency.

## Testing

Core is tested against hand-built fixtures, one per rule: a document listed in
two reports, an `MMDDYYYY ` name, a name with no date, a future-dated name,
an ISO and a US `DATE-TIME` in one file, a comma-bearing quoted filename, a
`CATEGORY@EMPLOYEE` source folder, a legacy `EMAILS_APPEAL` alias, an `Unknown`
owner, and an empty file.

The ignore list gets its own tests, because it changes every published figure:
an ignored value must leave the percentages computed over the remaining
population, must still report its own count, and must round-trip through config
so a restart does not silently re-include it.

The live figures quoted in this document become a miniature regression fixture —
same shapes, small enough to verify by hand.

**Live sample data is never copied into the repo or into fixtures.** It contains
real patient and staff names. `.realSamples/` is gitignored.

## Out of scope

- Joining the two log families.
- Holiday calendars.
- Charting libraries.
- Merging the two windows into a single dashboard.
- Merging near-duplicate staff identities automatically.
- Changing how the source logs are produced.

## Sequencing

Four phases, each independently shippable:

1. `DocumentDate` and the turn-around aggregates, with tests. No UI change.
2. Turn-around Summary tab, export and copy.
3. Production category, owner and aggregate rules, with tests. No UI change.
4. Production Summary tab, export and copy, and the shared polish pass.
