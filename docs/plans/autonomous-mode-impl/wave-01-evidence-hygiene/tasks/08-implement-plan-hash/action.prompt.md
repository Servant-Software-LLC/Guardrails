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

The command SHOULD already be registered in `CommandFactory.BuildRootCommand` (the previous task added
it). **First verify** it is — `grep 'PlanHashCommand' src/Guardrails.Cli/CommandFactory.cs`. If the
registration is present, do NOT touch `CommandFactory.cs`; your test passes by driving that same real
dispatch. If the registration is MISSING (the test-author stubbed the command but the registration did
not land), add the single line `rootCommand.Add(PlanHashCommand.Create(io));` to
`CommandFactory.BuildRootCommand` — that line is in your `writeScope` precisely so you never dead-end at
`needs-human` over a missing registration (W3). Keep the output a single clean `sha256:…` line the
`/guardrails-review` skill can parse.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/PlanHashCommand.cs` and `src/Guardrails.Cli/CommandFactory.cs` (the latter
ONLY to add the `PlanHashCommand` registration if it is missing). Do NOT edit the authored tests — if a
test is genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit
fails the write-scope check and burns a retry).

Completion criteria (your guardrails check these): `PlanHashCliTests` passes through the real
`CommandFactory` dispatch, and `CommandFactory` still registers `PlanHashCommand`.
