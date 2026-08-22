# Questions before the fresh todo list — 2026-08-22

The tracker now holds **173 open items** (checklist + the fresh QC's 46). A todo list
built by severity alone would be wrong, because the real ordering depends on facts only
you have (how the app is actually deployed), decisions only you can make (product calls),
and a few cheap measurements. Answer these and the todo list falls out; each question
names the checklist/audit IDs its answer moves. Recommendations are marked ▸ where I have
one.

## A · The four decisions that shape batch B

1. **Is "repair batch A's seams + the four new Highs" the whole of batch B?** The fresh
   QC found that six findings were introduced or left by batch A itself (Q2-01, Q2-04,
   Q2-11, Q2-12, Q2-17, Q2-43) and four new Highs total (Q2-01…Q2-04). A tight batch B =
   those ten, nothing else — small, coherent, and it restores the "0 High" state before
   anything new is attempted. ▸ Recommend yes; everything else waits for batch C.
2. **Is the PHI family the top of batch C?** QC-21 (patient paths in `crash.log` on the
   share), QC-22 (WebView2 history DB of every previewed document), R4 + DW-37 (the same
   log's rotation and interleaving) are the only open items touching patient data — for
   this app that is the reputational tail risk. ▸ Recommend yes, immediately after batch B.
3. **`docs/sample/` — may I move the 412 real exports to `S:\OrdoSort-samples` now?**
   (DW-13; one `Move-Item`; a QC subagent already read a row of one CSV, which is the
   exact predicted failure.) ▸ Recommend yes, today, independent of any batch.
4. **Are `docs/FileMover.py`, `docs/paper_mover_logger.py`, `docs/RemoveReadOnly.ps1`
   still used?** (DW-14) If retired → archive them out of the tree. If used → their logs
   (full document paths, in-tree, un-ignored) need redirecting. Only you know which.

## B · Deployment facts that decide severities (only you know these)

5. **How many stations run OrdoSort concurrently against one share today — really?**
   Moves: QC-28/QC-29 (peer id collisions), R2 (torn backup), Q2-21 (5-second Label Maker
   open), DW-37. If the honest answer is "one, usually", half the multi-station Importants
   drop below the fold; if "three every day", QC-28/29 rise.
6. **Are the inbox and the route destinations on different volumes/shares in production?**
   Moves: QC-03's residual risk, Q2-04, DW-01. The experiments proved the kill-mid-copy
   corruption on local volumes; if production filing is same-volume, DW-01's window
   narrows to renames; if cross-share, it is real and Q2-04's trigger is live.
7. **Does anyone hand-edit `config.json` / `destinations.json` in practice?** (The docs
   call it supported.) Moves: Q2-35 (duplicate hotkeys), Q2-40 (`history_db` dead-end),
   D2 (drift blocks startup), QC-29 (verbatim legacy ids). If nobody ever does, these are
   Minors in practice; if it's routine, D2 and Q2-35 climb.
8. **Are the printers network queues or local?** Moves: Q2-22 (constructor stall), QC-15
   ([U] on whether the driver honours `CopyCount`), DW-42.
9. **Is the Copies field in the print preview ever deliberately used?** (QC-15.) If not:
   the fix is deletion, which is far cheaper than making the claim loop honour it.
   ▸ Recommend deleting it unless you know someone uses it.
10. **Does anything downstream consume PageCounts exports for real numbers** (billing,
    manifests)? Moves Q2-10 between "Important" and "quietly High".

## C · Product calls the audits can't make

11. **Q2-03's fix shape: where should "two config folders are the same place" be
    refused?** Options: (a) Settings OK hard-errors when set-aside/inbox/route paths
    collide (the QC-08 machinery is sitting right there); (b) `Commit` refuses at filing
    time; (c) both. ▸ Recommend both — OK for visibility, Core for safety, matching the
    QC-02 precedent.
12. **QC-30's admin-wins silence: what should re-pointing a side-file feel like?** A
    prompt ("adopt the shared file's routes? your edits to this section will be
    dropped"), or a post-OK notice? Blocks the QC-30 fix from starting.
13. **The blank-set-aside nag (Q2-43): what's the right calm state for a station that
    never skips?** Options: warn once per session; warn only when a skip is attempted
    (Core already refuses — QC-02's fix); an explicit "no set-aside folder" choice in
    Settings. ▸ Recommend warn-on-first-skip only.
14. **Manifest spec: green-light or keep deferred?** It closes FL-07/09/10/11/12/30 in
    one build (6 of the 24 FL items) and is written and reviewed. If refinement is the
    theme of the month, this is the highest-leverage single yes. ▸ Recommend scheduling
    it as its own batch after the PHI family; say no and the six stay piecemeal.
15. **The FL backlog (24 items): one dedicated Filename-List pass, or fold into general
    batches?** ▸ Recommend one pass — same files, same tests, huge dedupe of effort.
16. **Reports: is revival plausible?** If "no, ever": the archive branches
    (`feature/reports-hub-phase2`) can be noted as cold storage and the obsolete section
    frozen. If "maybe": nothing changes. One sentence from you settles it.
17. **Severity ratifications: R2, R3, Q2-35 to High?** All three have an
    arguable "lies to the user / loses work" reading recorded on the row; the sources
    graded them Important. Your call is the tie-break — the ladder is yours.

## D · Cheap measurements that close standing [U]s (work, not decisions — pick which to fund)

18. **An SMB session of the four experiments** (repeat the kill/read-only/locked moves and
    the stall probes against the real share): settles QC-03's silent branch, Q2-04's
    trigger, R2's torn-copy realism, and the stall [U]s on Q2-20/22/24/25 in one sitting.
    ▸ Highest-value hour on the [U] list; needs the share reachable from a test station.
19. **Open the WebView2 History DB read-only** (QC-22's [U] — deliberately not done
    during the audits): confirms whether document *paths* are recorded, which decides
    whether QC-22 is a privacy item or a disk-hygiene item. Ten minutes.
20. **A two-station Label Maker rehearsal** (QC-28/29, Q2-21): only worth staging if the
    answer to question 5 is "more than one".
21. **`e2e.bat` under the DW-77 question** — "what did each of the 38 scenarios actually
    demonstrate?": the unit coverage suites just got this treatment and yielded six
    findings (Q2-14…Q2-19); the e2e suite never has.

## E · Standing process questions

22. **Write the flake ledger now?** DW-56/57 (two known flakes not yet in
    `docs/known-flakes.md`) plus the WPF exit-hang workaround from memory — one small
    docs commit, no code. ▸ Recommend yes, fold into the next batch's setup task.
23. **Fix the two defect *classes* as classes?** DW-78 (batch-mutated-under-a-live-list —
    now 8+ known instances incl. Q2-05/06/07/12) and Q2-44 (`OnError` unwired — 6 VMs):
    one seam-level task each (a shared busy-gate idiom; a required-OnError constructor)
    beats a dozen point fixes. ▸ Recommend yes; it's also the only approach that stops
    instance #9.
24. **Floors and corners for the probe suites** (Q2-15…Q2-19, Q2-36/37, Q2-45/46): these
    are test-only changes, safe to batch with anything. Fold into batch B's Task-1-style
    opener so later fixes are verified by suites that measure? ▸ Recommend yes — same
    reasoning as batch A's Task 1 ordering.

---

## How the answers become the todo list

- Answers to **A1–A2** fix the contents of batch B (repair + Highs) and batch C (PHI).
- **A3–A4** are same-day chores, no batch needed.
- **B5–B10** re-rank the 81 Importants: each answer moves its named IDs up or below the
  fold — I'll re-sort the checklist's Important section under a "batch D candidates"
  cut-line from your answers.
- **C11–C13** unblock three fixes that are otherwise ready to start; **C14–C16** decide
  whether two whole clusters (manifest build, FL pass) enter the queue; **C17** finalises
  the High count.
- **D18–D21** are fundable hours that convert [U]s into [V]s or closures — each one
  either promotes an item into a batch or retires it.
- **E22–E24** shape *how* the batches are built rather than what's in them.

Answer with question numbers ("1 yes, 5: two stations, 9: delete it…") and the fresh todo
list — batch B plan first — comes back ordered, scoped, and cited.
