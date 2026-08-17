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
- **Legacy fallback fires ONLY when there is no effective tier (D30).** No `action.tier`, no judge
  frontmatter `tier`, and no `tiering.defaultTier` ⇒ `promptRunners.<name>.model`, else the CLI
  default — exactly today's behavior. It does **not** matter whether `routing` blocks are configured
  elsewhere in the registry.
- **The other half of D30, and it needs its own test: an effective tier NEVER falls back to legacy.**
  Once a rung exists, resolution owns the outcome — an empty `Candidates(R)` climbs to a stronger
  rung, and a genuinely empty registry at-or-above the rung settles **`no-route`**. Assert that a
  config with an effective tier and no serving block does **not** quietly produce the runner's model.
  Through revision 4 the DoR read "no effective tier, *or no block serves it*" here, which
  contradicted §6.2/D26's halt for the same condition; revision 5 severed it in favour of the halt.
  This test is what stops the old reading from coming back.
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

**Not `tierSource` — that is a different pair's job, not a gap.** DoR §12.4's journal field is
assembled in wave 2. Its *input* — whether the rung came from `action.tier` or `tiering.defaultTier`,
which `PlanLoader` destroys at load — is restored by the parallel pair
`05-author-tests-tier-provenance` / `06-implement-tier-provenance` as
`ActionDefinition.TierOrigin`. `PlanLoader.cs` is outside your write scope and you do not need it.
Do not assert `tierSource` here, and do not assert `TierOrigin` either — task 05 owns those tests and
duplicating them just makes two places to fix.

What DOES belong to you is the third enum value: per **D31** (revision 5), a full
`action.runner`/`action.model` pin is the producer of `tierSource: "override"`. That is a *precedence*
rule, so assert its observable half — that a pinned action is recognizable as pinned after
resolution, with no rung resolved — rather than the journal string, which wave 2 writes.
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
