## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/06-author-tests-stage2-conformance`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/06-author-tests-stage2-conformance": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **Stage 2 real-seam conformance suite** — the wave's proof, and the thing the plan's
terminal gate (`<plan>/guardrails/03-dor-section-6-contract-landed.ps1`) discovers by name and
requires to pass. Read that gate before you start: it lists the required behaviours and the regex it
matches each one by.

- **`tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs`**
- namespace `Guardrails.Integration.Tests.ModelTiering`
- class **`Stage2ConformanceTests`** — this exact name, verbatim. The terminal gate discovers the
  suite with `--filter FullyQualifiedName~Stage2ConformanceTests`; a different name fails **every**
  clause at once and `mergeOnSuccess` withholds delivery.
- decorated **`[Trait("Category", "TierResolution")]`** at class level. Load-bearing: the plan-root
  Integration baseline preflight excludes `Category!=TierResolution`, so without the trait this
  plan's own intentionally-red suite would be swept into a later run's baseline.

Build every clause on **`Stage2PlanHarness`** (task 05). Do not write a second harness.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path — including
`Stage2PlanHarness.cs`, any production file, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If the harness is missing something you need (say, a way to reach
an attempt's log dir), do NOT edit it — write `{"needsHuman": "<what is missing>"}` to the state-out
path and stop.

## The nine test method names — VERBATIM, they are the contract

Three later tasks each turn on a SUBSET of these, and each one's guardrail selects its subset with
`FullyQualifiedName~Stage2ConformanceTests.<MethodName>`. A renamed method is not selected, its
guardrail's zero-match guard fires, and the task cannot go green. Use these exact names:

| # | method | greened by |
|---|---|---|
| 1 | `Resolution_RunsPerAttempt_AndReachesAttemptProvenance` | task 07 |
| 2 | `ResolverCandidacy_AgreesWith_ServesTier_Predicate` | task 07 |
| 3 | `Invariant7_RoutingEnabledConfig_ZeroTagPlan_UsesLegacyPath_WithNoTierActivity` | task 07 |
| 4 | `D30_TieredPlan_ClimbsToStrongerRung_AndNeverFallsBackToLegacy` | task 07 |
| 5 | `D31_FullPin_RecordsTierSourceOverride_WithProvenanceTierAbsent` | task 07 |
| 6 | `Climb_ToStrongerRung_IsRecordedInProvenance` | task 07 |
| 7 | `NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman` | task 08 |
| 8 | `Reattempt_BoundByCostlyCeiling_WarnsNamingTheExcludedOnlyForCostBlock` | task 09 |
| 9 | `Climb_ToStrongerRung_EmitsLoudWarningLine` | task 09 |

Prefer nine `[Fact]`s. If a clause genuinely wants a `[Theory]`, keep the METHOD name from the table.

## What each clause must prove

**§6 of `docs/plans/17-model-tiering.md` is the design of record and wins over any paraphrase here.**

1. **Per ATTEMPT, not once per task.** Run a tiered task that fails its first attempt and succeeds
   its second. Assert **both** attempt records carry the resolved route in `provenance` (`runner`,
   `model`, `tier`, `tierSource`), and that the recorded route matches the model that reached the
   captured `PromptInvocation` for **that same attempt**. Resolution being a pure function in v1 is
   exactly why this must be asserted structurally rather than by observing a difference: neither the
   tag nor the registry is frozen for the life of a run (a resumed run whose `guardrails.json` was
   edited between sessions moves an input mid-run), so a once-per-task implementation would serve a
   stale route and look identical here today.
2. **Candidacy agrees with `ServesTier`.** Build a registry mixing blocks that do and do not serve
   the rung — including one excluded ONLY because `costly: true`. Assert the block named in
   `provenance.runner` satisfies `PromptRunnerConfig.ServesTier(rung)` for the rung in
   `provenance.tier`, and that no `costly: true` block is ever the one selected. Compute the expected
   set with `ServesTier` over the config you built — **never** by calling `TierResolver`.
3. **Invariant 7 — the case implementers get wrong.** `routing` blocks **PRESENT** in the registry,
   the task carrying **no** `action.tier`, and **no** `tiering.defaultTier`. Assert the run resolves
   through the LEGACY path — the model is `promptRunners.<default>.model` — and that there was **zero
   tier-resolution activity**: `provenance.tier` is **absent**, `provenance.tierSource` is
   **absent**, no climb and no ceiling datum is recorded, and the attempt log dir contains no route
   disclosure naming a rung. "Routing is configured, so route" is the wrong reading; activation is
   PLAN-scoped, not config-scoped.
4. **D30 — legacy is the no-RUNG path and nothing else.** A task WITH an effective tier whose
   requested rung has no candidate, but where a STRONGER rung does. Assert the resolver **climbed**
   (`provenance.tier` is the stronger rung, and it is not the requested one) and that it did **not**
   fall back to `promptRunners.<default>.model`. Make the default pointer's block a distinctly-named
   model so "fell back" is unmistakable in the assertion.
5. **D31 — a full pin records `tierSource: "override"`.** A task with `action.runner` (and separately
   `action.model`) set. Assert `provenance.tierSource` is **`"override"`** and `provenance.tier` is
   **absent** — no rung resolved. Contrast it with a legacy attempt in the same clause or a sibling:
   legacy records **no `tierSource` at all**. "Bypasses tier resolution entirely" governs what is
   SELECTED, not what is LOGGED.
6. **The climb is recorded.** Same registry shape as clause 4; assert `provenance` distinguishes the
   REQUESTED rung from the SERVED one, so a climb is legible from the journal alone rather than only
   from a log line.
7. **`no-route` settles needs-human.** A rung whose only capable block is `costly: true` (so
   `Candidates` is empty at that rung and at every stronger one). Assert: the attempt outcome is the
   **`no-route`** token, the task settles **needs-human**, the costly block was **never** selected
   and — this is the half that matters — **the fake runner was never invoked for that task at all**.
   A no-route must be settled BEFORE an attempt is launched, not after one runs on some fallback.
   Assert the operator-facing reason names the rung and tells them to register a provider serving
   tier ≥ R.
8. **The D28 binding ceiling is LOUD on re-attempt.** A registry where a stronger block DECLARES the
   rung but is excluded ONLY because `costly: true`, and a task that fails its first attempt. Assert
   the SECOND attempt's route disclosure carries a warning that **names that block**. Read it from
   the attempt log dir file **`attempt-route.log`** (see the pinned surface below). Do not re-test
   `Costly` yourself: the datum rides on the resolution result, and re-deriving it would duplicate
   the candidacy predicate D22a forbids duplicating.
9. **The climb is loud too.** Same shape as clause 4; assert the attempt's `attempt-route.log`
   carries a warning line naming both the requested and the served rung. A climb absorbed silently
   is a route change the operator never sees.

## Two pinned surfaces (decided here so 07/08/09 implement to them)

- **The route disclosure file is `attempt-route.log`, in the attempt's own log dir**, a sibling of
  the existing `attempt-tool-grants.log` (#382) and written the same best-effort way. It carries the
  resolved runner name, model, effort, the requested and served rung and the `tierSource`; a
  **`WARNING:`**-prefixed line when a costly ceiling is binding, naming the blocks; and a
  **`WARNING:`**-prefixed line when the resolver climbed. Assert on the presence of the block NAME
  and of the rung tokens — not on exact prose, which would make the file a golden nobody owns.
- **`provenance` is the machine-readable copy** and is where every non-log assertion is made:
  `runner`, `kind`, `model`, `effort`, `tier`, `tierSource` (the names task 02 landed).

## The one prohibition, and it is what makes this suite worth anything

**This file must never reference `TierResolver` or `TierResolution`** — a guardrail enforces it. A
test that asks the resolver what it would have chosen and asserts the answer proves the RESOLVER,
which wave 1 already proved; it says nothing about whether anything CALLS it. Every clause here
observes the route the way an operator does: through the journal, the captured invocation, and the
attempt log dir. Faking the **process/CLI boundary** (`IPromptRunner`, via the harness) is
sanctioned; faking the in-process seam is not.

Every clause will FAIL right now — nothing wires the resolver into the attempt launch yet. **That is
the intended outcome**; failing is the point, not compiling is a mistake to fix. Do not implement any
production change.
