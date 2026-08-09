# ordosort.com Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the static OrdoSort landing page in `ordosort.com/`, deployed on Vercel, per `docs/superpowers/specs/2026-08-08-website-design.md`.

**Architecture:** One hand-written HTML page + one stylesheet + committed image assets. Theming is CSS custom properties switched by `prefers-color-scheme`. Screenshots come from the repo's existing smoke tool. No build step; Vercel serves the folder as-is.

**Tech Stack:** HTML5, CSS3, inline SVG. PowerShell for image checks. Playwright browser tools (MCP) for rendering verification. Vercel for hosting.

## Global Constraints

Every task's requirements implicitly include all of these.

- **≤2KB hand-written vanilla JS (`site.js`), no frameworks, no external requests.** *(2026-08-09, owner-approved: relaxes the original "zero JavaScript" constraint below to allow a small hand-written script — the 7-scheme switcher and scroll reveals — with no build step and no third-party code.)*
- **Zero external requests.** No CDN fonts, no analytics, no third-party anything. `font-family: system-ui, -apple-system, "Segoe UI", sans-serif`.
- **Palette is verbatim** from the spec (app's `ThemePalette.cs`):
  | Token | Light | Dark |
  |---|---|---|
  | `--bg` | `#F7F8F9` | `#1A1C1F` |
  | `--surface` | `#FFFFFF` | `#26292D` |
  | `--text` | `#171A1F` | `#E9EBEE` |
  | `--subtle` | `#545A63` | `#A8ADB4` |
  | `--border` | `#BAC0C8` | `#4C525A` |
  | `--accent` | `#2D323A` | `#CDD2DA` |
  | `--accent-text` | `#FFFFFF` | `#171A1F` |
  | `--bronze` | `#8C6D3F` | `#C9A96A` |
- Light is the default (`:root`); dark overrides inside `@media (prefers-color-scheme: dark)`.
- All rendered text pairings ≥ 4.5:1 in both themes.
- **No live release links.** The download CTA is a "coming soon" state everywhere until v1.0.0 (flip spots are marked with HTML comments).
- Product tone; "Free while in development" appears only in the Download card. No pricing, no email capture.
- 4 px border radii, bronze focus rings (`outline: 2px solid var(--bronze)`), `scroll-behavior: smooth` guarded by `prefers-reduced-motion`.
- All files LF line endings (repo is LF-only).
- Commits touch only `ordosort.com/` and use the repo's conventional style (`feat(web): …`).
- Screenshot assets stay under ~450 KB each and ≤ 1600 px wide.
- Model hints for SDD dispatch (user's frugality policy): Task 1 = sonnet (visual judgment); Tasks 2–5 = haiku (transcription-grade, exact code below); Task 6 = sonnet (judgment); Task 7 = **main session** (interactive Vercel MCP auth — do not dispatch a subagent).
- Reviewers are dispatched **read-only** (user policy; orchestrator checks `git status` after each task).

## File Structure

```
ordosort.com/
  index.html          the whole page (inline SVG mark; sections per spec)
  styles.css          tokens + all layout/component styles
  robots.txt          allow all
  vercel.json         security + cache headers
  assets/
    favicon.svg       theme-aware SVG favicon
    favicon-32.png    PNG fallback favicon
    apple-touch-icon.png  180×180
    og.png            1200×630 social card
    hero-light.png / hero-dark.png            from MainWindow-ready-*
    shot-settings-light.png / -dark.png       from Settings-*
    shot-labels-light.png / -dark.png         from LabelMaker-*
    shot-history-light.png / -dark.png        from History-*
```

---

### Task 1: Screenshot assets

**Files:**
- Create: `ordosort.com/assets/hero-light.png`, `hero-dark.png`, `shot-settings-light.png`, `shot-settings-dark.png`, `shot-labels-light.png`, `shot-labels-dark.png`, `shot-history-light.png`, `shot-history-dark.png`

**Interfaces:**
- Produces: exactly the eight PNG filenames above (Tasks 3–4 reference them verbatim), plus each image's pixel width/height reported in the task summary (Task 4 writes them into `width=`/`height=` attributes).

- [ ] **Step 1: Build the demo workbench** (from the repo root — cwd matters, the tool resolves `demo-full` from it)

```powershell
cd S:\OrdoSort
dotnet run --project tools/OrdoSort.Smoke -- demo-full
```

Expected: a printed summary of the generated workbench under `demo-full\`. If Windows Smart App Control blocks the built assembly, retry with `dotnet run --project tools/OrdoSort.Smoke -p:Deterministic=false -- demo-full`.

- [ ] **Step 2: Render the screenshots**

```powershell
cd S:\OrdoSort
dotnet run --project tools/OrdoSort.Smoke -- screenshots $env:TEMP\ordosort-shots both
```

Expected: **exit code 1 even on success** — the runner counts its unconditional WebView2 NOTE as a FAIL (known quirk; do not "fix" it). Success criteria instead: the output ends with the SCREENSHOTS summary, and `$env:TEMP\ordosort-shots` contains `MainWindow-ready-light.png`, `MainWindow-ready-dark.png`, `Settings-light.png`, `Settings-dark.png`, `LabelMaker-light.png`, `LabelMaker-dark.png`, `History-light.png`, `History-dark.png`. SKIP lines mentioning **other** windows are fine; a SKIP naming one of these eight is a failure to investigate.

- [ ] **Step 3: Visually inspect the eight picks**

Open each of the eight PNGs with the Read tool. Each must show a fully rendered window (no 0×0, no blank gray body). Fallback substitutions if one is broken: `MainWindow-ready` → `MainWindow-done`; `Settings` → `MatchMerge`; `LabelMaker` → `BulkRename`; `History` → `Triage`. Report any substitution prominently in the task summary (Task 4's alt text must then be adjusted by the orchestrator).

- [ ] **Step 4: Copy into the site under canonical names**

```powershell
New-Item -ItemType Directory -Force S:\OrdoSort\ordosort.com\assets
$m = @{ 'MainWindow-ready' = 'hero'; 'Settings' = 'shot-settings'; 'LabelMaker' = 'shot-labels'; 'History' = 'shot-history' }
foreach ($k in $m.Keys) { foreach ($t in 'light','dark') {
  Copy-Item "$env:TEMP\ordosort-shots\$k-$t.png" "S:\OrdoSort\ordosort.com\assets\$($m[$k])-$t.png"
} }
```

- [ ] **Step 5: Check dimensions and weight; downscale only if needed**

```powershell
Add-Type -AssemblyName System.Drawing
Get-ChildItem S:\OrdoSort\ordosort.com\assets\*.png | ForEach-Object {
  $img = [System.Drawing.Image]::FromFile($_.FullName)
  "{0}  {1}x{2}  {3:N0} KB" -f $_.Name, $img.Width, $img.Height, ($_.Length/1KB)
  $img.Dispose()
}
```

Record each width×height in the task summary. For any file > 1600 px wide or > 450 KB, downscale in place:

```powershell
Add-Type -AssemblyName System.Drawing
$src = 'S:\OrdoSort\ordosort.com\assets\NAME.png'; $maxW = 1600
$img = [System.Drawing.Image]::FromFile($src)
$w = $maxW; $h = [int]($img.Height * $maxW / $img.Width)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($img, 0, 0, $w, $h); $img.Dispose()
$tmp = "$src.tmp.png"; $bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $g.Dispose()
Move-Item -Force $tmp $src
```

(Re-record dimensions after any downscale.)

- [ ] **Step 6: Commit**

```powershell
cd S:\OrdoSort
git add ordosort.com/assets/*.png
git commit -m "feat(web): screenshot assets for ordosort.com, both themes"
```

---

### Task 2: Favicons

**Files:**
- Create: `ordosort.com/assets/favicon.svg`, `ordosort.com/assets/favicon-32.png`, `ordosort.com/assets/apple-touch-icon.png`

**Interfaces:**
- Produces: the three favicon files above; Task 3's `<head>` references them verbatim. The mark's three-diamond geometry (`M32 6 L8 18 l24 12 24-12z` stepped +16 y twice) is duplicated inline in Task 3's header and Task 5's OG card — same shape everywhere.

- [ ] **Step 1: Write the SVG favicon**

Create `ordosort.com/assets/favicon.svg`:

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <style>
    .s { fill: #2D323A } .b { fill: #8C6D3F }
    @media (prefers-color-scheme: dark) { .s { fill: #CDD2DA } .b { fill: #C9A96A } }
  </style>
  <path class="s" opacity=".55" d="M32 38 8 50l24 12 24-12z"/>
  <path class="b" d="M32 22 8 34l24 12 24-12z"/>
  <path class="s" d="M32 6 8 18l24 12 24-12z"/>
</svg>
```

- [ ] **Step 2: Write the PNG-generation scratch page**

Create `%TEMP%\favicon-render.html` (scratch, not committed):

```html
<!doctype html><meta charset="utf-8">
<body style="margin:0;background:#1A1C1F">
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="180" height="180" style="display:block;padding:0">
  <path fill="#CDD2DA" opacity=".55" d="M32 38 8 50l24 12 24-12z"/>
  <path fill="#C9A96A" d="M32 22 8 34l24 12 24-12z"/>
  <path fill="#CDD2DA" d="M32 6 8 18l24 12 24-12z"/>
</svg>
</body>
```

- [ ] **Step 3: Capture the PNGs with the Playwright browser tools**

1. `browser_navigate` to `file:///C:/Users/stoic/AppData/Local/Temp/favicon-render.html` (adjust to the actual `%TEMP%` path).
2. `browser_resize` to 180×180, then `browser_take_screenshot` (viewport, PNG) and save the result as `ordosort.com/assets/apple-touch-icon.png`.
3. Edit the scratch file's svg `width`/`height` to 32, `browser_navigate` again (reload), `browser_resize` to 32×32, screenshot → `ordosort.com/assets/favicon-32.png`.

If the screenshot tool can only save into its own output directory, copy the files into `ordosort.com/assets/` afterwards with `Copy-Item`.

- [ ] **Step 4: Verify**

Read all three files with the Read tool: the SVG opens as text with both palettes present; both PNGs show the three-diamond mark on the dark background at the right size (confirm with the Step-5 dimension script from Task 1: 32×32 and 180×180).

- [ ] **Step 5: Commit**

```powershell
cd S:\OrdoSort
git add ordosort.com/assets/favicon.svg ordosort.com/assets/favicon-32.png ordosort.com/assets/apple-touch-icon.png
git commit -m "feat(web): favicons — theme-aware SVG mark + PNG fallbacks"
```

---

### Task 3: Page skeleton, theming, header, hero, how-it-works, footer

**Files:**
- Create: `ordosort.com/index.html`, `ordosort.com/styles.css`

**Interfaces:**
- Consumes: `assets/hero-light.png`, `assets/hero-dark.png`, the three favicon files (Tasks 1–2).
- Produces: a complete valid page with `<main>` containing sections `#hero` and `#how`, and a `<footer>`. Task 4 inserts its sections **between `</section><!-- /how -->` and `<footer`**. CSS class vocabulary Task 4 reuses: `.container`, `.card`, `.btn`, `.btn-soon`, `.section-title` (Task 4 defines its own `.shot`, `.feature-grid`, `.gallery-grid`, `.download-card`, `.req`).

- [ ] **Step 1: Write index.html**

Create `ordosort.com/index.html` exactly:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>OrdoSort — Every document, where it belongs</title>
  <link rel="icon" href="assets/favicon.svg" type="image/svg+xml">
  <link rel="alternate icon" href="assets/favicon-32.png" type="image/png" sizes="32x32">
  <link rel="apple-touch-icon" href="assets/apple-touch-icon.png">
  <link rel="stylesheet" href="styles.css">
</head>
<body>
<header class="site-header">
  <div class="container header-row">
    <a class="brand" href="#top" aria-label="OrdoSort — home">
      <!-- BRAND MARK: swap this inline SVG (and assets/favicon.svg) when final logo art lands -->
      <svg viewBox="0 0 64 64" width="26" height="26" aria-hidden="true" class="mark">
        <path class="m-steel" opacity=".55" d="M32 38 8 50l24 12 24-12z"/>
        <path class="m-bronze" d="M32 22 8 34l24 12 24-12z"/>
        <path class="m-steel" d="M32 6 8 18l24 12 24-12z"/>
      </svg>
      <span class="wordmark">OrdoSort</span>
    </a>
    <nav class="site-nav" aria-label="Page">
      <a href="#features">Features</a>
      <a href="#screenshots">Screenshots</a>
      <a href="#download">Download</a>
    </nav>
  </div>
</header>

<main id="top">
  <section id="hero" class="hero">
    <div class="container">
      <h1>Every document,<br>where it belongs.</h1>
      <p class="lede">OrdoSort watches your inbox folder. When a PDF arrives, you type
      the name it should carry and press a destination key — it's renamed, moved, and
      written to an audit log that always knows where things went.</p>
      <div class="cta-row">
        <!-- v1.0 FLIP (1 of 2): at release, replace the span below with
             <a class="btn btn-primary" href="https://github.com/orest3781/OrdoSort/releases/latest">Download v1.0</a> -->
        <span class="btn btn-soon">v1.0 coming soon</span>
        <a class="cta-secondary" href="#screenshots">See it in action ↓</a>
      </div>
      <picture class="hero-shot">
        <source srcset="assets/hero-dark.png" media="(prefers-color-scheme: dark)">
        <img src="assets/hero-light.png"
             alt="The OrdoSort main window: a live inbox count and monitored-folder tiles, ready to file"
             width="HERO_W" height="HERO_H">
      </picture>
    </div>
  </section>

  <section id="how" class="how">
    <div class="container">
      <h2 class="section-title">How it works</h2>
      <ol class="steps">
        <li class="card">
          <span class="step-n">1</span>
          <h3>Arrives</h3>
          <p>A PDF lands in your inbox folder — scanned, exported, dropped. OrdoSort is
          watching, and new arrivals join the queue live.</p>
        </li>
        <li class="card">
          <span class="step-n">2</span>
          <h3>Name it</h3>
          <p>Type the name it should carry — autocomplete ranks your history by recency
          and frequency — and press one destination key.</p>
        </li>
        <li class="card">
          <span class="step-n">3</span>
          <h3>Accounted for</h3>
          <p>Renamed, moved, and logged. The audit history answers <em>where did that
          go, and when</em> — and it's backed up daily.</p>
        </li>
      </ol>
    </div>
  </section><!-- /how -->

  <footer class="site-footer">
    <div class="container footer-row">
      <p>© 2026 OrdoSort · Office workflow automation</p>
      <a href="https://github.com/orest3781/OrdoSort">GitHub</a>
    </div>
  </footer>
</main>
</body>
</html>
```

Replace `HERO_W`/`HERO_H` with the hero image's real pixel dimensions from Task 1's summary (e.g. `width="1280" height="860"`).

**Note:** the footer sits inside `<main>` only until Task 4 inserts its sections; Task 4's instructions move it out. (Valid HTML either way; the final page has `<footer>` after `</main>`.)

- [ ] **Step 2: Write styles.css**

Create `ordosort.com/styles.css` exactly:

```css
/* ordosort.com — tokens are the app's ThemePalette.cs, verbatim */
:root {
  --bg: #F7F8F9; --surface: #FFFFFF; --text: #171A1F; --subtle: #545A63;
  --border: #BAC0C8; --accent: #2D323A; --accent-text: #FFFFFF; --bronze: #8C6D3F;
  color-scheme: light dark;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #1A1C1F; --surface: #26292D; --text: #E9EBEE; --subtle: #A8ADB4;
    --border: #4C525A; --accent: #CDD2DA; --accent-text: #171A1F; --bronze: #C9A96A;
  }
}

* { box-sizing: border-box; margin: 0; }
html { scroll-behavior: smooth; }
@media (prefers-reduced-motion: reduce) { html { scroll-behavior: auto; } }

body {
  background: var(--bg); color: var(--text);
  font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
  line-height: 1.6;
}
.container { max-width: 1080px; margin: 0 auto; padding: 0 20px; }
img { max-width: 100%; height: auto; display: block; }
a { color: var(--bronze); }
a:focus-visible, .btn:focus-visible {
  outline: 2px solid var(--bronze); outline-offset: 2px; border-radius: 4px;
}

/* header */
.site-header {
  position: sticky; top: 0; z-index: 10;
  background: color-mix(in srgb, var(--bg) 86%, transparent);
  backdrop-filter: blur(8px);
  border-bottom: 1px solid var(--border);
}
.header-row { display: flex; align-items: center; justify-content: space-between; height: 56px; }
.brand { display: flex; align-items: center; gap: 10px; text-decoration: none; color: var(--text); }
.wordmark { font-weight: 700; font-size: 1.1rem; letter-spacing: .02em; }
.m-steel { fill: var(--accent); } .m-bronze { fill: var(--bronze); }
.site-nav { display: flex; gap: 22px; }
.site-nav a { color: var(--subtle); text-decoration: none; font-size: .95rem; }
.site-nav a:hover { color: var(--bronze); }

/* buttons */
.btn {
  display: inline-block; padding: 10px 22px; border-radius: 4px;
  font-weight: 600; text-decoration: none;
}
.btn-primary { background: var(--accent); color: var(--accent-text); }
.btn-soon {
  background: var(--surface); color: var(--subtle);
  border: 1px dashed var(--border); cursor: default;
}

/* hero */
.hero { padding: 72px 0 56px; text-align: center; }
.hero h1 { font-size: clamp(2.2rem, 5vw, 3.4rem); line-height: 1.15; letter-spacing: -.01em; }
.lede { max-width: 640px; margin: 20px auto 0; font-size: 1.12rem; color: var(--subtle); }
.cta-row { display: flex; gap: 18px; justify-content: center; align-items: center; margin-top: 28px; flex-wrap: wrap; }
.cta-secondary { color: var(--bronze); text-decoration: none; font-weight: 600; }
.cta-secondary:hover { text-decoration: underline; }
.hero-shot { display: block; margin-top: 48px; }
.hero-shot img {
  border: 1px solid var(--border); border-radius: 4px;
  box-shadow: 0 12px 40px rgb(0 0 0 / .18); margin: 0 auto;
}

/* sections */
section { padding: 56px 0; }
.section-title { font-size: 1.75rem; margin-bottom: 28px; text-align: center; }

/* cards + steps */
.card {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 4px; padding: 22px;
}
.steps { list-style: none; padding: 0; display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; }
.steps h3 { margin: 8px 0 6px; font-size: 1.05rem; }
.steps p { color: var(--subtle); font-size: .95rem; }
.step-n {
  display: inline-grid; place-items: center; width: 28px; height: 28px;
  background: var(--bronze); color: var(--bg); font-weight: 700;
  border-radius: 4px; font-size: .95rem;
}
@media (max-width: 720px) { .steps { grid-template-columns: 1fr; } }

/* footer */
.site-footer { border-top: 1px solid var(--border); padding: 28px 0; margin-top: 40px; }
.footer-row { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; color: var(--subtle); font-size: .95rem; }
```

- [ ] **Step 3: Render and verify both themes, both widths**

With the Playwright browser tools:
1. `browser_navigate` → `file:///S:/OrdoSort/ordosort.com/index.html`
2. `browser_resize` 1280×900 → `browser_take_screenshot`. Inspect: header, hero headline, coming-soon chip, hero screenshot (light variant), three step cards, footer. No layout breakage.
3. `browser_run_code_unsafe` → `await page.emulateMedia({ colorScheme: 'dark' })`, screenshot again. Inspect: dark tokens applied AND the hero `<picture>` now shows the dark screenshot.
4. `browser_resize` 390×844 (still dark) → screenshot. Steps stack to one column; no horizontal scrollbar. Confirm programmatically: `browser_run_code_unsafe` → `return await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)` must be `true`.
5. `browser_console_messages`: no errors (404s on assets would show here).

- [ ] **Step 4: Commit**

```powershell
cd S:\OrdoSort
git add ordosort.com/index.html ordosort.com/styles.css
git commit -m "feat(web): page skeleton, two-theme tokens, header/hero/how-it-works"
```

---

### Task 4: Features, screenshots gallery, download, footer placement

**Files:**
- Modify: `ordosort.com/index.html` (insert two sections + download; move footer after `</main>`)
- Modify: `ordosort.com/styles.css` (append section styles)

**Interfaces:**
- Consumes: Task 1's asset names and dimensions; Task 3's `.container`/`.card`/`.btn`/`.btn-soon`/`.section-title` classes and the `</section><!-- /how -->` insertion marker.
- Produces: the complete final page body.

- [ ] **Step 1: Insert the sections**

In `ordosort.com/index.html`, immediately after `</section><!-- /how -->`, insert:

```html
  <section id="features" class="features">
    <div class="container">
      <h2 class="section-title">Built for the daily pile</h2>
      <div class="feature-grid">
        <div class="card">
          <h3>Dashboard &amp; alerts</h3>
          <p>A compact dashboard parked in the corner: inbox count, monitored-folder
          tiles, and alert terms that flash red. Toasts, chimes, and taskbar badges
          reach you when something needs eyes.</p>
        </div>
        <div class="card">
          <h3>Naming that keeps up</h3>
          <p>Insert, replace, prefix, or append modes; per-route overrides and hotkeys;
          a live "will be filed as" preview that flags illegal names before you commit.</p>
        </div>
        <div class="card">
          <h3>An audit log you can trust</h3>
          <p>Every move lands in a network-safe SQLite history with daily backups, an
          in-app viewer, and CSV export. Several workstations can file into one shared log.</p>
        </div>
        <div class="card">
          <h3>Never loses a file</h3>
          <p>Files are only ever moved — never deleted, never overwritten. A taken name
          gets a (2) counter, and illegal characters are rejected up front.</p>
        </div>
        <div class="card">
          <h3>Tools for the awkward jobs</h3>
          <p>Unlock password-protected PDFs, bulk-rename with a hand-editable preview,
          match &amp; merge against a roster, and print barcode box labels at exact scale.</p>
        </div>
        <div class="card">
          <h3>Easy on the eyes</h3>
          <p>Follows Windows light or dark mode live. Every text color pairing is held
          to WCAG AA contrast — enforced by a unit test.</p>
        </div>
      </div>
    </div>
  </section>

  <section id="screenshots" class="gallery">
    <div class="container">
      <h2 class="section-title">See it in action</h2>
      <div class="gallery-grid">
        <figure>
          <picture>
            <source srcset="assets/shot-settings-dark.png" media="(prefers-color-scheme: dark)">
            <img class="shot" src="assets/shot-settings-light.png" loading="lazy"
                 alt="The Settings window: sectioned pages with live previews of routes and naming"
                 width="SET_W" height="SET_H">
          </picture>
          <figcaption>Settings, with live previews everywhere</figcaption>
        </figure>
        <figure>
          <picture>
            <source srcset="assets/shot-labels-dark.png" media="(prefers-color-scheme: dark)">
            <img class="shot" src="assets/shot-labels-light.png" loading="lazy"
                 alt="The Box labels tool: a live label preview with barcode and print layout"
                 width="LAB_W" height="LAB_H">
          </picture>
          <figcaption>Box labels with barcodes, printed at exact scale</figcaption>
        </figure>
        <figure>
          <picture>
            <source srcset="assets/shot-history-dark.png" media="(prefers-color-scheme: dark)">
            <img class="shot" src="assets/shot-history-light.png" loading="lazy"
                 alt="The History window: the filterable audit log with CSV export"
                 width="HIS_W" height="HIS_H">
          </picture>
          <figcaption>The audit history — filter, review, export</figcaption>
        </figure>
      </div>
    </div>
  </section>

  <section id="download" class="download">
    <div class="container">
      <div class="card download-card">
        <h2>Free while in development.</h2>
        <p>v1.0 is on its way — portable, small, and made for Windows 10/11.</p>
        <!-- v1.0 FLIP (2 of 2): at release, replace the span below with
             <a class="btn btn-primary" href="https://github.com/orest3781/OrdoSort/releases/latest">Download v1.0</a> -->
        <span class="btn btn-soon">v1.0 coming soon</span>
        <p class="req">Portable build (~3 MB) needs the .NET 8 Desktop Runtime — modern
        Windows offers it automatically if it's missing. The self-contained build
        (~70 MB) carries everything.</p>
      </div>
    </div>
  </section>
```

Replace `SET_W`/`SET_H`, `LAB_W`/`LAB_H`, `HIS_W`/`HIS_H` with the real dimensions from Task 1's summary. If Task 1 reported a substitution (e.g. Triage instead of History), rewrite that figure's `alt` and `figcaption` to describe the substituted window truthfully.

Then move the whole `<footer class="site-footer">…</footer>` block from inside `<main>` to immediately **after** `</main>`.

- [ ] **Step 2: Append section styles**

Append to `ordosort.com/styles.css`:

```css
/* features */
.feature-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; }
.feature-grid h3 { font-size: 1.05rem; margin-bottom: 6px; }
.feature-grid p { color: var(--subtle); font-size: .95rem; }

/* gallery */
.gallery-grid { display: grid; gap: 28px; }
.gallery-grid figure { margin: 0; }
.shot { border: 1px solid var(--border); border-radius: 4px; margin: 0 auto; }
.gallery-grid figcaption { text-align: center; color: var(--subtle); font-size: .9rem; margin-top: 10px; }

/* download */
.download-card { text-align: center; padding: 40px 24px; }
.download-card h2 { font-size: 1.6rem; }
.download-card > p { color: var(--subtle); margin: 10px 0 22px; }
.req { max-width: 520px; margin: 22px auto 0; font-size: .88rem; color: var(--subtle); }
```

- [ ] **Step 3: Render and verify**

Repeat Task 3 Step 3's Playwright pass (light 1280, dark 1280, dark 390). Additionally confirm: all three gallery images render in both schemes (dark variants swap in), captions read correctly, the download card shows the coming-soon chip, the footer now sits at the very bottom outside `<main>`, and `browser_console_messages` shows no 404s. The no-horizontal-scroll check at 390 px must still return `true`.

- [ ] **Step 4: Commit**

```powershell
cd S:\OrdoSort
git add ordosort.com/index.html ordosort.com/styles.css
git commit -m "feat(web): features grid, theme-matched gallery, download card"
```

---

### Task 5: SEO meta, OG card, robots.txt, vercel.json

**Files:**
- Modify: `ordosort.com/index.html` (head additions only)
- Create: `ordosort.com/assets/og.png`, `ordosort.com/robots.txt`, `ordosort.com/vercel.json`

**Interfaces:**
- Consumes: the three-diamond mark geometry (Task 2), Task 3's `<head>`.
- Produces: the complete deployable folder — Task 7 deploys it as-is.

- [ ] **Step 1: Build the OG card scratch page**

Create `%TEMP%\og-render.html` (scratch, not committed):

```html
<!doctype html><meta charset="utf-8">
<body style="margin:0;width:1200px;height:630px;background:#1A1C1F;display:flex;align-items:center;justify-content:center;font-family:system-ui,'Segoe UI',sans-serif">
<div style="display:flex;align-items:center;gap:48px">
  <svg viewBox="0 0 64 64" width="180" height="180">
    <path fill="#CDD2DA" opacity=".55" d="M32 38 8 50l24 12 24-12z"/>
    <path fill="#C9A96A" d="M32 22 8 34l24 12 24-12z"/>
    <path fill="#CDD2DA" d="M32 6 8 18l24 12 24-12z"/>
  </svg>
  <div>
    <div style="color:#E9EBEE;font-size:84px;font-weight:700;letter-spacing:.01em">OrdoSort</div>
    <div style="color:#C9A96A;font-size:34px;margin-top:10px">Every document, where it belongs.</div>
  </div>
</div>
</body>
```

- [ ] **Step 2: Capture it**

Playwright: `browser_resize` 1200×630 → `browser_navigate` to the scratch file → `browser_take_screenshot` → save/copy to `ordosort.com/assets/og.png`. Verify with Read: 1200×630, dark card, mark + wordmark + bronze tagline.

- [ ] **Step 3: Add the head meta**

In `ordosort.com/index.html`, after the `<title>` line, insert:

```html
  <meta name="description" content="OrdoSort watches your inbox folder: name each arriving PDF, press a destination key, and it's renamed, moved, and audit-logged. A filing workbench for Windows.">
  <link rel="canonical" href="https://ordosort.com/">
  <meta name="theme-color" content="#F7F8F9" media="(prefers-color-scheme: light)">
  <meta name="theme-color" content="#1A1C1F" media="(prefers-color-scheme: dark)">
  <meta property="og:type" content="website">
  <meta property="og:title" content="OrdoSort — Every document, where it belongs">
  <meta property="og:description" content="Name each arriving PDF, press a destination key — renamed, moved, audit-logged. A filing workbench for Windows.">
  <meta property="og:url" content="https://ordosort.com/">
  <meta property="og:image" content="https://ordosort.com/assets/og.png">
  <meta name="twitter:card" content="summary_large_image">
```

- [ ] **Step 4: Write robots.txt and vercel.json**

`ordosort.com/robots.txt`:

```
User-agent: *
Allow: /
```

`ordosort.com/vercel.json`:

```json
{
  "$schema": "https://openapi.vercel.sh/vercel.json",
  "headers": [
    {
      "source": "/(.*)",
      "headers": [
        { "key": "X-Content-Type-Options", "value": "nosniff" },
        { "key": "Referrer-Policy", "value": "strict-origin-when-cross-origin" },
        { "key": "X-Frame-Options", "value": "DENY" }
      ]
    },
    {
      "source": "/assets/(.*)",
      "headers": [
        { "key": "Cache-Control", "value": "public, max-age=604800" }
      ]
    }
  ]
}
```

(Cache is one week, **not** `immutable` — asset filenames carry no content hash, so a replaced screenshot must be able to roll over.)

- [ ] **Step 5: Verify**

Reload the page in Playwright; `browser_console_messages` clean; `browser_run_code_unsafe` → `return await page.evaluate(() => !!document.querySelector('meta[property="og:image"]'))` is `true`. Validate `vercel.json` parses: `Get-Content S:\OrdoSort\ordosort.com\vercel.json -Raw | ConvertFrom-Json` succeeds.

- [ ] **Step 6: Commit**

```powershell
cd S:\OrdoSort
git add ordosort.com/index.html ordosort.com/assets/og.png ordosort.com/robots.txt ordosort.com/vercel.json
git commit -m "feat(web): SEO meta, OG card, robots.txt, vercel headers"
```

---

### Task 6: Full verification pass

**Files:**
- Possibly modify: `ordosort.com/index.html`, `ordosort.com/styles.css` (fixes only)

**Interfaces:**
- Consumes: the complete page from Tasks 1–5.
- Produces: a verified page + a pass/fail report per check below.

- [ ] **Step 1: Programmatic contrast check**

Run this PowerShell script (scratch; do not commit). It must print `ALL PASS`:

```powershell
function Lum([string]$hex) {
  $c = $hex.TrimStart('#')
  $r = [Convert]::ToInt32($c.Substring(0,2),16) / 255.0
  $g = [Convert]::ToInt32($c.Substring(2,2),16) / 255.0
  $b = [Convert]::ToInt32($c.Substring(4,2),16) / 255.0
  $f = { param($v) if ($v -le 0.03928) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) } }
  0.2126 * (& $f $r) + 0.7152 * (& $f $g) + 0.0722 * (& $f $b)
}
function Ratio($a, $b) {
  $la = Lum $a; $lb = Lum $b
  $hi = [Math]::Max($la, $lb); $lo = [Math]::Min($la, $lb)
  [Math]::Round(($hi + 0.05) / ($lo + 0.05), 2)
}
$pairs = @(
  @('light text/bg',        '#171A1F', '#F7F8F9'), @('light text/surface',   '#171A1F', '#FFFFFF'),
  @('light subtle/bg',      '#545A63', '#F7F8F9'), @('light subtle/surface', '#545A63', '#FFFFFF'),
  @('light bronze/bg',      '#8C6D3F', '#F7F8F9'), @('light bronze/surface', '#8C6D3F', '#FFFFFF'),
  @('light accentText/accent', '#FFFFFF', '#2D323A'),
  @('dark text/bg',         '#E9EBEE', '#1A1C1F'), @('dark text/surface',    '#E9EBEE', '#26292D'),
  @('dark subtle/bg',       '#A8ADB4', '#1A1C1F'), @('dark subtle/surface',  '#A8ADB4', '#26292D'),
  @('dark bronze/bg',       '#C9A96A', '#1A1C1F'), @('dark bronze/surface',  '#C9A96A', '#26292D'),
  @('dark accentText/accent', '#171A1F', '#CDD2DA')
)
$fail = $false
foreach ($p in $pairs) {
  $r = Ratio $p[1] $p[2]
  $ok = if ($r -ge 4.5) { 'PASS' } else { $fail = $true; 'FAIL' }
  "{0}  {1}  {2}" -f $ok, $r, $p[0]
}
if ($fail) { 'CONTRAST FAILURES' } else { 'ALL PASS' }
```

If any pair fails, stop and report — the palette is spec-locked, so a failure means a wrong usage in CSS (fix the usage, never the palette).

- [ ] **Step 1b: Rendered-output contrast (the spec requires this, not just the token table)**

In Playwright, for each scheme (`page.emulateMedia({ colorScheme: 'light' })` then `'dark'`), run via `browser_run_code_unsafe`:

```js
return await page.evaluate(() => {
  const bgOf = (el) => {
    for (let e = el; e; e = e.parentElement) {
      const c = getComputedStyle(e).backgroundColor;
      if (c && !c.includes('0, 0, 0, 0') && c !== 'transparent') return c;
    }
    return getComputedStyle(document.body).backgroundColor;
  };
  const picks = ['body', '.lede', '.steps p', '.site-nav a', 'a[href*="github"]',
                 '.btn-soon', '.step-n', '.wordmark', '.gallery-grid figcaption', '.req'];
  return picks.map(sel => {
    const el = document.querySelector(sel);
    return el ? { sel, color: getComputedStyle(el).color, bg: bgOf(el) } : { sel, missing: true };
  });
});
```

No selector may come back `missing`. Feed each reported `color`/`bg` pair through the Step 1 ratio function (convert `rgb(r, g, b)` to hex first); every pair must be ≥ 4.5. This checks what the browser actually resolved — a wrong `var()` reference or an unthemed element shows up here even though the token table passes.

- [ ] **Step 2: Rendered matrix**

Playwright, against `file:///S:/OrdoSort/ordosort.com/index.html` — six screenshots: {light, dark} × {390×844, 768×1024, 1280×900}. Inspect each with the Read tool for: readable text, intact layout, correct theme variant of every image, no clipped or overlapping elements.

- [ ] **Step 3: Behavior checks**

1. Anchors: click each header nav link (`browser_click`); the page scrolls to the right section.
2. Keyboard: `browser_press_key` Tab repeatedly from page load; every link gets a visible bronze focus ring (screenshot mid-tab to prove it); the coming-soon spans are **not** focus stops.
3. Overflow: at 390×844, `scrollWidth <= innerWidth` returns `true` in both schemes.
4. `browser_console_messages`: zero errors, zero 404s.

- [ ] **Step 4: Fix anything found, re-verify, commit**

Apply minimal fixes; re-run the failed check until clean.

```powershell
cd S:\OrdoSort
git add ordosort.com/
git commit -m "fix(web): verification-pass fixes"
```

(Skip the commit if nothing needed fixing.)

---

### Task 7: Deploy to Vercel + DNS handoff (MAIN SESSION — do not dispatch)

**Files:** none (deploys `ordosort.com/` as-is)

**Interfaces:**
- Consumes: the verified folder from Task 6.
- Produces: a live Vercel deployment URL + a user checklist for the dashboard/DNS steps.

- [ ] **Step 1: Deploy via the Vercel MCP integration**

Load the Vercel tools with ToolSearch (`select:mcp__claude_ai_Vercel__deploy_to_vercel,mcp__claude_ai_Vercel__list_projects,mcp__claude_ai_Vercel__get_deployment`). Deploy the `ordosort.com` folder as project `ordosort-com` (team: the user's default). If the deploy tool requires a linked project, create/link per its interactive flow.

- [ ] **Step 2: Verify the deployment**

Fetch the returned deployment URL (WebFetch or Playwright): the page renders, `styles.css` and every asset load (no 404), response headers include `X-Content-Type-Options: nosniff` and the assets cache header.

- [ ] **Step 3: Push the branch**

```powershell
cd S:\OrdoSort
git push origin main
```

- [ ] **Step 4: Hand the user the finishing checklist**

Present exactly these remaining manual steps:
1. Vercel dashboard → project `ordosort-com` → Settings → Git: connect `orest3781/OrdoSort`, set **Root Directory = `ordosort.com`**, production branch `main`.
2. Vercel dashboard → Domains: add `ordosort.com` and `www.ordosort.com` (redirect www → apex).
3. Cloudflare DNS for ordosort.com: add the records Vercel displays (typically `A @ 76.76.21.21` and `CNAME www cname.vercel-dns.com`) — **DNS-only / grey cloud**, not proxied. Confirm the exact values against what Vercel's domain panel shows.
4. Wait for Vercel's domain verification to go green, then load https://ordosort.com in both OS themes.

---

## v1.0 release-day flip (recorded for the future, not a task now)

Two marked HTML comments (`v1.0 FLIP (1 of 2)` in the hero, `(2 of 2)` in the download card): replace each `<span class="btn btn-soon">…</span>` with
`<a class="btn btn-primary" href="https://github.com/orest3781/OrdoSort/releases/latest">Download v1.0</a>`, commit, push — Vercel redeploys automatically once git-connected.
