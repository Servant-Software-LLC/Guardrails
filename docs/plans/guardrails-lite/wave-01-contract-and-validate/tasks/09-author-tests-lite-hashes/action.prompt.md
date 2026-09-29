## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/09-author-tests-lite-hashes`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/09-author-tests-lite-hashes": { "someKey": "someValue" } }`.
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

**Context.** Guardrails Lite (issue #823, `docs/plans/guardrails-lite.charter.md`) must reproduce three
harness hashes BYTE-FOR-BYTE, because Lite's lock, its review attestation and a future
`guardrails`-resumes-a-Lite-run all depend on them. Read the design sheet
`docs/plans/guardrails-lite-wave01-design.md` ("Hash algorithm", "Kernel script I/O contract", "Paths")
first. **Core is the oracle:** every test compares Lite's output to the real C# on the same folder.
You author the TESTS, the FIXTURES and minimal STUBS — you do NOT implement hashing (the next task does).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteHashParityTests.cs`,
`tests/Guardrails.Integration.Tests/Lite/LiteMarkReviewedTests.cs`,
files under `tests/Guardrails.Integration.Tests/Lite/Fixtures/hashes/`, and the three stub files
`scripts/lite/lib/Hash.psm1`, `scripts/lite/plan-hash.ps1`, `scripts/lite/mark-reviewed.ps1`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these paths
— including changes to `LiteScriptHost.cs`, other test files, production code, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to
the state-out path and stop.

### The oracle — read this C# before writing a test
- `Guardrails.Core.Journal.PlanDefinitionHash.Compute(PlanDefinition)` and the shared primitives in
  `Guardrails.Core.Hashing.HashText` (`AppendFile`, `EnumerateFolderFiles`, `NormalizeNewlines`).
- `Guardrails.Core.Journal.TaskDefinitionHash.Compute(TaskNode)` and `TaskDefinitionFiles.Enumerate`.
- `Guardrails.Core.Journal.PlanHash.Compute(PlanDefinition)` (the narrow journal hash).
- `Guardrails.Core.Review.ReviewMarker` (`Evaluate`, `PathFor`, `FileName`) and `ReviewAttestation`;
  `src/Guardrails.Cli/Commands/MarkReviewedCommand.cs` `BuildAttestation` (a bare stamp is
  `{ source: "bare", tool: "guardrails <version>", actor: <reviewer or omitted> }`).
- Load a plan the way existing integration tests do: `new PlanLoader().Load(dir)` (grep
  `tests/Guardrails.Integration.Tests` for `new PlanLoader().Load(`); every corpus plan must load with
  `HasErrors == false` — assert that as each test's precondition so a broken fixture fails loudly
  instead of comparing two garbage hashes.

Drive Lite ONLY through `LiteScriptHost` (task 02, `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs`):
`LiteScriptHost.RunAsync("plan-hash", dir)`, `RunAsync("mark-reviewed", dir)`,
`RunAsync("mark-reviewed", dir, "-Reviewer", "alice")`, and `CopyFixture("hashes", name)` /
`CopyPlan(path)` so every test works on a throwaway copy. Every test that spawns pwsh starts with
`Assert.SkipUnless(LiteScriptHost.PwshAvailable, "pwsh (PowerShell 7) not found on PATH");`.

### The script contract the tests pin (from the design sheet)
- `plan-hash.ps1 <planDir>` prints ONE JSON line:
  `{ "script":"plan-hash", "ok":true, "planDefinitionHash":"sha256:…", "planHash":"sha256:…", "tasks":{ "<taskId>":"sha256:…", … } }`
  — `planDefinitionHash` = PlanDefinitionHash, `planHash` = the narrow PlanHash, `tasks` = TaskDefinitionHash
  per task id. Exit 0. A missing plan dir → exit **64**.
- `mark-reviewed.ps1 <planDir> [-Reviewer <name>]` writes `<planDir>/state/guardrails-review.json`:
  `{ "version":2, "reviewedAt":"<ISO-8601 UTC>", "planHash":"<PlanDefinitionHash>", "attestation":{ "source":"bare", "tool":"guardrails-lite <version>", "actor":"<name>" } }`
  (`actor` omitted without `-Reviewer`; `<version>` from `$env:GUARDRAILS_LITE_VERSION`, else `0.0.0-dev`),
  and prints `{ "script":"mark-reviewed", "ok":true, "planHash":"sha256:…" }`.

### Fixtures — `tests/Guardrails.Integration.Tests/Lite/Fixtures/hashes/<name>/`, each a full flat plan that LOADS without errors
Design them to exercise every facet of the algorithm:
- **`sidecars-and-gates`** — two tasks: `01-alpha` (script action `action.ps1`; `guardrails/01-a.ps1`
  plus a `guardrails/01-a.json` sidecar; a NESTED file `guardrails/nested/helper.txt`; a task-level
  `preflights/01-p.ps1`) and `02-beta` (a prompt action `action.prompt.md`, a `guardrails/01-b.prompt.md`
  judge, `dependsOn: ["01-alpha"]`); plus plan-level `guardrails/01-gate.ps1` with a `01-gate.json`
  sidecar and plan-level `preflights/01-pre.ps1`. A prompt action needs a `promptRunners` block in
  `guardrails.json` (copy the shape of `examples/hello-guardrails/hello-guardrails/guardrails.json` if it
  has one, else `docs/plans/guardrails-lite/guardrails.json`).
- **`explicit-action-path`** — a task whose `task.json` sets `"action": { "path": "run-me.ps1" }` so the
  hashed label is `action:run-me.ps1`, not `action.*` discovery.
- **`ordinal-order`** — task folders whose ORDINAL order differs from culture order: `10-b`, `2-a`,
  `A-upper`, `a-lower`, `_under`. (Ordinal: `10-b` < `2-a` < `A-upper` < `_under` < `a-lower`.)
Every task folder: `task.json` with `description`, `dependsOn`, `writeScope: []`; every guardrail script
begins with a `# catches:` line (the loader requires it in these folders).
**Do NOT commit files whose exact BYTES matter** (CRLF, lone CR, BOM, non-ASCII): git's line-ending
normalization would rewrite them. Those variants are SYNTHESISED by the tests at runtime into the temp
copy with `File.WriteAllBytes`.

The corpus for the `ForEvery…` theories = the three fixtures above + `examples/hello-guardrails/hello-guardrails`
+ `examples/parallel-hello/parallel-hello` (verify both paths exist with Glob before relying on them).

### Tests — pinned names (the guardrail looks each one up in the TRX)
Namespace `Guardrails.Integration.Tests.Lite`; both classes carry `[Trait("Category", "Lite")]`.
`[Theory]` + `[MemberData]` is fine for the `ForEvery…` methods (data rows keep the method name).

**`LiteHashParityTests`**
- `PlanDefinitionHash_MatchesCore_ForEveryCorpusPlan` — `planDefinitionHash` == `PlanDefinitionHash.Compute(plan)`.
- `TaskDefinitionHash_MatchesCore_ForEveryTaskInEveryCorpusPlan` — `tasks` has exactly Core's task ids,
  and each value == `TaskDefinitionHash.Compute(task)`.
- `NarrowPlanHash_MatchesCore_ForEveryCorpusPlan` — `planHash` == `PlanHash.Compute(plan)`.
- `PlanDefinitionHash_MatchesCore_WithCrlfLoneCrAndBom` — on a copy of `sidecars-and-gates`, rewrite
  `tasks/01-alpha/task.json` with a UTF-8 BOM + CRLF, `tasks/01-alpha/guardrails/01-a.ps1` with lone
  `\r` line endings, and the plan-level `guardrails/01-gate.json` with mixed CRLF/LF; assert Lite == Core
  for all three hashes.
- `PlanDefinitionHash_MatchesCore_ForNonAsciiContent` — write `é`, `日本語` and `🙂` into
  `tasks/02-beta/action.prompt.md` (UTF-8, no BOM); assert Lite == Core.
- `PlanDefinitionHash_MatchesCore_AfterEditingAGuardrailSidecar` — hash, then edit
  `tasks/01-alpha/guardrails/01-a.json`, hash again; assert Lite == Core both times AND the two Lite
  values differ (proves the sidecar is inside the hashed set, not merely that two constants agree).
- `PlanHashScript_EmitsAllThreeHashesForEveryTask` — exit 0, `script == "plan-hash"`, `ok == true`,
  all three keys present and each hash matching `^sha256:[0-9a-f]{64}$`, one `tasks` entry per task.
- `PlanHashScript_MissingPlanDir_ExitsWithUsageCode64` — a non-existent directory → `ExitCode == 64`.

**`LiteMarkReviewedTests`** (work on a copy of `sidecars-and-gates`)
- `MarkReviewed_WritesAMarkerCoreEvaluatesAsReviewed` — exit 0; `ReviewMarker.Evaluate(plan).State ==
  ReviewState.Reviewed` (reload the plan after the script runs).
- `MarkReviewed_MarkerIsVersion2WithBareAttestationAndLiteTool` — read the file: `version == 2`,
  `planHash` present, `attestation.source == "bare"`, `attestation.tool` starts with `guardrails-lite `,
  no `actor` key.
- `MarkReviewed_ThenEditingAGuardrail_CoreEvaluatesStale` — mark, then append a line to
  `tasks/01-alpha/guardrails/01-a.ps1`; assert the state is exactly `ReviewState.Stale` (NOT merely
  "not Reviewed" — `Missing` would also be "not Reviewed" and would let a marker-less stub pass).
- `MarkReviewed_ReviewerOption_IsRecordedAsActor` — with `-Reviewer alice`, `attestation.actor == "alice"`.
- `MarkReviewed_PrintsThePlanDefinitionHashItWrote` — the printed `planHash` == the file's `planHash`
  == `PlanDefinitionHash.Compute(plan)`.

### Stubs (#155 — the tests must COMPILE and RUN and FAIL, not fail to build)
- `scripts/lite/plan-hash.ps1` and `scripts/lite/mark-reviewed.ps1`: print
  `{"script":"<name>","ok":false,"stub":true}` and `exit 99`. Write nothing else (mark-reviewed must NOT
  create a marker).
- `scripts/lite/lib/Hash.psm1`: export `Get-LitePlanDefinitionHash -PlanDir`,
  `Get-LiteTaskDefinitionHash -PlanDir -TaskId`, `Get-LiteNarrowPlanHash -PlanDir`, each body `throw 'stub'`.
Every Lite script starts `#Requires -Version 7`, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`.

**Every one of the 13 pinned tests must FAIL against these stubs** (that is the TDD red the guardrail
counts, test by test). A test that passes against a stub asserts nothing — make each one assert on the
script's actual output, never on a value the test computed itself. Do NOT implement hashing.

### Completion criteria (these match the guardrails)
1. `dotnet build tests/Guardrails.Integration.Tests` succeeds (stubs make the scripts exist; C# compiles).
2. With the stubs in place, all 13 pinned tests run and each is observed **Failed** in the test results.
