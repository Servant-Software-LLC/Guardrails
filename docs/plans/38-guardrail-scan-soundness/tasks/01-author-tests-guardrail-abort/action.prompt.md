## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-guardrail-abort`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-guardrail-abort": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "01-author-tests-guardrail-abort": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
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

Author `tests/Guardrails.Core.Tests/Execution/GuardrailAbortTests.cs`, class **`GuardrailAbortTests`**,
every test carrying `[Trait("Category", "ScanSoundness")]`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/GuardrailAbortTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including changes to production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### What this pins

Today a PowerShell guardrail that hits an engine-raised error **aborts mid-script and the process still
exits 0**, so the harness records `Passed = true` for a check that never reached its own verdict. Design
of record: `docs/plans/38-guardrail-scan-soundness.md` §3 (read §3.3 before writing — it lists the exact
cases and the two ways a plausible fix breaks `exit 1`).

Write real `.ps1` files to a temp directory in each test and run them through the production path. Do not
fake the script runner: the whole defect lives in what a real `pwsh` child process does with an exit code,
so a faked runner would pin nothing. Tear the temp directory down in a `finally` / `IDisposable`.

**Pin exactly these six method names — the guardrails bind to them by name:**

| method | script under test | asserts |
|---|---|---|
| `AbortedGuardrail_IsNotAPass` | `$ErrorActionPreference='Continue'`, `try { $problems.Add("x"); exit 0 } finally {}` — a method call on a `$null` variable | the result is **NOT** passed |
| `AbortedGuardrail_ReasonNamesTheAbort_NotTheFirstStdoutLine` | same script, but writing `Write-Output 'guardrail starting'` first | the failure reason names the abort; it is **not** the string `guardrail starting` |
| `AbortedGuardrail_SurfacesTheInterpreterErrorText` | same script | the recorded output carries the interpreter's own error text, so a retrying agent reads WHY |
| `ExitOneGuardrail_StillFails` | `Write-Output 'a real finding'; exit 1` | the result is **NOT** passed, and its reason is the finding — **this is the regression guard; see below** |
| `ExitZeroGuardrail_StillPasses` | `Write-Output 'all good'; exit 0` | the result **IS** passed |
| `GuardrailWritingToStderr_ThenExitingZero_StillPasses` | writes to stderr via `[Console]::Error.WriteLine(...)`, then `exit 0` | the result **IS** passed — a guardrail that shells out to `git` or `dotnet` gets stderr noise, and that must never be read as failure |

**`ExitOneGuardrail_StillFails` and `ExitZeroGuardrail_StillPasses` and
`GuardrailWritingToStderr_ThenExitingZero_StillPasses` are GREEN on today's code, deliberately.** They are
not TDD-red rows and the census below declares them exempt. They exist because two independently plausible
fixes for this defect were measured to turn `exit 1` into exit 0 — a real guardrail finding reported as a
pass, which is a worse defect than the one being fixed. Do not delete them for being green, and do not
contort them into failing.

The three `Aborted*` tests MUST fail on the current tree. If one of them passes, the test is not coupled to
the behaviour it claims to pin — fix the test, not the production code (which you may not touch).

`GuardrailRunner` is `internal sealed`; `Guardrails.Core.Tests` already reaches internals, so follow
whatever the neighbouring tests in that project do rather than adding any assembly attribute (the `.csproj`
is outside your scope). If the production type genuinely cannot be driven from a test without a change you
are not permitted to make, write `{"needsHuman": "<what is missing>"}` and stop.
