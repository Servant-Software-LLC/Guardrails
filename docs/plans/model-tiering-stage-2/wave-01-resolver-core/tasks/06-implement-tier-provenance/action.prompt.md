## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/06-implement-tier-provenance`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/06-implement-tier-provenance": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the tests in `tests/Guardrails.Core.Tests/ModelTiering/ActionTierProvenanceTests.cs` pass by
populating **`ActionDefinition.TierOrigin`** in `src/Guardrails.Core/Loading/PlanLoader.cs`.

Task `05-author-tests-tier-provenance` already declared the `TierOrigin` enum
(`None` / `Task` / `PlanDefault`) and the property. Your job is the loader half.

**You may NOT edit the test file.** It is outside your write scope and the harness rejects any edit
to it after this task completes. If a test looks wrong, write
`{"needsHuman": "<which test and why it cannot be satisfied>"}` to the state-out path rather than
changing it.

### What to change

The collapse is one expression, in `ResolveAction` (~line 1120):

```csharp
Tier = rawAction?.Tier ?? defaultTier,
```

`rawAction?.Tier` and `defaultTier` both flow into a single nullable string and nothing records which
one won. Set `TierOrigin` alongside it, from the *same* decision — do not recompute the answer by
comparing values afterwards. **A comparison is wrong**, and it is wrong in the exact case the tests
single out: when a task's own `action.tier` happens to equal `tiering.defaultTier`, a comparison says
`PlanDefault` and the truth is `Task`. That is the shipped Stage 1 defect
(`PlanValidator.cs`: `tier != plan.Config.Tiering?.DefaultTier`) and reproducing it here fails
`ActionTier_SameTokenAsDefault_OriginIsStillTask`.

Three rules the tests pin:

- The origin must always **agree with what actually landed in `Tier`**. An unrecognized
  `tiering.defaultTier` does not propagate (`PropagatableDefaultTier`, GR2043), so the task keeps
  `Tier == null` and the origin is **`None`** — never `PlanDefault` over a value that was never
  filled in.
- `Tier == null` ⟺ `TierOrigin.None`. There is no state where one says "untagged" and the other
  does not.
- The **waved** path must behave identically to the flat one. `defaultTier` reaches wave tasks via
  `LoadWaves` → `LoadWaveTasks` → `LoadTask`, a different call path from flat `LoadTasks` → `LoadTask`.
  Both bottom out in `ResolveAction`, so a fix placed there covers both — but verify it, because
  `WavedPlan_DefaultReachesWaveTask_OriginIsPlanDefault` exists precisely to catch a fix that
  did not.

### Scope

`ActionDefinition.cs` is in your write scope for doc-comment or type adjustments only — the enum's
three members and the property name are fixed, because the tests and the wave-2 wiring both reference
them.

**Do not touch `PlanValidator.cs`.** Its `tier != DefaultTier` guess is the shipped workaround this
task obsoletes, but removing it is a separate change with its own tests and it is outside your write
scope. If you believe leaving it creates an inconsistency, say so in your state-out fragment under a
`notes` key — do not act on it.

**Do not add the journal field.** Writing `tierSource` into per-attempt provenance is wave 2's job
(DoR §12.4 / D31, where the `override` value is derived from the pin check). This task restores the
*input* that makes it computable; it does not consume it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/PlanLoader.cs` and `src/Guardrails.Core/Model/ActionDefinition.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside those
paths — including the test file, `PlanValidator.cs`, `TierResolver.cs`, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry.
