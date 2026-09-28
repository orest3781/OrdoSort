# Box label style and Box Labels settings — design

**Date:** 2026-09-28
**Status:** draft, for owner review

## Why

The owner wants box numbers readable on a shelf. They also want the standalone Box Labels app (`BoxLabels.exe`) to have a Settings menu, like OrdoSort has.

Today:
- Every label prints the code as one 26 pt line ("NGC 0000 0001", `BoxLabels.CodeFontSize`), about 0.23 in tall, between two date bars and above the barcode.
- `BoxLabels.exe` has no settings window. A bar across the top (`LabelStoreBar`) holds the shared-file path, "Change file…" and the theme.
- Inside OrdoSort the labels window follows OrdoSort's text size and font. Alone it can't change either.

## Decisions (owner, 2026-09-28)

| Question | Decision |
|---|---|
| What makes numbers readable | A choice of label styles; today's stays one of them |
| Style options | Layout (Standard, Big number, Huge running number), Show leading zeros (on/off), Date bars (black bars / plain text) |
| Box Labels Settings holds | Shared labels file, theme, text size and font, label style |
| Label style in OrdoSort | A new "Box labels" tab in OrdoSort's Settings |

**Assumptions stated to the owner and not objected to:**
- Label style is stored in the shared `box-labels.json`, so every station prints the same look.
- Theme, text size and font are per PC.
- The barcode always holds the full code and must still scan.
- Date bars moves out of the labels window into Settings.
- `LabelStoreBar` goes away.

## Requirements

### The label styles

All layouts keep the label at 4 × 2 in, 10 per US-letter sheet, the same sheet geometry and margins (`BoxLabels.SlotOrigin`, the 0.25 in top and bottom margins guarded by BoxLabelsTests), and both date lines (CREATED at the top, DESTROY AFTER at the bottom).

| Layout | Number text | Barcode | Client id |
|---|---|---|---|
| Standard | Today exactly: one centred line, `CodeFontSize` (≤ 26 pt) | Today exactly: 42 pt tall at y = 66 | Part of the number line |
| Big number | One centred line, as large as the label width allows | Shorter, never under 36 pt (0.5 in) | Part of the number line |
| Huge running number | Only the running number, as large as the space between the CREATED line and the barcode allows | Shorter, never under 36 pt | Small, above or beside the number, never overlapping it |

| Option | Effect | Default |
|---|---|---|
| Show leading zeros: on | Number text as today: "NGC 0000 4200" (Standard, Big), "0000 4200" (Huge) | ✅ |
| Show leading zeros: off | "NGC 4200" (Standard, Big), "4200" (Huge) | |
| Date bars: black bars | Today's black bars with white text | ✅ |
| Date bars: plain | Black text, no bars (today's "plain") | |

**Rules every layout keeps:**
- The barcode encodes the full code (`Code39.Encode(item.Code)`, e.g. `NGC00000001`) whatever the text shows. Its width, narrow bar width and quiet zones are unchanged. Only its height may shrink, and never below 36 pt.
- Text never runs past the label edges: font size is the smaller of the layout's largest size and what fits the width, as in `CodeFontSize` (Consolas is about 0.6 em per character).
- Nothing is drawn outside the label's 288 × 144 pt box, and no text overlaps a bar or another text.
- Dates stay invariant-culture `yyyy-MM-dd`.
- One routine draws every label (`BoxLabels.ComposeDrawing`). The on-screen preview, printing and Save PDF all replay it, as today.

**Expected readable size** (Consolas digits are about 0.64 em tall). These are targets, proven by drawing tests and by one printed test sheet the owner checks:

| Layout, zeros | Text at code 4200 | Digit height |
|---|---|---|
| Standard, on (today) | "NGC 0000 4200", 26 pt | ≈ 0.23 in |
| Big, on | "NGC 0000 4200", ≈ 34 pt (width-limited) | ≈ 0.30 in |
| Big, off | "NGC 4200", ≈ 55 pt, capped by height | ≈ 0.45 in |
| Huge, off | "4200", ≈ 75–80 pt | ≈ 0.65–0.7 in |

### Where the style is stored

`box-labels.json` (`BoxLabelsDoc`) gains two keys beside the existing `date_style`:

| Key | Values | Missing or unknown |
|---|---|---|
| `label_layout` | `"standard"`, `"big"`, `"huge"` | `"standard"` |
| `leading_zeros` | `true` / `false` | `true` |

An old file reads exactly as today's look, and unknown keys still round-trip (`Extras`). Writes go through `BoxLabelStore.Mutate`, the same safe shared write the label numbers use.

### The labels window (both apps)

- The "Date bars" radio buttons are removed from `LabelMakerWindow`, since they're set in Settings now.
- The preview and every print and save use the style read from the shared file:
  - when the window opens;
  - again in the same fresh read that claims numbers at print or save time, so a style another station just changed is what prints.
- The preview updates when the window regains focus after Settings closes.

### Box Labels app (`BoxLabels.exe`)

- **Menu bar:** File → "Change labels file…" and "Exit"; "Settings…" (Ctrl+,, as in OrdoSort).
- **`LabelStoreBar` is removed.** The labels file path is shown read-only in the window's status area, since knowing which shared file is in use matters.
- **Settings window, two tabs:**

| Tab | Controls | Saved to |
|---|---|---|
| General | Labels file (path + Change…), theme (Auto / Light / Dark), text size, font | `box-labels-app.json` beside the exe (per PC) |
| Label style | Layout, Show leading zeros, Date bars, and a live preview label drawn by `ComposeDrawing` | `box-labels.json` (shared) |

- **Text size and font** use OrdoSort's presets and font list, and the same key names: `ui_font_size` (0 = default, 6–72) and `ui_font_family`. They apply at start-up and on OK.
- **OK / Cancel** behave as in OrdoSort's Settings: OK saves and applies; Cancel changes nothing. Closing with unsaved edits asks "Discard / Keep editing".
- **When a save fails:**
  - Nothing that failed is put into use, and a message says which file couldn't be saved.
  - A failed shared-file write doesn't undo a General save that worked, and the message says so.

### OrdoSort

- **Settings gains a "Box labels" tab**, next to "Data files", which already points at the labels file. It holds the same Layout, Show leading zeros, Date bars and live preview as Box Labels' Label style tab: one shared control, not two copies.
- **On OK, the style is written to the shared file** after `config.json` saves. If that write fails, a message says "Label style not saved", and the other settings stay saved.
- **The text size, font and theme** of OrdoSort's labels window keep following OrdoSort's own settings, as today.

### Shared pieces

| Piece | Lives in | Used by |
|---|---|---|
| Label style model and drawing (`LabelStyle`, `ComposeDrawing`) | `OrdoSort.Core` | Everything that draws a label |
| Label style editor with live preview | `OrdoSort.Ui` | Box Labels' Label style tab, OrdoSort's Box labels tab |
| Text size and font choices | Wherever OrdoSort's Appearance tab gets them; moved to `OrdoSort.Ui` if needed so Box Labels can use them | Both Settings windows |

`ComposeDrawing`'s `string dateStyle` parameter is replaced by the style; the old parameter is removed, not kept beside it (one way to do it).

## Out of scope

- New label sizes, sheet formats or printers' own templates
- Colour printing; per-client styles
- Changing the barcode type (stays Code 39)
- The audit's other Box labels items (UX-07, UX-09, UX-23, UX-51), which stay in `docs/ux-audit-2026-09-28.md`

## Testing

| Area | Tests |
|---|---|
| Drawing (Core) | Per layout: the barcode is ≥ 36 pt tall and encodes the full code; nothing is outside 288 × 144; no text overlaps a bar or another text; the zeros on/off text; Standard with zeros on is identical to today's drawing (a regression pin); long client ids (8 letters) and 8-digit numbers still fit |
| Sheet | The 0.25 in top and bottom margins still hold for every layout (the existing BoxLabelsTests guards) |
| Storage | An old `box-labels.json` reads as Standard, zeros on; unknown values fall back; unknown keys round-trip; a style change survives a reload |
| Labels window | Preview, print and Save PDF use the stored style; a style changed in the file before printing is what prints; the Date bars radio is gone |
| Box Labels Settings | General saves per PC and applies text size and font; Label style saves to the shared file; Cancel changes nothing; a failed save is reported and nothing failed is applied |
| OrdoSort Settings | The Box labels tab saves the style to the shared file on OK; a failed shared write is reported without losing the other settings |
| Windows | Both Settings windows join `WindowOverflowTests` (18 px at minimum width), `AccessibleNameTests` and the Tab walk |
| By hand | One printed test sheet per layout, scanned with a hand scanner, and read from shelf distance by the owner |

## Success criteria

- [ ] With Huge running number and zeros off, a box's number is readable from shelf distance (owner's printed check)
- [ ] Every layout's barcode scans on the owner's scanner
- [ ] An existing install prints exactly as before until someone picks a new style
- [ ] `BoxLabels.exe` has Settings covering the labels file, theme, text size, font and label style
- [ ] OrdoSort's Settings has a Box labels tab; both apps print the same style from the same shared file
