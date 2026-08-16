## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/04-implement-resolution-precedence`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/04-implement-resolution-precedence": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the `Resolve` stub in `src/Guardrails.Core/Prompts/TierResolver.cs` so the tests
authored by `03-author-tests-resolution-precedence` pass. **Do NOT edit those tests.** If they are
genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than
changing them — an out-of-scope edit to a test file fails the write-scope check and burns a retry.

**`docs/plans/17-model-tiering.md` §6.1 is the design of record and wins over any paraphrase here.**

### The precedence chain, in order

1. **Full pin — `action.runner` or `action.model`.** Explicit always wins and **bypasses tier
   resolution entirely**. This is the sanctioned route to a `costly` model: a pin is a human naming a
   model for a task, and the costly floor constrains the harness's choices, never the human's. No
   warning, no dial, no ceremony.
2. **Tier resolution.** Effective tier = `action.tier` ?? `tiering.defaultTier`; the route is the best
   candidate from **`SelectCandidate`** — call it, do not re-derive selection here.
   **`action.effort` alone is NOT a bypass**: tier resolution still selects the block, and the effort
   override is applied to the *resolved route's* effort. `{ "tier": "medium", "effort": "xhigh" }`
   means *route by tier, but think hard*. `effort` mirrors `model`'s SHAPE but not its BYPASS — this
   is the correction the DoR states explicitly, and it is the rule most likely to be implemented
   backwards.
3. **Legacy fallback** — no effective tier, or no block serves it: `promptRunners.<name>.model`, else
   the CLI default, exactly as today.

### Invariant 7 — the guarantee you must not break

There are **two** fixtures, and the second is the one implementers get wrong:

**(a)** A plan with no tags, against a registry with **no `routing` block anywhere**, resolves through
the legacy path with **zero tier-resolution activity** and behaves byte-identically to today.

**(b)** The DoR's own named fixture: `routing` blocks **PRESENT**, the action carrying **no tier**, and
**no `tiering.defaultTier`** ⇒ *still* legacy resolution, *still* **zero** tier-resolution activity.
This is the case that reads like it should route ("routing is configured, so route") and does not.
Fixture (a) alone cannot catch it, and task 03's tests assert both — so an implementation that passes
(a) and fails (b) will fail its own pair's tests.

A single-model user never opted into any of this. Do not make the legacy path route through
resolution "for uniformity".

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/TierResolver.cs`
and `src/Guardrails.Core/Prompts/TierResolution.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including the test files, other production
files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.

Do not weaken or bypass the costly floor while implementing precedence: `SelectCandidate` enforces it,
and `Resolve` must not acquire its own candidate-filtering path around it.
