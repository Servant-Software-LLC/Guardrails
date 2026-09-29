## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key — this is a waved plan, so the key is
  `wave-01-contract-and-validate/03-author-tests-lite-validate-load` (NOT the bare folder
  name and NOT the stableId). The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-01-contract-and-validate/03-author-tests-lite-validate-load": { "someKey": "someValue" } }`.
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

You are writing the **TDD red half** for the first module of *Guardrails Lite*: a PowerShell 7
re-implementation of a subset of `guardrails validate` (design of record:
`docs/plans/guardrails-lite.charter.md`; the cross-task contract every Lite task shares is
`docs/plans/guardrails-lite-wave01-design.md` — **read both before you start**, especially the design
sheet's "Kernel script I/O contract", "Lite validate v1 code set" and "Cross-task APIs" sections).

You write **tests and throwing stubs only**. Do NOT implement the validator — a later task does.

### Files you create (exactly these)

1. `tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs` — namespace
   `Guardrails.Integration.Tests.Lite`, `public sealed class LiteValidateLoadTests`, class attribute
   `[Trait("Category", "Lite")]`.
2. **Committed fixture plan folders** under `tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-load/`
   — one folder per row of the table below, named exactly as the table says. **Naming is a contract:** a
   later task's parity test (task 07) walks this group IN PLACE (read-only, inside the git repo) and treats
   every folder whose name starts `invalid-` as deliberately broken (it must yield at least one code on
   both Lite and Core) and every other folder as clean. So: every minimally-broken fixture is
   `invalid-<code>-<defect>`, every clean one is `valid-<what>`, and each folder must be broken (or clean)
   **as committed** — never rely on a test-time edit to produce the defect.
   The clean baseline, `valid-baseline/`, is:
   - `guardrails.json` = `{ "version": 1 }`
   - `tasks/01-first/task.json` = `{ "description": "baseline task", "dependsOn": [], "stableId": "t1", "writeScope": [] }`
   - `tasks/01-first/action.ps1` = `exit 0`
   - `tasks/01-first/guardrails/01-check.ps1` = a first line `# catches: nothing - fixture guardrail` then `exit 0`

   Every other fixture is `valid-baseline` plus ONE edit (the table's "Fixture = baseline plus"). Author
   them cheaply: write `valid-baseline` once, then create each derived folder with a single
   `pwsh -NoProfile -Command` that copies the baseline and applies the edit (`Bash(pwsh *)` is allowed) —
   do not spend one Write per file across ~80 files. Git cannot commit an empty directory: where a
   fixture needs an empty-looking folder, the table says what file keeps it.
3. `scripts/lite/validate.ps1` — STUB. Body: `#Requires -Version 7`, then print exactly
   `{"script":"validate","ok":false,"stub":true}` on stdout and `exit 99`.
4. `scripts/lite/validate/Load.psm1` — STUB. Exports `function Invoke-LiteLoad { param([string]$PlanDir) throw 'stub' }` and `Export-ModuleMember -Function Invoke-LiteLoad`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs`, anything under
`tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-load/`, `scripts/lite/validate.ps1` and
`scripts/lite/validate/Load.psm1` (the two stub files). After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including changes to other production
files, `LiteScriptHost.cs`, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file (for example `LiteScriptHost` or `TempPlan` is absent or has a different shape than the design
sheet says), do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### How every test works — the ORACLE is Core

Each test copies ITS fixture to a temp dir and runs **both** against the copy:

- **Lite:** `using TempPlan plan = LiteScriptHost.CopyFixture("validate-load", "<fixture>");` then
  `LiteResult r = await LiteScriptHost.RunAsync("validate", plan.Dir);` (both from task 02 — use them,
  never redefine them).
- **Core (the oracle):** reproduce exactly what the CLI's `PlanProbe.LoadAndValidate`
  (`src/Guardrails.Cli/PlanProbe.cs`) does — `new PlanLoader().Load(dir)`; then, **only if**
  `load.Plan is not null && !load.HasErrors`, append `new PlanValidator().Validate(load.Plan)`. Read that
  method before you write the helper: semantic validation deliberately does NOT run when loading
  produced an error, and several codes below exist only on the semantic side.

Restrict both code sets to the **load code set** —
`GR1001 GR1002 GR1003 GR1004 GR1005 GR1006 GR1007 GR1009 GR2002 GR2003 GR2027 GR2029 GR2035` — as a
`private static readonly HashSet<string> LoadCodes`. Then assert, in this order:
1. **Non-vacuity:** for a fixture that targets code X, `Assert.Contains(X, core)` — the fixture must
   really trigger X in Core, or the test proves nothing.
2. **Parity:** the two restricted sets are equal (compare sorted lists so the failure message shows both).
3. **Exit code:** `1` when the restricted Core set is non-empty (every load code is an ERROR), `0` otherwise.

On the stubs every parity test FAILS (Lite exits 99 with no diagnostics). That is the intended red.

**Before committing a fixture, confirm Core really emits the code for it** — do not trust this list over
the source. For each code, grep its constant in `src/Guardrails.Core/Loading/PlanLoader.cs` and
`src/Guardrails.Core/Loading/PlanValidator.cs` (e.g. `grep -n "DiagnosticCodes.AmbiguousActionFile" src/Guardrails.Core/Loading/*.cs`)
and read the emit site. Two facts that decide fixtures, both measured at authoring time from that
source — **if your own reading disagrees, trust the source and say so in your summary:**
- `catches:` is enforced (GR2027) for `tasks/<id>/preflights/` and the plan-level `preflights/` and
  `guardrails/` folders, but **NOT** for a task's own `guardrails/` folder (`LoadGuardrailsFromFolder(..., enforceCatches: false)` in `LoadTask`).
- The loader enumerates task **directories** under `tasks/`; a plain file there (e.g. `.gitkeep`) is
  not a task, so `tasks/` holding only a file yields GR1009.

### Pinned test methods (use EXACTLY these names — a guardrail reads them from the test results)

All are `[Fact] public async Task <Name>()`, each running against the fixture named in its row.

| Method | Fixture (`validate-load/…`) = baseline plus | Core must emit |
|---|---|---|
| `ValidBaseline_ExitsZeroWithNoLoadCodes` | `valid-baseline` (no edit); also assert Lite `ok` is `true` | nothing from the load set |
| `Gr1001_MissingConfig` | `invalid-gr1001-missing-config`: no `guardrails.json` | GR1001 |
| `Gr1001_MissingTaskJson` | `invalid-gr1001-missing-task-json`: no `tasks/01-first/task.json` | GR1001 |
| `Gr1002_InvalidConfigJson` | `invalid-gr1002-config-json`: `guardrails.json` = `{ not json` | GR1002 |
| `Gr1002_InvalidSidecarJson` | `invalid-gr1002-sidecar-json`: add `tasks/01-first/guardrails/01-check.json` = `{ not json` | GR1002 |
| `Gr1003_ConfigMissingVersion` | `invalid-gr1003-config-missing-version`: `guardrails.json` = `{}` | GR1003 |
| `Gr1003_TaskMissingDescription` | `invalid-gr1003-task-missing-description`: `task.json` without `description` | GR1003 |
| `Gr1004_NoActionFile` | `invalid-gr1004-no-action-file`: no `action.ps1` | GR1004 |
| `Gr1005_TwoActionFiles` | `invalid-gr1005-two-action-files`: add `tasks/01-first/action.sh` (`exit 0`) | GR1005 |
| `Gr1006_ActionPathNotFound` | `invalid-gr1006-action-path-not-found`: `task.json` adds `"action": { "path": "missing.ps1" }` | GR1006 |
| `Gr1007_OrphanSidecar` | `invalid-gr1007-orphan-sidecar`: add `tasks/01-first/guardrails/02-orphan.json` = `{}` | GR1007 |
| `Gr1009_EmptyTasksDir` | `invalid-gr1009-empty-tasks-dir`: no `tasks/01-first/`; `tasks/` holds only a file `.gitkeep` | GR1009 |
| `Gr2003_TaskWithoutGuardrails` | `invalid-gr2003-task-without-guardrails`: no `tasks/01-first/guardrails/` | GR2003 |
| `Gr2027_PlanGuardrailMissingCatches` | `invalid-gr2027-plan-guardrail-missing-catches`: add plan-level `guardrails/01-plan-check.ps1` = `exit 0` (no catches line) | GR2027 |
| `Gr2027_TaskPreflightMissingCatches` | `invalid-gr2027-task-preflight-missing-catches`: add `tasks/01-first/preflights/01-pre.ps1` = `exit 0` | GR2027 |
| `TaskGuardrailWithoutCatches_IsNotGr2027` | `valid-task-guardrail-without-catches`: `01-check.ps1` is just `exit 0`; assert Lite exit 0 AND parity | nothing from the load set |
| `Gr2029_RetiredIntegrationGate` | `invalid-gr2029-retired-integration-gate`: `task.json` adds `"integrationGate": true` | GR2029 |
| `Gr2035_DuplicateCheckName` | `invalid-gr2035-duplicate-check-name`: add `tasks/01-first/guardrails/01-check.sh` (`exit 0`) | GR2035 |
| `LoadErrors_SuppressSemanticCodes` | `invalid-gr1003-suppresses-semantic`: no `tasks/01-first/guardrails/` AND a second task `tasks/02-second/` with an `action.ps1` and a `task.json` lacking `description` | GR1003 and NOT GR2003 — assert GR2003 is absent from BOTH sets |
| `MissingPlanFolder_ExitsUsage64` | no fixture — run Lite on a path that does not exist (no Core call) | assert exit `64` |
| `Output_IsOneJsonObjectWithDiagnostics` | `invalid-gr1004-no-action-file`; assert `r.Json` is non-null, `script` == `"validate"`, `ok` == `false`, and the GR1004 entry has non-empty `code`, `severity` == `"error"`, non-empty `message` | GR1004 |

**GR2002 (duplicate task id) has NO test, deliberately.** On a flat plan the task id is the folder name,
which the filesystem makes unique, so `PlanValidator.ValidateTaskIdsUnique` cannot fire on any fixture.
Do not invent one.

If `pwsh` is not on PATH, skip the way the design sheet says (`LiteScriptHost` / `TestShell` pattern).

### Done when
- `dotnet build tests/Guardrails.Integration.Tests` succeeds (the tests COMPILE against the stubs —
  a non-compiling test is a mistake to fix, a failing one is intended);
- running `dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateLoadTests"`
  shows **every** method in the table above **Failed** against the stubs (none skipped, none passing).
  A test that passes against a stub asserts nothing — make it invoke Lite and compare.
