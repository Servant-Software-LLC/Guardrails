## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/17-wire-exit-code-and-verify-composition-root` — NOT the stableId. (This
  task publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the escalation exit code + verify the composition root — PART 3 of 3, the DAG SINK** (issue #361
Phase 3; the #120 composition-root lesson). Tasks 15 and 16 wired the factory + Scheduler classify-then-act
+ resume reply channel. YOUR slice surfaces the distinct exit code AND is where the FULL drive-the-real-
factory proof (all five facts of the authored `SchedulerEscalationWiringTests`) finally goes green —
because only now is EVERYTHING wired.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree (which now includes tasks 15 + 16):
- `ExitCodes` (`src/Guardrails.Cli/ExitCodes.cs`) — grep `public const int`. Today: `Success = 0`,
  `HarnessError = 1`, `TaskFailed = 2`, `Cancelled = 3`. Add `EscalationsPending = 4` (the next free value)
  with an XML-doc comment, mirroring the existing constants' style.
- `RunCommand` (`src/Guardrails.Cli/Commands/RunCommand.cs`) — grep how it maps a `RunReport` to an exit
  code today (it already returns `ExitCodes.TaskFailed` for needs-human/blocked and `ExitCodes.Cancelled`
  for cancellation). Return `ExitCodes.EscalationsPending` when the run ends with **unresolved escalations**
  (an answer-required halt) — do NOT reuse `2` (`TaskFailed`) or `0`; the §7.1 point is that a firstmate
  consumer can tell an answer-required halt apart from a plain needs-human and never read either as green.
- The report signal for "unresolved escalations" comes from the Scheduler/journal wired in tasks 15/16
  (grep `escalat` in `RunReport.cs` / the journal, or the `decisions[]` `escalated` tokens with no matching
  `answer-injected`/`consumed`). Verify the exact accessor before branching on it; if the report exposes no
  such signal, write `{"needsHuman": "how does RunReport surface unresolved escalations?"}` and stop rather
  than inventing one.

Do NOT change the Scheduler / factory / component logic (tasks 15/16 own that). This task is the exit-code
constant + the RunCommand mapping only. Design of record: `docs/plans/12-autonomous-mode.md` §7.1 (exit code).

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&` call-operator, no pipe, no
`2>&1 |`, not the PowerShell tool (issue #374 blocks those as "multiple operations requiring approval"):

    dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests"

ALL FIVE facts must PASS now — including `Cli_RunWithUnresolvedEscalation…` (the exit-code fact you make
green). Do NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests`: its fixture-leaking
classes (`HarnessWriteRunTests`, `RetrySalvageTests`, `ScriptActionReproductionShortCircuitTests`,
`WriteScopeCheckTests`) drop `outside.txt` / `src/output.txt` / `docs/outside.txt` into the worktree →
write-scope false-positive rollback (#253). If a `git checkout <ref> -- <path>` salvage recovery is blocked
by the permission wall (#374), do NOT fight it — re-author the file directly with `Write` instead.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs` and
`src/Guardrails.Cli/ExitCodes.cs`. The harness runs a post-action `git diff` membership check and REJECTS
any edit outside these two paths — including `SchedulerFactory.cs` / `Scheduler.cs` (task 15),
`ActionRunner.cs` / `PromptComposer.cs` (task 16), the authored wiring test, or any component type's file.
An out-of-scope edit fails the task immediately and consumes a retry. If the exit code genuinely needs a
change to the Scheduler/report (e.g. the report exposes no unresolved-escalations signal), do NOT edit it —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Completion criteria (your guardrails check these): `ExitCodes.cs` declares `EscalationsPending = 4`,
`RunCommand.cs` returns `ExitCodes.EscalationsPending`, and ALL FIVE `SchedulerEscalationWiringTests` facts
pass — the drive-the-real-`SchedulerFactory.Create` composition-root proof, including the distinct exit code.
