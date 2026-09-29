## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/11-author-tests-lite-lock`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/11-author-tests-lite-lock": { "someKey": "someValue" } }`.
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

**Context.** Guardrails Lite (issue #823, `docs/plans/guardrails-lite.charter.md`, "Tamper resistance")
defends its verdicts with a **hash lock**: at run start `lock.ps1` records the definition hash of every
task's action/guardrails/preflights, and before accepting a pass the loop re-verifies it, so an agent
that "fixes" a failing guardrail by weakening it is caught even when the tamper hook is absent. Read the
design sheet `docs/plans/guardrails-lite-wave01-design.md` ("Kernel script I/O contract", `lock.ps1`).
You author the TESTS, one FIXTURE and a minimal STUB — you do NOT implement the lock (task 12 does,
reusing the Hash.psm1 that task 10 implements).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/Lite/LiteLockTests.cs`, files under
`tests/Guardrails.Integration.Tests/Lite/Fixtures/lock/`, and the stub `scripts/lite/lock.ps1`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these paths
— including `LiteScriptHost.cs`, `scripts/lite/lib/Hash.psm1`, other test files, production code, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The contract the tests pin
- `lock.ps1 <planDir>` writes `<planDir>/state/lite-run.lock.json`:
  `{ "version":1, "createdAt":"<ISO-8601 UTC>", "planDefinitionHash":"sha256:…", "tasks":{ "<taskId>":"sha256:<TaskDefinitionHash>" } }`
  and prints `{ "script":"lock", "ok":true, "planDefinitionHash":"sha256:…" }`, exit 0.
- If the lock file already exists, `lock.ps1 <planDir>` REFUSES: exit **1**, `ok:false`, the file is left
  byte-for-byte unchanged. `lock.ps1 <planDir> -Force` overwrites it (exit 0).
- `lock.ps1 <planDir> -Verify` recomputes and prints
  `{ "script":"lock", "ok":<bool>, "changed":[ … ] }` (`changed` ALWAYS present, empty when unchanged):
  exit **0** when every hash matches; exit **1** otherwise, with `changed` = every task id whose
  TaskDefinitionHash moved (sorted ordinal); when the plan hash moved but NO task hash did (only a
  plan-level file — `guardrails.json` or the plan-root `guardrails/` / `preflights/` — changed),
  `changed` = `["<plan>"]` exactly.
- `-Verify` with no lock file: exit **1**, `ok:false`, `"reason":"no-lock"`.
- A missing `<planDir>`: exit **64**.

### Fixture — `tests/Guardrails.Integration.Tests/Lite/Fixtures/lock/two-tasks-with-gate/`
A flat plan that LOADS without errors: `guardrails.json` (`{ "version": 1 }`), `tasks/01-first/` and
`tasks/02-second/` (each: `task.json` with `description`, `dependsOn` (`02-second` depends on
`01-first`), `writeScope: []`; an `action.ps1`; a `guardrails/01-check.ps1` starting with a `# catches:`
line), and a plan-level `guardrails/01-gate.ps1` (also starting `# catches:`).

### Tests — class `LiteLockTests`, namespace `Guardrails.Integration.Tests.Lite`, `[Trait("Category", "Lite")]`
Drive the script ONLY through `LiteScriptHost` (task 02): `LiteScriptHost.RunAsync("lock", dir)`,
`RunAsync("lock", dir, "-Force")`, `RunAsync("lock", dir, "-Verify")`, on
`LiteScriptHost.CopyFixture("lock", "two-tasks-with-gate")`. Start each test with
`Assert.SkipUnless(LiteScriptHost.PwshAvailable, "pwsh (PowerShell 7) not found on PATH");`.
The oracle is Core: `new PlanLoader().Load(dir)` then `PlanDefinitionHash.Compute(plan)` /
`TaskDefinitionHash.Compute(task)` (assert the load has no errors first). Pinned `[Fact]` names:

- **`Lock_WritesPlanAndPerTaskDefinitionHashesMatchingCore`** — exit 0; the lock file exists;
  `version == 1`; `createdAt` parses as a date; `planDefinitionHash` == Core; `tasks` has exactly Core's
  task ids with Core's `TaskDefinitionHash` values.
- **`Lock_RefusesWhenALockAlreadyExists`** — lock, capture the file's bytes, lock again: exit 1,
  `ok == false`, bytes unchanged.
- **`Lock_Force_OverwritesAnExistingLock`** — lock; append a line to
  `tasks/01-first/guardrails/01-check.ps1`; lock `-Force`: exit 0 and the file's `planDefinitionHash`
  equals Core's hash of the EDITED plan.
- **`Verify_UnchangedPlan_ExitsZero`** — lock, then `-Verify`: exit 0, `ok == true`, `changed` empty.
- **`Verify_AfterEditingATaskGuardrail_ExitsOneNamingThatTask`** — lock; append a line to
  `tasks/02-second/guardrails/01-check.ps1`; `-Verify`: exit 1 and `changed` is exactly `["02-second"]`.
- **`Verify_AfterEditingOnlyAPlanLevelGate_ExitsOneNamingPlan`** — lock; append a line to the plan-level
  `guardrails/01-gate.ps1`; `-Verify`: exit 1 and `changed` is exactly `["<plan>"]`.
- **`Verify_WithNoLockFile_ExitsOneWithReasonNoLock`** — `-Verify` on a fresh copy: exit 1,
  `ok == false`, `reason == "no-lock"`.
- **`Lock_MissingPlanDir_ExitsWithUsageCode64`** — a non-existent directory: exit 64.

### Stub (#155 — the tests must COMPILE and RUN and FAIL)
`scripts/lite/lock.ps1`: `#Requires -Version 7`, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`, then print `{"script":"lock","ok":false,"stub":true}` and `exit 99`.
It must write NOTHING. **Every one of the 8 pinned tests must FAIL against this stub** — assert on the
script's exit code and output and on the file it writes, never on something the test computed alone.
Do NOT implement the lock.

### Completion criteria (these match the guardrails)
1. `dotnet build tests/Guardrails.Integration.Tests` succeeds.
2. With the stub in place, all 8 pinned tests run and each is observed **Failed** in the test results.
