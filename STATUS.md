# STATUS — OrdoSort

## Now
| Item | Status | Notes |
|---|---|---|
| Box Labels standalone (`feature/box-labels-standalone`) | ✅ Done | Merged to `main` 2026-09-10 (`9dbe567`, 16 commits) and pushed. 3,236 tests green on the merged tree. The branch itself is kept, local and on GitHub |
| One config file: destinations, monitored folders, alerts moved into `config.json`; `box-labels.json` stays separate | ✅ Done | 2026-09-22. Old `destinations.json`/`monitored-folders.json`/`alerts.json` are no longer read (no migration, by decision). Settings "Data files" tab keeps only the box-labels path. Committed and pushed 2026-09-22 |
| Box Labels in the GitHub release (`boxlabels-vX-win-x64[-selfcontained].zip`) | ✅ Done | 2026-09-22. `release.yml` builds and attaches both; self-contained publish checked locally. Not yet run on GitHub. Committed and pushed 2026-09-22 |
| `check.bat` - the one check command (mirrors CI) | ✅ Done | Added 2026-09-13; README, CLAUDE.md and Quick start point at it. Committed and pushed 2026-09-22 |
| S:\ → A:\DEV path rewrite (45 files, comments and docs only) | ✅ Done | Committed `1f5b8de` and pushed 2026-09-09. `.gitignore` now points at `A:\DEV\_ARCHIVE\OrdoSort-samples` |

## Next
- [x] Delete the four local `fix/*` branches — all fully merged into `main`, nothing on GitHub
- [x] Merge `feature/box-labels-standalone` into `main` — long-lived branch, house rule says short-lived
- [x] Decide whether `feature/box-labels-standalone` and `docs/refinement-tracker` should stay — deleted 2026-09-12, local and on GitHub, both fully merged into `main`
- [x] CI red on `main` since 2026-09-10: `BoxLabelsAppFileCheckTests.OrdoSortsOwnConfigIsRefused` copied the gitignored `demo-full\config.json`. Fixed 2026-09-22: it now writes its config with `Config.Save`
- [ ] E2E red on `main` since 2026-09-09: 4 `[Zip] … the status line reports …` checks. `ZipAsync` now shows "Zipping…" before the work, so `Settle` stopped waiting before the posted "Created …" landed. The scenarios now wait for the dispatcher to drain (`claude/consolidate-save-files-4m6plg`); tick once E2E is green on GitHub
- [x] Draft PR #4 (`claude/consolidate-save-files-4m6plg`) superseded by `0a778e0` — closed 2026-09-22 with a note; its branch is still on GitHub
- [ ] A `check.bat wpf` run hung for an hour on 2026-09-22 (testhost idle, one test already done); two reruns of the same code passed in under 3 min. Likely the deadlock-prone tests below
- [ ] Settings → Destinations list rows announce as "OrdoSort.Wpf.ViewModels.RouteEditVm" to screen readers (UI Automation name is the type name, not the label). Seen in QC 2026-09-22
- [ ] `BulkRenameBatchTests.NeitherClearNorRemoveCanTouchTheListWhileTheBatchRuns` failed once in a full run 2026-09-22, passed 5/5 alone — timing-flaky
- [ ] Three xUnit1031 warnings in `OrdoSort.Wpf.Tests` — blocking task calls in `BulkRenameSortedNavigationTests` and `DeleteKeyTests`, which xUnit says can deadlock

## Blocked
| Item | Blocked on | Since |
|---|---|---|

## Dead ends
| Tried | Why it failed |
|---|---|

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
| Box Labels self-contained publish (2026-09-22, local) | ✅ BoxLabels.exe ~74 MB built | — |
| Merged tree identical to the tested tree | ✅ `git diff feature/box-labels-standalone HEAD` empty | — |
| BoxLabels.exe run by hand after the merge | ⬜ Not started | The standalone app has not been launched since merging |

## Quick start
```bash
check.bat        # or: check.bat core
```