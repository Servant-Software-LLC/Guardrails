## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/10-author-tests-per-tier-spend`, NOT the stableId
  and NOT the bare folder name. The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/10-author-tests-per-tier-spend": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the failing tests for **#230-lite — the per-tier spend line**. DoR §9.3 calls it *"the single
most important v1 deliverable after the routing itself: it is the evidence base for whether the
deferred subsystems (probes, ladder, steering) are ever worth building."*

You write the tests **and** the minimal stub they compile against. The tests must **compile and
FAIL** — failing is intentional, *not compiling is a mistake to fix*.

- **`tests/Guardrails.Core.Tests/ModelTiering/PerTierSpendTests.cs`**
- namespace `Guardrails.Core.Tests.ModelTiering`
- class **`PerTierSpendTests`** — this exact name; the implementation guardrail and the wave exit gate
  filter on it
- decorated **`[Trait("Category", "TierResolution")]`** at class level (the plan-root baseline
  preflight excludes `Category!=TierResolution`)

- **`src/Guardrails.Core/Journal/JournalTierSpend.cs`** — the stub, a sibling of the existing
  `JournalCost` (read that file first; this one aggregates the same journal, split by rung). A
  `public static class JournalTierSpend` whose entry points **throw `NotImplementedException`**, so a
  non-zero `dotnet test` unambiguously means the tests RAN and FAILED against a real stub.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/PerTierSpendTests.cs` and
`src/Guardrails.Core/Journal/JournalTierSpend.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including `JournalCost.cs`,
`RunCommand.cs` (task 11 owns it), the journal model, or the `.csproj`. An out-of-scope edit fails
the task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out
path and stop.

### The shape to design (and pin with your tests)

A summarizer over a `JournalDocument` that groups every attempt by its `provenance.tier`, summing
`costUsd` and — where present — `usage.inputTokens`/`usage.outputTokens`, and a renderer that turns
the summary into the operator-facing line. §9.3's worked example is
*"hard: 42k tok / $3.12 · easy: 180k tok / $0"*, **degrading to tokens-only where no cost was
reported** (a costless local provider still shows volume — that is exactly why the tokens surface
exists).

**Make "there is nothing to report" a first-class return value** — `null`, or an equivalent
explicitly-empty result — rather than an empty string the caller has to test. Task 11 wires the CLI
with `if (… is { } summary) { print }`, and a nullable result is what makes that shape possible and
checkable.

### The behaviours to encode

1. **Aggregation.** Attempts across several tasks and several rungs sum per rung: cost and both token
   counts. Two attempts of the SAME task at the same rung both count (resolution runs per attempt).
2. **Rung ordering is stable and ascending** (`easy`, `medium`, `hard` — `ActionTiers.All`'s order),
   not dictionary order, so the line does not shuffle between runs.
3. **Tokens-only degradation.** A rung whose attempts reported tokens but **no** cost renders its
   token volume and omits the money — not `$0.00`, which would assert a fact the runner never
   reported. A rung with cost but no tokens renders the money alone.
4. **INVARIANT 7 — the suppression rule, and the reason this class exists as its own unit.** §9.3 is
   stricter than "add a per-tier section": on a **tiering-inactive run** — no attempt resolved
   through routing, so no attempt carries `provenance.tier` — the summary is **nothing at all**. Not
   an empty section, not a header with no rows, and **not an `untiered:` bucket**. Assert:
   - a journal with **zero** tiered attempts summarizes to **null**;
   - a journal with a **mix** of tiered and untiered attempts reports the tiered rungs and **does
     NOT** emit an `untiered` (or `null` / `none` / `other`) bucket for the rest;
   - **assert on the rendered TEXT** for both, with a negative assertion that the string
     `untiered` never appears. A naive aggregator emitting an empty or `untiered:` section on every
     existing user's run is the single most likely way this wave breaks Invariant 7, and it is
     invisible in a structural assertion on a collection.
5. **Overhead spend is not a rung.** `JournalCost.Total` folds in `document.OverheadCostUsd` (the
   overwatcher, the AI-merge worker, the needs-human triage). That spend belongs to no tier — assert
   it does **not** silently land in a rung's bucket, and that the existing total is unaffected by
   anything you add.

Do NOT implement the aggregation or touch the CLI; that is task 11.
