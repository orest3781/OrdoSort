# Tools polish — design

**Date:** 2026-07-31
**Status:** Designs user-approved during the 2026-07-30 walkthrough (Unlock
option C chosen from mockups; segment-delete option B chosen from mockups;
date-bar toggle requested directly). Spec written during the overnight
session for morning ratification; sub-project 4 of 4 in the
workflow-refinement program (config split ✅ → filing/routing ✅ →
dashboard ✅ → **tools polish**).

## Context

Three tool refinements from the walkthrough remain, plus four accepted
Box-labels follow-ups from the config-split reviews that live in the same
code and belong in the same wave.

## 1. Unlock PDFs — redesign (walkthrough option C)

Current: file list, password box + Show, a `Saved:` picker, a standing
"Remember this password as:" row. New:

- **One password box** (+ Show) and the file list. The `Saved:` picker and
  the standing remember-as row are removed.
- **Saved passwords are tried automatically** on Unlock: the typed password
  (if any) is tried first, then every saved password, per file. The
  existing per-file outcomes (unlocked / wrong password / not encrypted)
  are unchanged.
- **Post-success save banner**: after a run where the TYPED password
  unlocked at least one file and it isn't already saved, a banner appears:
  `✓ N unlocked with a new password — save it as: [name] [Save]`. Saving
  adds it to the saved list (DPAPI on next config save, as today). The
  banner clears on the next run or on Close. This keeps today's rule —
  a password is saved only when it unlocks something — now made visual.
- **Manage saved… dialog**: a small modal owned by the Unlock window
  holding the saved-passwords manager exactly as it exists on the Settings
  page today (name + password boxes, Add/Remove, the DPAPI note). Saving
  passwords still lands in `config.json`'s `saved_passwords` (per-machine
  DPAPI — never in a shared file).
- **Settings page**: the saved-passwords section AND the Unlock explainer
  leave the sixth tab; the tab header renames `Tools & data` → `Data files`
  (the Data files section is all that remains).

## 2. Bulk rename — segment deletion (walkthrough option B)

- New transform control in the transform row: `Delete segment:` followed by
  checkboxes `1 2 3 4 last` (multi-select).
- Segments = the filename STEM split on `-` (single hyphen). Deletion is
  position-based (1-indexed; `last` = the final segment, whatever its
  index). A file whose stem has fewer segments than a checked position is
  unaffected by that position; `last` never removes the only segment
  (a one-segment stem is unaffected entirely).
- Pipeline order (Core `BulkRename`): review rename → **segment delete** →
  find/replace → affixes → case. Extensions never change; the live preview
  grid reflects results as always; hand-edited rows keep overriding.
- Removing segments re-joins the remainder with `-`.
- **Empty segments are real segments**: `a--b` splits to `[a, "", b]` —
  splitting WITHOUT removing empties and re-joining with `-` reconstructs
  the original exactly when nothing is deleted (lossless round trip, `--`
  markers included). Deleting position 2 of `a--b` removes the empty
  segment, yielding `a-b`.

## 3. Box labels — date-bar style toggle

- New option in the Box labels window: `Date bars:` radio pair —
  `◉ black bars (white text)` / `○ plain (black text, no bars)`.
- Stored in `box-labels.json` as `"date_style": "bars" | "plain"`
  (top-level key beside `label_clients`; default `"bars"` = today's look;
  unknown values read as `"bars"`). Org-wide consistent labels: every
  station renders the same style. Written through `BoxLabelStore` like all
  label mutations.
- The live card preview, the in-app print rendering, and the PDF export
  all honor the style. Plain style: created/destruction dates render as
  black text on white with the same layout box the bars occupied.

## 4. Folded-in Box-labels follow-ups (from config-split reviews)

- **Per-client Persist merge**: the label window's whole-list save no
  longer blind-overwrites. `Persist()` merges against the fresh on-disk
  doc: clients the window added/edited/removed are applied by id, but a
  DIFFERENT client's `next_number` advanced meanwhile by another station
  is preserved (an edit to a client the window did NOT touch keeps the
  disk value). Window-close with zero edits writes nothing.
- **HResult filtering in BoxLabelStore**: only sharing violations
  (HRESULT 0x80070020 ERROR_SHARING_VIOLATION / 0x80070021 ERROR_LOCK_VIOLATION)
  are retried as contention; other IOExceptions (disk full, dropped share)
  fail immediately with the actual message, not "another station…".
- **ClaimNumbers off the UI thread in Save-PDF**: the claim joins the
  existing `_scheduler.Run` offload that already wraps the PDF render.
  (`Print()` stays synchronous — it's inherently modal.)
- **Ceiling friendliness**: `ClaimNumbers` checks `start + count - 1 ≤
  BoxLabels.MaxNumber` on the FRESH counter inside the mutation and
  fails with a readable warning ("this batch would pass label
  99 999 999 — reset or renumber the client") WITHOUT consuming numbers.

## Non-goals

- Any change to unlock outcomes, bulk-rename find/replace/affix/case
  semantics, or label layout beyond the date-bar style.
- Sharing `saved_passwords` (stays per-machine DPAPI in config.json).
- UI/UX visual polish (the next program phase) and v1.0.0 tagging.

## Testing

- Wpf (headless, Unlock): auto-try order (typed first, then saved);
  banner appears only when the typed password unlocked ≥1 file and isn't
  already saved; Save adds it; banner clears next run; manage-dialog VM
  add/remove round-trips to config.
- Core (BulkRename): segment deletion positions incl. `last`, short stems,
  one-segment stems, re-join hygiene, pipeline order vs find/replace.
- Core (BoxLabels/store): `date_style` round-trip + unknown-value fallback;
  Persist merge preserves a concurrent counter advance on an untouched
  client; HResult filter retries sharing violations and fails fast on
  other IO errors; ceiling check refuses without consuming.
- Wpf (Settings): the sixth tab renamed with passwords/explainer gone and
  Data files intact.
- Baseline 639 (Core 352 + Wpf 287) grows only by additions plus
  sanctioned updates to tests touching the removed Unlock/Settings
  surfaces.

## Delivery

Directly on `main` (established), commits per task, push after the full
gate. Spec awaits the user's morning read — raise anything to change and
a follow-up commit adjusts; the underlying designs were approved
interactively during the walkthrough.
