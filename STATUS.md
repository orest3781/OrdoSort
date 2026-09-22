# STATUS — OrdoSort

## Now
| Item | Status | Notes |
|---|---|---|
| Box Labels standalone (`feature/box-labels-standalone`) | ✅ Done | Merged to `main` 2026-09-10 (`9dbe567`, 16 commits) and pushed. 3,236 tests green on the merged tree. The branch itself is kept, local and on GitHub |
| `check.bat` - the one check command (mirrors CI) | ✅ Done | Added 2026-09-13; README, CLAUDE.md and Quick start point at it. Committed and pushed 2026-09-22 |
| S:\ → A:\DEV path rewrite (45 files, comments and docs only) | ✅ Done | Committed `1f5b8de` and pushed 2026-09-09. `.gitignore` now points at `A:\DEV\_ARCHIVE\OrdoSort-samples` |

## Next
- [x] Delete the four local `fix/*` branches — all fully merged into `main`, nothing on GitHub
- [x] Merge `feature/box-labels-standalone` into `main` — long-lived branch, house rule says short-lived
- [x] Decide whether `feature/box-labels-standalone` and `docs/refinement-tracker` should stay — deleted 2026-09-12, local and on GitHub, both fully merged into `main`
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
| 2026-09-10 | Merged the long-lived branch with `--no-ff` rather than a PR | Matches the repo's existing merge-commit history; solo repo, and the work was already reviewed commit by commit |

## Verification
| Check | Result | Not tested |
|---|---|---|
| `dotnet test OrdoSort.sln` (2026-09-10, pre-merge) | ✅ 920 Core + 2,316 Wpf = 3,236 passed, 0 failed | — |
| `check.bat` full run (2026-09-13) | ✅ 920 Core + 2,316 Wpf = 3,236 passed, exit 0 | — |
| Merged tree identical to the tested tree | ✅ `git diff feature/box-labels-standalone HEAD` empty | — |
| BoxLabels.exe run by hand after the merge | ⬜ Not started | The standalone app has not been launched since merging |

## Quick start
```bash
check.bat        # or: check.bat core
```