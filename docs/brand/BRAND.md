# OrdoSort brand

What the brand is, and how to change it without breaking it. Set in the 2026-09 rebrand.

## Name and voice

| Item | Value |
|---|---|
| Name | OrdoSort. *Ordo* is Latin for order |
| Tagline | Every document, where it belongs. |
| Audience | Office staff who file scanned PDFs all day: front desks, records rooms, medical and legal offices |
| Voice | Plain and calm. Short sentences, everyday words, no hype |
| Sibling app | Box Labels (the standalone barcode label printer). Never mentions OrdoSort in its own UI |

## Mark

Three index dividers with stepped tabs, the front tab bronze: a drawer already sorted. Box Labels uses the same back tab behind an archive-box front with a handle cut-out.

| File | Use |
|---|---|
| `mark-ordosort.svg` | OrdoSort mark, 48-unit master |
| `mark-boxlabels.svg` | Box Labels mark, 48-unit master |
| `lockup-light.png` / `lockup-dark.png` | Mark plus wordmark, for light and dark pages (README banner) |
| `icon-review.png` | Every icon size on both Windows taskbar colours, actual pixels and 4× |

### Rules

| Rule | Why |
|---|---|
| No letters inside the icon | Windows 11 icon guidance; letters turn to noise at 16 px |
| Slate plate for OrdoSort, brass plate for Box Labels | Tells the two apps apart on one taskbar |
| Plates stay in luminance 0.14-0.26 | The only band that reaches 3:1 on both the light (#F3F3F3) and dark (#202020) taskbar |
| At every size, half the drawn pixels reach 3:1 on both taskbars | Windows 11 guidance; enforced by `BrandArtTests` |
| 16 px uses its own hand-tuned drawing | A scaled-down master goes soft at 16 px |
| Minimum size 16 px; clear space one tab width around the plate | Keeps the stepped tabs readable |
| Don't recolour, rotate, outline or add effects to the mark | The contrast rules above only hold for the drawn colours |

## Colour

The app's `ThemePalette.cs` is the source of truth; the website copies the same values in `ordosort.com/styles.css`.

| Token | Light | Dark | Role | Contrast on window |
|---|---|---|---|---|
| WindowBg | #F5F3EE | #1A1C1F | Page and window background | n/a |
| Surface | #FFFFFF | #24272B | Cards, inputs, grids | n/a |
| Text | #1C1F24 | #ECEAE5 | Body text | 14.9:1 / 14.2:1 |
| SubtleText | #585D66 | #A9ADB3 | Secondary text | 6.0:1 / 7.6:1 |
| Border | #C3BDB0 | #4A4E55 | Control borders | n/a |
| Accent | #24282E | #D6D2CA | Primary buttons (ink / paper) | n/a |
| AccentBronze | #7A5A26 | #D2AE6B | Brand accent: focus rings, tabs, links | 5.7:1 / 8.1:1 |

Every text pairing is held to 4.5:1 by `ThemeTests.TextPairs`. The choice in both apps and on the site is Auto (follow Windows), Light or Dark.

## Type

| Item | Value |
|---|---|
| Family | Atkinson Hyperlegible Next (Braille Institute), chosen because I, l, 1 and O, 0 never look alike |
| Weights shipped | Regular 400, SemiBold 600, Bold 700 |
| Version | 2.001, from github.com/googlefonts/atkinson-hyperlegible-next at commit 7925f50f649b3813257faf2f4c0b381011f434f1 |
| Licence | SIL Open Font License 1.1: `src/OrdoSort.Ui/Fonts/OFL.txt`, `ordosort.com/assets/fonts/OFL.txt`, and THIRD-PARTY-NOTICES |
| App | Bundled in `src/OrdoSort.Ui/Fonts` as the default UI font; Settings → App font can still pick Segoe UI Variable or others |
| Website | Self-hosted woff2 in `ordosort.com/assets/fonts` |
| Printed box labels | Unchanged (Segoe UI and Consolas): their size next to the barcode is fixed |

## Regenerating

The art is code: `tools/OrdoSort.Smoke/Brand/BrandArt.cs`. Everything below is generated from it.

- [ ] Edit the paths or colours in `BrandArt.cs`
- [ ] Run `dotnet run --project tools/OrdoSort.Smoke -- brand .` from the repo root
- [ ] Look at `docs/brand/icon-review.png` at actual size
- [ ] Run `check.bat`; `BrandArtTests` and `BrandAssetsTests` fail if the art breaks a rule or a committed file is stale

| Output | Made by the generator |
|---|---|
| `src/OrdoSort.Wpf/app.ico`, `src/BoxLabels.App/app.ico` | 8 frames, 16-256 px |
| `docs/brand/*.svg`, `*.png` | Marks, lockups, icon review |
| `ordosort.com/assets/favicon.svg`, `favicon-32.png`, `apple-touch-icon.png`, `icon-192.png`, `icon-512.png`, `og.png` | Website icons and social card |
| `ordosort.com/index.html` | Header mark between the `BRAND-MARK` comments |

`--check` reports text outputs that drifted, without writing anything. The in-app pictures are XAML (`src/OrdoSort.Ui/Theme/Illustrations.xaml`) and are drawn in the same style by hand.
