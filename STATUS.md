# STATUS — OrdoSort

## Now
| Item | Status | Notes |
|---|---|---|
| Box Labels standalone (`feature/box-labels-standalone`) | ✅ Done | Merged to `main` 2026-09-10 (`9dbe567`, 16 commits) and pushed. 3,236 tests green on the merged tree. The branch itself is kept, local and on GitHub |
| One config file: destinations, monitored folders, alerts moved into `config.json`; `box-labels.json` stays separate | ✅ Done | 2026-09-22. Old `destinations.json`/`monitored-folders.json`/`alerts.json` are no longer read (no migration, by decision). Settings "Data files" tab keeps only the box-labels path. Committed and pushed 2026-09-22 |
| Box Labels in the GitHub release (`boxlabels-vX-win-x64[-selfcontained].zip`) | ✅ Done | Shipped in **v1.6.0** (2026-09-24, tag on `9d4531f`): release run green, four zips attached; BoxLabels.exe in both zips reports 1.6.0 and carries THIRD-PARTY-NOTICES |
| `check.bat` - the one check command (mirrors CI) | ✅ Done | Added 2026-09-13; README, CLAUDE.md and Quick start point at it. Committed and pushed 2026-09-22 |
| Whole-app review and fix pass | ✅ Done | 2026-09-23. Five area reviews: 22 findings fixed (pushed), then the 9 box-labels findings (11 commits `8fbafe3`..`7f02e90`, local) once the owner decided Print uses the typed number. Every fix has a test; every fixer commit reviewed by hand before merging |
| Config save on a network share when File.Replace is denied (PR #6) + release workflow attaches zips to an existing release (PR #7) | ✅ Done | 2026-09-24, from a phone (Claude Code cloud session), merged to `main`; version bumped to 1.6.1, **not yet tagged or released**. The PR said check.bat still needed a Windows run: done 2026-09-24 — 941 Core + 2,364 Wpf passed |
| Repo cleanup | ✅ Done | 2026-09-23, local `main`, not pushed. Scripts other than check/run moved to `scripts\`; old tools to `docs\legacy-scripts\`; tracked `dev\` setup (`run.bat dev\config.json`); `.editorconfig` + format check in check.bat and CI; all build output in `artifacts\`; ~820 MB of local clutter and the fixer worktrees removed |
| Rebrand (plan: `~/.claude/plans/i-want-you-to-floofy-fiddle.md`) | 🔄 In progress | Owner picked (2026-09-24): concept **A** (tab dividers), palette **A** (ink & bronze), **Atkinson Hyperlegible Next**, font files approved. Review page: https://claude.ai/artifact/DkgUJTfP1CVYSPSdTuccrw. Phase 2 step 1 done: seven schemes cut to light/dark (paper→light, graphite→dark, the other five→auto). Next: new palettes |
| S:\ → A:\DEV path rewrite (45 files, comments and docs only) | ✅ Done | Committed `1f5b8de` and pushed 2026-09-09. `.gitignore` now points at `A:\DEV\_ARCHIVE\OrdoSort-samples` |

## Next
- [x] Delete the four local `fix/*` branches — all fully merged into `main`, nothing on GitHub
- [x] Merge `feature/box-labels-standalone` into `main` — long-lived branch, house rule says short-lived
- [x] Decide whether `feature/box-labels-standalone` and `docs/refinement-tracker` should stay — deleted 2026-09-12, local and on GitHub, both fully merged into `main`
- [x] CI red on `main` since 2026-09-10: `BoxLabelsAppFileCheckTests.OrdoSortsOwnConfigIsRefused` copied the gitignored `demo-full\config.json`. Fixed 2026-09-22: it now writes its config with `Config.Save`
- [x] E2E red on `main` since 2026-09-05/09: four Zip scenarios read the status line before the run's result arrived — `7144633` added a "Zipping N items…" line before the work, which made ScenarioKit.Settle's "status is non-empty" wait true at once. Settle now drains queued UI work first. Fixed 2026-09-23; all 46 scenarios pass locally and on GitHub (`e16bf95`)
- [x] Draft PR #4 (`claude/consolidate-save-files-4m6plg`) superseded by `0a778e0` — closed 2026-09-22 with a note; its branch is still on GitHub
- [ ] A `check.bat wpf` run hung for an hour on 2026-09-22 (testhost idle, one test already done); two reruns of the same code passed in under 3 min. Likely the deadlock-prone tests below
- [x] Settings → Destinations list rows announce as "OrdoSort.Wpf.ViewModels.RouteEditVm" to screen readers — fixed 2026-09-23 with the row below
- [ ] `BulkRenameBatchTests.NeitherClearNorRemoveCanTouchTheListWhileTheBatchRuns` failed once in a full run 2026-09-22, passed 5/5 alone — timing-flaky
- [ ] Three xUnit1031 warnings in `OrdoSort.Wpf.Tests` — blocking task calls in `BulkRenameSortedNavigationTests` and `DeleteKeyTests`, which xUnit says can deadlock
- [x] Box-labels review findings (9) — fixed 2026-09-23 (`8fbafe3`..`7f02e90`): Print/Save PDF start from a typed number (owner's call) and a print retires it, so closing can't roll a peer's counter back; Box Labels checks `--file` and remembered paths; store I/O errors and unexpected failures are reported; Save PDF opens its target before claiming; honest cancel/crash wording; client rows have screen-reader names; first-run seed never overwrites a peer; null `label_clients` handled
- [x] Monitored-folder paths resolve beside config.json (dashboard tiles, Settings preview, row notes, warnings), and Settings' Create it / Open buttons for destinations and monitored folders use the same resolved path — fixed 2026-09-23 (`7e84abf`); `dev\config.json` now uses relative watch paths. Checked live with the app started from C:\
- [x] Dashboard tile groups and tiles announced as "OrdoSort.Wpf.ViewModels.TileGroupViewModel" / "TileViewModel" — fixed 2026-09-23: groups say their section title, tiles their "Label: count". New test walks the dashboard's automation tree; checked live through UI Automation
- [x] The inbox scan skips hidden, system and dot files entirely — not queued (a hidden PDF included) and not counted as "other files ignored". Owner's call, fixed 2026-09-23; the dev dashboard no longer says "1 other file ignored"
- [x] `check.bat` / `dotnet test` reported success when a blocked test DLL ran NO tests — fixed 2026-09-24: `tests	est.runsettings` sets TreatNoTestsAsError, wired for every test run through RunSettingsFilePath in Directory.Build.props (check.bat, CI, IDEs, plain dotnet test)
- [ ] The Release `OrdoSort.Wpf.Tests.dll` stayed blocked after a `--no-incremental` rebuild: Release builds are deterministic, so the rebuilt file is byte-identical. Debug (non-deterministic by design, Directory.Build.targets) ran all 2,364 tests fine. Likely the same root cause as the intermittent stale-BAML failures below
- [x] Set-aside count no longer includes hidden, system or dot files (`desktop.ini`, `Thumbs.db`, `.gitkeep`) — owner's call, fixed 2026-09-23 (`6d68b53`)
- [ ] The five real-WebView2 tests (`WebViewPdfViewerGuardBehaviourTests`) fail, and the suite slows from ~2 to ~15 min, while another OrdoSort is running: all instances share `%LOCALAPPDATA%\OrdoSort\WebView2`. Confirmed on the pre-fix commit too. Tests should use their own user-data folder
- [x] List rows announced their type name in SettingsWindow, ManageSavedWindow and UnlockWindow — fixed 2026-09-23: destinations by label, monitored folders by label / "Section: …", saved passwords by label (never the password, pinned by a test), Unlock files by name + status. The test exemption list is gone; checked live through UI Automation
- [ ] QC of PR #6 (2026-09-24): confirm the rename fallback actually fixes "settings not saved" on the REAL share — nobody has run it there. Its stated cause is unproven: on local NTFS, ReplaceFile still succeeds with WRITE_DAC / WRITE_OWNER / WRITE_ATTRIBUTES denied on the destination, so the code comment's "needs WRITE_DAC" is wrong as written; a different file OWNER (another station's user) is the remaining suspect. Reword the comment once the share confirms
- [ ] PR #6 side effect: after a fallback save, config.json takes the folder's inherited permissions instead of its own. Explicit permissions an admin set on config.json are silently dropped on the first such save. Decide whether to document it or warn
- [ ] PR #6 test gap: "a transient denial fails the rename too and is retried" has no test
- [ ] PR #7 unproven path: that a release created on github.com (no tag push) triggers the Release workflow at all. Only a real phone release will show it
- [ ] Draft PR #5 (phone session's E2E fix) is superseded by `e16bf95` on main — close it
- [ ] QC-15, second half: Copies > 1 prints the same barcode on several boxes (docs/superpowers/refinement-master-checklist.md). The false "counter untouched" wording half is fixed
- [ ] Intermittent: an incremental Release build leaves stale BAML and ~1,130 WPF tests fail ("Provide value on StaticResourceExtension threw", "Unexpected record in Baml stream"). Seen twice on 2026-09-23 — after the box-labels merge, and after a XAML change built via the test project, then the Release exe run, then check.bat. Two deliberate reproductions of that second sequence both passed. Suspect Windows Application Control interfering with freshly built DLLs. Workaround: `dotnet build OrdoSort.sln -c Release --no-incremental`, then rerun
- [ ] CI build-and-test took ~15 min on 2026-09-23 (was ~8). Everything passed; watch whether it stays slow

## Blocked
| Item | Blocked on | Since |
|---|---|---|

## Dead ends
| Tried | Why it failed |
|---|---|
| Dark (ink) icon plate for the rebrand | Only 26-44% of the icon reached 3:1 on the dark taskbar (#202020). A plate needs luminance 0.14-0.26 to clear 3:1 on both taskbars; slate `#647080` and brass `#8A6A34` do |

## Decisions
| Date | Decision | Why |
|---|---|---|
| 2026-09-09 | Dated audit/plan docs keep the old `A:\DEV\OrdoSort-samples` wording | They record where the folder was on those dates; only the live `.gitignore` note was updated |
| 2026-09-22 | Config consolidated with no migration of the old side files | Owner's call. Anyone upgrading re-enters destinations, monitored folders and alerts |
| 2026-09-22 | No notice about leftover `destinations.json`/`monitored-folders.json`/`alerts.json` after upgrade | Owner's call. Consequence: every station sharing one config.json must be upgraded together — an old build strips the sections back out of config.json |
| 2026-09-22 | Box Labels ships as its own zips in the release, not inside OrdoSort's | People who only need labels download just that |
| 2026-09-10 | Merged the long-lived branch with `--no-ff` rather than a PR | Matches the repo's existing merge-commit history; solo repo, and the work was already reviewed commit by commit |

## Verification
| Check | Result | Not tested |
|---|---|---|
| `dotnet test OrdoSort.sln` (2026-09-10, pre-merge) | ✅ 920 Core + 2,316 Wpf = 3,236 passed, 0 failed | — |
| `check.bat` full run (2026-09-13) | ✅ 920 Core + 2,316 Wpf = 3,236 passed, exit 0 | — |
| `check.bat` full run (2026-09-22, after review fixes) | ✅ 910 Core passed; 2,312 of 2,313 Wpf passed. The one failure (`BulkRenameBatchTests.NeitherClearNorRemoveCanTouchTheListWhileTheBatchRuns`) passed 5 of 5 reruns — flaky, unrelated. 3 known xUnit1031 warnings | Release workflow not run on GitHub yet |
| Hand QC: real OrdoSort.exe driven via UI Automation (2026-09-22) | ✅ Data files tab shows only Box labels; added a destination + alert, OK saved both into config.json; old destinations.json ignored; old `destinations_file` key dropped; box-labels.json created | Monitored-folder edit not driven by hand (covered by tests) |
| Code review (/code-review high, 2026-09-22) | ✅ 6 of 7 findings fixed with tests: box-labels lock no longer breaks the shared-section refresh; Unlock fallback save keeps peers' destinations; duplicate key / locked config.json no longer crashes Settings; box_labels_file may not be config.json; refresh failure now warns instead of saving stale data | Finding 4 (notice about leftover old side files) declined by owner — see Decisions |
| `check.bat` full run (2026-09-23, all fixes + cleanup, artifacts layout) | ✅ Format check clean; 930 Core + 2,335 Wpf = 3,265 passed, 0 failed; 3 known xUnit1031 warnings | Not yet run on GitHub CI (not pushed) |
| Whole-app review merge (2026-09-23) | ✅ 21 fixer commits reviewed by hand before cherry-picking; one conflict (route validation) resolved by combining both fixes; `git cherry` confirmed every commit landed | Box-labels findings not fixed yet |
| Dev setup and moved scripts, by hand (2026-09-23) | ✅ `run.bat dev\config.json` launches from `artifacts\`; Box labels shows the three dev clients; `scripts\publish-boxlabels.bat` works from another folder | `scripts\e2e.bat` and `scripts\demo-full.bat` not run after the move |
| Box Labels self-contained publish (2026-09-22, local) | ✅ BoxLabels.exe ~74 MB built | — |
| Merged tree identical to the tested tree | ✅ `git diff feature/box-labels-standalone HEAD` empty | — |
| BoxLabels.exe run by hand after the merge | ⬜ Not started | The standalone app has not been launched since merging |

## Quick start
```bash
check.bat                    # or: check.bat core
run.bat dev\config.json      # try a change by hand
```