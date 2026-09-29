# Wave 2: run kernel

> Seeded from the "Delivery waves → Wave 2: run kernel" section of `docs/plans/guardrails-lite.charter.md`
> (the plan of record). This is the wave's INTENT. Its tasks are authored at this checkpoint against the
> materialized integration worktree, so every path and signature below comes from wave 1's real output.

This wave is the Lite loop, built as `pwsh` scripts under `scripts/lite/`. Each script prints one JSON
object and uses the exit-code vocabulary wave 1 established: `0` ok, `1` negative verdict, `2` refused,
`3` step-order violation, `64` usage. It builds on these wave-1 artifacts:

- `scripts/lite/validate.ps1` and `scripts/lite/validate/Load.psm1`: reuse the plan loader. Don't
  re-parse `task.json`.
- `scripts/lite/lib/Hash.psm1`: `TaskDefinitionHash` for the lock recheck and the journal's
  `definitionHash`.
- `scripts/lite/lock.ps1` and its `-Verify` mode.
- `tests/Guardrails.Integration.Tests/Lite/LiteScriptHost.cs`: the xUnit host for every new test class.

Deliverables (see the charter's "The deterministic kernel" table and "Tamper resistance"):

1. **A journal module with step tokens.** It writes `state/run.json` using the harness's shape
   (`version`, `runId`, `planHash`, `nextMergeSequence`, and `tasks{id → status/definitionHash/attempts[]}`).
   Lite-only data goes under a top-level `lite` key, which the harness ignores on read. Each kernel
   step records a per-attempt step token. A script refuses to run (exit `3`) unless its predecessor
   step has run for the same task and attempt.
2. **`next.ps1`**, the only thing that decides what runs next. It works sequentially and honours
   `dependsOn` and the journal statuses.
3. **`start-task.ps1`.** It prepares the workspace on the plan branch and **composes the task prompt
   deterministically** from the action body, dependency context and the previous attempt's feedback.
   It uses the harness's section headings (`PromptComposer.ComposeAction`).
4. **`run-action.ps1`**, which runs a script action with a timeout.
5. **`check.ps1`.** It runs the writeScope check (the same semantics as `WriteScopeCheck`/`WriteScope.IsInScope`),
   then the script guardrails in ordinal order with failFast, using the harness's guardrail environment
   variables. It lists the pending prompt-judges and each one's verdict-file path.
6. **`record.ps1`.** It folds in the verdicts, re-runs `lock.ps1 -Verify` and refuses a pass if a
   definition moved, and enforces `retries`. It sets `needs-human` when retries run out and commits the
   task's work with the `Guardrails-Task:` / `Guardrails-Task-Hash:` trailers.
7. **`reset.ps1 <plan> <task>… [-y]`**, with the harness's safe/unsafe rule (SSOT §7.2).
8. **`preflight.ps1`, `terminal-gate.ps1` and `report.ps1`.** The report is built only from the journal,
   and the orchestrator prints it verbatim.
9. **A non-interactive Lite driver plus an execution-parity test.** Script-only fixtures run through
   `guardrails run` and through the driver, and must produce the same final task states and verdicts.

Runtime: `pwsh` 7 only, **no network calls**, and tasks run **sequentially** (decision
`lite-parallelism`). The next wave, 3 (orchestration surface), is stubbed when this wave is authored.
