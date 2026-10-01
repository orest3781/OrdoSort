# Photos, GIFs and videos in the filing loop — design

Owner request, 2026-09-29: "a filing loop that can power through photos, gifs, videos, name and sort them. I need to be able to quickly scrub through a video". Builds on the filing loop (`2026-09-29-filing-loop-design.md`): read-ahead, show-before-move and the background filing queue are reused as they are.

## Owner decisions (2026-09-29)

| Question | Decision |
|---|---|
| How a photo or video is named | Date taken + typed name: `20260929-SMITH-KITCHEN.jpg` |
| Where it goes | The same routes and hotkeys as PDFs |
| Inbox | One inbox and one session; PDFs, photos, GIFs and videos mixed, in whatever order the inbox sorts them |
| Video player | Bundled VLC engine (LibVLCSharp), so iPhone `.mov` (HEVC), `.avi` and `.mkv` play without extra codecs |
| How a video starts | Paused on its first frame, muted |
| Subfolder | One `Media` folder inside the route folder, by default |
| Live photos (a `.heic` and a `.mov` with one name) | Owner unsure: filed as two separate items for now; revisit after using it |
| More file types | Owner unsure: the table below is the default, editable in Settings |

## Goal

| Measure | Target |
|---|---|
| Key press → next photo or GIF on screen, name box focused | Under 100 ms, as for PDFs |
| Key press → next video's first frame on screen | Under 300 ms when staged; under 1 s off the share |
| Scrub a video without leaving the name box | Every scrub action has a key that works while typing, plus the mouse |
| Filing safety | Unchanged: every move logged, every failure reported, Undo works |

## Today: where the loop assumes PDF

| Part | Code | Today | Change |
|---|---|---|---|
| Inbox scan | `Scanner.Eligible` | `.pdf` only (insert mode: only names with `--`) | Also the media types turned on in Settings, in every naming mode |
| Naming | `Naming.BuildTarget`, `Naming.PdfExt` | Always writes `.pdf` | Keeps the file's own extension; media files use the dated rule below |
| Page size | `ShellViewModel` → `PageShape.SizeOf` | Read once per session from the first PDF | Skipped for media; a photo or video fits the pane by its own size |
| Preview | `IPdfViewer` / `WebViewPdfViewer` | Edge shows the PDF | Becomes `IPreview` with three hosts: Edge for PDFs, Edge for photos and GIFs, VLC for video |
| Read-ahead | `DocumentStage` | Current + next 2, files over 200 MB not staged | Unchanged rules; large videos play off the share with a bigger read buffer |
| Page-size fit | `FitViewerToCurrentAsync` | Measures the session's first document | Measures the first PDF, so a session that starts on a photo still fits its PDFs |

## Design

### 1. File types

| Kind | Extensions (on by default) | Preview |
|---|---|---|
| Photo | `.jpg .jpeg .png .webp .bmp .heic .heif` | Edge, fitted to the pane, EXIF rotation honoured |
| Animated | `.gif` (and animated `.webp`) | Edge; plays and loops |
| Video | `.mp4 .mov .m4v .avi .mkv .webm .wmv .3gp` | VLC |
| Camera RAW | `.cr2 .cr3 .nef .arw .dng .raf .orf` (off by default) | The JPEG preview embedded in the file; "No preview" if there is none |

- Settings gets a **File types** section: a tick box per kind and an editable list of extensions per kind.
- HEIC: Edge can't show it. It is decoded through Windows' own imaging (WIC) into a JPEG in a preview folder of the app's own (made ahead for the next two photos, deleted when the app closes). Windows needs Microsoft's *HEIF Image Extensions* and *HEVC Video Extensions* for this; without them the pane says so, and filing still works.
- Anything else in the inbox is still ignored and counted, as today.

### 2. Naming: date taken + typed name

The rule for photos and videos, in every naming mode:

`<date taken>-<TYPED NAME><route suffix><counter><original extension>`

| Case | Result |
|---|---|
| Typed "SMITH KITCHEN" on `IMG_4031.HEIC` taken 2026-09-29 | `20260929-SMITH KITCHEN.heic`, cased and cleaned as typed names are for PDFs |
| Name box blank | The original name is kept, as blank does for PDFs |
| Name clashes at the destination | ` (2)`, ` (3)` before the extension, as for PDFs |
| The typed name starts with 8 digits that are a real date | That date replaces the date taken (the one way to correct a wrong camera clock) |

Where the date comes from, first found wins (EXIF's plain `DateTime` is left out: it is when the file was last edited):

| # | Source | Covers |
|---|---|---|
| 1 | EXIF `DateTimeOriginal` | JPEG, HEIC, PNG, WebP, RAW |
| 2 | QuickTime/MP4 creation time (stored in UTC, shown in local time) | `.mov .mp4 .m4v .3gp` |
| 3 | A date in the file name (`IMG_20260929_…`, `PXL_20260929…`, `2026-09-29 …`, `VID-20260929-WA…`) | WhatsApp, Android, screenshots |
| 4 | The file's last-modified time | GIFs and everything else |
| — | Today, marked "couldn't read the photo's date" | A file that can't be read at all (a share down). Never kept: the move reads it again |

- The date and where it came from show above the name box: `29 Sep 2026 · from the photo` / `· from the file name` / `· from the modified date`. The last is in amber, because a copied file's modified date is often the copy date.
- Metadata is read with **MetadataExtractor** (Apache-2.0; one library for EXIF, HEIC, RAW and QuickTime), off the UI thread, as part of the read-ahead, so it is ready before the file is on screen.
- Implemented as a pure `MediaNaming` in Core beside `Naming`, with the date read behind an interface so every rule is unit-tested without real photos.

### 3. Where it goes

- The same routes and hotkeys (`Ctrl+1`…) as PDFs.
- New Settings option, **Photos and videos go into**: `a Media subfolder` (default) / `the route folder itself` / `Photos or Videos subfolders`. A subfolder is created when missing.

### 4. Video: playing and scrubbing

The video is shown **paused on its first frame, muted**, as soon as it is on screen. You can name and file it without touching the video at all; `Alt+K` plays it.

Keys work while the cursor is in the name box. Plain arrows, Space and letters stay free for typing; every video key uses Alt. (Not `Alt+Space`: Windows keeps that for the window menu.) The Processing window handles these before WPF's access keys see them, and none of its buttons use an Alt access key today:

| Key | Does |
|---|---|
| `Alt+K` | Play / pause |
| `Alt+←` / `Alt+→` | Back / forward 5 s |
| `Alt+Shift+←` / `Alt+Shift+→` | Back / forward 30 s |
| `Alt+,` / `Alt+.` | One frame back / forward (pauses) |
| `Alt+J` / `Alt+L` | Rewind / fast forward, faster on each press (1×, 2×, 4×, 8×), as J/K/L in video editors; `Alt+K` pauses and resets to 1×. VLC can't play backwards, so rewind is a quick series of jumps back |
| `Alt+1` … `Alt+9` | Jump to 10 % … 90 %; `Alt+0` to the start (top-row digits: Alt with the number pad types an Alt-code character) |
| `Alt+M` | Sound on / off (stays for the session) |

With the mouse:

| Action | Does |
|---|---|
| Click or drag on the timeline | Jumps / scrubs, showing frames as you drag |
| Mouse wheel over the video | 2 s per notch (Shift: 10 s) |
| Hover over the timeline | Time under the pointer |

- The timeline sits under the video: elapsed / length, a played bar, and a **filmstrip** of 10 frames across the whole video, so a long clip can be judged at a glance and a click on a frame jumps there.
- The filmstrip is made in the background after the video is shown (a second, silent VLC player taking snapshots). Until it is ready the timeline is plain; it never delays the first frame.
- Filing a video stops it and releases the file before the move.
- Keys are set in code first; making them configurable is under Next.

### 5. The video engine

| Item | Design |
|---|---|
| Packages | `LibVLCSharp.WPF` + `VideoLAN.LibVLC.Windows` (x64 only) |
| Size | 42 MB beside the exe (about 110 MB before trimming), in a `libvlc` folder: the plugins are a whitelist in `OrdoSort.Wpf.csproj` (file access, the common containers, avcodec/dav1d and friends, hardware decoding, video and audio output, filters) |
| Licence | libVLC and LibVLCSharp are LGPL-2.1: loaded as separate DLLs, never merged into the app; THIRD-PARTY-NOTICES gets both licences and the source links. Any plugin under GPL is left out when trimming |
| Start-up | libVLC is loaded when a session has a video queued, not at app start, so a PDF-only session costs nothing. Measured on the owner's PC: engine 145 ms, first frame 219 ms after that |
| Drawing over the video | VLC draws into its own window, which WPF can't paint on top of (the same "airspace" limit Edge has). Nothing in the pane overlaps the video; the timeline, the date line and the filed bar sit outside it |
| Off the share | Large videos play straight off the share with VLC's read buffer raised (`file-caching` about 1.5 s) |
| Hardware decoding | On (`avcodec-hw=d3d11va`), with software decoding as VLC's own fallback |
| A video that won't play | The pane says "Can't play this video" with the reason; the name box still works and the file can be filed or set aside |

### 6. What stays the same

- Set aside (`Ctrl+K`), Undo (`Ctrl+Shift+Z`), name suggestions, the filed bar, files left, the order indicator and the session lock all work unchanged for every kind.
- History logs photos and videos like PDFs, with the extension in the file name.
- The PDF loop's behaviour and speed are unchanged. The existing PDF tests must pass untouched.

## Phases

| Phase | Delivers | Status |
|---|---|---|
| 1 | File types in Settings; mixed inbox; photos, GIFs and HEIC in the pane; date-taken naming; the subfolder option | Built on `feature/media-loop` (2026-09-29) |
| 2 | VLC video: play, all scrub keys, timeline, mouse wheel, speed, mute | Built on `feature/media-loop` (2026-09-30) |
| 3 | The filmstrip; camera RAW previews | Not started |

Each phase is its own branch and release, checked on Windows with `check.bat`, E2E and a live run on real phone photos and videos before merging.

## Testing

- [ ] `MediaNaming`: every date source and fallback, the typed-date override, blank names, clashes, extensions kept, route suffixes
- [ ] File-name date patterns: iPhone, Android (`IMG_`, `PXL_`, `VID_`), WhatsApp, screenshots, and look-alikes that aren't dates
- [ ] Scanner: a mixed inbox in each naming mode; a turned-off type is ignored and counted
- [ ] Metadata read on real sample files (a small set of JPEG, HEIC, MOV, MP4, GIF committed under `tests/fixtures/media`, a few KB each)
- [ ] Video keys: each key's effect on the player's position and state, through a fake player behind an `IVideoPlayer` interface
- [ ] Filing a video releases it first (the move never meets an open file)
- [ ] The PDF loop's own suites pass untouched

## Open questions for the owner

- [x] Video start: paused on the first frame, muted
- [x] Subfolder default: one `Media` folder
- [ ] Live photos filed together: undecided; separate for now
- [ ] More file types: undecided; the default table, editable in Settings

## Next (not in these phases)

- Configurable video keys in Settings
- Rotate a photo before filing
- Auto-sort by date without looking (the "sort-by-rules" window)
- Trim a video before filing
