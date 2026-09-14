## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `05-author-tests-wave-scoped-interlock`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-author-tests-wave-scoped-interlock": { "someKey": "someValue" } }`.
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

Author failing tests for the interlock scoping DECIDED in review — design 39 §1a, sharpened in review
round 4 (`d39-interlock-ride-along`).

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs`
**Test class:** `WaveScopedInterlockTests`
**Stub files:** `src/Guardrails.Core/Execution/DecisionEntry.cs` and `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`

**The design's premise was FALSE, and the correction is your first stub (review, 2026-09-11).** §1a
said *"`decisions[]` entries carry a wave attribution already"*. They do not. `DecisionEntry`
has eighteen public members and none is a wave; `Subject` is free text documented as *"a task id
/ wave dir / the drifted unit(s)"*, and only the `Boundary = "wave"` factories put a wave dir
there. The `task` boundary, where the needs-human gate records a `proceeded-best-guess`, puts a
TASK ID. So deriving the wave by string-splitting a §14.2 wave-qualified id would be undocumented,
waved-plans-only, and silently wrong for exactly the boundaries that matter. Add a real `Wave` member
as a stub; task 06 populates it at the two sites that create a suppressing decision.

Declare it as a WORKING optional member, like the other optional members on `DecisionEntry`:

```csharp
/// <summary>The wave directory this decision concerned; null when it concerned no single wave.</summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? Wave { get; init; }
```

Keep the attribute. `run.json` is serialized with nulls included (`JournalJson` sets
`DefaultIgnoreCondition = Never`), so without it every decision the harness writes gains
`"wave": null`, against the additive-field promise in the comment above those members.

**The interlock's new entry point is your second stub (round 4, 2026-09-13).** A delivery carries every
wave since the previous delivery point (§1b), so the check takes the SET of waves the delivery covers,
never a single wave. Add these to `RunOutcomePolicy.cs`, beside the run-scoped pair and mirroring its
#597 coupling, both throwing `NotImplementedException`:

```csharp
public static DecisionEntry? SuppressingDecisionForDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
    throw new NotImplementedException();

public static bool SuppressesDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
    throw new NotImplementedException();
```

Document the contract task 06 implements: the first `proceeded-best-guess` / `proceeded-unreviewed`
decision whose `Wave` is one of `coveredWaves` — or whose `Wave` is null, because a suppressing decision
recorded outside any wave holds every delivery (the check fails closed) — else null. Leave the existing
single-argument `SuppressesDelivery` / `SuppressingDecision` untouched: the run-end interlock still uses
them. Every test below except the declared-exempt one calls the new pair, or reads a `DecisionEntry.Wave`
that no decision-creating site populates yet, and that is what makes it red on the stubs.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**What the interlock is, and why per-wave delivery would otherwise BYPASS it.** #361/#340: a run whose
result was shaped by a machine decision (`proceeded-best-guess` / `proceeded-unreviewed`) defaults
delivery OFF. Today that check is **run-scoped** — evaluated once, at the end. Per-wave delivery
merges BEFORE the run ends, so a wave could ship while a suppressing decision sits later in the run,
or a decision in wave 4 could retroactively suppress waves 1-3 that already landed.

**DECIDED (round 2, sharpened in round 4):** a delivery is held when **any wave it carries** recorded a
suppressing decision. The round-2 answer read literally — only the delivering wave's own decisions count
— would let a clean wave's delivery carry an earlier HELD wave's machine-decided commits onto the user's
branch, which is the #361 escape that answer ruled out.

**Pin these behaviours to these EXACT method names:**

- `AWaveWithNoSuppressingDecision_Delivers`
- `AWaveWithASuppressingDecision_DoesNotDeliver`
- `ADecisionInALaterWave_DoesNotRetroactivelySuppressAnEarlierDelivery` — the direction that cannot be
  undone: the earlier wave's merge already happened, and the later wave is not in its covered set.
- `ADecisionInAnEarlierDeliveredWave_DoesNotSuppressALaterCleanWave` — the opposite direction: an earlier
  wave whose work ALREADY reached the user's branch is not in the later delivery's covered set, so its
  decision does not hold that delivery. The scoping is genuinely per delivery rather than a sticky
  run-level flag wearing a wave's name.
- `AHeldWavesWorkRidingAlong_HoldsTheLaterDelivery` — wave 02 records a `proceeded-best-guess` and is
  held; wave 03 is clean; the delivery at wave 03 covers wave 02 and wave 03 and IS suppressed, and the
  returned entry is wave 02's decision.
- `TheWaveAttributionIsRecordedOnTheDecision_NotParsedFromSubject` — assert the wave is read
  from the decision's own member. A test that passes by splitting `Subject` would certify the
  parsing convention this task exists to avoid.
- `TheSchedulersProceededUnreviewedDecision_RecordsItsWave` — drive the REAL `Scheduler` through the
  review gate's proceed-unreviewed path, the way
  `SchedulerReviewGateTests.ProceedUnreviewed_UnreviewedWaveRuns_RecordsProceededUnreviewedDecision` does:
  - a `WavePlanBuilder` plan whose wave 02 is an empty JIT stub carrying a `brief.md`;
  - `autonomyPolicy: auto` with `gateThresholds.review-gate` set to `ReviewGateDecision.ProceedUnreviewed`;
  - a stub breakdown runner that authors wave 02;
  - a `RecordingWorktreeProvider`, a real `RunJournal` and a `FileEscalationSink`.

  That file's helpers are private nested types, so copy the few you need into your class. Assert that the
  recorded `proceeded-unreviewed` entry's `Wave` equals wave 02's directory. Every other row builds its
  decisions by hand, so without this one nothing proves a real decision site stamps the wave, and task 06
  could implement the policy while stamping nothing. It is red on the stubs because no site populates
  `Wave` yet.
- `TheOperatorOverrideStillLiftsTheInterlock` — `--merge-on-success` remains the documented override
  and must still lift the RUN-END interlock. This row pins the run-end call only. The barrier delivery's
  override belongs to task 08, through the delivery predicate it shares with `Finalize`, and task 07 pins
  it; do not try to exercise a barrier here.

**One row is DECLARED EXEMPT from the red census:** `TheOperatorOverrideStillLiftsTheInterlock`. The
override already wins on current code (#361/#597, `RunReport.DeliveryForcedPastDecision`); wave
scoping changes which decisions suppress, never whether the override lifts them, so a correct test is
green on arrival. It must still EXIST: the census asserts that, and task 06's forward census requires
it observed `Passed`.

The other tests MUST COMPILE and FAIL. Do NOT implement the scoping.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs`, `src/Guardrails.Core/Execution/DecisionEntry.cs`, and `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
