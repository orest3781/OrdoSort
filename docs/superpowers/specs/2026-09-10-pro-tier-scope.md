# OrdoSort Pro: paid tier scope

Date: 2026-09-10
Status: proposal for owner decision. Nothing here is built.

## Summary

OrdoSort v1.5.x is a finished free product: nine releases in three weeks, a
download site, CI, a 2,000-test suite. It has no price. This document scopes a
paid tier ("Pro") that leaves the free app untouched and sells the things an
office manager pays for: management reports, read-and-suggest filing, team
audit, retention, and support.

Recommended shape: **open core.** OrdoSort stays MIT and free. Pro features
ship in a separate, proprietary `OrdoSort.Pro` assembly unlocked by an offline
license file. One-time purchase per seat with a year of updates, matching how
this segment already buys (FileCenter: $97 / $197 / $297 per user, one-time,
maintenance renewed at 25% of list).

## Who buys

The app is built for a scanning and records intake operation: front-desk
scans, faxes, referrals, medical records, mailroom, box labels with retention
dates, turn-around SLA reporting against PECF upload reports. The buyer is the
supervisor or office manager of a team like that, in:

- release-of-information and medical-records departments and vendors
- scanning bureaus and back-office intake teams
- law, accounting and insurance offices with a scan-and-file clerk

They buy on a one-time price, will not put PHI in a cloud tool, run Windows on
a share, and need an audit trail they can hand to a compliance reviewer. The
first reference customer is the operation the app was built in, if the owner
can get permission to name it.

## Free vs Pro

| Free (unchanged, MIT)                         | Pro (proprietary assembly, licensed)                     |
|-----------------------------------------------|-----------------------------------------------------------|
| Routing loop, four naming modes, hotkeys      | Read-and-suggest: OCR the PDF, prefill name and route     |
| Dashboard, alerts, toasts, sounds             | Reports hub: turn-around SLA and production dashboards    |
| History (shared SQLite, backups, CSV export)  | Team audit: who filed what from which station, per-station stats, tamper-evident export |
| All nine tools, Box Labels standalone         | Retention: destruction-due lists and pull sheets from the box-label store |
| Community support via GitHub                  | Email support with a response target; signed builds; update check |

Rule for the split: anything a single clerk needs to do the job stays free.
Anything a manager needs to run or defend the operation is Pro.

## Pro features

### P1. Team audit (effort: ~1.5 weeks)

`history` has no user or station column today; several stations write to one
`history.sqlite` and nothing says who did what. Pro adds:

- `station` and `user` columns (machine name, Windows user), written by every
  commit, set-aside and undo; free builds write them too so the log is
  complete when Pro is switched on later.
- History viewer filters by station and user; per-station daily counts.
- Tamper-evident export: each exported row carries a running SHA-256 chain and
  the export ends with a signed digest, so a reviewer can show nothing was
  edited after the fact.
- Seat awareness: the number of distinct stations active in the last 24 hours
  is what the license counts.

Why paid: only a supervisor cares, and it is the feature a compliance reviewer
asks for.

### P2. Read-and-suggest (effort: ~2 weeks)

When a PDF opens, run local OCR on the first page and propose the name and
route before the clerk types anything.

- OCR: `Windows.Media.Ocr` (in the OS on Windows 10/11, no install, no
  network, no PHI leaves the machine). Rasterise page one with PdfSharp or the
  existing WebView2 print path.
- Suggestion rules, all local: configurable regex per route ("Referral" in the
  header routes to Referrals; the name after "Patient:" fills the name field),
  plus the existing autocomplete history as a prior. Confidence shown on the
  preview; the clerk confirms with the usual hotkey, so a wrong suggestion
  costs nothing.
- Later, optional: a local model through Ollama for free-text documents. Not
  in v1 of Pro; the rules cover the fixed-layout documents this operation sees.

Why paid: it is the time saver, and it is what "AI document routing" products
charge for, done without their cloud.

### P3. Reports hub (effort: ~3 to 4 weeks)

Already designed and approved (`2026-08-15-reports-hub-design.md`, phases 1
and 2 planned in detail), zero lines shipped. Turn-around SLA figures from
PECF upload reports and the daily production dashboard from move logs, scan
reports and mailroom reports, with drill-down and xlsx export. It replaces two
hand-built spreadsheets per reporting cycle.

Why paid: this is management output. It is also the most specific to the
first customer's feeds; the feed readers must be configurable (column mapping,
filename conventions) before a second customer can use it. Ship it as the
first Pro update, not in the launch build, so launch is not blocked on it.

### P4. Retention and destruction (effort: ~1 week)

The box-label store already carries created and destruction dates and
per-client retention offsets. Pro adds a destruction-due list (this month, next
quarter), a printable pull sheet, and a "destroyed on" record with who signed
it off.

Why paid: records-retention reporting is a line item in every records
management product.

### P5. Support, signing, updates (effort: ~3 days plus a certificate)

- Code signing through Azure Trusted Signing (the release workflow already has
  the hook, unconfigured). Unsigned executables trip SmartScreen; nobody buys
  past that dialog. This is a prerequisite for charging at all.
- An update check on launch (reads the GitHub releases JSON, shows a banner;
  no auto-install).
- Support address and a stated response target for Pro licensees.

## Licensing mechanics

- **Offline license file** `ordosort.license` beside the shared `config.json`,
  so every station on the share is licensed at once. JSON payload signed with
  Ed25519; the public key ships in the app. Fields: licensee, edition, seats,
  issued, updates-until, license id.
- **Perpetual use, time-boxed updates.** Pro features never stop working. A
  build published after `updates-until` refuses the license for Pro features
  and says why, until renewed.
- **Seats** are distinct active stations in the last 24 hours (from P1). Over
  the limit: a warning for 14 days, then Pro features pause on the newest
  station. Never blocks filing.
- **Trial**: a built-in 30-day Pro trial from first launch, no key needed.
- **Free stays MIT.** Pro code lives in a private repo and ships as
  `OrdoSort.Pro.dll` loaded by the free app when present and licensed. Someone
  can strip the gate; the buyer here is an office, not a hobbyist, and the
  support relationship is part of what they pay for.

## Pricing

| Plan            | Price                         | Includes                              |
|-----------------|-------------------------------|---------------------------------------|
| Free            | $0                            | Everything shipped today              |
| Pro, per seat   | $149 one-time                 | All Pro features, 12 months of updates, email support |
| Pro renewal     | $39 per seat per year         | Another 12 months of updates and support |
| Team 5          | $599                          | 5 seats                               |
| Team 10         | $999                          | 10 seats                              |

Anchors: FileCenter $97 to $297 per user one-time with 25% maintenance;
Folderit $5 to $25 per user per month. $149 sits between FileCenter Standard
and Pro on a narrower but deeper feature set. Raise, do not lower, after the
first ten sales.

## Purchase flow

Lemon Squeezy as merchant of record (handles sales tax and VAT, no company
setup needed). Its order webhook calls a small route on ordosort.com (Vercel)
that signs and emails the license file. For the first customers, do it by
hand: an order email arrives, a script signs the file, you reply with it. The
webhook can come after the tenth sale.

## Go-to-market

- Pricing page and Pro section on ordosort.com; the download card stops
  saying "free to use" and says "free, with Pro for teams".
- Comparison pages: "OrdoSort vs FileCenter", "PaperPort alternative". These
  are the searches this buyer actually makes.
- Free listings on Capterra, G2 and AlternativeTo.
- The Microsoft Store as a second download channel (signed build required).
- Records-management and health-information communities (AHIMA forums,
  LinkedIn groups, r/healthIT, r/sysadmin) with the audit and no-cloud story.
- Reference customer: the operation the app was built in, with permission.

## Prerequisites and risks

- **Employer IP.** OrdoSort was built around one workplace's workflow. Before
  a single sale, confirm in writing that the employer has no claim on the code
  and no objection to selling it, and that no sample data from `docs/sample/`
  has ever left the machine. This is the one item that can stop the plan.
- **PHI stance.** The app never transmits documents; say so in plain words on
  the site and in the license. Decline BAA requests: there is nothing to sign
  one over.
- **Code signing** before charging (P5). Budget roughly $10 a month.
- **Support load.** Pro support is a promise; set the target at two business
  days and keep it.
- **Windows only.** Fine for this buyer. Say it clearly.
- **Reports hub specificity.** Do not promise it to a second customer until
  the feed readers are configurable.

## Sequence

1. **Week 1.** Employer IP answer. Code signing. License format, signer script,
   trial. Pricing page with "notify me" for Pro.
2. **Weeks 2 to 3.** P1 team audit and P4 retention. Both are small, both are
   what a manager sees first.
3. **Weeks 4 to 5.** P2 read-and-suggest. Launch Pro at the end of this
   phase with P1, P2, P4 and P5.
4. **Weeks 6 to 9.** P3 reports hub as the first Pro update, feed readers
   made configurable.

Roughly nine weeks of part-time work, five to first sale.

## Success and kill criteria

Ninety days after launch: ten paid seats or three paying organisations means
keep going and raise the price. Fewer means fold the Pro features into the
free app, keep the signed builds, and stop spending on it.

## Decisions needed from the owner

1. Open core with a proprietary Pro assembly, or relicense the whole repo to
   source-available? (Recommendation: open core.)
2. One-time per seat, or subscription? (Recommendation: one-time, as above.)
3. Is the employer question already answered?
4. Is the Reports hub a Pro feature, or is it too specific to sell at all?
5. Launch name: "OrdoSort Pro" or "OrdoSort Team"?
