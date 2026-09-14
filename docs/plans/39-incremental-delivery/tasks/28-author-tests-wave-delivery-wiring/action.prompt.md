## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `28-author-tests-wave-delivery-wiring`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "28-author-tests-wave-delivery-wiring": { "someKey": "someValue" } }`.
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

Author failing tests for the wave delivery WIRING — design 39 §4 ("The record, pinned at review") and §5
("Wiring and halts"). Tasks 09–13 build the record, its write path and the `WaveDelivered` event, and task
08 delivers at the barrier, but nothing connects them. The first breakdown built both and wired neither:
#120's shape, a feature green in every unit test and dead in the product. These tests drive the REAL
`Scheduler` and read what it left on disk.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryWiringTests.cs`
**Test class:** `WaveDeliveryWiringTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Write this stub, and keep it WORKING:** `RunReport.WaveDeliveries` in
`src/Guardrails.Core/Execution/RunReport.cs`, keyed by wave directory:

```csharp
public IReadOnlyDictionary<string, Journal.WaveDeliveredRecord> WaveDeliveries { get; init; } =
    new Dictionary<string, Journal.WaveDeliveredRecord>();
```

It must NOT throw. Hundreds of tests construct and print `RunReport`, and a throwing getter breaks every
one of them. The red comes from the Scheduler never filling it; task 29 does that.

**What your base already does.** Task 08 owns the barrier delivery DECISION, and it is on your base. At a
wave whose `WaveNode.IsDeliveryPoint` is true (`delivers: true` and at least one exit-gate check), it first
applies the delivery predicate it shares with `Scheduler.Finalize`: `mergeOnSuccess`, the #361 interlock
over every wave the delivery carries (lifted by the operator's `--merge-on-success`), and the serial-mode
guard. Only then does it call `CreateTrialDelivery`, gate the trial, call `PromoteTrialDelivery` (skipped
when the trial reports `AlreadyDelivered`), and call `DiscardTrialDelivery` in a `finally`. What your base
does NOT do is write `waves.<dir>.delivered`, raise `WaveDelivered`, or fill `RunReport.WaveDeliveries`.
Task 29 adds those, which is why every row below that asserts one of them is red on your base.

**The fixture.** Build it from what the house already has, and read `SchedulerWaveExecutionTests` first.
- **The plan:** `WavePlanBuilder` — `Task`, `WaveBrief` carrying `delivers: true` in the brief's YAML front
  matter on each delivering wave (the form `WaveDeliversFlagTests` uses), and `WaveGuardrail` giving every
  delivering wave an exit gate: a `delivers: true` wave without one is not a delivery point (GR2079 warns
  about exactly that). With `reVerifier: null` the Scheduler counts every wave gate as passed
  (`RunWaveExitGateAsync`), so a gate that must FAIL needs a test-local `IReVerifier` that fails only that
  wave's exit guardrails and passes every other call. `IReVerifier.ReVerifyAsync` is handed the worktree
  path it gates, so the same seam can fail a gate only on a trial's `WorktreePath`. Change the run's configuration on the loaded plan
  with `plan with { Config = plan.Config with { ... } }`, the form `SchedulerWaveExecutionTests` uses for
  `AutonomyPolicy`.
- **The journal:** a real `RunJournal.LoadOrCreate(plan)`. Read the record back from `run.json` on disk
  (`RunJournal.JournalPath`) through `JournalJson.Options` — never from the in-memory document, which
  cannot tell a persisted record from an unpersisted one. A row that SEEDS a record writes it through
  `RunJournal.RecordWaveDelivery` (task 10) before any Scheduler exists, then builds the Scheduler over a
  fresh `RunJournal.LoadOrCreate(plan)`, the way a resumed process would.
- **The executor:** a fake shaped like `SchedulerWaveExecutionTests`' `WaveFakeExecutor`. It can fail named
  tasks, and it can plant a decision by calling `RecordDecision` on the journal with `DecisionEntry.Wave`
  naming the wave (task 06).
- **The provider double.** `RecordingWorktreeProvider` is sealed, so write a test-local `IWorktreeProvider`
  that wraps one and delegates to it. Delegate EXPLICITLY every member the recorder implements, including
  default-bodied ones like `CommitWaveMarker`: an interface default does not fall through to the wrapped
  instance. The three trial-delivery members keep THROWING default bodies on `IWorktreeProvider` (task 31),
  so the double implements all three itself:
  - `CreateTrialDelivery` counts its calls, reads `run.json` FROM DISK at call time and keeps what it found
    for that wave, and returns a trial for the wave with `UserTipWasAncestor = true`, `WorktreePath` null,
    `AlreadyDelivered` false unless a test asks, and a `Commit` that is recognizable and is neither the
    plan-branch tip nor any marker sha (for example `trial-<waveDir>`). When a test asks, it returns a
    `Refusal` with its `RefusalDetail` instead.
  - **Report `UserTipWasAncestor = true` in every row but one, and say why in a comment.** Task 15's
    post-delivery refresh runs after a promotion fast-forwards a trial whose `UserTipWasAncestor` is false,
    and it runs git in the integration worktree. `RecordingWorktreeProvider`'s integration path is
    `integ://<runId>/_integration`, which is not a directory, so a false value on a promoted trial would send
    that row into the #150 fault path once task 15 lands. The one exception is
    `AFailedTrialTreeGate_IsRecordedRefused_AndIsNeverPromoted`: its trial fails its gate, nothing is
    promoted, and so task 15's refresh never runs there. The recognizable `Commit` is what lets row 2 tell
    the trial's commit from the plan-branch tip.
  - `PromoteTrialDelivery`, the member that moves the user's branch, reads `run.json` FROM DISK at call
    time and keeps what it found for that wave, counts its calls, and returns the result the test
    configured (`FastForwarded` by default), with a refusal's detail on `LastMergeOnSuccessDetail`.
  - `DiscardTrialDelivery` does nothing.
- **The observer:** a test-local `IRunObserver` whose `WaveDelivered(wave, delivery)` records the wave, the
  instance, and the record it reads from `run.json` on disk at call time.

**Pin these behaviours to these EXACT method names.** Each names the cheapest wrong implementation it
rejects.

1. `TheDeliveryIsJournaledRunning_BeforeTheTrialIsBuilt` — one delivering wave. Inside
   `CreateTrialDelivery`, and again inside `PromoteTrialDelivery`, the record on disk reads
   `status: running`, with `startedAt` set, `covers` already filled, and `at`, `commit` and `outcome` all
   null. Rejects: a single write after the promotion (#625), and a `running` write placed after the trial
   is built. The trial merge runs the user's git hooks, so it is already something the operator can see.
2. `ADeliveredWave_IsRecordedDeliveredWithThePromotedCommit` — after the run, the record on disk reads
   `delivered`, with `at`, `outcome: fast-forwarded`, and `commit` equal to the trial's `Commit`. Rejects:
   a record left at `running`, and the plan-branch tip recorded as the commit.
3. `ADeliveryCovers_EveryWaveSinceTheLastDelivery` — waves 01, 02 and 03, where only 02 and 03 deliver.
   Wave 02's `covers` is `[01, 02]`, wave 03's is `[03]`, and wave 01 has no `delivered` key. Rejects:
   listing only the delivering wave, or every wave in the run.
4. `CoversAfterAResume_StillStartsAfterTheLastDeliveredWave` — waves 01 and 03 deliver, and 02 does not.
   The first run delivers 01 and stops in wave 03, through a failing wave-03 task or a failing wave-03
   exit gate. Resume with a NEW `Scheduler` over `RunJournal.LoadOrCreate(plan)`, with nothing failing.
   Wave 03's `covers` is `[02, 03]`. Rejects: computing `covers` from in-memory state a resume discards.
   That state yields `[03]` when it counts only the waves this process ran, and `[01, 02, 03]` when it
   restarts from the first wave.
5. `TheSchedulerRaisesWaveDelivered_AfterTheRecordIsPersisted` — TWO runs in this one test, each over its
   own plan and journal. **Run A:** waves 01 and 02 both deliver, and a wave-02 task plants a
   `DecisionTokens.ProceededBestGuess` decision attributed to wave 02. The observer is called exactly
   once, for wave 01; the record it reads from disk at call time is already `delivered`, and the instance
   it is handed matches that record. Wave 02's suppressed delivery raises nothing. **Run B:** one
   delivering wave whose promotion returns `BranchMoved`; nothing is raised. Keep the two apart: after
   task 17 a refusal halts the run, and after a suppression every later delivery in the run is held too,
   so neither can share a run with a delivery that must still raise. Rejects: raising before the write
   (#513), raising on a refusal or a suppression, and never raising (#120).
6. `ARefusedDelivery_IsRecordedRefusedWithItsOutcome_AndRaisesNoEvent` — the double's promotion returns
   `BranchMoved` with a detail. The record on disk reads `refused`, with `outcome: branch-moved`, that
   detail, `at`, and no `commit`, and `WaveDelivered` is never raised. Assert the RECORD only — not the
   wave's status, whether the run halts, or what later waves do — so the test holds both before and after
   task 17 adds the halt.
7. `ASuppressedDelivery_IsRecordedSuppressed_AndNeverMovesTheUsersBranch` — waves 01 and 02, where only 02
   delivers. A wave-01 task plants a `DecisionTokens.ProceededBestGuess` decision attributed to wave 01.
   Wave 02's record reads `suppressed`, its `detail` names the decision and its subject, and it has no
   `commit`; neither `CreateTrialDelivery` nor `PromoteTrialDelivery` was called. Rejects: consulting the
   interlock after the trial is built or after the promotion, and consulting only the delivering wave's
   own decisions (review round 4: a held wave's work riding along holds the delivery). The two
   never-called assertions describe what task 08 already does; this row is red on your base because no
   `suppressed` record is written.
8. `AHaltedRunsReport_StillCarriesEarlierWaveDeliveries` — waves 01, 02 and 03, where 02 delivers and
   wave 03's exit gate fails. The returned report carries a `WaveHalt`, and its `WaveDeliveries` holds
   wave 02 as `delivered` and nothing for 01 or 03. Rejects: stamping the map only in `Finalize`, which a
   barrier halt returns before.
9. `APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone` — a waved plan none of whose waves sets
   `delivers: true`. No wave in `run.json` has a `delivered` key, the report's `WaveDeliveries` is empty,
   and `WaveDelivered` is never raised. This is the never-weaker requirement.
10. `ATrialThatCannotBeBuilt_IsRecordedRefused_AndIsNeverPromoted` — one delivering wave. The double's
   `CreateTrialDelivery` returns a trial whose `Refusal` is `HookRejected`, with a `RefusalDetail`. The
   record on disk reads `refused`, with `outcome: hook-rejected`, that detail, `at`, and no `commit`;
   `PromoteTrialDelivery` is never called and `WaveDelivered` is never raised. Assert the record and the
   calls only, not the wave's status or what follows, so the test holds before and after task 17 adds the
   halt. Rejects: gating or promoting a trial that was never built, and dropping a refusal the user's own
   hook raised before any gate ran.
11. `AResumeAfterACrashMidDelivery_RecordsAnAlreadyDeliveredTrialAsDelivered` — one delivering wave. Seed
   that wave's record as `running`, with `startedAt` and `covers`, the state a crash between the `running`
   write and the settle leaves behind, then run a Scheduler over the reloaded journal. The double's
   `CreateTrialDelivery` returns `AlreadyDelivered = true`: the wave's commits already reached the user's
   branch before the crash. The record on disk reads `delivered`, with `outcome: fast-forwarded` and
   `commit` equal to the trial's `Commit`; `PromoteTrialDelivery` is never called; `WaveDelivered` is
   raised exactly once. Rejects: settling a delivery that already landed as `refused` (review measured a
   resume that reported a git hook rejection for a hook that never ran, then halted every resume after),
   leaving it `running`, and never announcing a delivery that no process announced. The seeded record is
   `running`, not `delivered`, so this is the case with no prior `delivered` record: a fresh record,
   announced once.
12. `AResumeOverADeliveredRecord_KeepsItAndRaisesNoEvent` — one delivering wave. Seed that wave's record as
   `delivered`, with a distinctive `at` and `commit`, the state a crash after the settled write but before
   the wave marker leaves behind, then run a Scheduler over the reloaded journal with the double returning
   `AlreadyDelivered = true`. After the run the record on disk is `delivered` with the seeded `at` and
   `commit` restored verbatim, and `WaveDelivered` is never raised. The record may read `running` while the
   trial is built, because a barrier delivery that begins always journals `running` (#625); do not assert
   otherwise. Rejects: settling a fresh record over a delivery that already landed and was already recorded
   (a new `at` or `commit`), and announcing the same delivery twice.
13. `AForcedDelivery_NamesTheDecisionItOverrodeInItsDetail` — waves 01 and 02, where only 02 delivers. A
   wave-01 task plants a `DecisionTokens.ProceededBestGuess` decision attributed to wave 01, and the plan's
   `MergeOnSuccessForcedByOperator` is true, as the operator's `--merge-on-success` sets it. Wave 02's
   record reads `delivered`, with `commit` equal to the trial's `Commit` and a `detail` naming the
   decision's token and its subject. Rejects: a forced delivery that `run.json` records exactly like a
   clean one, which loses #597's durable trace of the override.
14. `ASerialWavedRun_NeverDeliversAtABarrier` — a Scheduler constructed with no worktree provider (serial
   mode, so no integration handle) over a waved plan with a delivery point. No wave in `run.json` has a
   `delivered` key, the report's `WaveDeliveries` is empty, and `WaveDelivered` is never raised. Rejects:
   journaling a delivery in a mode that has no separate plan branch to deliver.
15. `ABarrierDelivery_WithMergeOnSuccessOff_WritesNoRecord` — one delivering wave, with the plan's
   `MergeOnSuccess` false. No wave has a `delivered` key, neither `CreateTrialDelivery` nor
   `PromoteTrialDelivery` is called, `WaveDelivered` is never raised, and `WaveDeliveries` is empty.
   Rejects: a record, or a trial, for a delivery the operator turned off.
16. `AFailedTrialTreeGate_IsRecordedRefused_AndIsNeverPromoted` — one delivering wave, and the one row whose
   double returns `UserTipWasAncestor = false` with a `WorktreePath` (for example
   `trial-worktree://<waveDir>`): the user's branch moved, so the trial needed a merge commit and task 08
   gates it in that worktree. The test-local `IReVerifier` fails that wave's exit guardrails only when it is
   handed the trial's `WorktreePath`, and passes them on the integration worktree. The record on disk reads
   `refused`, with `outcome: trial-gate-failed`, `at`, no `commit`, and a `detail` naming each failing
   check and the user tip the trial merged (the trial's `UserTip`); `PromoteTrialDelivery` is never called
   and `WaveDelivered` is never raised. Assert the record and the calls only, not the wave's status or the
   halt, which task 17 owns. No promotion happens, so task 15's refresh never runs in this row. Rejects: a
   record left at `running` after the gate refused the merged tree, a gate failure recorded as a trial
   that could not be built, and promoting a tree whose gate failed.

**Four rows are exempt from the red census, not from existing.** Each is green on your base by
construction, and task 29's forward census requires each one Passed:

- `APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone` — nothing on your base writes the record, fills the
  map or raises the event, for any plan.
- `AResumeOverADeliveredRecord_KeepsItAndRaisesNoEvent` — nothing on your base writes a record or raises
  the event, so the seeded record survives untouched.
- `ASerialWavedRun_NeverDeliversAtABarrier` — nothing writes the record, and task 08's serial guard never
  reaches the barrier delivery.
- `ABarrierDelivery_WithMergeOnSuccessOff_WritesNoRecord` — nothing writes the record, and task 08's
  shared predicate never builds a trial when delivery is off.

Write them to assert the guarantee. Do NOT couple any of them to the missing wiring to force a red.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The tests MUST COMPILE, and the other twelve MUST FAIL. Do NOT wire the Scheduler.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryWiringTests.cs` and `src/Guardrails.Core/Execution/RunReport.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
