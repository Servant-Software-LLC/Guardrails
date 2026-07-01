## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `03-author-tests-four-folder-loader`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-four-folder-loader": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author failing tests + the minimal MODEL STUBS for the four-folder loader/validator (Deliverable 2, and
test-group #7 from Deliverable 11). The four folders are: plan-level `<plan>/preflights/` and
`<plan>/guardrails/` (siblings of `tasks/`, `guardrails.json`, `state/` at the plan root), and task-level
`tasks/<id>/preflights/` (sibling of the existing `tasks/<id>/guardrails/`). Each holds deterministic-first
guardrail files with the SAME grammar as today's `tasks/<id>/guardrails/`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/FourFolderLoaderTests.cs`, `tests/Guardrails.Core.Tests/FourFolderValidationTests.cs`,
new fixture dirs under `tests/Guardrails.Core.Tests/TestData/`, and the minimal STUBS in
`src/Guardrails.Core/Model/PlanDefinition.cs`, `src/Guardrails.Core/Model/TaskNode.cs`,
`src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this task the harness runs a `git diff` check and
rejects any edit outside these paths — including `PlanLoader.cs`, `PlanValidator.cs`, or the `.csproj`. An
out-of-scope edit fails the task and consumes a retry. If you hit a compile error from a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` and stop.

Minimal STUBS to add (just enough for the tests to COMPILE; the implementation task fills the real logic):
- `PlanDefinition`: add `public IReadOnlyList<GuardrailDefinition> PlanPreflights { get; init; } = [];` and
  `public IReadOnlyList<GuardrailDefinition> PlanGuardrails { get; init; } = [];` (empty defaults — the
  loader does NOT yet populate them, so tests asserting they are populated will fail = red).
- `TaskNode`: add `public IReadOnlyList<GuardrailDefinition> Preflights { get; init; } = [];` (empty default).
- `DiagnosticCodes`: allocate CONTIGUOUS new `const string` codes starting at **GR2027** (GR2026 is the last
  taken; confirm at run time). This resolves the deferred GR-numbering (plan note #1). Allocate enough for:
  a malformed/empty plan-level `<plan>/preflights/` folder, the re-homed GR2018 rule for `<plan>/guardrails/`
  (a multi-leaf/fan-in plan whose terminal folder is empty OR contains only a tautological check), a
  malformed task-level `tasks/<id>/preflights/` folder, and a legacy `integrationGate` unsupported-key
  diagnostic. Record the exact code→meaning mapping you chose in a comment at the top of the new codes and
  in `state` (see below), so the implementation task and the SSOT edit use the same numbers.

Tests to author (tag every new test class `[Trait("Category", "Preflights")]`):
1. A fixture plan under `TestData/` with all FOUR folders **validates clean** (no diagnostics / expected exit 0).
2. A fixture with a **malformed declaration in each of the four folders** yields the expected GR2027+ code
   (one assertion per new code).
3. A **multi-leaf / fan-in** fixture whose `<plan>/guardrails/` folder is **EMPTY** FAILS validation
   (re-homed GR2018 rule).
4. A multi-leaf/fan-in fixture whose `<plan>/guardrails/` folder contains **only a tautological `exit 0`
   file** (no real integration-set re-run) FAILS validation (content teeth preserved, not merely "non-empty").
5. A plan still declaring the old `integrationGate: true` task kind is handled per the retirement decision —
   assert the explicit unsupported-key diagnostic (lean toward an explicit diagnostic over silent ignore).
6. GR2017 is retired: a multi-leaf plan that declares NO terminal `integrationGate` sink but HAS a non-empty
   real `<plan>/guardrails/` folder validates clean (no GR2017).
7. The existing `scope: "integration"` per-union tag on a guardrail still parses (no regression).

Reuse the existing loader/validator test conventions and helpers (`PlanLoaderTests.cs`, `PlanFixtures.cs`,
`TestData/` fixture-dir style, `TestPaths.cs`). The tests MUST **compile** (against the stubs) and **fail**
(the loader does not populate the folders and the validator does not emit the new codes yet) — failing is
intentional, NOT compiling is a mistake. Do NOT implement the loader/validator logic. Keep everything
warning-clean (`TreatWarningsAsErrors=true`).

**Publish to state** the GR-code allocation you chose, so downstream tasks agree on the numbers:
`{ "03-author-tests-four-folder-loader": { "grCodes": { "GR2027": "<meaning>", "GR2028": "<meaning>", ... } } }`
