## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-01-resolver-core/02-implement-candidate-selection`, NOT the stableId and NOT the
  bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-01-resolver-core/02-implement-candidate-selection": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the `SelectCandidate` stub in `src/Guardrails.Core/Prompts/TierResolver.cs` so
the tests authored by `01-author-tests-candidate-selection` pass. **Do NOT edit those tests.** If they
are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than
changing them — an out-of-scope edit to a test file fails the write-scope check and burns a retry.

**`docs/plans/17-model-tiering.md` §6.2 is the design of record and wins over any paraphrase here.**

### The three rules that must not be softened

1. **Call the SHARED predicate — do not re-implement it.** `PromptRunnerConfig.ServesTier(tier)`
   already encodes `routing` present ∧ `tier ∈ routing.tiers` ∧ `costly is not true`. It is a
   **correctness requirement (D22a)**, not tidiness, that validate's GR2048 check and this resolver
   use the SAME predicate: if GR2048 counted a costly block as serving a rung and the resolver did
   not, validation would pass and every task at that rung would die at runtime on `no-route`. A
   guardrail on this task fails if `TierResolver.cs` inlines its own copy of the predicate.
2. **Never weaker than asked.** An empty candidate set climbs to the nearest STRONGER rung with a
   non-empty set, recording the climb in the result. It never routes down; there is no downward lever
   in v1.
3. **Never costly without the human.** `costly: true` is excluded at EVERY rung — its own and any
   climbed-to rung. No override, no `--force`, no autonomy dial. Do not add a bypass parameter,
   config key or flag, however convenient it looks: it is the one rule in this design with no
   override, and a later stage (the judge bump) depends on the exclusion holding at every rung.

Order candidates by **ascending `strength`** so the WEAKEST capable model wins; `strength` unspecified
sorts LAST; ties break by declaration order. When no rung at-or-above the requested one has a
candidate, produce the `no-route` condition — a distinguishable result, not a silent fallback.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/TierResolver.cs`
and `src/Guardrails.Core/Prompts/TierResolution.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including the test file, other production
files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.

Leave the `Resolve(...)` precedence entry point still throwing `NotImplementedException` — task
`04-implement-resolution-precedence` fills it, and its own TDD-red guardrail depends on it still
throwing when that task starts.
