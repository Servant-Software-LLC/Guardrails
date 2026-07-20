## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/16-wire-classify-then-act-into-scheduler` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the classify-then-act reply channel — PART 2 of 3** (issue #361 Phase 3; the #120 lesson). Task 15
already wired `SchedulerFactory.Create` to construct + inject `FileEscalationSink` / `CriticalityJudge` /
`BlockerRetry` and added the classify-then-act DISPATCH at the gate (escalate / record-best-guess /
blocker-retry). YOUR slice completes the **reply channel** so the wired behaviour becomes OBSERVABLE:
resume answer-consumption + the best-guess/answer INJECTION into the next attempt's prompt. Task 17 then
surfaces the exit code.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. These reflect the plan-authoring-time state — confirm each still holds in the
materialized tree (which now includes task 15's Scheduler.cs changes) before assuming its shape:
- `PromptComposer.ComposeAction` (`src/Guardrails.Core/Prompts/PromptComposer.cs`) — it ALREADY takes a
  trailing optional `string? injectedHumanAnswer = null` param (task 13) and appends the pinned
  `[BEGIN UNTRUSTED HUMAN ANSWER]…[END UNTRUSTED HUMAN ANSWER]` envelope via `AppendInjectedHumanAnswer`.
  So PromptComposer likely needs NO change — your job is to make `ActionRunner` actually PASS a non-null
  value through it.
- `ActionRunner` (`src/Guardrails.Core/Execution/ActionRunner.cs`) — the SOLE caller of
  `PromptComposer.ComposeAction` (grep `PromptComposer.ComposeAction`). Today it omits the
  `injectedHumanAnswer` argument (defaults null). Thread the injected answer/best-guess text here.
- `AnswerFileConsumer` (`src/Guardrails.Core/Execution/AnswerFileConsumer.cs`) — constructor
  `AnswerFileConsumer(string escalationsDir)`; construct it at the RESUME use-site in `Scheduler.cs`
  (do NOT factory-inject it — an unused injected field breaks the `TreatWarningsAsErrors` build).
- The classify-then-act dispatch task 15 added in `Scheduler.cs` — grep `GateClassifier` /
  `FileEscalationSink` to find it; extend the same region for resume + injection.

Wire it (design of record `docs/plans/12-autonomous-mode.md` §7.2 sink, §7.6 resume consumption):
- **Resume answer-consumption**: before a unit re-hits an escalated gate, run `AnswerFileConsumer` (§7.6):
  on a valid `…answer.json` inject the answer via `ActionRunner` → `PromptComposer.ComposeAction`'s
  `injectedHumanAnswer` (the delimited-UNTRUSTED section), record `decision == answer-injected`, and flip
  the escalation `status` to `consumed`; on malformed/absent, re-escalate. NEVER forge a review attestation.
- **Best-guess injection**: for the below-threshold judgment-call path task 15 records as
  `proceeded-best-guess`, thread the best-guess text into the NEXT attempt's composed prompt through the
  same `ActionRunner` → `injectedHumanAnswer` channel (delimited untrusted data).
- Do NOT weaken the authored `SchedulerEscalationWiringTests` — make them pass by WIRING. Do NOT change the
  component types' internal logic.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&` call-operator, no pipe, no
`2>&1 |`, not the PowerShell tool (issue #374 blocks those as "multiple operations requiring approval"):

    dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~SchedulerEscalationWiringTests"

The FOUR classify-then-act / resume facts must PASS now; the fifth, `Cli_RunWithUnresolvedEscalation…`
(the exit-code fact), stays RED until task 17 adds `ExitCodes.EscalationsPending` — that is EXPECTED, so you
should see exactly 4 passing and 1 failing on that one method. (Your guardrail excludes that fact; you do
not need to make it pass.) Do NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests`:
its fixture-leaking classes (`HarnessWriteRunTests`, `RetrySalvageTests`,
`ScriptActionReproductionShortCircuitTests`, `WriteScopeCheckTests`) drop `outside.txt` / `src/output.txt` /
`docs/outside.txt` into the worktree → write-scope false-positive rollback (#253). If a
`git checkout <ref> -- <path>` salvage recovery is blocked by the permission wall (#374), do NOT fight it —
re-author the file directly with `Write` instead.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`,
`src/Guardrails.Core/Execution/ActionRunner.cs`, and `src/Guardrails.Core/Prompts/PromptComposer.cs`. The
harness runs a post-action `git diff` membership check and REJECTS any edit outside these paths — including
`SchedulerFactory.cs` (task 15), `RunCommand.cs` / `ExitCodes.cs` (task 17), the authored wiring test, or
any component type's file. An out-of-scope edit fails the task immediately and consumes a retry. If wiring
genuinely needs a change to a component type or another file, do NOT edit it — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Completion criteria (your guardrails check these): the solution builds, and the four classify-then-act /
resume facts of `SchedulerEscalationWiringTests` pass (escalated / proceeded-best-guess-injected /
blocker-retried / answer-injected), driving the REAL `SchedulerFactory.Create`.
