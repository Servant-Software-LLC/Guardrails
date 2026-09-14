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

**The fixture.** Build it from what the house already has, and read `SchedulerWaveExecutionTests` first.
- **The plan:** `WavePlanBuilder` — `Task`, `WaveBrief` carrying `delivers: true` in the brief's YAML front
  matter on each delivering wave (the form `WaveDeliversFlagTests` uses), and `WaveGuardrail` giving every
  delivering wave an exit gate: GR2079 exists because a `delivers: true` wave without one cannot deliver.
  With `reVerifier: null` the Scheduler counts every wave gate as passed (`RunWaveExitGateAsync`), so a
  gate that must FAIL needs a test-local `IReVerifier` that fails only that wave's exit guardrails and
  passes every other call.
- **The journal:** a real `RunJournal.LoadOrCreate(plan)`. Read the record back from `run.json` on disk
  (`RunJournal.JournalPath`) through `JournalJson.Options` — never from the in-memory document, which
  cannot tell a persisted record from an unpersisted one.
- **The executor:** a fake shaped like `SchedulerWaveExecutionTests`' `WaveFakeExecutor`. It can fail named
  tasks, and it can plant a decision by calling `RecordDecision` on the journal with `DecisionEntry.Wave`
  naming the wave (task 06).
- **The provider double.** `RecordingWorktreeProvider` is sealed, so write a test-local `IWorktreeProvider`
  that wraps one and delegates to it. Delegate EXPLICITLY every member the recorder implements, including
  default-bodied ones like `CommitWaveMarker`: an interface default does not fall through to the wrapped
  instance. The double implements the three trial-delivery members itself (task 31 declares them; task
  08's barrier calls all three):
  - `CreateTrialDelivery` returns a trial for the wave whose `Commit` is a recognizable value that is
    neither the plan-branch tip nor any marker sha (for example `trial-<waveDir>`) — or, when a test asks,
    a `Refusal` with its `RefusalDetail`;
  - `PromoteTrialDelivery`, the member that moves the user's branch, reads `run.json` FROM DISK at call
    time and keeps what it found for that wave, counts its calls, and returns the result the test
    configured (`FastForwarded` by default), with a refusal's detail on `LastMergeOnSuccessDetail`;
  - `DiscardTrialDelivery` does nothing.
- **The observer:** a test-local `IRunObserver` whose `WaveDelivered(wave, delivery)` records the wave, the
  instance, and the record it reads from `run.json` on disk at call time.

**Pin these behaviours to these EXACT method names.** Each names the cheapest wrong implementation it
rejects.

1. `TheDeliveryIsJournaledRunning_BeforeTheUsersBranchMoves` — one delivering wave. Inside
   `PromoteTrialDelivery`, the record on disk reads `status: running`, with `startedAt` set and `covers`
   already filled. Rejects: a single write after the promotion (#625).
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
5. `TheSchedulerRaisesWaveDelivered_AfterTheRecordIsPersisted` — the observer is called once per
   delivered wave, with that wave, and the record it reads from disk at call time is already `delivered`.
   The instance it is handed matches that record. In the same test, a refused promotion and a suppressed
   delivery raise nothing. Rejects: raising before the write (#513), raising on a refusal or a
   suppression, and never raising (#120).
6. `ARefusedDelivery_IsRecordedRefusedWithItsOutcome_AndRaisesNoEvent` — the double's promotion returns
   `BranchMoved` with a detail. The record on disk reads `refused`, with `outcome: branch-moved`, that
   detail, `at`, and no `commit`, and `WaveDelivered` is never raised. Assert the RECORD only — not the
   wave's status, whether the run halts, or what later waves do — so the test holds both before and after
   task 17 adds the halt.
7. `ASuppressedDelivery_IsRecordedSuppressed_AndNeverMovesTheUsersBranch` — waves 01 and 02, where only 02
   delivers. A wave-01 task plants a `DecisionTokens.ProceededBestGuess` decision attributed to wave 01.
   Wave 02's record reads `suppressed`, its `detail` names the decision and its subject, it has no
   `commit`, and `PromoteTrialDelivery` was never called. Rejects: consulting the interlock after the
   promotion, and consulting only the delivering wave's own decisions (review round 4: a held wave's work
   riding along holds the delivery).
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
   `PromoteTrialDelivery` is never called and `WaveDelivered` is never raised. Rejects: gating or promoting
   a trial that was never built, and dropping a refusal the user's own hook raised before any gate ran.

`APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone` is exempt from the red census. Nothing on your base
writes the record, fills the map, or raises the event for any plan, so a correct test is green on arrival.
It must still exist, and task 29's forward census requires it Passed. Do NOT couple it to the missing
wiring to force a red.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The tests MUST COMPILE, and the other nine MUST FAIL. Do NOT wire the Scheduler.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryWiringTests.cs` and `src/Guardrails.Core/Execution/RunReport.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
