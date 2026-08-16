# Reports hub: TAT and Production dashboards over four live feeds

Date: 2026-08-15
Status: approved design, ready for planning
Supersedes: `2026-08-11-report-redesign-design.md` (approved, never implemented).
Its data rules carry forward except where this document explicitly changes them;
the two changes are called out inline as **[supersedes 08-11]**.

## Problem

The person who runs this app also produces two Excel deliverables by hand each
reporting cycle:

- **TAT.xlsx** — turn-around SLA figures built from PECF upload reports:
  a cleaned row table (`CAVO_REPORTS`), a bucket-count pivot, and a
  month × SLA percentage grid (`BREAKDOWN`).
- **production dashboard.xlsx** — a daily two-sided pivot: *DELIVERED*
  (staff × category, records + pages, from file-move logs plus scanned-paper
  logs) beside *PHYSICAL RECEIVED* (mailroom entries by staff × record type).

Both are assembled by copy-paste, hand-scrubbing, and pivot refreshes. The
app's existing Turn-around and Production windows load some of this data but
answer none of these questions — they are raw grids.

This design replaces both windows with a single **Reports hub** that reads the
four raw feeds directly and renders the two dashboards the Excel files exist to
produce, plus the drill-down and search the spreadsheets never had.

## The four feeds

Each feed is one folder chosen by the user, **always scanned recursively**, and
persisted in config. Sample data verified against the live exports in
`docs/sample/` (gitignored; see PHI stance).

### 1. Upload reports → Turn-around

`**\<YYYYMMDD>-<HHMM>-PECF Report.xlsx`, one sheet, 16 columns, fixed layout
(all 143 sample files share it). The report's **own filename** carries the
upload date and time. Sample: 143 files, 23,552 rows, 2026-07-01 → 08-10,
6–7 reports per working day in `<YYYYMMDD>\` subfolders.

### 2. Move logs → Production · Delivered

`**\<CATEGORY>\<YYYYMMDD>-<CATEGORY>-MOVE-LOG.csv`, ten columns
(`DATE-TIME, FILE-OWNER, FILE-NAME, ACTION, PDF-PAGE-COUNT, SOURCE-FOLDER,
SOURCE-PATH, DESTINATION-PATH, PDF-FILE-SIZE, LAST-ACCESSED` — the writer is
`docs/paper_mover_logger.py`'s config). Sample: 1,730 files, 163,282 rows,
2025-08-28 → 2026-08-11, five real categories. The dashboard workbook's
`FILE MOVE LOGS` sheet corroborates the totals.

### 3. Scan reports → Production · Delivered (PAPER)

One-line `.txt` files whose **filename is the record**:
`<batchId>,<records>,<pages>,<YYYY-MM-DD>,<HH-MM-SS>,<operator>,.txt`.
The file **contents are never opened** — the line inside holds a patient name.
Sample: 187 files, 1,142 records, 624,244 pages, 2026-08-03 → 08-14, three
operators. These are the `SCANNED PAPER LOGS` half of the workbook's `Append1`.

### 4. Mailroom reports → Production · Received

`**\*MailRoomReport*.xlsx`, one sheet, 14 columns, fixed layout (all 53 sample
files share it). The reader is **column-whitelisted**: it loads exactly
`Entered Date`, `Entered By`, `Receipt Type`, `Record Type`, and `Barcode`
(the machine-generated dedupe key). The patient-name, claim-number, and
admit/discharge columns are never materialized in memory, and a test proves
it. Workbooks overlap in coverage; rows dedupe by Barcode across files.
Sample: 53 files, 10,961 rows, 2026-05-29 → 08-13.

The email-mailbox CSVs in the sample folder are **not** a feed — they are not
in the Excel dashboard today and stay out of scope (the hub's sidebar leaves
room for them as a future page).

## Decisions

### 1. One hub window, three pages

A new `ReportsWindow` with a slim left sidebar — **Turn-around**,
**Production**, **Sources** — replaces `TurnaroundWindow` and
`ProductionWindow`, which are deleted. The Reports menu keeps its two entries
plus a hub entry; all three open the hub, the existing two landing on their
page. Each dashboard page has **Summary** (default) and **Detail** tabs, a
header with the loaded date span and file counts, and Refresh / Copy summary /
Export actions. The sidebar footer shows last-refresh time and total feed
counts.

Approved mockups (local, gitignored):
`.superpowers/brainstorm/445-1786845441/content/{tat-page-v2,production-page,sources-page}.html`.

### 2. Turn-around page — mimics TAT.xlsx

- **Hero tile**: percentage in 0–1 business days, with a month-over-month
  delta chip. Beside it, the four bucket counts: Same Day / 1 / 2 / 3+.
- **By month** — the `BREAKDOWN` grid: rows months, columns 0–1 / 2 / 3+,
  each cell percentage with its record count.
- **By source** — the same buckets per SourceType (Email, FAX, Paper, CD).
- **Weekly trend** — a drawn bar sparkline of the 0–1 share.
- **Set aside strip** — ignored sources, future-dated, duplicates, and no-date
  counts, each clickable to inspect the rows behind it.
- **Detail tab** — the deduplicated document grid (upload date, document date,
  TAT, bucket, source, filename, page count, destination) with an inline
  filter box.

### 3. Headline metric is business days **[supersedes 08-11]**

The 08-11 spec chose calendar days as the headline. The workbook it mimics
does not: `TAT` equals `busday_count(FileDate, UploadDate)` on 23,565 of
23,672 rows (the 107 exceptions are the future-dated coercions covered in
Data rules). The hub reports what leadership actually receives — **business
days**, bucketed Same Day / 1 / 2 / 3+ and rolled up to 0–1 / 2 / 3+ for the
SLA grid. No calendar-day secondary line.

### 4. Production page — mimics the two-sided pivot

- **Scope control**: Day / Week / Month / All plus a date stepper, defaulting
  to the **latest day with data** — the Excel pivot's daily view. Widening the
  scope recomputes every panel over the range.
- **Category cards**: APPEALS, MR, FAX, PAPER, CD, PORTAL — records and pages
  each, busiest highlighted, zero-activity categories dimmed.
- **Delivered by staff**: the pivot's Staff → Category nesting as an
  expandable table — records, pages, share bar, busiest-category badge —
  with a records-per-day sparkline beneath.
- **Physical received**: its own compact panel — total entered, staff ×
  record type table, receipt-type chips.
- **Data health strip**: unknown owners, duplicate rows, near-duplicate
  identities, no-activity files — whole-range counts, clickable to inspect.
- **Detail tab**: the normalized row grids (moves, scans, received) with an
  inline filter box.

### 5. Global search

**Ctrl+K** (or the header search box) from any page searches every loaded
feed at once — filename, control ID, staff, category, barcode. Results are
grouped by feed with match counts, so one query shows a document's whole
journey: uploaded, moved, received. **Enter** opens that page's Detail tab
with the query applied as a live filter and matches highlighted. Each Detail
tab keeps its own inline filter independent of global search.

### 6. Sources page

One card per feed: folder path + Browse + per-feed refresh, found-file status
(files, rows, date span, load time), and that feed's own rules rendered on the
card — the ignore checklist on Upload reports, alias folding on Move logs, the
filename-only rule on Scan reports, the column whitelist on Mailroom. A
"Refresh all feeds" action sits in the header.

Warnings turn a card amber and say exactly what was skipped (locked files,
unreadable folders) — skipped files are listed, never silently dropped. An
unconfigured feed renders as an empty card with a Choose-folder prompt; its
dashboard panels show a designed empty state pointing at Sources.

### 7. Ignore lists (carried forward from 08-11)

Both dashboards carry a persisted ignore list presented as a checklist of
values discovered in the loaded data — `tat_ignored_sources`,
`production_ignored_categories`. Unchecking a value removes it from every
figure but its count stays on screen ("ECAA ignored · 2,251"), so absent data
and deliberately excluded data are never confused. Fresh installs ship with
empty lists; this deployment unchecks ECAA on the TAT side.

### 8. Refresh model

Feeds load when the hub opens and on explicit refresh (per-feed or all). No
filesystem watchers — these folders live on network shares and the numbers
change a handful of times a day. Loading runs off the UI thread via the
existing `DebouncedProbe` pattern; every panel binds to the last completed
snapshot, so the UI never shows a half-loaded state.

### 9. Export and copy (carried forward from 08-11)

Per dashboard page, Export writes one `.xlsx`: sheet 1 the summary figures
including every set-aside count, sheet 2 the underlying detail rows. Copy
summary places the headline figures on the clipboard as plain text for email.

### 10. Visual standard

The hub adopts the app's established theme resources, typography, and status
vocabulary. Sparklines and share bars are drawn elements — no charting
dependency. Summary pages must be screenshot-presentable at the window's
minimum width. The approved mockups are the layout reference; exact colors
come from the app's theme, not the mockups.

## Data rules

### Turn-around

1. **Upload date** = the report's own filename. **Document date** = the
   `FileName` prefix via `DocumentDate`, which understands `YYYYMMDD-`,
   `MMDDYYYY ` (space), and `MM.DD.YYYY ` (dotted) forms — so re-including
   ECAA later yields real dates, not a wall of exclusions.
2. **De-duplicate by FileName, earliest report wins** (245 duplicate rows in
   the sample).
3. **TAT = business days** between document date and upload date (weekends
   excluded, no holiday calendar).
4. **Future-dated documents are excluded and counted** — never coerced.
   The workbook hand-edits these 107 rows to "1 Business Day"; the hub
   instead shows them in the set-aside strip. Accepted consequence: the hub's
   headline reads ~96.6% where the workbook reads 96.1% — the difference is
   the visible exclusions, defensible on screen.
5. **Unparseable names are excluded and counted**, never inferred. With ECAA
   ignored, every remaining sample document parses.

### Production · Delivered

Carried forward from 08-11, verified against the sample:

6. **Category comes from the containing folder**; legacy aliases fold
   (`EMAILS_APPEAL` → APPEALS, `EMAILS_MR` → MR); `CATEGORY@EMPLOYEE`
   source-folder forms are recognized.
7. **Attribution is `FILE-OWNER`** — who moved it, not whose queue it came
   from. The page states this.
8. **Unknown and blank owners are surfaced as counts; near-duplicate
   identities (`nguevara`/`nguevera`) are reported, never auto-merged.**
9. **Both `DATE-TIME` formats parse** — ISO and US, mixed row-by-row.
10. **Quoted filenames containing commas parse.**
11. **Empty log files are no-activity days**, not errors (581 of 1,730).
12. **Repeated filenames dedupe** (1,816 names, 1,863 extra rows).
13. **Scan-report txt filenames** contribute PAPER records: batch id, record
    count, page count, date, operator — parsed from the name alone.

### Production · Received

14. **Dedupe by Barcode across workbooks** — the exports overlap.
15. **Token variants of the same value normalize** for Receipt Type and
    Record Type (`CERTIFIED MAIL`→`CERTIFIEDMAIL`, `FED EX`→`FEDEX`,
    `THUMB DRIVE`→`THUMBDRIVE`, `DISK DAMAGED`→`DISKDAMAGED`): typography,
    not identity. **People never normalize** — rule 8 applies to `Entered By`.
16. **Counting unit**: one row = one package entered (the pivot's
    "Count of Receipt Type").

Every excluded, ignored, or ambiguous row is counted and displayed next to
the figure it affects.

### Verified reference figures

Computed independently from the sample feeds; these become the shape of the
regression fixtures (fixtures themselves are synthetic — see PHI stance).

| Figure | Value |
|---|---|
| PECF rows → documents after dedupe | 23,552 → 23,307 |
| ECAA set aside (2,251 raw rows; after dedupe) | 2,247 |
| Future-dated excluded | 107 |
| Measurable documents | 20,953 |
| Business-day buckets 0–1 / 2 / 3+ | 96.60% / 3.14% / 0.26% |
| Month grid — Jul, 0–1 | 95.9% (16,381) |
| Month grid — Aug, 0–1 | 96.9% (6,377) |
| Delivered, 2026-08-12 | 871 records / 263,362 pages |
| Received, 2026-08-12 | 102 packages |

## PHI stance

- Live sample data stays in `docs/sample/` (gitignored) or outside the tree;
  **it is never copied into fixtures, tests, or commits.** All fixtures are
  synthetic.
- The mailroom reader's column whitelist is enforced by a test that feeds a
  workbook with decoy name/claim columns and asserts they are absent from
  every surface the reader exposes.
- The scan-report reader takes filenames only; a test asserts the file
  contents are never read (the fixture file's content is a sentinel that
  would fail the test if surfaced).
- Filenames themselves (which can embed patient names) appear only inside
  the app's own grids and exports, as they already do elsewhere in the app.

## Architecture

Computation stays in `OrdoSort.Core`, UI-free and tested; view models expose
computed values; views bind. This follows the existing split.

### Core

- **`DocumentDate`** — the three filename date conventions, one tested place.
- **`IgnoreList`** — discovered values, membership, per-value counts,
  config round-trip. Shared by both dashboards.
- **Feed readers** — `UploadReportFeed` (xlsx via `XlsxTable`),
  `MoveLogFeed` (CSV rules 6–12), `ScanReportFeed` (filename parsing,
  rule 13), `MailroomFeed` (column whitelist, rules 14–16). Each returns
  rows plus a load report: files found, skipped files with reasons, date
  span, row counts.
- **`TurnaroundSummary`** — dedupe, business-day classifier, bucket counts,
  month grid, by-source matrix, weekly series, set-aside counts.
- **`ProductionSummary`** — category/owner rules, per-scope aggregation
  (day/week/month/all), category cards, staff × category nesting, received
  counts, data-health counts. The existing `ProductionReport`
  group/count/sum engine is the starting point.
- **Search index** — per-feed searchable fields (filename, control ID,
  staff, category, barcode), returning grouped matches.
- **Export builder** — the two-sheet workbook per page, plus the
  summary-to-text formatter shared with Copy summary.

The 08-11 principle stands: these stay small named seams so the parked
report-builder could later expose them as steps.

### View models and views

`ReportsViewModel` owns the feeds, the search box, and three page view
models: `TurnaroundPageViewModel`, `ProductionPageViewModel`,
`SourcesPageViewModel`. Feed loading is off-thread; pages bind to immutable
snapshot objects. Views: `ReportsWindow.xaml` plus one UserControl per page;
tiles, grids, cards are plain bound controls; sparklines are drawn.

### Config

New keys (final names follow `Config.cs` conventions at planning):
`reports_upload_folder`, `reports_movelog_folder`,
`reports_scanreport_folder`, `reports_mailroom_folder`,
`tat_ignored_sources`, `production_ignored_categories`.

### Removals

`TurnaroundWindow`, `ProductionWindow`, and their view models are deleted
once the hub reaches parity (end of phase 4). The E2E suite's
`ReportScenarios` rework onto the hub in the same phase.

## Testing

- One fixture per data rule (1–16), synthetic throughout: duplicate-across-
  reports, each date convention, no-date name, future-dated name, mixed
  ISO/US times in one file, quoted comma filename, `CATEGORY@EMPLOYEE`
  folder, legacy alias, unknown owner, empty file, scan filename parse,
  barcode overlap, token-variant normalization.
- The two PHI-enforcement tests described in PHI stance.
- Ignore-list tests carried from 08-11: percentages recompute over the
  remaining population, ignored counts still display, config round-trip.
- Search: grouped results across feeds, Enter-to-filtered-detail behavior at
  the view-model level.
- A miniature regression fixture shaped like the verified reference figures.
- E2E: hub scenarios replace the two windows' scenarios (open hub, configure
  a source, summary renders, search jumps to detail, export writes a
  workbook).

## Out of scope

- The email-mailbox CSVs (future sidebar page at most).
- Holiday calendars; charting libraries.
- Auto-merging near-duplicate staff identities.
- Filesystem watchers / live tailing of the feeds.
- The report builder (still parked, seams preserved).
- Changing how any source log is produced.

## Sequencing

Four phases, each independently shippable:

1. **Core feeds + TAT engine.** `DocumentDate`, `IgnoreList`,
   `UploadReportFeed`, `TurnaroundSummary`, tests, regression fixture.
   No UI change.
2. **Hub shell + Sources + Turn-around page.** `ReportsWindow`, sidebar,
   Sources cards for the upload feed, TAT Summary/Detail, export + copy,
   menu rewiring. Old windows still present.
3. **Production engines.** `MoveLogFeed`, `ScanReportFeed`, `MailroomFeed`,
   `ProductionSummary`, tests. No UI change.
4. **Production page + search + retirement.** Production Summary/Detail,
   remaining Sources cards, global search, delete the old windows, E2E
   rework, polish pass.
