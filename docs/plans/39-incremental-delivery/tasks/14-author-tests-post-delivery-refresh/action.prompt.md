## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `14-author-tests-post-delivery-refresh`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-author-tests-post-delivery-refresh": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

**Drive the REAL `GitWorktreeProvider` over temp repos — the house fake cannot express this
(review, 2026-09-11).** `FakeWorktreeProvider.MergePlanBranchIntoUserBranch` HARDCODES
`MergeOnSuccessResult.FastForwarded` and makes no commits, so on the fake
`AFastForwardDelivery_DoesNotRefresh` is trivially green and every other behaviour below is
**inexpressible**: the refresh is a real merge commit, and its parent order and trailer are what these
tests pin. House precedent agrees: 26 test files construct `new GitWorktreeProvider` against temp repos,
3 use the fake.

Author failing tests for the refresh DECIDED in review — design 39 §1c, including its subsection
**"How a refresh is recorded (post-plan-40 refinement)"**, which fixes the record, the commit shape and
the gate-halt disclosure pinned below.

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/PostDeliveryRefreshTests.cs`
**Test class:** `PostDeliveryRefreshTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The decision and its precise trigger.** Today the plan branch is CONTINUOUS: `RunWavedAsync` drains
every wave on it and `MergePlanBranchIntoUserBranch` is one-directional, so delivery publishes but does
not synchronise. DECIDED: **refresh only when the user's branch moved** — its tip was NOT an ancestor
of the plan-branch tip at delivery time.

**What "fast-forward delivery" means in these test names — read this before writing any assertion.**
Under §1's trial merge the promotion of the user's branch is ALWAYS a fast-forward to
`refs/guardrails/trial/<waveDir>`, so `MergeOnSuccessResult.FastForwarded` is true on every delivery
and discriminates nothing. The names keep the design's vocabulary:
- a **fast-forward delivery** is one where the user's tip WAS an ancestor of the plan-branch tip
  (nothing moved, so the trial merge had nothing to merge);
- a **non-fast-forward delivery** is one where it was NOT (a teammate commit landed on the user's
  branch mid-run, so the trial merge had to create a merge commit).

A test that builds its quiet case by asserting on `FastForwarded` pins nothing.

**The fixture.** Real `GitWorktreeProvider` over a temp repo. Wave-01's `brief.md` front matter carries
`delivers: true`. For every non-fast-forward scenario, the user's branch gets a REAL commit adding
`teammate.txt` while the run is in flight (after the plan branch is cut, before wave-01 delivers), so the
delivery is not a fast-forward by the ancestry definition above.

**Make every mid-run change with a script, never a provider double (review 2026-09-13).** A guardrail on
this task rejects the test file if, outside comments, it uses `FakeWorktreeProvider` or
`RecordingWorktreeProvider`, or declares or mocks an `IWorktreeProvider`; it also requires
`new GitWorktreeProvider(`. The house fixture is `WaveExecutionRunTests`: it writes a plan folder with a
`.ps1` or `.sh` script per OS and runs the real Scheduler over `new GitWorktreeProvider(repoPath,
worktreeRoot)`. A task or gate script can run git against the user's repo at the absolute path the fixture
writes into it, so the teammate commit is made by a wave-01 task script.

**Why it matters, so the tests assert the right thing.** Once the user's branch advances
independently, delivery 2 becomes a merge commit on their branch only, delivery 3 can no longer
fast-forward, and each later delivery merges a plan branch one more wave out of date — with AI-merge
withheld by SSOT §5.3, a conflict HALTS the run. And the refresh admits content NO TASK AUTHORED into the
tree the next wave's gates run over, so a broken teammate commit must be NAMED when a gate fails, never
silently blamed on the wave.

**Pin these behaviours to these EXACT method names:**

- `AFastForwardDelivery_DoesNotRefresh` — the quiet case stays cheap: assert NO extra merge commit on the
  plan branch, AND that `run.json` has no `refreshed` section at all (absent, not an empty array).
- `ANonFastForwardDelivery_RefreshesThePlanBranch`
- `AfterARefresh_TheNextWaveBuildsOnTheUsersNewCommits`
- `TheRefreshIsRecordedAsProvenance` — assert ALL of:
  - `run.json` carries exactly ONE `refreshed` entry and NO `supplied` section;
  - `from` is the pinned delivery branch, and `deliveredWave` is wave-01's directory name;
  - `upstream` equals the user's branch tip after delivery AND equals `<commit>^2`;
  - `commit` appears in `git log --first-parent guardrails/<plan>` — the plan branch was merged INTO,
    never fast-forwarded onto the delivered commit — and therefore so does its first parent `<commit>^1`;
  - `<commit>^1` differs from `upstream`, and `teammate.txt` is ABSENT from `<commit>^1`'s tree
    (`git cat-file -e <commit>^1:teammate.txt` fails): the first parent is the plan side, not the
    user's. Do NOT assert that `<commit>^1` equals a plan-branch tip you captured yourself: the harness
    may commit on the plan branch between the wave's last task and the refresh, so a correct
    implementation can legitimately put a different plan-side commit there;
  - `paths` contains `teammate.txt`;
  - the commit message carries `Refreshed-From: <branch>` and `Guardrails-Run: <runId>`, and does NOT
    contain `Supplied-By:`.

  It must reject: fast-forwarding the plan branch onto the delivered commit; a `supplied[]` entry with
  `by: "refresh"`; the pre-delivery user tip or the trial ref recorded as `upstream`; a merge by branch
  name that records the plan branch as `from`.
- `AnEntryGateFailureOverARefreshedTree_NamesTheRefresh` — wave-02 carries an entry preflight that FAILS
  when `teammate.txt` exists. Assert the run's `WaveHalt.Headline` STARTS WITH
  `Wave '<wave-02 dir>' entry preflight FAILED: <check name>` (the failing check stays first) and CONTAINS
  both the delivery branch name and the first 10 characters of `upstream`; and assert `run.json`'s
  `halt.headline` is identical to it. It must reject: the note unit-tested but never called from
  `BuildGateHalt` (passing but blind, #382); the disclosure replacing the check names; the disclosure placed
  only in the detail.
- `AnExitGateFailureOverARefreshedTree_NamesTheRefresh` — the same assertions with NO entry preflight and a
  wave-02 EXIT gate that fails when `teammate.txt` exists; the headline starts with
  `Wave '<wave-02 dir>' exit gate FAILED: <check name>`. It must reject: wiring only the entry-gate halt.
- `TheRefreshLandsBeforeTheWaveMarker` — in the non-fast-forward scenario, read wave-01's `markerSha` from
  `run.json` (`waves.<wave-01 dir>.markerSha`) and assert that `git merge-base --is-ancestor
  <refreshed[0].commit> <markerSha>` succeeds. At a delivering barrier the order is: the delivery settles,
  then the refresh commit and its record, then the wave marker. It must reject: writing the marker first,
  which lets a crash between the two resume past a wave whose refresh never happened, so the next wave builds
  on the stale base with no `refreshed` record naming the difference.
- `AFailedRefresh_AbortsWithNoRecord_AndNoLaterWaveRuns` — make the refresh merge itself fail. Wave-01's
  exit gate script creates an UNTRACKED `teammate.txt` in its working directory when that file is absent,
  and exits 0. The plan-branch exit gate runs in the integration worktree, where the plan side has no such
  file, so the script leaves one there. The trial-tree gate runs in the trial worktree, where `teammate.txt`
  is tracked, so the script does nothing. After the promotion, merging `upstream` into the integration
  worktree would overwrite that untracked file, and git refuses. Give wave-02's task a script that writes a
  sentinel file at an absolute path outside the repo. Assert all of:
  - `RunAsync` RETURNS a report whose `Abort` is set, the #150 honest-halt report, rather than throwing;
  - `run.json` has no `refreshed` section;
  - wave-01's `delivered` record still reads `delivered`, since the delivery itself landed;
  - the sentinel does not exist, so wave-02 never ran.

  It must reject: continuing to wave-02 on the stale base, recording a refresh that has no commit, and
  cleaning the integration worktree to force the merge through.

Assert through the real journal on disk (`run.json`) and real git (`git log`, `git rev-parse`), never
through a double. `RefreshedRecord`, `RunJournal.RecordRefreshed` and `UnauthoredContentNote` already
exist and are covered by `RefreshProvenanceTests` (tasks 24/25); this suite proves the Scheduler actually
refreshes, records, and names.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

`AFastForwardDelivery_DoesNotRefresh` is declared exempt from the red census: in the quiet case nothing
moved, so the current code, which never refreshes, already leaves a correct test of it green. It must still
exist, and task 15's forward census requires it Passed. The other seven tests MUST COMPILE and FAIL. Do NOT
implement the refresh.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/WaveDelivery/PostDeliveryRefreshTests.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

