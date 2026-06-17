## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement M3 (§6, §8, §10 of `docs/plans/05-disjoint-scope-ownership.md`) so the
`ScopeValidation` tests pass:

- Add the three diagnostic-code constants to
  `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, following the existing pattern
  (GR2014 is currently the highest). Use these exact codes:
  - `GR2015` — subsumption guard (ERROR).
  - `GR2016` — overlapping scopes among independents (WARNING).
  - `GR2017` — malformed glob (ERROR).
- Implement the checks in `src/Guardrails.Core/Loading/PlanValidator.cs`:
  - GR2015 (error): a task that `dependsOn` a test-author task but whose `writeScope`
    does not exclude that ancestor's outputs (universal/absent scope, or a scope that
    intersects the ancestor's). This is the ONLY test protection now, so it is a hard
    error, not a warning.
  - GR2016 (warning): two independent tasks (no DAG path between them, via
    `DependencyGraph`) whose writeScopes overlap (via `WriteScope.Overlaps`).
  - GR2017 (error): a malformed glob (`?`, brace, negation) in a writeScope. Reuse
    `WorkspaceContainment` for workspace-escape checks.

Make the `FullyQualifiedName~ScopeValidation` tests pass WITHOUT modifying the test
file. Do NOT change the M1/M2 code. If a test contradicts the design doc, write
{"needsHuman": "<why>"} and stop rather than editing it. Publish nothing to state.
