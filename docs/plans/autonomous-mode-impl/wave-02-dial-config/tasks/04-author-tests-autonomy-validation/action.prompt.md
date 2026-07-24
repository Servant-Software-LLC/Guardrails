## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/04-author-tests-autonomy-validation` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the autonomy-block **validation** — GR2039 and GR2040
(issue #361, doc 12 §3.4/§3.5/§5.2, decided §10 M). The repo uses **xUnit (xunit.v3)**; mirror
`tests/Guardrails.Core.Tests/PlanValidatorTests.cs` for how a plan is loaded and diagnostics asserted
(load a `guardrails.json` and assert a `DiagnosticCodes.*` appears / does not appear).

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/AutonomyValidatorTests.cs` — the GR2039/GR2040 matrix,
   all FAILING against the current validator (which does not yet validate the block):
   - **GR2039** — an `autonomy.escalationThreshold` with an unrecognized value emits GR2039; an
     unrecognized `gateThresholds` value (a `needs-human`/`wave-checkpoint` that is not a criticality
     level, or a `review-gate` that is neither `escalate` nor `proceed-unreviewed`) emits GR2039.
   - **GR2040 run-wide** — `gateThresholds.review-gate: "proceed-unreviewed"` + `escalationThreshold:
     "critical"` emits GR2040 (a load-time error).
   - **GR2040 per-gate route-around (Finding 3)** — `escalationThreshold: "high"` +
     `gateThresholds: { "needs-human": "critical", "review-gate": "proceed-unreviewed" }` STILL emits
     GR2040 (it keys on the reachable end-state, so a per-gate `critical` cannot route around it).
   - **Negatives** — a valid block (e.g. `escalationThreshold: "high"`, `review-gate: "escalate"`) emits
     NEITHER code; `proceed-unreviewed` + `escalationThreshold: "high"` (not `critical`, no per-gate
     `critical`) emits NO GR2040 (proceed-unreviewed is allowed at the cautious/`high` dials).

2. **The compile stub** — add the two new codes to `src/Guardrails.Core/Loading/DiagnosticCodes.cs`:
   a `GR2039` constant (invalid `escalationThreshold`/`gateThresholds` value) and a `GR2040` constant
   (the compound-config incompatibility). Give them clear names and the exact string values `"GR2039"` /
   `"GR2040"`; update the "CURRENT next-free code" comment to `GR2041` (GR2038 stays reserved for
   design-360). Do NOT implement the validation itself — with the validator unchanged, the diagnostics
   are ABSENT, so the tests FAIL (TDD red). The tests must COMPILE (the constants exist) and FAIL.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/AutonomyValidatorTests.cs` and
`src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this task the harness runs a `git diff` check
and rejects any edit outside these paths — including `PlanValidator.cs` (a later task implements the
rule). An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
from a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is
missing>"}` and stop.
