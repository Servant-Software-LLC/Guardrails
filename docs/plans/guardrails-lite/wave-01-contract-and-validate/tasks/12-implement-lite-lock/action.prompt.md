## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/12-implement-lite-lock`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/12-implement-lite-lock": { "someKey": "someValue" } }`.
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

**Fill real logic over the stub `scripts/lite/lock.ps1`** so the already-authored
`tests/Guardrails.Integration.Tests/Lite/LiteLockTests.cs` passes. That is your ONLY file. **Do NOT edit
the tests, the fixture under `tests/Guardrails.Integration.Tests/Lite/Fixtures/lock/`, or
`scripts/lite/lib/Hash.psm1`** — all outside your `writeScope`; the harness rejects the edit. If a test is
genuinely wrong, or Hash.psm1 is missing a function you need, write
`{"needsHuman": "<what and why>"}` and stop rather than editing it.

**Reuse Hash.psm1; do not re-implement hashing.** `Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1')`
and call `Get-LitePlanDefinitionHash -PlanDir` and `Get-LiteTaskDefinitionHash -PlanDir -TaskId` (task 10
implemented and parity-tested them against Core). Task ids are the folder names under `<planDir>/tasks/`,
sorted ordinal (`[StringComparer]::Ordinal`, never `Sort-Object`). Read the whole test file first — it is
the spec; the contract it pins (also in `docs/plans/guardrails-lite-wave01-design.md`) is:

- `lock.ps1 <planDir>` — writes `<planDir>/state/lite-run.lock.json` (create `state/`):
  `{ "version":1, "createdAt":"<[DateTimeOffset]::UtcNow.ToString('o')>", "planDefinitionHash":…, "tasks":{ "<id>":… } }`;
  prints `{ "script":"lock","ok":true,"planDefinitionHash":… }` as ONE compressed JSON line; exit 0.
- If the lock file exists and `-Force` is not given: print `ok:false` (with a `reason`), exit **1**, and
  do NOT touch the file. `-Force` overwrites.
- `-Verify` — read the lock; recompute; print `{ "script":"lock","ok":<bool>,"changed":[…] }` with
  `changed` ALWAYS present (empty array when unchanged — beware PowerShell collapsing a one-element or
  empty array in `ConvertTo-Json`; build it as `[string[]]` / `@(...)` and verify the emitted JSON is an
  array in both cases). Exit 0 when all hashes match; else exit **1** with `changed` = every task id whose
  hash differs from the lock or that was added/removed (ordinal-sorted); if the plan hash differs but no
  task hash does, `changed` = `["<plan>"]`. No lock file → exit **1**, `ok:false`, `"reason":"no-lock"`.
- A missing `<planDir>` → exit **64**, `ok:false`, message on stderr.

Keep `#Requires -Version 7`, `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`.
**No network calls.** Remove every stub marker (`"stub":true`, `exit 99`).

Verify with:
`dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteLockTests"`

### Completion criteria (these match the guardrails)
1. `dotnet build tests/Guardrails.Integration.Tests` succeeds.
2. All 8 pinned `LiteLockTests` run and pass.
3. `lock.ps1` carries no stub marker and imports `lib/Hash.psm1` rather than hashing on its own.
