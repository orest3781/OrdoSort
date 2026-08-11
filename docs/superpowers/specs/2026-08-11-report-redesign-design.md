# Report redesign: summary-first Turn-around and Production windows

Date: 2026-08-11
Status: approved design, ready for planning

## Problem

The Turn-around Time and Production windows load real data correctly but answer
no question. Loading the live sample set (117 PECF workbooks, 323 move logs)
produces a 13,127-row grid and a 36,891-row grid with a one-line status bar and
nothing else. The person who needs these numbers reports them upward to their
boss's boss, monthly, in three forms over time: read off the screen, sent as a
spreadsheet, and shown as a screenshot. None of those are served by a raw grid.

Four things are wrong at once, and the live data made each concrete:

1. **No insight.** The windows show rows, not answers. The reported metric —
   share of documents turned around in 0–1 / 2 / 3+ days — is computed by hand
   outside the app.
2. **Overwhelming grids.** Production shows 1,104 groups because
   `SOURCE-FOLDER` carries per-batch paths (`PAPER/MEDR002245`); there are only
   five real categories. Turn-around shows fifteen columns from the source
   workbook.
3. **Dated presentation.** Neither window matches the visual standard set by the
   rest of the app, and neither is presentable as a screenshot.
4. **Real-data friction.** Duplicate documents across reports, future-dated
   filenames, empty holiday files, retry rows, and unparseable dates all
   silently distort the numbers today.

## What the TAT data actually is

This matters because it constrains the math, and it is not obvious from the
files. The PECF workbooks were never produced as turn-around reports. They are
generated *after* documents are uploaded, and they are the only available signal
for separating "when the document was worked" from "when it entered the system":

- **Document date** — the `YYYYMMDD` prefix of each row's `FileName`, i.e. when
  the document was worked.
- **Upload date** — the `YYYYMMDD-HHMM` prefix of the report's own filename,
  i.e. when the report that first lists the document was generated.

Turn-around is the distance between those two. Because the reports run every
weekday morning, a document is nearly always listed on the next report.

## Decisions

Each was chosen against the live sample set, not in the abstract.

### 1. Business days headline, calendar days breakdown

Measured on 10,801 de-duplicated documents:

| Counting | 0–1 | 2 | 3+ |
|---|---|---|---|
| Calendar days | 94.4% | 0.0% | 5.6% |
| Business days | 99.8% | 0.2% | 0.0% |

Business days alone flattens the report: every category reads 100% and the
category table stops distinguishing anything. Calendar days alone misattributes
the weekend to the team — the 3+ bucket is almost entirely Friday→Monday, and
the "2 days" bucket is empty because a two-day gap essentially never occurs.

Both are computed. The hero figure is business days ("99.8% same or next
business day"). The category breakdown below it is calendar days, carrying an
explicit note that 3+ calendar is weekend-driven. Buckets stay 0–1 / 2 / 3+ in
both, matching what leadership already receives.

No holiday calendar in v1. Weekends only.

### 2. Turn-around Summary tab: hero number with side matrix

A new tab, opened by default. Left: the business-day hero percentage, its
month-over-month delta, and the 2 / 3+ / excluded counts beneath it. Right: the
calendar-day category matrix (Category, Docs, 0–1, 2, 3+, with an "All" row) and
a weekly sparkline. The existing Documents / Daily / Weekly / By category tabs
remain as drill-down.

### 3. Production Summary tab: category cards with staff table

A new tab, opened by default. A strip of five category cards (documents per
category), then a staff table (Staff, Docs, Pages, busiest category). No trend
chart. The existing grid remains as drill-down.

### 4. Data-integrity rules

Applied consistently in both windows, each surfaced as a visible count rather
than a silent drop:

- **De-duplicate Turn-around documents by `Controlid`, keeping the earliest
  report** the document appears in. 724 documents appear more than once (one
  appears 19 times); counting every row inflates volume by 7.5% (11,675 vs
  10,801 documents).
- **Exclude documents whose upload date precedes their document date** — 212 in
  the sample, from future-dated filenames. Never render a negative turn-around.
- **Exclude documents with no parseable document date** — 266 in the sample.
- **Normalize `SOURCE-FOLDER` to its first path segment** in Production: 1,092
  distinct values become 5 real categories (EMAILS_APPEAL 11,181, EMAILS_MR
  10,262, FAX 5,407, PAPER 5,221, CD 4,813).
- **Count each production document once**, and report retries separately: 104
  rows carry a retry action and 360 filenames repeat (385 extra rows).
- **Report empty source files as "no activity" days**, not errors — 8 in the
  sample, corresponding to holidays.

Every excluded document is counted and displayed next to the figure it was
excluded from, so the denominator can be defended when questioned.

### 5. Export and copy

The Export button produces one `.xlsx` workbook: sheet 1 is the summary
(the figures shown on the Summary tab, including exclusion counts), sheet 2 is
the underlying detail rows. Alongside it, a **Copy summary** button places the
headline figures on the clipboard as plain text, ready to paste into an email.

### 6. Visual polish

Both windows adopt the app's established typography, spacing, and status
vocabulary, and gain a designed empty state. The Summary tabs must be
screenshot-presentable without cropping at the windows' minimum width.

## Architecture

Computation stays in `OrdoSort.Core`, testable without a UI. The view models
expose already-computed values; the views bind and lay out. This follows the
existing split (`TurnaroundTime` / `ProductionReport` feeding
`TurnaroundViewModel` / `ProductionViewModel`).

### Core

- `TurnaroundTime`: add business-day distance; add a bucket classifier
  (0–1 / 2 / 3+) parameterised by which distance it is given; add a summary
  aggregate carrying totals, per-bucket counts and percentages, per-category
  breakdown, weekly series, and the three exclusion counts.
- New `DocumentDedupe` (or an equivalent seam on `TurnaroundTime`): reduce rows
  to one per `Controlid`, earliest report wins. Kept separate from the
  turn-around math so both are testable alone.
- `ProductionReport`: add first-path-segment category normalization; add a
  summary aggregate carrying per-category document and page totals, per-staff
  totals with busiest category, day coverage, retry count, and no-activity day
  count.
- New export builder producing the two-sheet workbook, and a summary-to-text
  formatter shared with the Copy button.

### View models

Both gain a `Summary` property holding the Core aggregate and a `SelectedTab`
defaulting to Summary. The existing `DebouncedProbe` load path already produces
the table off the UI thread; the summary is computed in that same off-thread
step, never on the UI thread.

### Views

Each window gains one Summary tab. The tiles, matrix, cards, and staff table are
plain bound controls; the sparkline is a small drawn element, not a charting
dependency.

## Testing

Core aggregates are tested directly against hand-computed fixtures, including
one fixture per data-integrity rule (a duplicate `Controlid` across two reports,
a future-dated filename, an unparseable date, a `PAPER/MEDR…` category, a retry
row, an empty file). The exact live-sample figures quoted in this document
become a regression fixture in miniature: the same shapes, small enough to
verify by hand.

View-model tests assert the Summary property populates and that the Summary tab
is selected on load. Existing window tests continue to cover layout at minimum
width.

Live sample data is never copied into the repo or into test fixtures. It
contains real patient and staff names and is gitignored.

## Out of scope

- Holiday calendars.
- Charting libraries.
- Merging the two windows into a single dashboard.
- Changing how the source reports are produced.

## Sequencing

Three phases, each independently shippable:

1. Core math and data-integrity rules, with tests. No UI change.
2. Turn-around Summary tab, plus its export and copy.
3. Production Summary tab, plus its export and copy, and the shared polish pass.
