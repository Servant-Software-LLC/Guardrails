## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key — this is a waved plan, so the key is
  `wave-01-contract-and-validate/04-implement-lite-validate-load` (NOT the bare folder
  name and NOT the stableId). The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-01-contract-and-validate/04-implement-lite-validate-load": { "someKey": "someValue" } }`.
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

Implement the first module of *Guardrails Lite* — a PowerShell 7 re-implementation of a subset of
`guardrails validate` — so the already-authored tests in
`tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs` pass. Read first:
`docs/plans/guardrails-lite-wave01-design.md` (the shared contract — "Kernel script I/O contract",
"Lite validate v1 code set", "Cross-task APIs"), then the test file itself, which is the specification.

**Fill real logic over the two stub files; do NOT edit the tests or the fixtures.** Make them pass by
fixing the implementation. If the authored tests are genuinely wrong or unsatisfiable, write
`{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` rather than changing them — an edit to
any test or fixture file is outside this task's write scope and fails the task.

### 1. `scripts/lite/validate.ps1 <planDir>` — the dispatcher

- Opens with `#Requires -Version 7`, then its `param(...)` block (PowerShell requires `param` to be the
  first statement), then `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` — all
  within the first 15 lines, uncommented.
- `planDir` missing or not an existing directory → print `{"script":"validate","ok":false,"error":"<why>"}` and `exit 64`.
- `Import-Module (Join-Path $PSScriptRoot 'validate/Load.psm1') -Force`; `$r = Invoke-LiteLoad -PlanDir $planDir`.
- **Rule modules run ONLY when the load was clean** — `$r.Plan` is non-null AND `$r.Diagnostics` holds no
  `severity -eq 'error'`. This mirrors Core exactly: `PlanProbe.LoadAndValidate`
  (`src/Guardrails.Cli/PlanProbe.cs`) runs `PlanValidator` only when `load.Plan is not null && !load.HasErrors`.
  When clean, for each of `Graph.psm1`, `Subset.psm1` that EXISTS in `scripts/lite/validate/`, import it
  and append `Invoke-LiteRules -Plan $r.Plan`. (Later tasks author those modules; a stub returning `@()`
  may already be present — that is fine.)
- Emit ONE compressed JSON object as the last stdout line:
  `{"script":"validate","ok":<no errors>,"diagnostics":[{"code","severity","message","path"}...]}`
  (`ConvertTo-Json -Depth 6 -Compress`; make `diagnostics` an array even with 0 or 1 entries).
  `exit 1` iff any diagnostic has `severity` `error`, else `exit 0`.
- No network calls, no external modules. Human-facing noise goes to stderr, never stdout.

### 2. `scripts/lite/validate/Load.psm1` — `Invoke-LiteLoad -PlanDir`

Returns `@{ Plan = <plan object or $null>; Diagnostics = @(...) }` with the plan-object shape pinned in
the design sheet ("Cross-task APIs": `Root`, `Config`, `Waved`, `Tasks` — each `Id`, `Dir`, `Json`,
`ActionPath`, `GuardrailFiles`, `PreflightFiles` — and `PlanGuardrailFiles`, `PlanPreflightFiles`;
tasks sorted Ordinal by `Id`; file lists sorted Ordinal by file name). Later modules consume that object,
so keep the property names exact.

It emits the **load code set**: GR1001 GR1002 GR1003 GR1004 GR1005 GR1006 GR1007 GR1009 (the loader
phase) and GR2002 GR2003 GR2027 GR2029 GR2035. The oracle is Core, and the tests compare against it, so
**port the rules from the C#, do not reinvent them.** Find each emit site with a grep, e.g.
`grep -n "DiagnosticCodes\." src/Guardrails.Core/Loading/PlanLoader.cs` and
`grep -n "DiagnosticCodes\.\(DuplicateTaskId\|NoGuardrails\|RetiredIntegrationGateKey\|DuplicateCheckName\)" src/Guardrails.Core/Loading/PlanValidator.cs`,
and read the method around each hit (`LoadConfig`, `LoadTasks`, `LoadTask`, `ResolveAction`,
`DiscoverActionByConvention`, `LoadGuardrailsFromFolder`, `ApplySidecar`, `HasCatchesDeclaration` /
`LeadingCommentDeclaresCatches` / `FrontmatterDeclaresCatches`, `GuardrailName`,
`ValidateGuardrailsPresent`, `ValidateNoLegacyIntegrationGate`, `ValidateDuplicateCheckNames`).
Points that are easy to get wrong:

- **Phases.** Loader codes come first; GR2002/GR2003/GR2029/GR2035 are VALIDATOR codes and must only be
  emitted when the loader phase produced no error (same boundary as the dispatcher rule above — the test
  `LoadErrors_SuppressSemanticCodes` pins it).
- **Early returns.** A missing/unparseable `guardrails.json` or missing `version` stops the load (no
  tasks read, `Plan` is `$null`). A task whose `task.json` is missing/unparseable, lacks `description`,
  or has no resolvable action is SKIPPED (no task node) but the load continues with the other tasks.
- **`catches:` enforcement (GR2027)** applies to `tasks/<id>/preflights/` and the plan-level
  `preflights/` and `guardrails/` folders — NOT to a task's own `guardrails/` folder.
- **Guardrail folders** are read non-recursively; `.json` files are sidecars when a same-basename script
  sits beside them, otherwise GR1007; an unparseable sidecar is GR1002.
- **Check names** (GR2035): a `.prompt.md` file drops the whole `.prompt.md`; anything else drops only its
  final extension. Compare Ordinal.
- JSON parsing must accept what Core accepts: comments and trailing commas (`PlanJson.Options`), and
  property names case-insensitively. `ConvertFrom-Json` does not accept comments — strip `//` and
  `/* */` outside strings, or parse with `[System.Text.Json.JsonDocument]` using
  `JsonDocumentOptions { CommentHandling = Skip; AllowTrailingCommas = true }`.
- `Waved` is `$true` when the plan root contains any `wave-NN-*` directory. Lite v1 does not LOAD waved
  plans (a later task refuses them with GR2090) — set the flag and return no tasks for them; do not
  emit a load-set code for a waved layout.
- GR2002 (duplicate task id) cannot fire on a flat plan (ids are unique folder names); implement it
  defensively anyway (Ordinal duplicate check over task ids). It has no test.

### Done when
`dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateLoadTests"`
passes with every test executed, and both files carry the preamble (`#Requires -Version 7`, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`) within their first 15 lines — `Load.psm1` has no `param` block, so
there the three lines simply come first.
