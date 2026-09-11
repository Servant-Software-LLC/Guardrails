## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `07-author-tests-deliver-at-wave-barrier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-deliver-at-wave-barrier": { "someKey": "someValue" } }`.
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

Author failing tests for the core of design 39 §1: **fire the existing delivery at each wave's
existing exit gate, instead of once at run end.**

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/WaveBarrierDeliveryTests.cs`
**Test class:** `WaveBarrierDeliveryTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Drive the REAL Scheduler**, not an injected fake of the delivery. A test that injects its own
delivery and asserts it was called proves the fake was called — that is how a feature ships wired to
nothing and green. Assert the effect only the production path emits: the user's branch actually
carries the wave's commits.

**The one thing §1 says this design must get right:** the gate must run against the tree the delivery
will PRODUCE — the user's branch with this wave's commits merged onto it — not merely the plan branch
as it stands. The plan branch also carries the run's own harness commits and anything a prior wave
left. Perform the merge first, then gate.

**Pin these behaviours to these EXACT method names:**

- `ADeliveringWaveMergesAtItsOwnBarrier` — not at run end.
- `ANonDeliveringWaveRidesAlongToTheNextDeliveryPoint` — §1b: a delivering wave ships everything
  accumulated since the last delivery point.
- `AWaveWhoseExitGateFails_DoesNotDeliver`
- `TheGateRunsAgainstTheMergedTree_NotThePlanBranchAlone` — the §1 correctness point above.
- `APlanMarkingNoWave_StillMergesOnceAtRunEnd` — the never-weaker requirement, asserted end to end.

The tests MUST COMPILE and FAIL. Do NOT wire the delivery.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside them. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

