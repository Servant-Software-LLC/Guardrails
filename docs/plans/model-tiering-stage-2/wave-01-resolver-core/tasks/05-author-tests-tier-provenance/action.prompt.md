## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/05-author-tests-tier-provenance`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/05-author-tests-tier-provenance": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Why this task exists

DoR §12.4 requires per-attempt provenance to record **`tierSource`** — which of `task` /
`plan-default` / `override` supplied the rung. Two of those three are **not computable today**, and
that is a defect in the shipped model rather than something the Stage 2 resolver can work around:

```csharp
// PlanLoader.cs ~1120
Tier = rawAction?.Tier ?? defaultTier,
```

The loader collapses `action.tier` and `tiering.defaultTier` into one nullable string and
`ActionDefinition` keeps no record of which one won, so by the time anything downstream reads
`Tier`, the answer to "where did this come from?" has been destroyed. Stage 1 papered over this with
a guess (`PlanValidator.cs`: `tier != plan.Config.Tiering?.DefaultTier`), which is wrong whenever a
task's own tier happens to equal the default — the common case, not an edge case.

Shipping wave 2 without this fix means emitting `tierSource: "task"` unconditionally: a green run
carrying a wrong value, which is exactly the Stage 1 failure this stage exists to not repeat.

**`override` is NOT your problem.** Per DoR §6.1 item 1 / **D31**, a full `action.runner`/`action.model`
pin is its producer, and a pin is visible to the *resolver* (which sees `ActionDefinition.Runner` /
`.Model` directly). Only the `task` vs `plan-default` distinction is destroyed at load, so only that
is restored here.

## Task

Author the **failing tests** in
`tests/Guardrails.Core.Tests/ModelTiering/ActionTierProvenanceTests.cs` — class
**`ActionTierProvenanceTests`**, every test tagged `[Trait("Category", "TierResolution")]`.

The class name and file path are **pinned** — this task's guardrails filter on
`FullyQualifiedName~ActionTierProvenanceTests` and the implementation task copies that filter
verbatim.

**Declare the property so your tests compile.** Add to `src/Guardrails.Core/Model/ActionDefinition.cs`:

- a `TierOrigin` enum with exactly three members — **`None`**, **`Task`**, **`PlanDefault`**;
- a `public TierOrigin TierOrigin { get; init; }` property on `ActionDefinition`, defaulting to
  `None`.

Give it an XML doc comment explaining that it records **which source supplied `Tier`**, that it is
the input the journal's `tierSource` is derived from (together with the pin check the resolver does
for `override`, D31), and that `None` means no tier was resolved at all.

**Do not touch `PlanLoader.cs`** — it is outside your write scope, and leaving it unchanged is what
makes your tests fail. Every `ActionDefinition` the loader produces will carry `TierOrigin.None`,
so every assertion below goes red. That is the TDD red.

**Read `docs/plans/17-model-tiering.md` §12.4 and §6.1 — the design of record wins over this summary.**

### Behaviors the tests MUST encode

Drive these through the **real loader** (`PlanLoader` loading a plan folder from disk — a temp
fixture is fine), not by constructing an `ActionDefinition` by hand. A hand-built record asserts your
own object initializer and proves nothing about the collapse this task exists to undo.

**The test METHOD NAMES below are pinned.** Your `03-covers-key-behaviors` guardrail runs
`dotnet test --list-tests` and looks for each marker in the DISCOVERED name list — it never reads the
file's text, so a behaviour named in a comment earns nothing and a renamed test reads as a missing
one. Add more tests freely; do not rename these six.

| # | Test method name | What it asserts |
|---|---|---|
| 1 | `ActionTier_Set_OriginIsTask` | `action.tier` set ⇒ `TierOrigin.Task`, and `Tier` is that value |
| 2 | `ActionTier_Absent_DefaultSupplied_OriginIsPlanDefault` | no `action.tier`, `tiering.defaultTier` set ⇒ `TierOrigin.PlanDefault`, `Tier` is the default |
| 3 | `ActionTier_SameTokenAsDefault_OriginIsStillTask` | **the case Stage 1's guess gets wrong** |
| 4 | `NoTierAnywhere_OriginIsNone_AndTierIsNull` | neither set ⇒ `TierOrigin.None`, `Tier` null |
| 5 | `UnrecognizedDefaultTier_DoesNotPropagate_OriginIsNone` | GR2043's non-propagating default |
| 6 | `WavedPlan_DefaultReachesWaveTask_OriginIsPlanDefault` | the waved propagation path |

Notes on the ones that are easy to get wrong:

- **(3) is the whole point of the task.** `action.tier` set to *the same token as*
  `tiering.defaultTier` must still be `TierOrigin.Task`. A comparison-after-the-fact implementation
  (`tier != config.Tiering?.DefaultTier`, which is what `PlanValidator.cs` does today) passes every
  other test on this list and fails only this one. Without it the task certifies the bug it exists
  to fix.
- **(5)** `PropagatableDefaultTier` refuses to propagate an unrecognized token (GR2043 reports it
  once, at its declaration site). So the task stays `Tier == null` and the origin must be **`None`**,
  not `PlanDefault` over a value that never landed. Origin must always agree with what is actually
  in `Tier`.
- **(6)** the default reaches wave tasks through `LoadWaves`/`LoadWaveTasks`, a *different* call path
  from flat `LoadTasks`. An implementation that patches only one is half-done in a way no other test
  here can see.

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
populate the property in the loader.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/ActionTierProvenanceTests.cs` and
`src/Guardrails.Core/Model/ActionDefinition.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including `PlanLoader.cs`,
`PlanValidator.cs`, other test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
