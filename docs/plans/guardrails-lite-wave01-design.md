# Guardrails Lite — wave 01 design sheet (SHARED CONTRACT for all authoring agents)

Plan of record: `docs/plans/guardrails-lite.charter.md` (reviewed; all 7 questions answered).
Plan folder: `docs/plans/guardrails-lite/` (WAVED, JIT). This sheet pins every cross-task name so
independently-authored task folders agree. Do not deviate; if something here is wrong, stop and report.

## Settled breakdown facts
- Stack: dotnet. `$testFramework` = xunit.v3 (3.2.2). `$testRunner` = **vstest** (no MTP properties anywhere) → `--filter` scopes correctly.
- `$tiering` = not-configured → NO `tier` keys, no tiering block, no tiering report lines.
- `$charter` = true (input is `.charter.md`) → Step 0d does not run; no decisions.md.
- Baseline preflight: SKIPPED — the plan adds new isolated files/tests and changes no existing behaviour (greenfield for the touched area).
- Harness fact: `guardrails.json` unknown top-level keys are IGNORED silently (RawRunConfig has no extension data) → the `"profile":"lite"` contract needs NO loader change, only SSOT + a pinning test.
- Next free GR code: **GR2090** → reserved BY NAME (comment, like GR2083) as `LiteUnsupportedFeature`; next-free comment moves to GR2091. The harness never emits it; only `validate.ps1` does.
- Script runtime: **pwsh 7 only**. Every Lite script starts `#Requires -Version 7`, `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`.

## Paths (pinned)
| What | Path |
|---|---|
| Kernel scripts | `scripts/lite/<name>.ps1` |
| Validator rule modules | `scripts/lite/validate/Load.psm1`, `Graph.psm1`, `Subset.psm1` |
| Hash module | `scripts/lite/lib/Hash.psm1` |
| Lite tests (integration project) | `tests/Guardrails.Integration.Tests/Lite/<Class>.cs`, namespace `Guardrails.Integration.Tests.Lite` |
| Lite fixtures | `tests/Guardrails.Integration.Tests/Lite/Fixtures/<group>/<fixture-name>/` (each a full plan folder) |
| Contract pinning test | `tests/Guardrails.Core.Tests/Loading/LiteProfileContractTests.cs` |
| Script host (test infra) | `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs` |

Fixture folders must be copied to the test output or read from the source tree — the host resolves the repo root by walking up to the directory containing `Guardrails.sln` (there is NO .slnx), and tests copy a fixture into a per-test temp dir before running a script against it (scripts may write `state/`). Never write into the source fixture.

## Kernel script I/O contract (every `scripts/lite/*.ps1`)
- Prints **exactly one JSON object** to stdout (compressed, UTF-8, last line). Diagnostics for humans go to stderr.
- Common fields: `{ "script": "<name>", "ok": <bool>, ... }`.
- Exit codes: `0` ok · `1` negative verdict (validation errors, hash mismatch detected by a verify) · `2` refused (unsafe/lock changed — reserved for wave 02) · `3` step-order violation (wave 02) · `64` usage error (bad args, missing plan folder).
- `validate.ps1 <planDir>` → `{ "script":"validate","ok":bool,"diagnostics":[{"code":"GR####","severity":"error|warning","message":"...","path":"<rel or null>"}] }`; exit 1 iff any `error`.
- `plan-hash.ps1 <planDir>` → `{ "script":"plan-hash","ok":true,"planDefinitionHash":"sha256:…","planHash":"sha256:…","tasks":{"<id>":"sha256:…"} }` (planDefinitionHash = PlanDefinitionHash; planHash = the narrow journal PlanHash; tasks = TaskDefinitionHash per task).
- `mark-reviewed.ps1 <planDir> [-Reviewer <name>]` writes `state/guardrails-review.json` exactly as the harness's ReviewMarker v2 (`{"version":2,"reviewedAt":ISO-8601 UTC,"planHash":"<PlanDefinitionHash>","attestation":{"source":"bare","tool":"guardrails-lite <version>","actor"?:…}}`), prints `{ "script":"mark-reviewed","ok":true,"planHash":… }`.
- `lock.ps1 <planDir>` writes `state/lite-run.lock.json` = `{"version":1,"createdAt":ISO,"planDefinitionHash":…,"tasks":{"<id>":"<TaskDefinitionHash>"}}`; refuses (exit 1, `ok:false`) if a lock already exists unless `-Force`. `lock.ps1 <planDir> -Verify` recomputes and exits 0 if identical, else exit 1 with `changed:[ "<id>", ... ]` naming every task whose definition hash moved (and `"<plan>"` when only plan-level gate files moved).
- Version: `scripts/lite/VERSION` is NOT in wave 01 (wave 04 distribution). mark-reviewed reads `$env:GUARDRAILS_LITE_VERSION` or falls back to `0.0.0-dev`.

## Hash algorithm (MUST match Core byte-for-byte — oracle is Core, see tests)
`PlanDefinitionHash.Compute` (src/Guardrails.Core/Journal/PlanDefinitionHash.cs:54-90) + `HashText` (src/Guardrails.Core/Hashing/HashText.cs): each file → `label + U+001F + NormalizeNewlines(ReadAllText) + U+001E` (absent file = empty body; BOM stripped; CRLF/CR→LF). Order: `guardrails.json`; tasks sorted Ordinal by id with labels `task:<id>/` + {`task.json`, `action:<rel>`, `guardrails/**`, `preflights/**`}; `<plan>/guardrails/**`; `<plan>/preflights/**`; (waves — Lite refuses waved plans, but Hash.psm1 need not support them). Folder labels = relative path, `\`→`/`, Ordinal sort, recursive. SHA-256 of UTF-8 → `sha256:` + lowercase hex. TaskDefinitionHash = same file set, labels WITHOUT the `task:<id>/` prefix. Narrow PlanHash (src/Guardrails.Core/Journal/PlanHash.cs) = `guardrails.json` + each `task:<id>` → task.json only. READ THE C# BEFORE WRITING THE PROMPT; pin method names, not line numbers (#203/#578).

## Lite validate v1 code set (the parity contract)
IMPLEMENTED by Lite (Lite must emit these exactly when Core does, on the fixture corpus):
- **Load.psm1** (task 04): GR1001, GR1002, GR1003, GR1004, GR1005, GR1006, GR1007, GR1009, GR2002, GR2003, GR2027, GR2029, GR2035.
- **Graph.psm1** (task 06): GR2001, GR2007, GR2010, GR2011, GR2019, GR2021, GR2041, and GR2025 (warning: review marker missing/stale — uses Hash.psm1? NO: Graph must not depend on task 10. GR2025 moves to Subset.psm1, task 08, which depends on 10).
- **Subset.psm1** (task 08): GR2090 LiteUnsupportedFeature (one diagnostic per unsupported feature, message names the feature) + GR2025.
GR2090 fires for: waved layout (any `wave-NN-*` dir), `maxParallelism > 1` is NOT an error (decision `lite-parallelism`: runs sequentially) → it is a **warning** GR2090 `severity:"warning"`; overwatch/autonomy blocks, `tiering`, promptRunner `routing`/gateway blocks, `mergeOnSuccess`/AI-merge config, `maxCostUsd` → error unless the key is inert (then warning) — task 07's tests pin the exact list; the task prompt must cite the charter's subset table (docs/plans/guardrails-lite.charter.md "What Lite is").
Everything else Core emits is on the parity ALLOW-LIST (Lite need not emit it). The allow-list lives in the parity test as a named `static readonly HashSet<string> NotImplementedByLite`.
`profile`: validate.ps1 does NOT require `"profile":"lite"` (the harness accepts it and ignores it); Subset.psm1 emits a warning GR2090 when it is absent ("plan not marked lite") — task 07 pins this.

## Wave 01 tasks (ids, scopes, deps, test classes) — FINAL
Trait on every Lite test class: `[Trait("Category","Lite")]`. Task-level filters: `--filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.<Class>"` (or Core.Tests namespace for the contract).

| # | folder | kind | writeScope | dependsOn | test class(es) |
|---|---|---|---|---|---|
| 01 | `01-record-lite-profile-contract` | prompt | `docs/plans/02-schemas-and-contracts.md`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs`, `tests/Guardrails.Core.Tests/Loading/LiteProfileContractTests.cs` | — | `LiteProfileContractTests` (Core.Tests, namespace `Guardrails.Core.Tests.Loading`) — PINS existing behaviour (green on arrival by design; GR2075 named exception) |
| 02 | `02-add-lite-script-host` | prompt | `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs`, `tests/Guardrails.Integration.Tests/Lite/LiteScriptHostTests.cs` | — | `LiteScriptHostTests` (test infra self-test; GR2075 named exception) |
| 03 | `03-author-tests-lite-validate-load` | prompt | `tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs`, `tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-load/`, `scripts/lite/validate.ps1`, `scripts/lite/validate/Load.psm1` | 02 | `LiteValidateLoadTests` |
| 04 | `04-implement-lite-validate-load` | prompt | `scripts/lite/validate.ps1`, `scripts/lite/validate/Load.psm1` | 03 | (passes `LiteValidateLoadTests`) |
| 05 | `05-author-tests-lite-validate-graph` | prompt | `tests/.../Lite/LiteValidateGraphTests.cs`, `tests/.../Lite/Fixtures/validate-graph/`, `scripts/lite/validate/Graph.psm1` | 02, 03 | `LiteValidateGraphTests` |
| 06 | `06-implement-lite-validate-graph` | prompt | `scripts/lite/validate/Graph.psm1` | 04, 05 | (passes `LiteValidateGraphTests`) |
| 07 | `07-author-tests-lite-validate-subset` | prompt | `tests/.../Lite/LiteValidateSubsetTests.cs`, `tests/.../Lite/LiteValidationParityTests.cs`, `tests/.../Lite/Fixtures/validate-subset/`, `scripts/lite/validate/Subset.psm1` | 02, 03 | `LiteValidateSubsetTests`, `LiteValidationParityTests` |
| 08 | `08-implement-lite-validate-subset-and-parity` | prompt | `scripts/lite/validate/` (all three modules: parity closure may need Load/Graph fixes) | 04, 06, 07, 10 | (passes both 07 classes) |
| 09 | `09-author-tests-lite-hashes` | prompt | `tests/.../Lite/LiteHashParityTests.cs`, `tests/.../Lite/LiteMarkReviewedTests.cs`, `tests/.../Lite/Fixtures/hashes/`, `scripts/lite/lib/Hash.psm1`, `scripts/lite/plan-hash.ps1`, `scripts/lite/mark-reviewed.ps1` | 02 | `LiteHashParityTests`, `LiteMarkReviewedTests` |
| 10 | `10-implement-lite-hashes` | prompt | `scripts/lite/lib/Hash.psm1`, `scripts/lite/plan-hash.ps1`, `scripts/lite/mark-reviewed.ps1` | 09 | (passes both 09 classes) |
| 11 | `11-author-tests-lite-lock` | prompt | `tests/.../Lite/LiteLockTests.cs`, `tests/.../Lite/Fixtures/lock/`, `scripts/lite/lock.ps1` | 02 | `LiteLockTests` |
| 12 | `12-implement-lite-lock` | prompt | `scripts/lite/lock.ps1` | 10, 11 | (passes `LiteLockTests`) |

`tests/.../` = `tests/Guardrails.Integration.Tests`. Edge reasons: 03/05/07/09/11 ← 02 (they use LiteScriptHost); 05/07 ← 03 (need the validate.ps1 dispatcher stub to exist); 06 ← 04 (the real dispatcher loads Graph.psm1); 08 ← 10 (GR2025 needs Hash.psm1 PlanDefinitionHash) and ← 04/06 (parity spans all modules); 12 ← 10 (lock uses Hash.psm1).
Parity oracle: tests call Core directly (`PlanLoader` → `PlanValidator.Validate` / `PlanDefinitionHash.Compute` / `TaskDefinitionHash.Compute` / `PlanHash.Compute`) — grep for how existing integration tests load a plan and reuse that; never shell out to an installed `guardrails` binary.
The corpus for parity = every fixture under `Lite/Fixtures/**` that is a plan folder + `examples/hello-guardrails/hello-guardrails` + `examples/parallel-hello/<its plan folder>` (verify exact paths with Glob).

**Stub rule (#155):** each author-tests task writes the throwing/empty stubs so the test project compiles and the tests RUN and FAIL: a stub `.ps1` prints `{"script":"<name>","ok":false,"stub":true}` and `exit 99`; a stub `.psm1` exports its function returning an empty array. Implementation tasks replace stubs; their writeScope EXCLUDES the test files and fixtures.

**Red census (#375):** every author-tests task pins test METHOD names in its prompt (one per enumerated behaviour) and its `02-tests-fail-on-stubs.ps1` is the PER-TEST census over the TRX (stacks/dotnet.md §4.4), with the zero-match guard (§4.3) PROVEN to fire. Its `01-build-passes.ps1` builds `tests/Guardrails.Integration.Tests` (or Core.Tests for 01). Implementation tasks: `01-build-passes`, `02-tests-pass` (#179 re-emit, §4.2; zero-match guard; filter names the pair's class), and where useful a structural check (e.g. no `exit 99` / `"stub":true` left in the scripts it owns — a source-shape check with a committed `samples/` pair).

## Gates
- `wave-01-contract-and-validate/guardrails/` (wave EXIT, all LOCAL, no scope key): `01-solution-builds.ps1` (dotnet build Guardrails.sln -c Debug), `02-lite-tests-pass.ps1` (`Category=Lite` across Integration + the Core contract class, #179 re-emit, zero-match guard), `03-no-stubs-remain.ps1` (no `"stub":true` / `exit 99` under `scripts/lite/`).
- `wave-01-contract-and-validate/preflights/`: none (wave 1; greenfield).
- Plan root `guardrails-lite/guardrails/01-union-conflict-free.ps1` + `.json` `{"scope":"integration"}`: union-safe conditional — for each file under `scripts/lite/` and `tests/Guardrails.Integration.Tests/Lite/` that EXISTS, fail on line-anchored `(?m)^<<<<<<<` / `(?m)^>>>>>>>`. (GR2028 credit.)
- `wave-02-run-kernel/`: stub (`tasks/` empty + `brief.md`), authored by the lead.

## guardrails.json (plan root) — lead writes it
version 1, maxParallelism 4, retries 2, interpreters ps1→pwsh, promptRunners claude (maxTurns 50, acceptEdits, allowedTools Read/Edit/Write/Grep/Glob, `Bash(dotnet *)`, `Bash(pwsh *)`, read-only git).

## Authoring rules every agent MUST follow (from /plan-breakdown — the full skill is in your context)
Harness-contract header verbatim in every prompt (state key = wave-qualified id `wave-01-contract-and-validate/<folder>`); Scope boundary paragraph in every test-author prompt; pinned test class + method names; `$ErrorActionPreference='Stop'` + `$PSNativeCommandUseErrorActionPreference=$false` opening every .ps1 guardrail and explicit `exit`; `# catches:` line; multi-line failure `if` blocks; #179 re-emit on tests-pass; no `-v q` on dotnet test; `$env:DOTNET_CLI_UI_LANGUAGE='en'`; zero-match guard on executed count, proven to fire; measured baseline counts on required-present clauses (#478); `.md` targets strip `<!-- -->`; committed `samples/` pairs for source-shape code checks honouring `GR_SUBJECT`/argv[0]; `stableId` minted (lowercase base36, unique — prefix yours: A-agent uses `a…`, B `b…`, C `c…`); `writeScope` exactly as the table; one `action.prompt.md` per task; `maxTurns: 75` on 08 (parity closure) and 10 (byte-exact hash port) via `task.json` `"action": {"maxTurns": 75}`.
Structural claims in prompts: ship the command, not the list (#578). Execute every runnable guardrail against valid+invalid samples in a TEMP dir (#302) and record the results in your hand-back.

## Cross-task APIs (pinned — tests and modules authored by different agents call these)
**C# — `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs` (task 02), namespace `Guardrails.Integration.Tests.Lite`:**
- `public static class LiteScriptHost`
  - `public static string RepoRoot { get; }` — walks up from `AppContext.BaseDirectory` to the dir holding `Guardrails.sln`.
  - `public static Task<LiteResult> RunAsync(string scriptName, params string[] args)` — runs `pwsh -NoProfile -File <RepoRoot>/scripts/lite/<scriptName>.ps1 <args>` (scriptName without `.ps1`), 120 s timeout, captures stdout/stderr; parses the LAST non-empty stdout line as JSON.
  - `public static TempPlan CopyFixture(string group, string name)` — copies `Lite/Fixtures/<group>/<name>/` (from the SOURCE tree under RepoRoot) into a fresh temp dir; `TempPlan : IDisposable` with `string Dir`; Dispose deletes it (strip read-only first).
  - `public static TempPlan CopyPlan(string repoRelativePlanDir)` — same, for `examples/...` plan folders.
- `public sealed record LiteResult(int ExitCode, System.Text.Json.JsonElement? Json, string Stdout, string Stderr)` with helper `public IReadOnlyList<string> DiagnosticCodes()` (reads `Json.diagnostics[*].code`, empty if absent).
- Skip rule: if `pwsh` is not on PATH, tests `Assert.Skip("pwsh not found")` — use `TestShell`'s existing detection pattern (tests/Guardrails.Integration.Tests/TestShell.cs).

**PowerShell — validator modules:**
- `scripts/lite/validate.ps1 <planDir>` (dispatcher, task 04): `Import-Module Load.psm1`; `$r = Invoke-LiteLoad -PlanDir $planDir` → `@{ Plan = <plan|$null>; Diagnostics = @(...) }`; if `$r.Plan` is non-null, for each of `Graph.psm1`, `Subset.psm1` that EXISTS in `scripts/lite/validate/`, import it and append `Invoke-LiteRules -Plan $r.Plan`. Emit the JSON, exit 1 iff any error.
- Diagnostic object: `[pscustomobject]@{ code='GR####'; severity='error'|'warning'; message='...'; path=<planDir-relative string or $null> }`.
- Plan object from `Invoke-LiteLoad`: `[pscustomobject]@{ Root; Config (the parsed guardrails.json, PSCustomObject); Waved (bool); Tasks = @([pscustomobject]@{ Id; Dir; Json (parsed task.json); ActionPath (full path or $null); GuardrailFiles = @(full paths, ordinal by name); PreflightFiles = @(...) }); PlanGuardrailFiles; PlanPreflightFiles }`. Tasks sorted Ordinal by Id.
- `Graph.psm1` and `Subset.psm1` each export exactly `Invoke-LiteRules -Plan <object>` returning diagnostic objects. Stubs return `@()`.
- `scripts/lite/lib/Hash.psm1` exports `Get-LitePlanDefinitionHash -PlanDir`, `Get-LiteTaskDefinitionHash -PlanDir -TaskId`, `Get-LiteNarrowPlanHash -PlanDir` (each returns `sha256:<hex>`). Stubs throw `NotImplemented`-style: `throw 'stub'`.

## Post-fan-out pins (lead, after agent C)
- Solution file is `Guardrails.sln` (no `.slnx`).
- Fixture naming: a deliberately-broken fixture is named `invalid-<code-or-defect>`; clean baselines have no prefix. Parity (task 08) treats `invalid-*` as must-yield-≥1-code on both sides and runs IN PLACE (read-only) over the `validate-load`, `validate-graph`, `validate-subset` groups + the two examples.
- Parity compares against an explicit `ImplementedByLite` set (Core emits workspace codes Lite never owns); oracle = `Guardrails.Cli.PlanProbe.LoadAndValidate` + `PlanValidator.ReviewMarkerDiagnostics`.
