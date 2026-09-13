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

Author failing tests for the interlock scoping DECIDED in review — design 39 §1a.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs`
**Test class:** `WaveScopedInterlockTests`
**Stub file:** `src/Guardrails.Core/Execution/DecisionEntry.cs`

**The design's premise was FALSE, and the correction is your stub (review, 2026-09-11).** §1a
said *"`decisions[]` entries carry a wave attribution already"*. They do not. `DecisionEntry`
has eighteen public members and none is a wave; `Subject` is free text documented as *"a task id
/ wave dir / the drifted unit(s)"*, and only the `Boundary = "wave"` factories put a wave dir
there. The `task` (Overwatch) and `drift` boundaries — the ones that actually produce
`proceeded-best-guess` — put a TASK ID. So deriving the wave by string-splitting a §14.2
wave-qualified id would be undocumented, waved-plans-only, and silently wrong for exactly the
boundaries that matter. Add a real `Wave` member as a stub; task 06 populates it at the point
each decision is made.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**What the interlock is, and why per-wave delivery would otherwise BYPASS it.** #361/#340: a run whose
result was shaped by a machine decision (`proceeded-best-guess` / `proceeded-unreviewed`) defaults
delivery OFF. Today that check is **run-scoped** — evaluated once, at the end. Per-wave delivery
merges BEFORE the run ends, so a wave could ship while a suppressing decision sits later in the run,
or a decision in wave 4 could retroactively suppress waves 1-3 that already landed.

**DECIDED:** a wave delivers only if **no suppressing decision was recorded DURING THAT WAVE**.

**Pin these behaviours to these EXACT method names:**

- `AWaveWithNoSuppressingDecision_Delivers`
- `AWaveWithASuppressingDecision_DoesNotDeliver`
- `ADecisionInALaterWave_DoesNotRetroactivelySuppressAnEarlierDelivery` — the direction that cannot be
  undone: the earlier wave's merge already happened.
- `ADecisionInAnEarlierWave_DoesNotSuppressALaterCleanWave`
- `TheWaveAttributionIsRecordedOnTheDecision_NotParsedFromSubject` — assert the wave is read
  from the decision's own member. A test that passes by splitting `Subject` would certify the
  parsing convention this task exists to avoid. — the opposite direction, so the scoping
  is genuinely per-wave rather than a sticky run-level flag wearing a wave's name.
- `TheOperatorOverrideStillLiftsTheInterlock` — `--merge-on-success` remains the documented override
  and must still work per wave.

The tests MUST COMPILE and FAIL. Do NOT implement the scoping.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs` and `src/Guardrails.Core/Execution/DecisionEntry.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

