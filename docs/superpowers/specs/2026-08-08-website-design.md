# ordosort.com — Landing Page Design

**Date:** 2026-08-08
**Status:** Approved by user (brainstorming session)

## What this is

A single static landing page for OrdoSort at ordosort.com, living in the
repo's `ordosort.com/` folder and hosted on Vercel (user has a Pro
account). The domain is registered at Cloudflare; DNS stays there and
points at Vercel.

## Decisions made during brainstorming

| Question | Decision |
|---|---|
| Site's job | Landing page + download pointer (no docs section yet) |
| Download button | "Coming soon" state until v1.0.0 is tagged; flipped by a one-line edit |
| Visual direction | Both themes, OS-aware via `prefers-color-scheme`, extending the app's "crisp workbench" identity |
| Positioning | Product with a future price — "free while in development" tone, no open-source claims (repo has no LICENSE yet) |
| Email capture | None — fully static, no backend, no third-party form |
| Stack | Hand-written `index.html` + `styles.css` + `assets/` — no framework, no build step |
| Hosting | Vercel, git-connected to `orest3781/OrdoSort`, root directory `ordosort.com` |

## Page structure (top to bottom)

1. **Header** — flat SVG mark + "OrdoSort" wordmark left; anchor links
   *Features · Screenshots · Download* right. Sticky, translucent.
2. **Hero** — headline **"Every document, where it belongs."** Subhead
   (two sentences): a PDF lands in your inbox folder; you type its name,
   press a destination key, and it's renamed, moved, and audit-logged.
   Primary button: "v1.0 coming soon" (styled as pending, not a dead
   link). Secondary anchor: "See it in action ↓". Below: large
   theme-matched screenshot — the main routing window if it presents
   well despite its blank WebView2 pane (see Screenshots pipeline),
   otherwise the most photogenic window (likely the dashboard).
3. **How it works** — three numbered steps: *Arrives* (inbox watched
   live) → *Name it* (type, autocomplete, one keypress) → *Accounted
   for* (renamed, moved, audit row written).
4. **Features** — six cards:
   - Dashboard & alerts (corner dashboard, tiles, alert terms, toasts)
   - Naming modes & autocomplete (insert/replace/prefix/append,
     recency+frequency ranking)
   - Audit history (network-safe SQLite, daily backups, CSV export)
   - Never loses a file (move-only, no overwrite, illegal-name guard)
   - Tools (Unlock PDFs, Bulk rename, Match & merge, Box labels)
   - Follows your OS theme (light/dark live, WCAG AA enforced by test)
5. **Screenshots** — gallery of 3–4 shots (dashboard, settings, one
   tool) served theme-matched via `<picture>` +
   `prefers-color-scheme` media queries.
6. **Download** — card: "**Free while in development.** v1.0 is on its
   way." Requirements: Windows 10/11; portable ~3 MB (needs .NET 8
   Desktop Runtime) or self-contained ~70 MB.
7. **Footer** — © 2026 OrdoSort · "Office workflow automation" · quiet
   GitHub link.

## Tone

Product voice: confident, concrete. "Free while in development" appears
only in the Download card. No pricing page, no "buy" language.

## Visual system

CSS custom properties on `:root`, switched by `@media
(prefers-color-scheme: dark)`. Values are the app's literal palette
(`src/OrdoSort.Wpf/Theme/ThemePalette.cs`):

| Token | Light | Dark |
|---|---|---|
| Background | `#F7F8F9` | `#1A1C1F` |
| Surface | `#FFFFFF` | `#26292D` |
| Text | `#171A1F` | `#E9EBEE` |
| Subtle text | `#545A63` | `#A8ADB4` |
| Border | `#BAC0C8` | `#4C525A` |
| Accent (buttons) | `#2D323A` | `#CDD2DA` |
| Accent text | `#FFFFFF` | `#171A1F` |
| Bronze (links, focus, highlights) | `#8C6D3F` | `#C9A96A` |

Flat 4 px radii, bronze focus rings, `system-ui` font stack (zero font
downloads), strong size ramp. Every text/background pairing ≥ 4.5:1 in
both themes — the app's own standard, verified on rendered output.

## Screenshots pipeline

- Generated with the existing smoke tool: `screenshots <outdir> both`
  (renders every window off-screen in both themes).
- Known quirks: the command **always exits 1** (it counts its WebView2
  NOTE as a FAIL — ignore the exit code); the WebView2 PDF pane renders
  blank in captures, so favor the dashboard/settings/tools windows or
  compositions where the viewer isn't the focus.
- The hero shot plus 3–4 gallery shots picked (4–5 images total per
  theme), downscaled/compressed to web weight, committed under
  `ordosort.com/assets/`.

## Logo, favicon, social card

- The 3D concept photo (`docs/logo-concept.jpg`) is not web-usable.
- Ship a simple flat SVG mark (layered diamond stack echoing the
  concept) + text wordmark. Favicon and a 1200×630 Open Graph card
  (dark background, mark + tagline) derive from it.
- When final logo art lands (already on the release checklist), it's a
  drop-in swap of one SVG.

## Deployment

- Vercel project `ordosort-com`: git-connected to
  `orest3781/OrdoSort`, **root directory `ordosort.com`**, framework
  preset "Other", no build command, no output directory.
- Minimal `vercel.json`: security headers (at minimum
  `X-Content-Type-Options: nosniff`, a conservative
  `Referrer-Policy`), long-cache headers for `assets/`.
- Domain `ordosort.com` attached on Vercel, `www` redirecting to apex.
- DNS at Cloudflare: the A/CNAME records Vercel specifies, set to
  **DNS-only (grey cloud)** — proxying Cloudflare in front of Vercel
  causes redirect/SSL trouble.
- SEO/meta: title, description, canonical, Open Graph + Twitter card,
  `theme-color` for both schemes, `robots.txt` allowing all.

## The v1.0 flip

One documented edit at release time: the coming-soon button becomes a
"Download v1.0" pair linking to
`https://github.com/orest3781/OrdoSort/releases/latest` (zip filenames
carry version numbers, so the releases page is linked rather than a
hard-coded asset URL). An HTML comment marks the exact spot.

## Quality bar / verification

- Semantic HTML, alt text on every image, keyboard-visible bronze focus
  rings, `prefers-reduced-motion` respected.
- Before claiming done: Playwright renders the page in both color
  schemes at mobile (~390 px) and desktop (~1280 px) widths;
  contrast-check the rendered output, not just the token table.

## Out of scope (explicitly)

- Docs section, blog, changelog pages (revisit with Astro if ever real)
- Email capture / any backend
- Pricing page or purchase flow
- Analytics (can be added later; Vercel Pro includes Web Analytics)
- The final logo art (separate effort already on the release checklist)
