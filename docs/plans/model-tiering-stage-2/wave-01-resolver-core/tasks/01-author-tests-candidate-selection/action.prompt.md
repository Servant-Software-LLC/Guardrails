## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/01-author-tests-candidate-selection`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/01-author-tests-candidate-selection": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **failing tests** for DoR §6.2 candidate selection, and the **minimal stubs** the rest of
this wave compiles against.

**Read `docs/plans/17-model-tiering.md` §6.2 first — it is the design of record and it wins over any
paraphrase below.**

### Files to create

1. `src/Guardrails.Core/Prompts/TierResolution.cs` — the result record. Carry at least: the selected
   `PromptRunnerConfig` block and its name, the effective model string, the effective effort, and
   enough provenance to answer "how did I get here" — the rung actually served, whether the resolver
   **climbed** from the requested rung to a stronger one, and **both of the following, which are
   knowable ONLY inside the resolver and which wave 2 cannot re-derive without duplicating the
   candidacy predicate**:
   - **The D28 binding-ceiling datum** — whether a *stronger* block was excluded **only** because it
     is `costly: true`. Wave 2 must log a loud warning on re-attempt when that is so (DoR §6.2, D28).
     That fact exists nowhere but `SelectCandidate`; if this record does not carry it, wave 2 must
     either retrofit this record after wave 1's exit gate has certified the resolver core complete,
     or re-test `Costly` outside the resolver — which is exactly the duplication D22a forbids and
     which this task's own guardrails reject.
   *(Not `tierSource`. DoR §12.4 requires it, but it is **not computable at this layer**: the loader
   collapses `action.tier` and `tiering.defaultTier` into one field at load —
   `PlanLoader.cs`, `Tier = rawAction?.Tier ?? defaultTier` — and `ActionDefinition` keeps no
   provenance, so nothing downstream can tell `task` from `plan-default`. Making it computable needs a
   change to `PlanLoader`/`ActionDefinition`, which is outside every wave-1 `writeScope`. It is
   recorded as wave 2's problem, with that caveat, in the wave-2 brief. Do not invent a field you
   cannot populate honestly.)*
2. `src/Guardrails.Core/Prompts/TierResolver.cs` — a **stub only** for this task. Declare BOTH entry
   points the wave needs, each throwing `NotImplementedException`:
   - `SelectCandidate(...)` — the §6.2 selection this task's tests exercise;
   - `Resolve(...)` — the §6.1 precedence entry point. **Declare it now, throwing**, so task 03 can
     author precedence tests that COMPILE against a stable signature. Do not implement either.
3. `tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs` — class
   **`TierResolverCandidateSelectionTests`**, every test tagged `[Trait("Category", "TierResolution")]`.

The class name and file path above are **pinned** — this task's guardrails filter on
`FullyQualifiedName~TierResolverCandidateSelectionTests`, and its sibling implementation task copies
that filter verbatim. Renaming either breaks both halves of the pair.

### Behaviors the tests MUST encode (DoR §6.2)

- **The shared predicate decides candidacy.** `Candidates(R)` = blocks where `routing` is present AND
  `R ∈ routing.tiers` AND `costly` is not `true`.
- **Ascending `strength` ordering — the WEAKEST capable model wins.** A `hard` task gets the weakest
  block the operator declared capable of `hard`. There is no numeric tier→strength mapping.
- **`strength` unspecified sorts LAST**; ties break by declaration order.
- **The costly floor**: a `costly: true` block is excluded at its own rung AND at a climbed-to
  stronger rung. Assert both — a resolver that filters costly only at the requested rung passes a
  one-rung test and violates the floor on the climb.
- **`costly` is TRI-STATE at the schema and TWO-state here**: absent (`null`) and explicit `false`
  BOTH serve; only an explicit `true` excludes. An un-annotated registry must stay routable.
- **The climb**: an empty candidate set at rung R climbs to the nearest STRONGER rung with a non-empty
  set, and the result records that it climbed. It NEVER routes to a weaker rung.
- **No candidate at any rung at-or-above R** yields the `no-route` condition rather than a silent
  fallback or an exception the caller cannot distinguish.
- **The D28 datum is REPORTED, not just obeyed.** Assert that when the only stronger block is
  `costly: true`, the result says so — the floor excluded it *and the resolution records that the
  ceiling bound*. A test that only checks the costly block was not selected leaves wave 2 nothing to
  warn from.

Tests must **COMPILE and FAIL** against the stubs — failing is intentional; NOT compiling is a mistake
to fix. Do **not** implement the behavior.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs`,
`src/Guardrails.Core/Prompts/TierResolver.cs` and `src/Guardrails.Core/Prompts/TierResolution.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including changes to other production files, neighbouring test files, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The D22a agreement property — the most important test in this file

`PromptRunnerConfig.ServesTier(tier)` is the ONE candidacy predicate, and validate's GR2048 check uses
it. DoR D22a calls a single shared predicate **"a correctness requirement, not tidiness"**: if
validation and the runtime resolver ever disagree about which blocks serve a rung, validation passes
and every task at that rung dies at runtime on `no-route`.

Earlier drafts of this plan tried to enforce that by grepping the implementation for `ServesTier` and
banning re-implementations. That was measured and **abandoned** — every such check was either evadable
(an inlined copy spelled a different way, or `ServesTier` named only in a comment) or false-red on a
correct implementation. Prove it **behaviourally instead**, which catches drift however it is spelled:

> **Write a property test asserting the resolver's candidate set AGREES with `ServesTier` for every
> (block, rung) pair in a registry you construct** — build a registry spanning the interesting shapes
> (routing absent; routing present serving the rung; routing present not serving it; `costly: true`;
> `costly: false`; `costly` absent; `strength` present and absent), then for every rung assert that a
> block is a candidate **iff** `block.ServesTier(rung)` is true.

That one test is worth more than any regex: an inlined copy that is equivalent today passes it, and
**fails the moment it drifts** — which is exactly the failure D22a exists to prevent, and the only
moment at which it matters.
