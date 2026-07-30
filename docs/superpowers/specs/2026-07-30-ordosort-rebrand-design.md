# OrdoSort rebrand & repo rebuild — design

**Date:** 2026-07-30
**Status:** Approved by user (approach, versioning, archive handling, pages site)

## Context

The app has carried three names: **FileRouter** (assembly, namespaces, config
identifiers), **Sendu** (repository), and **Paper Trail** (product). The final
name is **OrdoSort**. The source of truth is an extracted release archive at
`S:\OrdoSort\sendu-1.4.0\sendu-1.4.0\` (no git history) — a working C#/.NET 8
WPF document-routing app with Core + WPF projects, two xUnit test suites, a
smoke-test tool, demo generators, and CI workflows.

The GitHub repository `orest3781/OrdoSort` exists and is empty.

## Goal

Rebuild the project as **OrdoSort** at `S:\OrdoSort` (repo root), with a full
clean rebrand — no FileRouter/Sendu/Paper Trail residue in code, docs, or git
history — and push it to `https://github.com/orest3781/OrdoSort`. The sole
exception is this design spec, which necessarily documents the rename.

## Decisions (user-approved)

| Question | Decision |
|---|---|
| Rebuild depth | Full clean rebrand — projects, namespaces, assembly, docs |
| Old-install compatibility | Clean break — no config/history migration |
| Execution approach | In-place mechanical rebrand (rename, not rewrite) |
| Versioning | Fresh line; first release tag will be `v1.0.0` |
| Old archive | After verification, move `sendu-1.4.0` to `S:\tmp\sendu-1.4.0` |
| GitHub Pages site | Drop the old Sendu `docs/index.html`; rebuild later separately |

## Repo layout

`S:\OrdoSort` becomes the git repo root:

```
OrdoSort.sln
src/OrdoSort.Core/          pure logic — no UI, unit-tested
src/OrdoSort.Wpf/           the app: MVVM view models + XAML
tests/OrdoSort.Core.Tests/  xUnit — routing rules
tests/OrdoSort.Wpf.Tests/   xUnit — app logic, headless
tools/OrdoSort.Smoke/       UI proofs against the real WebView2 viewer
docs/                       logo + this spec (old Paper Trail art removed)
demo/                       demo workspace seed data
.github/workflows/          ci.yml, release.yml (renamed artifacts)
run.bat  reset.bat  publish.bat  demo-full.bat
.gitignore  .gitattributes  README.md
```

## Rename scope

Mechanical, case-aware rename across the entire tree. Code **logic does not
change** — only identifiers, names, and strings.

| Old | New |
|---|---|
| `FileRouterNet.sln` | `OrdoSort.sln` |
| `FileRouter.Core` (project, folder, namespace) | `OrdoSort.Core` |
| `FileRouter.Wpf` (project, folder, namespace) | `OrdoSort.Wpf` |
| `FileRouter.Core.Tests` / `FileRouter.Wpf.Tests` | `OrdoSort.Core.Tests` / `OrdoSort.Wpf.Tests` |
| `FileRouter.Smoke` | `OrdoSort.Smoke` |
| Assembly / exe name | `OrdoSort.exe` |
| Release artifacts `papertrail-vX-*.zip` | `ordosort-vX-*.zip` |
| Window titles, in-app product strings, sound-set name | OrdoSort |
| `Sendu` / `Paper Trail` / `papertrail` strings anywhere | removed or replaced |

Also covered: `.bat` scripts' project paths, workflow `dotnet` paths, README,
`demo` config generators, and any FileRouter-named internal identifiers
(mutexes, app-data folder names, DPAPI entropy strings). Clean break means
renamed identifiers need no back-compat aliases.

## Identity & docs

- **README** rewritten for OrdoSort, preserving the current README's structure
  (tagline, download, design goals, features, structure, build/demo). No
  "formerly known as" note.
- **Banner:** `ordosort-logo-concept.jpg` moves to `docs/` and is referenced
  as the README banner until final art exists.
- **Removed:** the entire old `docs/` content — Paper Trail `banner.png` and
  `wordmark.png`, the Pages site (`index.html`, `.nojekyll`, `assets/` with
  favicon, og image, wordmark), and all historical design docs and plans
  under `docs/superpowers/`. This spec is the first document of the new
  lineage.

## Verification gate (before first commit)

1. `dotnet build` succeeds on the renamed solution.
2. `dotnet test` — both suites green.
3. Grep sweep over the tree (excluding `docs/superpowers/` history, i.e. this
   spec) shows **zero** occurrences of `filerouter`, `sendu`, `papertrail`,
   or `paper trail` (case-insensitive).
4. `reset.bat` demo generation still works (config paths renamed).

## Git & push

- `git init` at `S:\OrdoSort`, branch `main`.
- Existing `.gitignore` / `.gitattributes` carried over.
- Clean initial commit(s): spec + rebranded codebase.
- Remote `origin` → `https://github.com/orest3781/OrdoSort.git`, push `main`.
- No tag yet at push time; `v1.0.0` when the first release is cut.

## Out of scope

- Any feature or behavior changes to the app.
- Config/history migration from old installs.
- A new GitHub Pages site.
- Final logo/wordmark design (concept JPG is a placeholder).
