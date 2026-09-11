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

Author failing tests for the behaviour DECIDED in review — design 39 §1c.

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/BranchMovedHaltTests.cs`
**Test class:** `BranchMovedHaltTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Why this changes character under per-wave delivery.** #588 pinned the delivery target at run start
and made a moved HEAD a REFUSAL rather than a redirect — correct, and the incident behind it was real.
But today that refusal fires ONCE, at run end, with every task complete and safely on the plan branch:
a soft landing. Per-wave it fires at wave 2's exit with three waves still to run, and **the condition
is not transient** — the operator checked out a different branch, so every later delivery hits the
identical refusal. DECIDED: **halt at that wave.**

**Pin these behaviours to these EXACT method names:**

- `ADeliveryHittingBranchMoved_HaltsTheRunAtThatWave`
- `LaterWavesDoNotRun_AfterABranchMovedHalt` — the point of halting: continuing pays for waves whose
  delivery is already known to be impossible.
- `TheHaltNamesThePinnedTargetAndTheCurrentHead` — an operator cannot act on "delivery refused" alone.
- `AlreadyDeliveredWavesStayDelivered` — the halt must not try to unwind merges that already landed on
  the user's branch.
- `TheUsersCheckoutIsNotModified` — #588's safe direction: refusing leaves the checkout untouched
  rather than checking the pinned branch back out and stomping a deliberate switch.

The tests MUST COMPILE and FAIL. Do NOT implement the halt.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside them. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

