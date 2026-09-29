## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key — this is a waved plan, so the key is
  `wave-01-contract-and-validate/05-author-tests-lite-validate-graph` (NOT the bare folder
  name and NOT the stableId). The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-01-contract-and-validate/05-author-tests-lite-validate-graph": { "someKey": "someValue" } }`.
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

You are writing the **TDD red half** for the graph/scope module of *Guardrails Lite* — the PowerShell 7
re-implementation of a subset of `guardrails validate` (design of record:
`docs/plans/guardrails-lite.charter.md`; shared cross-task contract:
`docs/plans/guardrails-lite-wave01-design.md` — **read the design sheet first**, especially "Lite validate
v1 code set" and "Cross-task APIs"). The sibling load module's tests,
`tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs`, already exist on your base — **mirror
their oracle helper and fixture style**; do not edit that file.

You write **tests, committed fixtures and a stub only**. Do NOT implement the rules — a later task does.

### Files you create (exactly these)

1. `tests/Guardrails.Integration.Tests/Lite/LiteValidateGraphTests.cs` — namespace
   `Guardrails.Integration.Tests.Lite`, `public sealed class LiteValidateGraphTests`, class attribute
   `[Trait("Category", "Lite")]`.
2. **Committed fixture plan folders** under `tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-graph/`
   — one folder per row of the table below, named exactly as the table says. **Naming is a contract:** a
   later task's parity test (task 07) walks this group IN PLACE (read-only, inside the git repo) and treats
   every folder whose name starts `invalid-` as deliberately broken (it must yield at least one code on
   both Lite and Core) and every other folder as clean. So every broken fixture is
   `invalid-<code>-<defect>`, every clean one is `valid-<what>`, and each folder must be broken (or clean)
   **as committed** — never rely on a test-time edit to produce the defect. Every fixture — broken or
   clean — must LOAD cleanly (graph codes are validator codes, and the validator only runs over a load
   with no errors).
   The clean baseline, `valid-baseline/`, is:
   - `guardrails.json` = `{ "version": 1 }`
   - `tasks/01-first/task.json` = `{ "description": "first", "dependsOn": [], "stableId": "t1", "writeScope": ["out/first.txt"] }`
   - `tasks/02-second/task.json` = `{ "description": "second", "dependsOn": ["01-first"], "stableId": "t2", "writeScope": ["out/second.txt"] }`
   - each task: `action.ps1` = `exit 0`, and `guardrails/01-check.ps1` = first line `# catches: nothing - fixture guardrail`, then `exit 0`
   - plan-level `guardrails/01-plan-check.ps1` = first line `# catches: nothing - fixture plan gate`, then `exit 0`

   Every other fixture is `valid-baseline` plus ONE edit. Author them cheaply: write `valid-baseline` once,
   then create each derived folder with a single `pwsh -NoProfile -Command` that copies the baseline and
   applies the edit (`Bash(pwsh *)` is allowed) — do not spend one Write per file across ~100 files.
3. `scripts/lite/validate/Graph.psm1` — STUB: `function Invoke-LiteRules { param($Plan) @() }` and
   `Export-ModuleMember -Function Invoke-LiteRules`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteValidateGraphTests.cs`, anything under
`tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-graph/`, and `scripts/lite/validate/Graph.psm1`
(the stub). After this task completes, the harness runs a `git diff` check and rejects any edit outside
these paths — including `scripts/lite/validate.ps1`, `Load.psm1`, `LiteScriptHost.cs`,
`LiteValidateLoadTests.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that
file — write `{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out
path and stop.

### How every test works — the ORACLE is Core

Same recipe as `LiteValidateLoadTests`: `using TempPlan plan = LiteScriptHost.CopyFixture("validate-graph", "<fixture>");`,
then run Lite (`LiteScriptHost.RunAsync("validate", plan.Dir)`) and Core (`new PlanLoader().Load(dir)`,
then `new PlanValidator().Validate(load.Plan)` only when `load.Plan is not null && !load.HasErrors` — the
rule in `PlanProbe.LoadAndValidate`, `src/Guardrails.Cli/PlanProbe.cs`) against the same copy. Restrict
both to the **graph code set** `GR2001 GR2007 GR2010 GR2011 GR2019 GR2021 GR2041` (a
`private static readonly HashSet<string> GraphCodes`). Assert in order: (1) **non-vacuity** — for a
fixture targeting X, `Assert.Contains(X, core)`, and for EVERY test `Assert.False(load.HasErrors)` so a
fixture that broke the load (and silently disabled the validator) cannot pass as "no graph codes";
(2) **parity** — the restricted sets are equal (sorted lists); (3) **exit code** — `1` when the restricted
Core set is non-empty (every graph code is an ERROR).

**Confirm each trigger in the source before committing its fixture** — grep the constant, e.g.
`grep -n "DiagnosticCodes.WriteScopeEscapesWorkspace" src/Guardrails.Core/Loading/PlanValidator.cs`, and
read the method (`ValidateDependencies`, `ValidateNoCycles` → `Graph/DependencyGraph.FindCycle`,
`ValidateStableIdsUnique`, `ValidateStableIdFormat`, `ValidateWriteScopes`, `ValidateGuardrailScopeValues`).
Facts measured from that source at authoring time — **if your reading disagrees, trust the source and say
so in your summary:** a sidecar's `scope` is trimmed and lower-cased before it is checked
(`ApplySidecar` in `PlanLoader.cs`), so `" Integration "` is valid; GR2019 splits entries on both `/` and
`\` and also fires for a rooted path; `writeScope: []` is VALID while an absent `writeScope` is GR2041.

### Pinned test methods (use EXACTLY these names — a guardrail reads them from the test results)

All `[Fact] public async Task <Name>()`, each running against the fixture named in its row.

| Method | Fixture (`validate-graph/…`) = baseline plus | Core must emit |
|---|---|---|
| `ValidBaseline_HasNoGraphCodes` | `valid-baseline` (no edit) | nothing from the graph set |
| `Gr2001_UnknownDependency` | `invalid-gr2001-unknown-dependency`: `02-second` `dependsOn` = `["99-missing"]` | GR2001 |
| `Gr2007_DependencyCycle` | `invalid-gr2007-dependency-cycle`: `01-first` `dependsOn` = `["02-second"]` | GR2007 |
| `Gr2010_DuplicateStableId` | `invalid-gr2010-duplicate-stable-id`: `02-second` `stableId` = `"t1"` | GR2010 |
| `Gr2011_InvalidStableId` | `invalid-gr2011-invalid-stable-id`: `01-first` `stableId` = `"Bad:Id"` | GR2011 |
| `Gr2019_ParentSegment` | `invalid-gr2019-parent-segment`: `01-first` `writeScope` = `["../outside.txt"]` | GR2019 |
| `Gr2019_RootedPath` | `invalid-gr2019-rooted-path`: `01-first` `writeScope` = `["/abs/path.txt"]` | GR2019 |
| `Gr2021_InvalidScopeOnTaskGuardrail` | `invalid-gr2021-task-guardrail-scope`: add `tasks/01-first/guardrails/01-check.json` = `{ "scope": "bogus" }` | GR2021 |
| `Gr2021_InvalidScopeOnPlanGuardrail` | `invalid-gr2021-plan-guardrail-scope`: add `guardrails/01-plan-check.json` = `{ "scope": "bogus" }` | GR2021 |
| `ScopeValue_IsTrimmedAndCaseInsensitive` | `valid-scope-trimmed-case-insensitive`: add `tasks/01-first/guardrails/01-check.json` = `{ "scope": " Integration " }` | nothing from the graph set |
| `Gr2041_MissingWriteScope` | `invalid-gr2041-missing-write-scope`: `01-first` has no `writeScope` | GR2041 |
| `EmptyWriteScope_IsNotGr2041` | `valid-empty-write-scope`: `01-first` `writeScope` = `[]` | nothing from the graph set |
| `GraphDiagnostic_IsErrorSeverityAndExitsOne` | `invalid-gr2001-unknown-dependency`; assert exit `1`, `ok` == `false`, and the GR2001 entry has `severity` == `"error"` and a non-empty `message` | GR2001 |

Skip when `pwsh` is absent the same way `LiteValidateLoadTests` does.

### What "red" means here — read before you check your work
Against the stub `Graph.psm1` every **`invalid-*` fixture** test must FAIL (Lite reports no graph code, Core reports
one). Three tests assert that Lite and Core both report NOTHING — `ValidBaseline_HasNoGraphCodes`,
`ScopeValue_IsTrimmedAndCaseInsensitive`, `EmptyWriteScope_IsNotGr2041`. Depending on whether the real
`validate.ps1` dispatcher (task 04) has already landed on your base, those three may PASS against the
stub — that is correct, not a tautology, and the guardrail declares them as exemptions. Do not weaken them
to make them red. Every other test must be **Failed**.

### Done when
- `dotnet build tests/Guardrails.Integration.Tests` succeeds;
- `dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateGraphTests"`
  runs every method above, and all ten non-exempt ones are **Failed** against the stub.
