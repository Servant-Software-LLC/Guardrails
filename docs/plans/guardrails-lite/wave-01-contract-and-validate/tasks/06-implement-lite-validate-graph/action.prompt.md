## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key — this is a waved plan, so the key is
  `wave-01-contract-and-validate/06-implement-lite-validate-graph` (NOT the bare folder
  name and NOT the stableId). The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-01-contract-and-validate/06-implement-lite-validate-graph": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your task key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them. Nest one inside your
  task key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Implement the graph/scope rule module of *Guardrails Lite* so the already-authored tests in
`tests/Guardrails.Integration.Tests/Lite/LiteValidateGraphTests.cs` pass. Read first:
`docs/plans/guardrails-lite-wave01-design.md` ("Lite validate v1 code set", "Cross-task APIs"), then the
test file (the specification), then the dispatcher `scripts/lite/validate.ps1` and the plan object
`scripts/lite/validate/Load.psm1` builds (`Invoke-LiteLoad`) — both landed in task 04; describe them from
the files as they are on your base, not from this prompt.

**Fill real logic over the stub `scripts/lite/validate/Graph.psm1`; do NOT edit the tests or fixtures.**
If the authored tests are genuinely wrong or unsatisfiable, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing them.

### `scripts/lite/validate/Graph.psm1` — `Invoke-LiteRules -Plan <plan object>`

- Opens with `#Requires -Version 7`, `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`
  (within the first 15 lines, uncommented), and exports exactly `Invoke-LiteRules`.
- Returns diagnostic objects `[pscustomobject]@{ code; severity = 'error'; message; path }` for the
  **graph code set**: GR2001 GR2007 GR2010 GR2011 GR2019 GR2021 GR2041. Nothing else — codes outside the
  set belong to other modules.
- The oracle is Core; **port each rule from `src/Guardrails.Core/Loading/PlanValidator.cs`**, finding it
  with a grep (e.g. `grep -n "DiagnosticCodes\.\(UnknownDependency\|DependencyCycle\|DuplicateStableId\|InvalidStableId\|WriteScopeEscapesWorkspace\|InvalidGuardrailScopeValue\|MissingWriteScope\)" src/Guardrails.Core/Loading/PlanValidator.cs`)
  and reading the enclosing method (`ValidateDependencies`, `ValidateNoCycles` →
  `src/Guardrails.Core/Graph/DependencyGraph.cs` `FindCycle`, `ValidateStableIdsUnique`,
  `ValidateStableIdFormat` and its `StableIdPattern`, `ValidateWriteScopes`, `ValidateGuardrailScopeValues`).
  Details that decide parity:
  - **GR2041 vs `[]`:** an ABSENT (or JSON `null`) `writeScope` is GR2041; a present empty array is valid.
    Under `Set-StrictMode`, reading a missing property throws — test presence with
    `$task.Json.PSObject.Properties['writeScope']` (or the equivalent on whatever the loader returns).
  - **GR2019:** `Path.IsPathRooted(entry)` OR any segment of `entry.Split('/', '\')` equals `..`; blank
    entries are skipped. Use `[System.IO.Path]::IsPathRooted` so the platform rule matches Core's.
  - **GR2021:** read each guardrail's same-basename `.json` sidecar yourself (the plan object carries
    file paths, not sidecar contents) for task `guardrails/` + `preflights/` and the plan-level
    `preflights/` + `guardrails/`; the scope is trimmed and lower-cased before comparison (`ApplySidecar`
    in `src/Guardrails.Core/Loading/PlanLoader.cs`), and only `integration` / `local` are valid. An
    unparseable sidecar is the loader's GR1002, not yours — skip it.
  - **GR2007:** one diagnostic for the plan when any cycle exists (Core reports the first cycle it finds);
    a `dependsOn` naming an unknown task is GR2001, not a cycle.
- No network calls, no external modules.

### If a test fails because of the DISPATCHER, not your module
`scripts/lite/validate.ps1` is in this task's write scope for exactly one reason: the graph tests are the
first to exercise the dispatcher's rule-module path (import `Graph.psm1` when it exists; run rule modules
only over a load with no errors — `PlanProbe.LoadAndValidate` in `src/Guardrails.Cli/PlanProbe.cs`). If
that path is wrong, fix it minimally there and say so in your summary. Do not restructure the dispatcher,
and do not touch `Load.psm1` (a load-set test regression would be yours to explain).

### Done when
`dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateGraphTests"`
passes with every test executed, and `LiteValidateLoadTests` still pass (the same command with
`LiteValidateLoadTests` in the filter).
