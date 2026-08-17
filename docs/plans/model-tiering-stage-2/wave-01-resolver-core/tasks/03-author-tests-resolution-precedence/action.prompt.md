## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/03-author-tests-resolution-precedence`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/03-author-tests-resolution-precedence": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **failing tests** for DoR §6.1 precedence, in
`tests/Guardrails.Core.Tests/ModelTiering/TierResolverPrecedenceTests.cs` — class
**`TierResolverPrecedenceTests`**, every test tagged `[Trait("Category", "TierResolution")]`.

The class name and file path are **pinned** — this task's guardrails filter on
`FullyQualifiedName~TierResolverPrecedenceTests` and the implementation task copies that filter
verbatim.

**No stubs to write.** Task `01-author-tests-candidate-selection` already declared the
`TierResolver.Resolve(...)` entry point throwing `NotImplementedException`, so your tests **compile**
against a stable signature and **fail** because the precedence behavior is absent. That is the red.
If the existing signature genuinely cannot express a case below, write
`{"needsHuman": "<what the signature is missing>"}` rather than editing `TierResolver.cs` — it is
outside your write scope.

**Read `docs/plans/17-model-tiering.md` §6.1 — it is the design of record and wins over this summary.**

### Behaviors the tests MUST encode

- **A full pin bypasses tier resolution ENTIRELY.** `action.runner` selects a named block;
  `action.model` overrides the model string. Neither consults the tier, and a pin is the sanctioned
  route to a `costly` model — assert that a pinned costly block IS used (the floor constrains the
  harness choosing, never the human assigning).
- **`action.effort` ALONE is NOT a bypass.** This is the correction the DoR calls out explicitly:
  `{ "tier": "medium", "effort": "xhigh" }` means *route by tier, but think hard* — tier resolution
  still selects the block, and the effort override is applied to the RESOLVED route's effort. Assert
  the block still comes from resolution, not from the presence of `effort`.
- **Effective tier = `action.tier` ?? `tiering.defaultTier`.** Assert the default is consulted only
  when the action carries no tier of its own.
- **Legacy fallback.** No effective tier, or no block serves it: `promptRunners.<name>.model`, else
  the CLI default — exactly today's behavior.
- **Invariant 7 needs BOTH fixtures, and the second is the one that matters.** Assert (a) a config
  with **no `routing` block anywhere** resolves through the legacy path with no tier-resolution
  activity — the easy case; **and (b) the DoR's own named fixture: `routing` blocks PRESENT, the
  action carrying NO tier, and NO `tiering.defaultTier`** ⇒ still legacy resolution, still **zero**
  tier-resolution activity. (b) is the case an implementer gets wrong, because "routing is
  configured" reads like "so route". Fixture (a) alone cannot catch that.
Name the Invariant 7 tests so the two fixtures are distinguishable at a glance — e.g.
`RoutingEnabled_ZeroTagPlan_ResolvesViaLegacyPath` for (b). Your guardrail looks for a
routing-enabled/zero-tag/untagged marker precisely because fixture (a) alone reads like coverage and
is not.

**Not `tierSource`.** DoR §12.4 lists it, but it is **not computable at this layer**: `PlanLoader`
collapses `action.tier` and `tiering.defaultTier` into one field at load
(`Tier = rawAction?.Tier ?? defaultTier`) and `ActionDefinition` keeps no provenance, so nothing
downstream can tell `task` from `plan-default`. `PlanLoader.cs` is outside every wave-1 `writeScope`.
It is recorded as wave 2's problem, with that caveat, in the wave-2 brief. Do not assert a field the
shipped model cannot populate honestly — and note the enum's third value, `override`, has **no
producing rule anywhere in the DoR**, so do not invent one.
- **A pin does not silently coexist with a tier.** Where both are present the pin wins (the tier is
  dead weight `validate` warns about) — assert the pin's precedence, not the warning.

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
implement the behavior.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/TierResolverPrecedenceTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path — including
`TierResolver.cs`, other test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
