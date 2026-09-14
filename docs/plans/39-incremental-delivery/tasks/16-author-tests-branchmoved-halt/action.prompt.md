## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `16-author-tests-branchmoved-halt`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "16-author-tests-branchmoved-halt": { "someKey": "someValue" } }`.
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
`MergeOnSuccessResult.FastForwarded` and never returns `BranchMoved`, so a refusal is inexpressible on
the fake. A barrier delivery now goes through the trial-delivery members tasks 30/31 added to
`IWorktreeProvider`, and a refusal arrives by one of TWO routes. `CreateTrialDelivery` refuses while
building the trial, before any gate runs: a conflict with the user's branch, or the user's git hook
rejecting the trial merge commit (#149, which holds deliveries instead of halting; see below), returned as
the trial's `Refusal` with its `RefusalDetail`.
`PromoteTrialDelivery` refuses after the gate: #588 (`BranchMoved`, which covers both a switched checkout
and the user's branch advancing after the trial was built) or #448 (a dirty working tree), with the detail
on `LastMergeOnSuccessDetail`.

**Provoke each refusal with a script, never a provider double (review 2026-09-13).** A guardrail on this
task requires `new GitWorktreeProvider(` in code. Outside comments, it rejects any of these:
- `FakeWorktreeProvider` or `RecordingWorktreeProvider`;
- a string literal naming any provider type other than `GitWorktreeProvider` or `IWorktreeProvider`;
- a type implementing `IWorktreeProvider`, or a using-alias of `IWorktreeProvider`;
- `IWorktreeProvider` as the first type argument of a generic other than a delegate, `Lazy`, `Task`,
  `ValueTask`, a collection, `IsAssignableFrom` or `IsType`, so `Mock<>` and `Substitute.For<>` are out;
- `DispatchProxy`.

The house fixture is `WaveExecutionRunTests`: it writes a plan folder with a
`.ps1` or `.sh` script per OS and runs the real Scheduler over `new GitWorktreeProvider(repoPath,
worktreeRoot)`. A task or gate script can run git against the user's repo at the absolute path the fixture
writes into it:

- **A switched checkout:** a wave-02 task script checks out another branch in the user's repo.
- **A conflict:** a wave-02 task writes a file on the plan side, and its script commits different content
  at the same path on the user's branch, so the trial merge cannot be built.
- **A branch that advanced after the trial was built:** a wave-02 task script commits `teammate.txt` on the
  user's branch, so the trial needs a merge commit and its gate runs in the trial worktree. Wave-02's exit
  gate script commits again on the user's branch, but only when `teammate.txt` exists in its working
  directory. That is true in the trial worktree and false in the integration worktree, where the
  plan-branch gate runs first.
- **A hook that rejects only the trial:** install a `pre-commit` hook in the user's repo that exits non-zero
  unless an untracked `hook-ok.txt` exists in its working directory, and create that file in the user's
  checkout only. A wave-01 task script commits `teammate.txt` on the user's branch, and the hook passes there.
  The trial then needs a merge commit, which the harness builds in its own worktree, where `hook-ok.txt` is
  absent, so the hook rejects it. The run-end merge runs in the user's checkout, where the hook passes.

**Every fixture ends with a wave after the last delivering one.** The plan's final wave never delivers at its
barrier (review round 5, `d39-barrier-terminal-gate`), so a refusal pinned at wave-02's barrier needs a
wave-03.

Author failing tests for the behaviour DECIDED in review — design 39 §1c and §4 (round 4).

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/BranchMovedHaltTests.cs`
**Test class:** `BranchMovedHaltTests`
**Stub:** add one member, `DeliveryRefused`, to `WaveHaltKind` in
`src/Guardrails.Core/Execution/RunReport.cs`, so the tests compile. An enum member is its whole
declaration; the red comes from the Scheduler never producing it, which task 17 fixes.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Why this changes character under per-wave delivery.** #588 pinned the delivery target at run start
and made a moved HEAD a REFUSAL rather than a redirect — correct, and the incident behind it was real.
But today that refusal fires ONCE, at run end, with every task complete and safely on the plan branch:
a soft landing. Per-wave it fires at a wave's barrier with later waves still to run, and **the condition
is not transient** — the operator checked out a different branch, so every later delivery hits the
identical refusal.

DECIDED (design 39, round 4, narrowed in round 5): **a refused wave delivery halts the run at that wave** —
a moved branch, a conflict or a dirty working tree — under the new `WaveHaltKind.DeliveryRefused`. Two
outcomes do NOT halt here. A hook-rejected trial holds this and every later barrier delivery to run end,
where the merge runs the user's hooks in their own checkout (`d39-hooks-untracked-tooling`). A failed
trial-tree gate is an exit-gate halt that task 08 raises (`d39-trial-gate-failure`). No `RunHaltKind` is added: run.json's `halt` section is scoped to gates
(#432), so the refusal is durable on the wave instead. The wave's completed marker is written only after
its delivery settles, so a resume re-attempts a refused delivery at that wave. Task 29 already records
the refusal as `refused` on `waves.<dir>.delivered`; this suite pins the HALT.

**Pin these behaviours to these EXACT method names:**

- `ADeliveryHittingBranchMoved_HaltsTheRunAtThatWave`
- `LaterWavesDoNotRun_AfterABranchMovedHalt` — the point of halting: continuing pays for waves whose
  delivery is already known to be impossible.
- `TheHaltNamesThePinnedTargetAndTheCurrentHead` — an operator cannot act on "delivery refused" alone. The
  checkout switched to another branch: the halt headline carries the provider's detail,
  `run started on '<branch>'; HEAD is now '<other>'`, and the halt's `Detail` contains
  `check out '<branch>' again`, the remedy for this cause.
- `TheHaltKindIsDeliveryRefused_NotAGateFailure` — rejects reusing `ExitGateFailed`, which the console
  prints as "WAVE EXIT GATE FAILED" over a wave whose every check passed.
- `TheRefusalIsDurable_OnTheWaveNotInHalt` — the wave's `delivered` record reads `refused`, the wave's
  status is needs-human, and run.json has NO `halt` section. Rejects a gate `halt` with zero failed
  checks.
- `AConflictingWaveDelivery_AlsoHaltsAtThatWave` — rejects halting on `BranchMoved` alone. The conflict
  surfaces when the trial is BUILT (`CreateTrialDelivery` returns a `Refusal`), so this row also rejects a
  halt wired only to `PromoteTrialDelivery`'s result.
- `AHookRejectedTrial_DoesNotHaltTheRun_AndHoldsLaterDeliveries` — the one refusal that does NOT halt. A
  rejecting hook may need tooling or untracked files that exist only in the user's own checkout, so the run
  holds this and every later barrier delivery to run end, where the merge runs the hook there. Use the hook
  fixture above with three waves: wave-01 and wave-02 carry `delivers: true`, and wave-03 is the final
  wave. Each wave's task writes its own file on the plan side. Wave-01's task script also writes the user's
  tip after its commit to a sentinel file, and wave-03's task script writes the user's branch tip to a
  second sentinel. Assert all of:
  - wave-01's `delivered` record reads `refused` with outcome `hook-rejected`, so the hook really rejected
    the trial, and wave-02's reads `suppressed`;
  - the run has no `DeliveryRefused` halt, and wave-03 ran;
  - the two sentinels hold the same sha, so neither barrier promoted anything;
  - after the run, the user's branch contains all three waves' files, so the run-end delivery landed.

  It rejects halting on `hook-rejected` like the other refusals, and a later barrier that delivers past the
  held wave.
- `TheHaltNamesBothTips_WhenTheUsersBranchAdvancedAfterTheTrial` — the other `BranchMoved` cause: the user's
  branch gained a commit after the trial was built, as a user keeps working while the gate runs. Assert all of:
  - the halt headline contains `'<branch>' moved from <sha10> to <sha10> after the trial was built`, where
    the first `<sha10>` is the first 10 characters of the tip the trial was built from and the second is the
    user's new tip;
  - the halt's `Detail` contains `resume` and does NOT contain `check out`;
  - the user's branch still points at their new commit.

  It rejects one detail text for both causes, which would send an operator who only committed to check out a
  branch they never left.
- `ARefusedDelivery_RecordsAHaltedDecision` — `run.json`'s `decisions[]` gains exactly one entry whose `gate`
  is `delivery-refused`. That entry has `boundary` `wave`, `decision` `halted`, `subject` and `wave` both set to
  the refused wave's directory, and a `headline` equal to the halt's headline. It rejects a halt whose cause is
  durable only in the wave's `delivered` record, and a suppressing token (`proceeded-best-guess`,
  `proceeded-unreviewed`) that would hold every later delivery.
- `AResumeAfterARefusedDelivery_ReattemptsItAtThatWave` — rejects marking the wave complete before its
  delivery settles, which lets a resume skip the refused delivery forever.
- `AlreadyDeliveredWavesStayDelivered` — the halt must not try to unwind merges that already landed on
  the user's branch.
- `TheUsersCheckoutIsNotModified` — #588's safe direction: refusing leaves the checkout untouched
  rather than checking the pinned branch back out and stomping a deliberate switch.

`AlreadyDeliveredWavesStayDelivered` and `TheUsersCheckoutIsNotModified` are the never-weaker halves,
and both are declared EXEMPT from the red census: nothing on this task's base unwinds a merge that
already landed, and the #588 refusal never checks the pinned branch back out (neither at run end nor in
task 31's promotion re-check at a barrier), so correct tests of either are green on arrival. Write them
to assert the guarantee, not to fail.

`AHookRejectedTrial_DoesNotHaltTheRun_AndHoldsLaterDeliveries` is declared EXEMPT too, and it is green on
this task's base for four reasons:
- nothing halts on any refusal until task 17;
- task 08 already holds later barrier deliveries after a hook rejection;
- task 29 already records the rejection;
- the run-end delivery already lands.

Its teeth are task 17's forward census, which requires it Passed, so task 17 cannot halt on a hook
rejection. All three exempt tests must still exist, and task 17's forward census requires all twelve
Passed.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The other nine tests MUST COMPILE and FAIL. Do NOT implement the halt.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/WaveDelivery/BranchMovedHaltTests.cs` and
`src/Guardrails.Core/Execution/RunReport.cs`. After this task completes, the harness runs a `git diff`
membership check and rejects any edit outside these paths. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
