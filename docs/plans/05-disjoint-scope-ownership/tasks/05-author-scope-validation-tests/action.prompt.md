## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author failing xUnit.v3 tests in
`tests/Guardrails.Core.Tests/ScopeValidationTests.cs` for the three new scope
diagnostics (§6, §8, §10 M3 of `docs/plans/05-disjoint-scope-ownership.md`). Mirror
the existing validator-test conventions in `PlanValidatorTests.cs` (how it builds a
plan fixture and asserts a `Diagnostic` with a given code/severity). The diagnostics:

- **GR2015 (ERROR):** a task that `dependsOn` a test-author task but declares a
  `writeScope` that does NOT exclude that ancestor's outputs. This is the load-bearing
  subsumption soundness guard and is a HARD ERROR — and these authored tests are its
  ONLY proof. Assert severity Error, and pin the FULL case matrix, each as its OWN
  mandatory test case:
  - (a) the dependent declares a **universal `["**"]`** writeScope → GR2015 fires (Error).
  - (b) the dependent has an **absent** `writeScope` (resolves to universal) → fires (Error).
  - (c) the dependent declares a scope that **intersects but does not exclude** the
    ancestor's outputs — e.g. impl `["src/**","tests/Feature/Helpers/**"]` against an
    ancestor that owns `tests/Feature/**` → fires (Error).
  - (d) the dependent declares a scope that **properly excludes** the ancestor's test
    paths → does NOT fire (the clean case; assert no GR2015 diagnostic).
- **GR2016 (WARNING):** two INDEPENDENT tasks (no DAG path between them) whose
  writeScopes overlap → a warning (lost parallelism / plan smell). Reuse
  `DependencyGraph` for "no DAG path between A and B". Assert severity Warning, plus a
  clean case: disjoint independents produce no GR2016.
- **GR2017 (ERROR):** a malformed glob in a writeScope — `?`, brace-expansion
  `{a,b}`, or negation `!`. Assert severity Error, plus a clean case: a valid glob
  produces no GR2017. Reuse `WorkspaceContainment` for workspace-escape checks where
  relevant.

Mirror the existing validator-test conventions in `PlanValidatorTests.cs` and keep
the file framework-concrete: xUnit.v3, a class named `ScopeValidationTests` in
`tests/Guardrails.Core.Tests/ScopeValidationTests.cs`.

The tests MUST fail (or fail to compile, because the GR2015/2016/2017 code constants
and the validator logic do not exist yet) against current code — that is intentional.
Do NOT implement the validator logic or add the diagnostic-code constants here.

You do NOT need to hash anything or write to state — `captureHashes` handles it.
Publish nothing to state.
