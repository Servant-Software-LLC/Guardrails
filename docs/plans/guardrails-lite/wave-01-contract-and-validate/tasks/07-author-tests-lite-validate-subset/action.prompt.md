## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/07-author-tests-lite-validate-subset`, NOT the stableId. The harness
  REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/07-author-tests-lite-validate-subset": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

You are writing the **TDD-red tests** for the third and last rule module of Guardrails Lite's
`validate.ps1`: `scripts/lite/validate/Subset.psm1`. It refuses plans that use features Lite does not
implement (GR2090) and emits the review-marker nudge (GR2025). You are also writing the
**validation-parity** test, which pins Lite's `validate.ps1` to the harness's own validator. This task
publishes no state.

Read these before you write anything:
- `docs/plans/guardrails-lite-wave01-design.md` — the SHARED CONTRACT. Its sections "Kernel script I/O
  contract", "Lite validate v1 code set" and "Cross-task APIs" are binding: the JSON your test parses,
  the `LiteScriptHost` API you call, and the `Invoke-LiteRules -Plan` signature your stub exports.
- `docs/plans/guardrails-lite.charter.md`, section "What \"Lite\" is: a strict subset of the plan
  format" — the subset table that GR2090 enforces.
- `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs` (an ancestor task wrote it) — use its real
  API. `tests/Guardrails.Integration.Tests/Lite/LiteValidateLoadTests.cs` shows the house style for a
  Lite test class; mirror it.

### Files you create (exactly these)

1. `tests/Guardrails.Integration.Tests/Lite/LiteValidateSubsetTests.cs` — class **`LiteValidateSubsetTests`**,
   namespace `Guardrails.Integration.Tests.Lite`, `[Trait("Category","Lite")]` on the class.
2. `tests/Guardrails.Integration.Tests/Lite/LiteValidationParityTests.cs` — class **`LiteValidationParityTests`**,
   same namespace and trait.
3. `tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-subset/<fixture>/` — the fixture plan folders
   listed below.
4. `scripts/lite/validate/Subset.psm1` — a **STUB**: it exports exactly `Invoke-LiteRules` with a
   `-Plan` parameter and returns `@()`. Nothing else. Don't implement it; a later task does that.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteValidateSubsetTests.cs`,
`tests/Guardrails.Integration.Tests/Lite/LiteValidationParityTests.cs`,
`tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-subset/` and `scripts/lite/validate/Subset.psm1`
(the stub). After this task completes, the harness runs a `git diff` check and rejects any edit outside
these paths — including changes to `validate.ps1`, `Load.psm1`, `LiteScriptHost.cs`, other test files,
or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### The fixtures (`Fixtures/validate-subset/`) — build every one from the base

**Base (`clean-lite/`)** must be a plan that the harness validator accepts with **no errors**:
- `guardrails.json`: `{ "version": 1, "profile": "lite", "interpreters": { "ps1": ["pwsh", "-NoProfile", "-File"] } }`
- `tasks/01-hello/task.json`: `{ "description": "write out/hello.txt", "dependsOn": [], "stableId": "fx01ab", "writeScope": ["out/"] }`
- `tasks/01-hello/action.ps1`: creates `out/hello.txt`.
- `tasks/01-hello/guardrails/01-hello-exists.ps1`: opens with a `# catches:` line and fails when
  `out/hello.txt` is absent.

Every other fixture is `clean-lite` with **exactly one** change:

| fixture | the one change |
|---|---|
| `no-profile` | `profile` key removed |
| `waved` | no root `tasks/`; the task lives under `wave-01-only/tasks/01-hello/` |
| `max-parallelism-4` | `"maxParallelism": 4` |
| `autonomy-block` | `"autonomy": {}` |
| `tiering-block` | `"tiering": { "defaultTier": "medium" }` |
| `routing-block` | `"promptRunners": { "default": "claude", "claude": { "kind": "claude", "routing": { "tiers": ["medium"] } } }` |
| `gateway-baseurl` | `"promptRunners": { "default": "claude", "claude": { "kind": "claude", "baseUrl": "http://127.0.0.1:4000", "authTokenEnv": "GW_TOKEN" } }` |
| `overwatch-profile` | `"promptRunners": { "default": "claude", "claude": { "kind": "claude" }, "overwatch": { "kind": "claude" } }` |
| `ai-merge-profile` | as `overwatch-profile`, with the reserved profile named `ai-merge` |
| `max-cost-usd` | `"maxCostUsd": 5` |
| `stall-timeout` | `"promptRunners": { "default": "claude", "claude": { "kind": "claude", "stallTimeoutSeconds": 600 } }` |
| `invalid-unknown-dependency` | `dependsOn: ["99-missing"]` (a GR2001) |
| `invalid-no-guardrails` | the task has no `guardrails/` folder (a GR2003) |
| `invalid-cycle` | two tasks, `01-a` and `02-b`, each `dependsOn` the other (a GR2007) |

**Verify the key names before you rely on them.** Don't trust this table's spelling. Run
`Grep "public .* \{ get; set; \}"` over `src/Guardrails.Core/Loading/RawManifests.cs` and confirm that each
key above is the camelCase of a property on `RawRunConfig`, `RawPromptRunner` or `RawPromptRunnerRouting`.
Then grep `ReservedActionRoleProfileNames` and `ReservedAdvisoryRoleProfileNames` in
`src/Guardrails.Core/Loading/PlanValidator.cs` for the reserved profile names. **If the grep disagrees with
this table, trust the grep**, use what it says, and say so in your summary.

The prefix `invalid-` is a CONTRACT: `LiteValidationParityTests` treats every fixture directory whose name
starts with `invalid-` as deliberately invalid.

### `LiteValidateSubsetTests` — pin these exact method names

Each test copies its fixture with `LiteScriptHost.CopyFixture("validate-subset", "<fixture>")` and runs
`LiteScriptHost.RunAsync("validate", tmp.Dir)`. A GR2090 assertion means **at least one** diagnostic with
`code == "GR2090"`, the stated `severity`, and a `message` containing the stated token
(case-insensitive). Every test ALSO asserts that the JSON's `script` is `"validate"` and that it carries
no `"stub": true`. That second assertion is what makes each test fail against a stub.

| method | fixture | asserts |
|---|---|---|
| `Subset_WavedLayout_IsError` | `waved` | GR2090 `error`, token `wave`; exit code 1 |
| `Subset_AutonomyBlock_IsError` | `autonomy-block` | GR2090 `error`, token `autonomy`; exit 1 |
| `Subset_RoutingBlock_IsError` | `routing-block` | GR2090 `error`, token `routing`; exit 1 |
| `Subset_GatewayBaseUrl_IsError` | `gateway-baseurl` | GR2090 `error`, token `baseUrl`; exit 1 |
| `Subset_OverwatchProfile_IsError` | `overwatch-profile` | GR2090 `error`, token `overwatch`; exit 1 |
| `Subset_AiMergeProfile_IsError` | `ai-merge-profile` | GR2090 `error`, token `ai-merge`; exit 1 |
| `Subset_MaxParallelismAboveOne_IsWarning` | `max-parallelism-4` | GR2090 `warning`, token `maxParallelism`; NO GR2090 error |
| `Subset_TieringBlock_IsWarning` | `tiering-block` | GR2090 `warning`, token `tiering`; NO GR2090 error |
| `Subset_MaxCostUsd_IsWarning` | `max-cost-usd` | GR2090 `warning`, token `maxCostUsd`; NO GR2090 error |
| `Subset_StallTimeoutSeconds_IsWarning` | `stall-timeout` | GR2090 `warning`, token `stallTimeoutSeconds`; NO GR2090 error |
| `Subset_MissingProfile_IsWarning` | `no-profile` | GR2090 `warning`, token `profile`; NO GR2090 error |
| `Subset_CleanLitePlan_PassesWithNoGR2090` | `clean-lite` | exit 0, `ok: true`, ZERO GR2090 diagnostics |
| `ReviewMarker_Missing_EmitsGR2025Warning` | `clean-lite` | a GR2025 with severity `warning` |
| `ReviewMarker_FreshFromCore_EmitsNoGR2025` | `clean-lite` | writes a fresh marker with the HARNESS's own code into the temp copy — `new PlanLoader().Load(tmp.Dir)`, then `ReviewMarker.Write(plan, DateTimeOffset.UtcNow)` (grep `public static void Write` in `src/Guardrails.Core/Review/ReviewMarker.cs` for the real signature) — then runs validate and asserts NO GR2025, exit 0 |

Why these severities: the charter decides it. A refused feature is an **error**, meaning Lite will not run the
plan. A key that is **inert** in Lite is a **warning**. `maxParallelism > 1` runs sequentially (decision
`lite-parallelism`), `tiering` routes nothing, and Lite can meter neither cost nor stalls. A missing
`"profile": "lite"` means the plan was not authored for Lite, which is worth saying but not a refusal
(decision `lite-plan-marker`).

### `LiteValidationParityTests` — the drift contract

The oracle is the harness itself, called **in-process**. Never shell out to an installed `guardrails`. The
diagnostics `guardrails validate` prints come from `Guardrails.Cli.PlanProbe.LoadAndValidate(folder)`, plus
`PlanValidator.ReviewMarkerDiagnostics(plan, ReviewNudgeSurface.Validate)` when the plan loaded. Read
`ValidateCommand.Run` in `src/Guardrails.Cli/Commands/ValidateCommand.cs` and collect **exactly** what it
collects, except the GR2072 check-set warning, which is about the binary, not the plan.

- `static readonly HashSet<string> ImplementedByLite`: the union of the three code lists in the design
  sheet's "Lite validate v1 code set" (Load, Graph, Subset), **excluding GR2090** (Core never emits it).
- **`Parity_LiteMatchesCore(string fixture)`** is a `[Theory]` with a `[MemberData]` that yields, as
  `fixture` ids:
  - every directory under `tests/Guardrails.Integration.Tests/Lite/Fixtures/validate-load/`,
    `.../validate-graph/` and `.../validate-subset/` that holds a `guardrails.json`, as `"<group>/<name>"`;
  - plus `"examples/hello-guardrails"` and `"examples/parallel-hello"`, which map to
    `examples/<name>/<name>/`.

  Enumerate only those three groups, **not** `Fixtures/**`. The hash and lock fixtures belong to tasks
  that are not ancestors of the task that must make this green, so including them would leave the parity
  test with no owner. Run both validators **in place** (both are read-only). Resolve paths from
  `LiteScriptHost.RepoRoot`.

  Per row:
  1. Assert Lite's JSON `script` is `"validate"`, there is no `"stub": true`, and the exit code is 0 or 1.
     That is what makes every row red against the stub.
  2. If Lite emitted any GR2090 with severity `error`, the plan is outside Lite's subset and parity is
     undefined. Assert only that Lite exited 1, and return.
  3. Otherwise assert **set equality**: `(CoreCodes ∩ ImplementedByLite) == (LiteCodes \ {GR2090})`. On a
     mismatch, report the codes Core has that Lite lacks, and those Lite has that Core lacks.
  4. Assert `(LiteCodes \ {GR2090}) ⊆ ImplementedByLite`. Lite must not invent codes it does not own.
- **`Parity_InvalidFixtures_AreNonVacuous`** (`[Fact]`): for every enumerated fixture whose name starts
  with `invalid-`, BOTH Core and Lite yield at least one code in `ImplementedByLite`. This is what stops a
  `validate.ps1` that emits nothing from passing parity on the valid rows. Name each failing fixture in the
  message.
- **`Parity_CorpusIsNonEmpty`** (`[Fact]`): the `MemberData` source yields at least 8 rows and includes both
  `examples/...` ids. This test is green against the stubs **by construction** (it never runs Lite). It is a
  declared exemption in the red census, and it exists so a corpus walk that finds nothing cannot pass
  silently.

These parity rows are pinned by the red census: `validate-subset/clean-lite`, `validate-subset/no-profile`,
`validate-subset/max-parallelism-4`, `validate-subset/invalid-unknown-dependency`,
`validate-subset/invalid-no-guardrails`, `validate-subset/invalid-cycle`, `examples/hello-guardrails`,
`examples/parallel-hello`. The theory parameter must be named **`fixture`** and the ids spelled exactly as
above, because the census matches the TRX row name `Parity_LiteMatchesCore(fixture: "<id>")`. Make each
`MemberData` row a plain `string` so xUnit discovers the rows individually.

### Completion criteria (these match the guardrails)

- `dotnet build tests/Guardrails.Integration.Tests` succeeds.
- Run with
  `--filter "Category=Lite&(FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateSubsetTests|FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidationParityTests)"`,
  every pinned method and every pinned parity row above RUNS and **FAILS** against the stubs, except
  `Parity_CorpusIsNonEmpty`, which must run and pass. The tests must **compile and fail**: failing is
  intentional, not compiling is a mistake to fix. Don't make them pass, and don't implement `Subset.psm1`.
- Don't `[Skip]` anything. If `pwsh` is absent, follow the design sheet's skip rule and nothing else.
