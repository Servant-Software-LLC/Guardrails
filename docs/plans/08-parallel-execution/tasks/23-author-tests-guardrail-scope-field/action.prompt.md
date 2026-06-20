## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author xUnit.v3 tests in the single new file
`tests/Guardrails.Core.Tests/GuardrailScopeTests.cs` (class name exactly `GuardrailScopeTests`,
selected via `--filter "FullyQualifiedName~GuardrailScopeTests"`). Encode plan 08 §4.3 / Decision 2 /
Stage-2 BEFORE the `scope` field exists:
- A guardrail declares an optional `scope: "integration" | "local"` (default `"local"`), parsed from
  the deterministic guardrail sidecar key (§4.1) and from prompt-guardrail frontmatter (§4.2), surfaced
  on `GuardrailDefinition`.
- The **integration-guardrail set** = all `scope: "integration"` guardrails across the plan.
- The scope FILTER on the union re-verify (`IReVerifier`): an `integration`-scoped guardrail re-runs at
  EVERY union point; a **distant, non-colliding** task's `local` guardrail re-runs at a union only if
  the merge touched its files; a **colliding sibling's** `local` guardrail re-runs REGARDLESS of
  touched-files (the B-3 split - assert that a touched-files local-skip wrongly applied to a colliding
  sibling makes this test FAIL); the terminal `integrationGate` sink runs the same integration set on
  the final HEAD.

These reference the not-yet-existing `GuardrailDefinition.Scope` + the scope filter, so the project will
not compile against current code - that is the intended "fails on current code" signal. Do NOT implement
the field/filter - tests only, in this one file. Publish nothing to state.
