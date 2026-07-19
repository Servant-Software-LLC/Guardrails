## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/08-implement-plan-hash` — NOT the stableId. (This task publishes nothing to
  state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the read-only `guardrails plan-hash <folder>` command by filling REAL logic over the stub
`src/Guardrails.Cli/Commands/PlanHashCommand.cs` the previous task authored. Make the authored
`PlanHashCliTests` pass WITHOUT editing them.

The command:
- Loads the plan folder (use the existing `PlanLoader`, matching how `ValidateCommand` / `MarkReviewedCommand`
  load a plan), and on a load error prints the diagnostics and exits non-zero (mirror the existing
  commands' error handling).
- On success, prints the plan's `Guardrails.Core.Journal.PlanDefinitionHash.Compute(plan)` value (the
  `sha256:…` string) to stdout via the injected `IConsoleIo`, and exits 0. Read-only — it writes nothing.

The command is ALREADY registered in `CommandFactory.BuildRootCommand` (the previous task added it); do
NOT re-register it — your test passes by driving that same real dispatch. Keep the output a single clean
`sha256:…` line the `/guardrails-review` skill can parse.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/PlanHashCommand.cs`. Do NOT edit the authored tests or CommandFactory — if
a test is genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit
fails the write-scope check and burns a retry).

Completion criteria (your guardrails check these): `PlanHashCliTests` passes through the real
`CommandFactory` dispatch, and `CommandFactory` still registers `PlanHashCommand`.
