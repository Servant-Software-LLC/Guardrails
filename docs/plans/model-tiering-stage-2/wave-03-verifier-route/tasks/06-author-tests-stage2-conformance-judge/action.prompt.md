## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/06-author-tests-stage2-conformance-judge`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/06-author-tests-stage2-conformance-judge": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Extend** `tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs` with the
real-seam judge clauses. This is the suite the **plan terminal gate** reads by NAME, and its
`6.5/D29` clause is the ONE unsatisfied clause in the whole plan — the reason wave 3 exists.

**The five test METHOD NAMES below are pinned.** The terminal gate matches discovered names against
`(?i)judge|verifier|strengthbump|mintier|pinnedactor`, and task 07's guardrail filters on them:

| # | method | asserts |
|---|---|---|
| 1 | `Judge_ResolvesThroughSameResolver_AtActorsRung` | §6.5 rules 2–3: same resolver, actor's rung |
| 2 | `Judge_WeakActor_StrengthBump_NotTierBump` | D24a — bump in strength, rung unchanged |
| 3 | `Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds` | rule 5 — advisory, run proceeds, no halt |
| 4 | `Judge_PinnedCostlyActor_MayBumpIntoCostly_D29` | D29 carve-out, and the `default` pointer does NOT trigger it |
| 5 | `Judge_VerifierMinTier_RaisesNeverLowers` | §6.5.1 — the floor only raises |

Add more freely; do not rename these five.

### Drive the REAL seam (#382) — this is the point of the suite

Use **`Stage2PlanHarness`**, which already drives the real `PlanLoader`/`TaskExecutor`/`Scheduler`
and fakes only `IPromptRunner` (the process boundary). Extend the harness if it cannot yet express a
plan with a prompt-JUDGE guardrail — that is legitimate and in your scope.

**The harness must NOT call `TierResolver` or `TierResolution`** — wave 2's harness-shape guardrail
forbids it and that prohibition still binds. Asking the resolver what it *would* have chosen and
asserting the answer PASSES against a completely unwired `GuardrailRunner`: it proves the resolver
(waves 1–2 already did) and says nothing about whether anything CALLS it. Observe the judge route
through the **journal** and the **captured prompt invocation** instead.

These tests are **RED until task 07 lands the wiring** — that is correct and intended. Today
`GuardrailRunner` picks a judge's block from frontmatter-or-default with no tier awareness at all.

**Wave 2's nine facts must keep passing.** You are extending a green suite; a regression there fails
the wave exit gate, which asserts a floor on the executed count. Raising that floor is task 07's
concern — do not lower an existing assertion to make room.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs` and
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside those paths — including anything
under `src/`, the wave's guardrail scripts, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
