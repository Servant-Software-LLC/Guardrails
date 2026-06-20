## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §4.3 / Decision 2 so `GuardrailScopeTests` pass:
- `Model/GuardrailDefinition.cs`: add a `Scope` ("integration" | "local", default "local").
- `Loading/PromptFileParser.cs` + the deterministic guardrail sidecar parser: read `scope` from the
  prompt frontmatter and from the sidecar `.json` key.
- The integration-guardrail-set FILTER on `IReVerifier` (the seam itself is task 16's; this is the free
  predicate over the guardrail list): the integration set = all `scope: "integration"` guardrails. At a
  union, re-run (1) the union task's full set; (2) EVERY colliding sibling's FULL set UNCONDITIONALLY
  (no touched-files skip - B-3); (3) the integration set. The touched-files local-skip applies ONLY to
  a distant, non-colliding task's `local` guardrails. The terminal gate runs the same integration set.

Make `GuardrailScopeTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Do NOT implement the AI-merge worker (task 26). Keep the solution building.
Publish nothing to state.
